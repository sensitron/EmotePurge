#!/usr/bin/env node
// Local pre-flight check that estimates whether the current branch would pass SonarCloud's
// Quality Gate condition "coverage on new code >= 80%" (see .github/workflows/sonarcloud.yml)
// — WITHOUT calling SonarCloud at all. Meant to be run before pushing/opening a PR, so a
// coverage shortfall is visible locally instead of only after CI/SonarCloud comes back red.
//
// APPROXIMATION — READ THIS BEFORE TRUSTING A GREEN RESULT:
// SonarCloud's "new code" coverage is computed LINE-BY-LINE: it diffs against the base branch
// (via git blame) and only counts lines that are actually new/changed as the denominator. This
// script approximates that at FILE granularity instead: for every file touched on this branch,
// it takes that file's *entire* line coverage (covered lines / coverable lines, from the same
// OpenCover/lcov reports Sonar itself would read) as a stand-in for "new code coverage" of that
// file. Line-accurate diffing was deliberately not attempted here — re-implementing Sonar's
// blame-based new-code detection would be its own non-trivial piece of software with its own
// failure modes, and a wrong "close approximation" is worse than an honest coarse one.
//
// What this means in practice:
//   - Brand-new files: the approximation is close to accurate (nearly all lines ARE new).
//   - A single new/changed line dropped into a large, already well-tested file: this script
//     will report that file's overall (mostly old, mostly covered) percentage, which can look
//     fine while SonarCloud reports 0% new-code coverage for the same file, because only that
//     one new line counts there. This is the direction where this script can give a false
//     "all clear" that SonarCloud then contradicts.
//   - The opposite also happens: a file with a poor OVERALL percentage (lots of old, untested
//     code) whose few new lines happen to be exactly the ones covered by a new test would score
//     red here while SonarCloud scores that same diff 100% (only the new lines count there).
// Neither direction is bounded — this is a genuine approximation, not a safe upper or lower
// bound on what SonarCloud will report. Treat a green result as "probably fine, not a
// guarantee" (especially for small, surgical diffs into large files), and treat a red result as
// "worth a closer look", not as proof SonarCloud will also fail the gate.
//
// Required tools: git, dotnet, npm (unless the corresponding side is skipped).
// Docker must be running for the backend side (dotnet test uses Testcontainers).
//
// Usage:
//   node scripts/coverage-local.mjs [--base <ref>] [--skip-tests] [--backend-only|--frontend-only]
//
//   --base <ref>      Base ref to diff against (three-dot: <ref>...HEAD). Default: origin/main.
//   --skip-tests      Don't run the test suites; just read whatever reports are already on disk.
//   --backend-only    Only run/evaluate the .NET (OpenCover) side.
//   --frontend-only   Only run/evaluate the Angular (lcov) side.

import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

// Quality Gate threshold this script approximates. Must match the SonarCloud project's actual
// gate condition ("Coverage on New Code" >= 80%) — that value lives in SonarCloud's project
// settings, not in this repo, so there is nothing to keep it "in sync" with beyond this comment.
const NEW_CODE_COVERAGE_THRESHOLD_PERCENT = 80;

// Languages SonarCloud actually analyzes coverage for in this project. `.spec.ts` is
// deliberately excluded even though it ends in `.ts`: Sonar does not count test files as
// coverable "new code" — only the production code they exercise is measured.
const ANALYZED_EXTENSIONS = [".cs", ".ts"];

// These four lists MUST be kept in sync by hand with .github/workflows/sonarcloud.yml's
// `sonar.exclusions` / `sonar.coverage.exclusions` scanner arguments (see the "Sonar Scanner
// begin" step there). There is no shared source of truth between the workflow and this script.
const SONAR_EXCLUSIONS = [
  "**/Migrations/**",
  "src/EmotePurge.Api/wwwroot/**",
  "web/dist/**",
  "**/node_modules/**",
];
const SONAR_COVERAGE_EXCLUSIONS = [
  "web/e2e/**",
  "**/Migrations/**",
  "scripts/**",
];
// Not part of sonar.exclusions/sonar.coverage.exclusions: the .NET scanner learns to treat these
// xUnit projects as test code (not "new code") from `<IsTestProject>true</IsTestProject>` in
// their .csproj files, a mechanism this script has no access to. Excluding tests/** by hand
// reproduces the same effect.
const NON_PRODUCTION_EXCLUSIONS = ["tests/**"];
const COVERAGE_EXCLUSION_PATTERNS = [
  ...SONAR_EXCLUSIONS,
  ...SONAR_COVERAGE_EXCLUSIONS,
  ...NON_PRODUCTION_EXCLUSIONS,
];

const OPENCOVER_GLOB_MARKER = "TestResults";
const OPENCOVER_REPORT_FILENAME = "coverage.opencover.xml";
const LCOV_REPORT_PATH = "web/coverage/web/lcov.info";

// Directories never worth descending into while hunting for coverage.opencover.xml files —
// either they can't contain one (node_modules, .git, build output) or descending into them is
// simply wasteful (web/ never contains .NET TestResults).
const WALK_SKIP_DIRECTORIES = new Set([
  "node_modules",
  ".git",
  "bin",
  "obj",
  "web",
  ".angular",
  ".sonar",
  ".github",
]);

function parseArgs(argv) {
  const args = {
    base: "origin/main",
    skipTests: false,
    backendOnly: false,
    frontendOnly: false,
    help: false,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--help" || arg === "-h") {
      args.help = true;
    } else if (arg === "--skip-tests") {
      args.skipTests = true;
    } else if (arg === "--backend-only") {
      args.backendOnly = true;
    } else if (arg === "--frontend-only") {
      args.frontendOnly = true;
    } else if (arg === "--base") {
      const value = argv[i + 1];
      if (!value)
        throw new Error("--base braucht einen Wert, z. B. --base origin/main");
      args.base = value;
      i += 1;
    } else {
      throw new Error(`Unbekannte Option: ${arg} (siehe --help)`);
    }
  }
  if (args.backendOnly && args.frontendOnly) {
    throw new Error(
      "--backend-only und --frontend-only schließen sich gegenseitig aus.",
    );
  }
  return args;
}

function printHelp() {
  console.log(
    [
      "Lokale Näherung an SonarCloud's Quality-Gate-Bedingung 'Coverage on New Code >= 80%'.",
      "",
      "Usage:",
      "  node scripts/coverage-local.mjs [--base <ref>] [--skip-tests] [--backend-only|--frontend-only]",
      "",
      "  --base <ref>      Basis-Ref für den Diff (Drei-Punkt: <ref>...HEAD). Default: origin/main.",
      "  --skip-tests      Testläufe überspringen, nur vorhandene Reports auswerten.",
      "  --backend-only    Nur die .NET/OpenCover-Seite.",
      "  --frontend-only   Nur die Angular/lcov-Seite.",
    ].join("\n"),
  );
}

function runInherited(command, commandArgs, cwd) {
  const result = spawnSync(command, commandArgs, { cwd, stdio: "inherit" });
  if (result.error) {
    if (result.error.code === "ENOENT") {
      throw new Error(
        `Befehl "${command}" wurde nicht gefunden — ist er installiert und im PATH?`,
      );
    }
    throw new Error(
      `Befehl "${command} ${commandArgs.join(" ")}" konnte nicht gestartet werden: ${result.error.message}`,
    );
  }
  return result.status ?? 1;
}

function resolveRepoRoot() {
  const result = spawnSync("git", ["rev-parse", "--show-toplevel"], {
    encoding: "utf8",
  });
  if (result.error) {
    if (result.error.code === "ENOENT") {
      throw new Error(
        '"git" wurde nicht gefunden — ist es installiert und im PATH?',
      );
    }
    throw new Error(
      `"git rev-parse --show-toplevel" konnte nicht gestartet werden: ${result.error.message}`,
    );
  }
  if (result.status !== 0) {
    throw new Error(
      `"git rev-parse --show-toplevel" ist fehlgeschlagen (Exit ${result.status}). Läuft dieses Skript in einem Git-Repository?\n${(result.stderr ?? "").trim()}`,
    );
  }
  return result.stdout.trim();
}

function isDockerRunning() {
  const result = spawnSync("docker", ["info"], { stdio: "ignore" });
  return !result.error && result.status === 0;
}

function runBackendTests(repoRoot) {
  if (!isDockerRunning()) {
    throw new Error(
      "Docker läuft nicht (oder ist nicht erreichbar) — dotnet test EmotePurge.slnx braucht einen laufenden " +
        "Docker-Daemon für die Testcontainers-Integrationstests in EmotePurge.Infrastructure.Tests. " +
        "Docker starten und erneut versuchen, oder --frontend-only nutzen.",
    );
  }
  console.log(
    '--- Backend: dotnet test EmotePurge.slnx --collect:"XPlat Code Coverage;Format=opencover" ---',
  );
  const exitCode = runInherited(
    "dotnet",
    [
      "test",
      "EmotePurge.slnx",
      "--collect:XPlat Code Coverage;Format=opencover",
    ],
    repoRoot,
  );
  if (exitCode !== 0) {
    throw new Error(
      `dotnet test ist mit Exit-Code ${exitCode} fehlgeschlagen — siehe Ausgabe oberhalb.`,
    );
  }
}

function runFrontendTests(repoRoot) {
  console.log(
    "--- Frontend: npm --prefix web test -- --watch=false --coverage --coverage-reporters=lcov ---",
  );
  const exitCode = runInherited(
    "npm",
    [
      "--prefix",
      "web",
      "test",
      "--",
      "--watch=false",
      "--coverage",
      "--coverage-reporters=lcov",
    ],
    repoRoot,
  );
  if (exitCode !== 0) {
    throw new Error(
      `npm test ist mit Exit-Code ${exitCode} fehlgeschlagen — siehe Ausgabe oberhalb.`,
    );
  }
}

// Walks the repo looking for coverage.opencover.xml files under any TestResults/ directory.
// Each `dotnet test` run creates a NEW GUID subfolder under TestResults/ and leaves old ones
// behind — a real trap: reading all of them would double-count stale runs, and reading an old
// one instead of the newest would silently score a previous (possibly unrelated) code state.
// So: group by the TestResults/ directory itself (one group per test project) and keep only the
// file with the newest mtime in each group.
function findLatestOpenCoverReports(repoRoot) {
  const found = []; // { dir: <.../TestResults>, file: <full path>, mtimeMs }

  function walk(dir) {
    let entries;
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      return; // unreadable directory (permissions, race) — skip rather than crash the whole scan
    }
    for (const entry of entries) {
      if (entry.name.startsWith(".") && entry.name !== ".") continue;
      const fullPath = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (WALK_SKIP_DIRECTORIES.has(entry.name)) continue;
        walk(fullPath);
      } else if (
        entry.isFile() &&
        entry.name === OPENCOVER_REPORT_FILENAME &&
        dir.includes(OPENCOVER_GLOB_MARKER)
      ) {
        const testResultsDir = findAncestorNamed(dir, OPENCOVER_GLOB_MARKER);
        found.push({
          dir: testResultsDir ?? dir,
          file: fullPath,
          mtimeMs: statSync(fullPath).mtimeMs,
        });
      }
    }
  }

  walk(repoRoot);

  const latestByGroup = new Map();
  for (const entry of found) {
    const current = latestByGroup.get(entry.dir);
    if (!current || entry.mtimeMs > current.mtimeMs)
      latestByGroup.set(entry.dir, entry);
  }
  return [...latestByGroup.values()].map((entry) => entry.file);
}

function findAncestorNamed(dir, name) {
  let current = dir;
  while (current && current !== path.dirname(current)) {
    if (path.basename(current) === name) return current;
    current = path.dirname(current);
  }
  return null;
}

function extractAttr(tag, attrName) {
  const match = new RegExp(`${attrName}="([^"]*)"`).exec(tag);
  return match ? match[1] : null;
}

// Parses one OpenCover XML report into `fullPath -> Map<lineNumber, covered>`. A line counts as
// covered if ANY SequencePoint on that (fileid, line) pair has vc > 0 — a line can carry several
// sequence points (e.g. multiple statements, branches), so points are deduplicated by
// (fileid, sl) and OR-ed together rather than counted individually.
function parseOpenCoverXml(xmlText) {
  const pathByFileUid = new Map();
  for (const tag of xmlText.match(/<File\b[^>]*\/?>/g) ?? []) {
    const uid = extractAttr(tag, "uid");
    const fullPath = extractAttr(tag, "fullPath");
    if (uid && fullPath) pathByFileUid.set(uid, fullPath);
  }

  const linesByFileUid = new Map(); // fileUid -> Map<line, covered>
  for (const tag of xmlText.match(/<SequencePoint\b[^>]*\/>/g) ?? []) {
    const fileid = extractAttr(tag, "fileid");
    const startLine = extractAttr(tag, "sl");
    const visitCount = extractAttr(tag, "vc");
    if (!fileid || startLine === null) continue;
    const line = Number(startLine);
    const covered = Number(visitCount) > 0;
    if (!linesByFileUid.has(fileid)) linesByFileUid.set(fileid, new Map());
    const lineMap = linesByFileUid.get(fileid);
    lineMap.set(line, (lineMap.get(line) ?? false) || covered);
  }

  const result = new Map(); // fullPath -> Map<line, covered>
  for (const [uid, lineMap] of linesByFileUid) {
    const fullPath = pathByFileUid.get(uid);
    if (fullPath) result.set(fullPath, lineMap);
  }
  return result;
}

// Parses web/coverage/web/lcov.info into `path -> Map<lineNumber, covered>`. Standard lcov
// tracefile grammar: SF: starts a per-file block, DA:<line>,<hits> reports one line's hit count,
// end_of_record closes the block.
function parseLcov(lcovText) {
  const result = new Map();
  let currentPath = null;
  let currentLines = null;
  for (const rawLine of lcovText.split("\n")) {
    const line = rawLine.trim();
    if (line.startsWith("SF:")) {
      currentPath = line.slice(3).trim();
      currentLines = new Map();
    } else if (line.startsWith("DA:") && currentLines) {
      const [lineNoText, hitsText] = line.slice(3).split(",");
      const lineNo = Number(lineNoText);
      const hits = Number(hitsText);
      if (Number.isFinite(lineNo))
        currentLines.set(
          lineNo,
          (currentLines.get(lineNo) ?? false) || hits > 0,
        );
    } else if (line === "end_of_record") {
      if (currentPath && currentLines) result.set(currentPath, currentLines);
      currentPath = null;
      currentLines = null;
    }
  }
  return result;
}

// Normalizes an OpenCover/lcov path (absolute machine path, or a path already relative to some
// base) to a repo-relative POSIX path, so backend and frontend coverage data — and the `git diff`
// output — all key into the same namespace.
function normalizeToRepoRelativePosix(rawPath, repoRoot) {
  const slashed = rawPath.replace(/\\/g, "/");
  const looksAbsolute =
    path.isAbsolute(rawPath) || /^[A-Za-z]:\//.test(slashed);
  if (looksAbsolute) {
    return path.relative(repoRoot, rawPath).replace(/\\/g, "/");
  }
  const withoutDotSlash = slashed.replace(/^\.\//, "");
  // lcov entries are sometimes relative to the Angular project (web/) rather than the repo root —
  // if the raw relative path doesn't exist from the repo root but does under web/, it's the
  // web-relative form.
  if (
    !existsSync(path.join(repoRoot, withoutDotSlash)) &&
    existsSync(path.join(repoRoot, "web", withoutDotSlash))
  ) {
    return `web/${withoutDotSlash}`;
  }
  return withoutDotSlash;
}

// Merges a raw (path -> Map<line, covered>) report into the accumulator, keyed by repo-relative
// POSIX path. Multiple reports can legitimately cover the same file (e.g. a Core class exercised
// by more than one test project) — lines are OR-ed together, never overwritten.
function mergeCoverageReport(accumulator, rawReport, repoRoot) {
  for (const [rawPath, lineMap] of rawReport) {
    const normalizedPath = normalizeToRepoRelativePosix(rawPath, repoRoot);
    if (!accumulator.has(normalizedPath))
      accumulator.set(normalizedPath, new Map());
    const target = accumulator.get(normalizedPath);
    for (const [line, covered] of lineMap) {
      target.set(line, (target.get(line) ?? false) || covered);
    }
  }
}

function loadBackendCoverage(repoRoot) {
  const reportFiles = findLatestOpenCoverReports(repoRoot);
  const coverage = new Map();
  for (const file of reportFiles) {
    const xmlText = readFileSync(file, "utf8");
    mergeCoverageReport(coverage, parseOpenCoverXml(xmlText), repoRoot);
  }
  return { coverage, reportCount: reportFiles.length };
}

function loadFrontendCoverage(repoRoot) {
  const lcovPath = path.join(repoRoot, LCOV_REPORT_PATH);
  if (!existsSync(lcovPath)) {
    return { coverage: new Map(), reportCount: 0 };
  }
  const lcovText = readFileSync(lcovPath, "utf8");
  const coverage = new Map();
  mergeCoverageReport(coverage, parseLcov(lcovText), repoRoot);
  return { coverage, reportCount: 1 };
}

// Converts one exclusion glob (as used in sonar.exclusions / sonar.coverage.exclusions) into a
// RegExp. Supports the two forms actually used in this project's config: `**/x/**` (any depth on
// both sides) and `x/**` (a fixed prefix directory). `*` matches within one path segment.
function globToRegExp(pattern) {
  let out = "";
  let i = 0;
  while (i < pattern.length) {
    if (pattern.startsWith("**/", i)) {
      out += "(?:.*/)?";
      i += 3;
    } else if (pattern.startsWith("/**", i)) {
      out += "(?:/.*)?";
      i += 3;
    } else if (pattern.startsWith("**", i)) {
      out += ".*";
      i += 2;
    } else if (pattern[i] === "*") {
      out += "[^/]*";
      i += 1;
    } else {
      const ch = pattern[i];
      out += /[.+^${}()|[\]\\]/.test(ch) ? `\\${ch}` : ch;
      i += 1;
    }
  }
  return new RegExp(`^${out}$`);
}

const EXCLUSION_REGEXES = COVERAGE_EXCLUSION_PATTERNS.map(globToRegExp);

function isExcludedFromCoverage(relativePath) {
  return EXCLUSION_REGEXES.some((regex) => regex.test(relativePath));
}

function isAnalyzedSourceFile(relativePath) {
  if (relativePath.endsWith(".spec.ts")) return false;
  return ANALYZED_EXTENSIONS.some((ext) => relativePath.endsWith(ext));
}

function getChangedFiles(repoRoot, base) {
  const diffRange = `${base}...HEAD`;
  const result = spawnSync("git", ["diff", "--name-only", diffRange], {
    cwd: repoRoot,
    encoding: "utf8",
  });
  if (result.error) {
    if (result.error.code === "ENOENT") {
      throw new Error(
        '"git" wurde nicht gefunden — ist es installiert und im PATH?',
      );
    }
    throw new Error(
      `"git diff --name-only ${diffRange}" konnte nicht gestartet werden: ${result.error.message}`,
    );
  }
  if (result.status !== 0) {
    throw new Error(
      `Basis-Ref "${base}" konnte nicht aufgelöst werden (git diff --name-only ${diffRange} ist fehlgeschlagen). ` +
        `Prüfe den Wert von --base (Standard: origin/main) und ob ein "git fetch" nötig ist.\n` +
        `Git-Fehlermeldung:\n${result.stderr.trim()}`,
    );
  }
  return result.stdout
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .filter((relativePath) => existsSync(path.join(repoRoot, relativePath))); // ignore deleted files
}

function buildFileReport(changedFiles, coverageByPath) {
  const measured = [];
  const unmeasured = [];
  for (const filePath of changedFiles) {
    const lineMap = coverageByPath.get(filePath);
    if (!lineMap || lineMap.size === 0) {
      unmeasured.push(filePath);
      continue;
    }
    let covered = 0;
    for (const isCovered of lineMap.values()) {
      if (isCovered) covered += 1;
    }
    const total = lineMap.size;
    measured.push({
      filePath,
      covered,
      total,
      percent: (covered / total) * 100,
    });
  }
  measured.sort((a, b) => a.percent - b.percent);
  return { measured, unmeasured };
}

function formatPercent(percent) {
  return `${percent.toFixed(1)}%`;
}

function printFileTable(measured) {
  if (measured.length === 0) {
    console.log("  (keine gemessenen Dateien)");
    return;
  }
  const pathWidth = Math.max(
    ...measured.map((row) => row.filePath.length),
    "Datei".length,
  );
  const header = `  ${"Datei".padEnd(pathWidth)}  Zeilen (gedeckt/gesamt)  Anteil`;
  console.log(header);
  console.log(`  ${"-".repeat(header.length - 2)}`);
  for (const row of measured) {
    const linesText = `${row.covered}/${row.total}`.padStart(19);
    console.log(
      `  ${row.filePath.padEnd(pathWidth)}  ${linesText}  ${formatPercent(row.percent)}`,
    );
  }
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    printHelp();
    return;
  }

  const runBackend = !args.frontendOnly;
  const runFrontend = !args.backendOnly;

  const repoRoot = resolveRepoRoot();

  if (!args.skipTests) {
    if (runBackend) runBackendTests(repoRoot);
    if (runFrontend) runFrontendTests(repoRoot);
  } else {
    console.log(
      "--skip-tests gesetzt — werte nur vorhandene Coverage-Reports aus, ohne Tests neu laufen zu lassen.",
    );
  }

  console.log("");
  console.log(
    `Ermittle geänderte Dateien gegenüber ${args.base} (git diff --name-only ${args.base}...HEAD)...`,
  );
  const changedFiles = getChangedFiles(repoRoot, args.base);

  const relevantFiles = changedFiles
    .filter(isAnalyzedSourceFile)
    .filter((filePath) => !isExcludedFromCoverage(filePath));

  console.log(
    `${changedFiles.length} geänderte Datei(en) insgesamt, davon ${relevantFiles.length} relevant ` +
      "(.cs/.ts, keine .spec.ts, nicht ausgeschlossen).",
  );

  let backendCoverage = new Map();
  let backendReportCount = 0;
  let frontendCoverage = new Map();
  let frontendReportCount = 0;

  if (runBackend) {
    const backend = loadBackendCoverage(repoRoot);
    backendCoverage = backend.coverage;
    backendReportCount = backend.reportCount;
    if (backendReportCount === 0) {
      throw new Error(
        `Kein OpenCover-Report gefunden (Muster **/${OPENCOVER_GLOB_MARKER}/**/${OPENCOVER_REPORT_FILENAME}). ` +
          'Wurde dotnet test mit --collect:"XPlat Code Coverage;Format=opencover" schon einmal ausgeführt? ' +
          "Ohne --skip-tests sollte das automatisch passiert sein — prüfe die Ausgabe oberhalb auf Fehler.",
      );
    }
  }
  if (runFrontend) {
    const frontend = loadFrontendCoverage(repoRoot);
    frontendCoverage = frontend.coverage;
    frontendReportCount = frontend.reportCount;
    if (frontendReportCount === 0) {
      throw new Error(
        `Kein lcov-Report gefunden unter ${LCOV_REPORT_PATH}. Wurde ` +
          '"npm --prefix web test -- --watch=false --coverage --coverage-reporters=lcov" schon einmal ausgeführt? ' +
          "Ohne --skip-tests sollte das automatisch passiert sein — prüfe die Ausgabe oberhalb auf Fehler.",
      );
    }
  }

  const combinedCoverage = new Map();
  for (const [filePath, lineMap] of backendCoverage)
    combinedCoverage.set(filePath, lineMap);
  for (const [filePath, lineMap] of frontendCoverage)
    combinedCoverage.set(filePath, lineMap);

  const { measured, unmeasured } = buildFileReport(
    relevantFiles,
    combinedCoverage,
  );

  console.log(
    "\n=== Coverage je geänderter Datei (dateigenaue Näherung, aufsteigend nach Anteil) ===",
  );
  printFileTable(measured);

  if (unmeasured.length > 0) {
    console.log(
      "\n=== Nicht gemessen (keine Coverage-Daten gefunden — Report fehlt oder keine Tests) ===",
    );
    for (const filePath of unmeasured) console.log(`  - ${filePath}`);
  }

  if (relevantFiles.length === 0) {
    console.log(
      "\nKeine relevanten geänderten Dateien (.cs/.ts, keine .spec.ts, nicht ausgeschlossen) — nichts zu bewerten.",
    );
    return;
  }

  const totalCovered = measured.reduce((sum, row) => sum + row.covered, 0);
  const totalLines = measured.reduce((sum, row) => sum + row.total, 0);
  const overallPercent =
    totalLines > 0 ? (totalCovered / totalLines) * 100 : null;

  console.log("\n=== Gesamturteil ===");
  if (overallPercent === null) {
    console.log(
      "Keine der relevanten Dateien hat Coverage-Daten — Gesamtquote nicht berechenbar.",
    );
    process.exitCode = 1;
    return;
  }

  console.log(
    `Gesamtquote über ${measured.length} gemessene Datei(en): ${totalCovered}/${totalLines} Zeilen = ${formatPercent(overallPercent)}.`,
  );
  if (unmeasured.length > 0) {
    console.log(
      `Zusätzlich ${unmeasured.length} nicht gemessene Datei(en) — die Gesamtquote sagt über diese nichts aus.`,
    );
  }

  const belowThreshold = overallPercent < NEW_CODE_COVERAGE_THRESHOLD_PERCENT;
  if (belowThreshold) {
    console.log(
      `\n⚠ WARNUNG: ${formatPercent(overallPercent)} liegt unter der ${NEW_CODE_COVERAGE_THRESHOLD_PERCENT}%-Schwelle. ` +
        "Das ist ein deutliches Signal, dass SonarClouds Quality Gate diesen Branch ablehnen könnte — aber auch " +
        "diese Richtung ist nur eine dateigenaue Näherung, keine Garantie: Sonar zählt nur tatsächlich neue/" +
        "geänderte Zeilen, nicht die ganze Datei, und kann im Einzelfall auch günstiger ausfallen als diese Zahl.",
    );
  } else {
    console.log(
      `\n✓ ${formatPercent(overallPercent)} liegt über der ${NEW_CODE_COVERAGE_THRESHOLD_PERCENT}%-Schwelle — aber das ist ` +
        "eine dateigenaue Näherung, keine Garantie: SonarCloud misst zeilengenau auf tatsächlich neuen/geänderten " +
        "Zeilen und kann bei kleinen Änderungen in großen, alten Dateien strenger urteilen als diese Zahl.",
    );
  }

  if (belowThreshold) {
    process.exitCode = 1;
  }
}

const isDirectRun =
  process.argv[1] && import.meta.url === `file://${process.argv[1]}`;
if (isDirectRun) {
  main().catch((error) => {
    console.error(`Abbruch mit Fehler: ${error.message}`);
    process.exitCode = 1;
  });
}

export {
  parseArgs,
  globToRegExp,
  isAnalyzedSourceFile,
  parseOpenCoverXml,
  parseLcov,
  normalizeToRepoRelativePosix,
  buildFileReport,
};
