#!/usr/bin/env node
// Exports open SonarCloud findings (bugs, vulnerabilities, code smells) as a SARIF 2.1.0
// file, for upload to GitHub code scanning ("Security" tab). This script is READ-ONLY
// against both SonarCloud and GitHub: it makes no GitHub API calls at all. GitHub itself
// owns alert identity and lifecycle once a SARIF file is uploaded (via
// `github/codeql-action/upload-sarif` or `gh api .../code-scanning/sarifs`) — a finding
// missing from the next upload is automatically marked "Fixed", and a finding that
// reappears reactivates the same alert (matched by rule + location + fingerprint). No
// issue creation, no labels, no dedup bookkeeping needed here.
//
// NOTE: SonarCloud "Security Hotspots" are NOT included. Hotspots live under a separate
// API (`/api/hotspots/search`) with their own TO_REVIEW/REVIEWED workflow and are out of
// scope for this exporter — only `/api/issues/search` (bugs, vulnerabilities, code
// smells) is used.
//
// Required env: SONAR_PROJECT_KEY
// Optional env: SONAR_TOKEN (HTTP Basic, token as username, empty password),
//               SONAR_HOST_URL (default "https://sonarcloud.io"),
//               SONAR_REPORT_TASK_FILE (default ".sonarqube/out/.sonar/report-task.txt"),
//               SARIF_OUTPUT (default "sonar.sarif"),
//               SONAR_SKIP_CE_WAIT ("true"/"false", default "false" — testing/debug only,
//               see waitForComputeEngineTask below)

import { readFile, writeFile, appendFile, stat } from "node:fs/promises";
import path from "node:path";

// GitHub code scanning SARIF limits, as documented at
// https://docs.github.com/en/code-security/reference/code-scanning/sarif-files/troubleshoot-sarif-uploads/results-exceed-limit
// and https://docs.github.com/en/code-security/code-scanning/troubleshooting-sarif-uploads/file-too-large
// (checked live 2026-09-06). These are hard/soft limits GitHub enforces on upload, not
// numbers we invented — if GitHub changes them, update the comment + constants together.
const GITHUB_LIMITS = {
  maxResultsPerRunHard: 25000, // uploads above this are rejected
  maxResultsPerRunDisplayed: 5000, // only the top N (by severity) are shown/prioritized
  maxRulesPerRun: 25000,
  maxRunsPerFile: 20,
  maxTagsPerRule: 20, // only the top 10 are displayed
  maxFileSizeBytes: 10 * 1024 * 1024, // upload is rejected above this
};

const SONAR_ISSUES_API = "/api/issues/search";
const SONAR_RULES_API = "/api/rules/search";
const SONAR_CE_TASK_API = "/api/ce/task";
const RULE_CHUNK_SIZE = 60; // keeps rule_keys query strings comfortably short
const CE_POLL_INTERVAL_MS = 5000;
const CE_POLL_TIMEOUT_MS = 10 * 60 * 1000;

function readEnv() {
  const sonarProjectKey = process.env.SONAR_PROJECT_KEY;
  if (!sonarProjectKey) {
    throw new Error("Fehlende Umgebungsvariable: SONAR_PROJECT_KEY");
  }
  const sonarHostUrl = (process.env.SONAR_HOST_URL || "https://sonarcloud.io").replace(/\/$/, "");
  const reportTaskFile = process.env.SONAR_REPORT_TASK_FILE || ".sonarqube/out/.sonar/report-task.txt";
  const sarifOutput = process.env.SARIF_OUTPUT || "sonar.sarif";
  const skipCeWait = String(process.env.SONAR_SKIP_CE_WAIT ?? "false").toLowerCase() === "true";
  return {
    sonarProjectKey,
    sonarToken: process.env.SONAR_TOKEN,
    sonarHostUrl,
    reportTaskFile,
    sarifOutput,
    skipCeWait,
  };
}

function authHeaders(token) {
  return token ? { Authorization: `Basic ${Buffer.from(`${token}:`).toString("base64")}` } : {};
}

async function httpJson(url, options, context) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const body = await response.text().catch(() => "<kein Body lesbar>");
    throw new Error(
      `HTTP-Fehler bei ${context}: ${response.status} ${response.statusText}\n${url}\n${body}`,
    );
  }
  return response.json();
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function parseCeTaskId(reportTaskContent) {
  const match = /^ceTaskId=(.+)$/m.exec(reportTaskContent);
  return match ? match[1].trim() : null;
}

// `dotnet-sonarscanner end` only uploads and returns immediately — the server-side
// evaluation (Compute Engine) keeps running asynchronously afterwards. Querying the
// issues API right away would return the PREVIOUS analysis's findings, silently stale.
// The `end` step leaves behind report-task.txt with a `ceTaskId=`/`ceTaskUrl=` pair;
// we read the id from there and poll the CE task API until it leaves PENDING/IN_PROGRESS.
async function waitForComputeEngineTask(env) {
  if (env.skipCeWait) {
    console.log(
      "SONAR_SKIP_CE_WAIT=true gesetzt — Compute-Engine-Wartelauf wird übersprungen. " +
        "NUR für Tests/Debugging gedacht: im echten CI-Lauf muss der Scanner-`end`-Schritt " +
        "vorher gelaufen sein und die Analyse muss abgeschlossen sein, sonst wird ein veralteter Stand exportiert.",
    );
    return;
  }

  const resolvedPath = path.isAbsolute(env.reportTaskFile)
    ? env.reportTaskFile
    : path.resolve(process.cwd(), env.reportTaskFile);

  let content;
  try {
    content = await readFile(resolvedPath, "utf8");
  } catch {
    throw new Error(
      `report-task.txt nicht gefunden unter ${resolvedPath}. Der Scanner-\`end\`-Schritt ` +
        `(z. B. \`dotnet-sonarscanner end\`) muss vorher erfolgreich gelaufen sein — sonst gibt es keinen ` +
        `Compute-Engine-Task, auf den gewartet werden könnte, und ein Export ohne Wartelauf würde ` +
        `stillschweigend den alten Analysestand liefern. Zum lokalen Testen ohne echten Scanner-Lauf ` +
        `SONAR_SKIP_CE_WAIT=true setzen.`,
    );
  }

  const ceTaskId = parseCeTaskId(content);
  if (!ceTaskId) {
    throw new Error(`report-task.txt unter ${resolvedPath} enthält keine "ceTaskId="-Zeile.`);
  }

  console.log(`Warte auf Compute-Engine-Task ${ceTaskId}...`);
  const headers = authHeaders(env.sonarToken);
  const deadline = Date.now() + CE_POLL_TIMEOUT_MS;
  for (;;) {
    const url = new URL(`${env.sonarHostUrl}${SONAR_CE_TASK_API}`);
    url.searchParams.set("id", ceTaskId);
    const data = await httpJson(url, { headers }, `Compute-Engine-Task ${ceTaskId} abfragen`);
    const status = data.task?.status;
    if (status === "SUCCESS") {
      console.log(`Compute-Engine-Task ${ceTaskId} abgeschlossen (SUCCESS).`);
      return;
    }
    if (status === "FAILED" || status === "CANCELED") {
      throw new Error(`Compute-Engine-Task ${ceTaskId} hat Status ${status} — Analyse fehlgeschlagen oder abgebrochen.`);
    }
    if (status !== "PENDING" && status !== "IN_PROGRESS") {
      throw new Error(`Compute-Engine-Task ${ceTaskId} hat unerwarteten Status "${status}".`);
    }
    if (Date.now() >= deadline) {
      throw new Error(
        `Timeout beim Warten auf Compute-Engine-Task ${ceTaskId} nach ${CE_POLL_TIMEOUT_MS / 1000}s ` +
          `(letzter Status: ${status}).`,
      );
    }
    await sleep(CE_POLL_INTERVAL_MS);
  }
}

// Fetches all open findings (resolved=false) from SonarCloud's issue search, paginating
// with ps=500 until paging.total is reached. Aborts loudly instead of silently truncating
// if SonarCloud's own 10,000-result search cap would be hit.
async function fetchAllSonarIssues(env) {
  const headers = authHeaders(env.sonarToken);
  const pageSize = 500;
  let page = 1;
  let total = null;
  const issues = [];
  for (;;) {
    const url = new URL(`${env.sonarHostUrl}${SONAR_ISSUES_API}`);
    url.searchParams.set("componentKeys", env.sonarProjectKey);
    url.searchParams.set("resolved", "false");
    url.searchParams.set("ps", String(pageSize));
    url.searchParams.set("p", String(page));
    const data = await httpJson(url, { headers }, `SonarCloud-Suche (Seite ${page})`);
    total = data.paging?.total ?? data.total ?? 0;
    if (total > 10000) {
      throw new Error(
        `SonarCloud meldet ${total} offene Findings — das liegt über der API-Deckelung von 10.000 Treffern ` +
          `pro Suche. Ein Weiterpaginieren würde still unvollständige Ergebnisse liefern. Abbruch.`,
      );
    }
    issues.push(...(data.issues ?? []));
    if (issues.length >= total || (data.issues ?? []).length === 0) break;
    page += 1;
  }
  if (issues.length !== total) {
    throw new Error(
      `Paginierung inkonsistent: ${issues.length} Findings geladen, aber SonarCloud meldet total=${total}.`,
    );
  }
  return issues;
}

function chunk(array, size) {
  const chunks = [];
  for (let i = 0; i < array.length; i += size) {
    chunks.push(array.slice(i, i + size));
  }
  return chunks;
}

// Fetches rule metadata (name, description, tags) for the given rule keys, in chunks to
// keep query strings short. Returns a Map keyed by rule key; a rule missing from the
// response (network hiccup, retired rule, or — as observed live against the public
// SonarCloud API without a token — htmlDesc/mdDesc/descriptionSections simply not being
// returned even when requested) is handled by the caller via graceful degradation, not
// by throwing.
async function fetchRuleMetadata(env, ruleKeys) {
  const metadata = new Map();
  if (ruleKeys.length === 0) return metadata;

  // The rules API needs an organization, unlike issues/search. SonarCloud issues always
  // carry it; take it from the first finding we have.
  const organization = env.organization;
  const headers = authHeaders(env.sonarToken);

  for (const keyChunk of chunk(ruleKeys, RULE_CHUNK_SIZE)) {
    const url = new URL(`${env.sonarHostUrl}${SONAR_RULES_API}`);
    url.searchParams.set("organization", organization);
    url.searchParams.set("rule_keys", keyChunk.join(","));
    url.searchParams.set("ps", String(RULE_CHUNK_SIZE));
    url.searchParams.set("f", "name,htmlDesc,severity,sysTags");
    let data;
    try {
      data = await httpJson(url, { headers }, "Regelmetadaten laden");
    } catch (error) {
      console.warn(
        `Warnung: Regelmetadaten für einen Block (${keyChunk.length} Regeln) konnten nicht geladen werden ` +
          `(${error.message}). Diese Regeln degradieren auf Regel-ID als Name.`,
      );
      continue;
    }
    for (const rule of data.rules ?? []) {
      metadata.set(rule.key, rule);
    }
  }
  return metadata;
}

function htmlToPlainText(html) {
  if (!html) return null;
  return html
    .replace(/<br\s*\/?>/gi, "\n")
    .replace(/<\/(p|div|li|h[1-6])>/gi, "\n")
    .replace(/<[^>]+>/g, "")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/[ \t]+\n/g, "\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

function ruleLanguage(ruleKey) {
  const separatorIndex = ruleKey.indexOf(":");
  return separatorIndex === -1 ? ruleKey : ruleKey.slice(0, separatorIndex);
}

function ruleHelpUri(hostUrl, organization, ruleKey) {
  return `${hostUrl}/organizations/${encodeURIComponent(organization)}/rules?open=${encodeURIComponent(ruleKey)}&rule_key=${encodeURIComponent(ruleKey)}`;
}

// GitHub reads security-severity from the RULE descriptor
// (runs[].tool.driver.rules[].properties.security-severity), never from a result's own
// properties bag. Placing it on the result passes SARIF schema validation but is silently
// ignored, so the findings lose their High/Medium/Low classification. Sonar carries the
// severity per issue while SARIF carries it per rule, so a rule that produced several
// vulnerabilities is scored by its worst one.
function buildSecuritySeverityByRule(issues) {
  const byRule = new Map();
  for (const issue of issues) {
    if (issue.type !== "VULNERABILITY") continue;
    const score = Number(securitySeverityScore(issue.severity));
    const current = byRule.get(issue.rule);
    if (current === undefined || score > current) byRule.set(issue.rule, score);
  }
  return new Map([...byRule].map(([ruleKey, score]) => [ruleKey, score.toFixed(1)]));
}

// Builds the SARIF rule (reportingDescriptor) objects, in the same order the caller
// will use for ruleIndex. Missing metadata degrades to the rule key as name/description
// rather than failing the export.
function buildRuleEntries(ruleKeys, metadataByKey, hostUrl, organization, securitySeverityByRule) {
  return ruleKeys.map((ruleKey) => {
    const meta = metadataByKey.get(ruleKey);
    const name = meta?.name ?? ruleKey;
    const fullDescriptionText = htmlToPlainText(meta?.htmlDesc) ?? name;
    const tags = [...(meta?.sysTags ?? [])];
    if (meta?.type) tags.push(meta.type.toLowerCase().replace("_", "-"));

    const properties = { tags };
    const securitySeverity = securitySeverityByRule?.get(ruleKey);
    if (securitySeverity !== undefined) {
      properties["security-severity"] = securitySeverity;
      // GitHub only treats a scored rule as a security rule when it is tagged as one.
      if (!tags.includes("security")) tags.push("security");
    }

    return {
      id: ruleKey,
      name,
      shortDescription: { text: name },
      fullDescription: { text: fullDescriptionText },
      helpUri: ruleHelpUri(hostUrl, organization, ruleKey),
      properties,
    };
  });
}

function severityToSarifLevel(severity) {
  switch (severity) {
    case "BLOCKER":
    case "CRITICAL":
      return "error";
    case "MAJOR":
      return "warning";
    case "MINOR":
    case "INFO":
      return "note";
    default:
      return "note";
  }
}

function securitySeverityScore(severity) {
  switch (severity) {
    case "BLOCKER":
      return "9.0";
    case "CRITICAL":
      return "7.5";
    case "MAJOR":
      return "5.5";
    case "MINOR":
      return "3.0";
    default:
      return "1.0";
  }
}

function stripProjectPrefix(component, projectKey) {
  const prefix = `${projectKey}:`;
  return component.startsWith(prefix) ? component.slice(prefix.length) : null;
}

function buildRegion(issue) {
  if (issue.textRange) {
    const region = {
      startLine: issue.textRange.startLine,
      endLine: issue.textRange.endLine,
    };
    if (typeof issue.textRange.startOffset === "number") {
      region.startColumn = issue.textRange.startOffset + 1; // Sonar offsets are 0-based, SARIF columns 1-based
    }
    if (typeof issue.textRange.endOffset === "number") {
      region.endColumn = issue.textRange.endOffset + 1;
    }
    return region;
  }
  if (typeof issue.line === "number") {
    return { startLine: issue.line };
  }
  return { startLine: 1 };
}

// Builds one SARIF result per finding. Returns null for findings that resolve to no
// file path at all (project-level findings) — the caller counts and skips those.
function buildResult(issue, projectKey, ruleIndexByKey) {
  const filePath = stripProjectPrefix(issue.component, projectKey);
  if (!filePath) return null;

  const result = {
    ruleId: issue.rule,
    ruleIndex: ruleIndexByKey.get(issue.rule),
    level: severityToSarifLevel(issue.severity),
    message: { text: issue.message },
    locations: [
      {
        physicalLocation: {
          artifactLocation: { uri: filePath },
          region: buildRegion(issue),
        },
      },
    ],
  };

  if (issue.hash) {
    result.partialFingerprints = { primaryLocationLineHash: issue.hash };
  }

  return result;
}

function groupCount(items, keyFn) {
  const counts = new Map();
  for (const item of items) {
    const key = keyFn(item);
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  return counts;
}

function formatCountsTable(counts) {
  return [...counts.entries()]
    .sort((a, b) => b[1] - a[1])
    .map(([key, count]) => `  - ${key}: ${count}`)
    .join("\n");
}

function buildMarkdownSummary({ issues, results, skippedNoFile, sarifOutput, warnings }) {
  const rows = (counts) =>
    [...counts.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([key, count]) => `| ${key} | ${count} |`)
      .join("\n") || "| _(keine)_ | 0 |";

  const byType = groupCount(issues, (issue) => issue.type);
  const bySeverity = groupCount(issues, (issue) => issue.severity);
  const byLanguage = groupCount(issues, (issue) => ruleLanguage(issue.rule));

  return [
    "## SonarCloud → SARIF Export",
    "",
    `- Findings insgesamt: **${issues.length}**`,
    `- SARIF-Results geschrieben: **${results.length}**`,
    `- Ausgelassen (kein Dateipfad): **${skippedNoFile}**`,
    `- Ausgabedatei: \`${sarifOutput}\``,
    "",
    "### Nach Typ",
    "",
    "| Typ | Anzahl |",
    "| --- | --- |",
    rows(byType),
    "",
    "### Nach Severity",
    "",
    "| Severity | Anzahl |",
    "| --- | --- |",
    rows(bySeverity),
    "",
    "### Nach Sprache",
    "",
    "| Sprache | Anzahl |",
    "| --- | --- |",
    rows(byLanguage),
    "",
    ...(warnings.length > 0 ? ["### Warnungen", "", ...warnings.map((w) => `- ${w}`), ""] : []),
  ].join("\n");
}

async function main() {
  const env = readEnv();

  await waitForComputeEngineTask(env);

  console.log(`Lade offene SonarCloud-Findings für Projekt "${env.sonarProjectKey}"...`);
  const issues = await fetchAllSonarIssues(env);
  console.log(`${issues.length} offene Findings geladen.`);

  env.organization = issues[0]?.organization;

  const uniqueRuleKeys = [...new Set(issues.map((issue) => issue.rule))].sort();
  console.log(`Lade Regelmetadaten für ${uniqueRuleKeys.length} eindeutige Regeln...`);
  const ruleMetadata = uniqueRuleKeys.length > 0 ? await fetchRuleMetadata(env, uniqueRuleKeys) : new Map();
  const rulesMissingMetadata = uniqueRuleKeys.filter((key) => !ruleMetadata.has(key) || !ruleMetadata.get(key)?.name);
  if (rulesMissingMetadata.length > 0) {
    console.warn(
      `Warnung: ${rulesMissingMetadata.length} Regel(n) ohne Namen aus der Rules-API, degradiere auf Regel-ID: ` +
        rulesMissingMetadata.join(", "),
    );
  }

  const securitySeverityByRule = buildSecuritySeverityByRule(issues);
  const ruleEntries = buildRuleEntries(
    uniqueRuleKeys,
    ruleMetadata,
    env.sonarHostUrl,
    env.organization ?? "unknown",
    securitySeverityByRule,
  );
  const ruleIndexByKey = new Map(uniqueRuleKeys.map((key, index) => [key, index]));

  let skippedNoFile = 0;
  const results = [];
  for (const issue of issues) {
    const result = buildResult(issue, env.sonarProjectKey, ruleIndexByKey);
    if (result === null) {
      skippedNoFile += 1;
      continue;
    }
    results.push(result);
  }

  const warnings = [];
  if (ruleEntries.length > GITHUB_LIMITS.maxRulesPerRun) {
    warnings.push(
      `${ruleEntries.length} Regeln überschreiten GitHubs Hard-Limit von ${GITHUB_LIMITS.maxRulesPerRun} Regeln pro Run.`,
    );
  }
  if (results.length > GITHUB_LIMITS.maxResultsPerRunHard) {
    warnings.push(
      `${results.length} Results überschreiten GitHubs Hard-Limit von ${GITHUB_LIMITS.maxResultsPerRunHard} Results ` +
        `pro Run — der Upload würde von GitHub abgelehnt.`,
    );
  } else if (results.length > GITHUB_LIMITS.maxResultsPerRunDisplayed) {
    warnings.push(
      `${results.length} Results überschreiten GitHubs Soft-Limit von ${GITHUB_LIMITS.maxResultsPerRunDisplayed} — ` +
        `nur die Top ${GITHUB_LIMITS.maxResultsPerRunDisplayed} nach Severity werden priorisiert angezeigt.`,
    );
  }
  for (const rule of ruleEntries) {
    if (rule.properties.tags.length > GITHUB_LIMITS.maxTagsPerRule) {
      warnings.push(
        `Regel ${rule.id} hat ${rule.properties.tags.length} Tags, GitHub zeigt davon nur die ersten 10 an ` +
          `(Hard-Limit ${GITHUB_LIMITS.maxTagsPerRule}).`,
      );
    }
  }

  const sarif = {
    $schema: "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
    version: "2.1.0",
    runs: [
      {
        tool: {
          driver: {
            name: "SonarCloud",
            informationUri: `${env.sonarHostUrl}/dashboard?id=${encodeURIComponent(env.sonarProjectKey)}`,
            rules: ruleEntries,
          },
        },
        results,
      },
    ],
  };

  const sarifJson = JSON.stringify(sarif, null, 2);
  await writeFile(env.sarifOutput, sarifJson, "utf8");
  const fileStats = await stat(env.sarifOutput);
  console.log(`SARIF-Datei geschrieben: ${env.sarifOutput} (${fileStats.size} Bytes).`);

  if (fileStats.size > GITHUB_LIMITS.maxFileSizeBytes) {
    warnings.push(
      `Datei ist ${fileStats.size} Bytes groß und überschreitet GitHubs 10-MB-Upload-Limit — der Upload würde abgelehnt.`,
    );
  } else if (fileStats.size > GITHUB_LIMITS.maxFileSizeBytes * 0.8) {
    warnings.push(
      `Datei nähert sich mit ${fileStats.size} Bytes GitHubs 10-MB-Upload-Limit.`,
    );
  }

  const byType = groupCount(issues, (issue) => issue.type);
  const bySeverity = groupCount(issues, (issue) => issue.severity);
  const byLanguage = groupCount(issues, (issue) => ruleLanguage(issue.rule));

  console.log("\n=== Zusammenfassung ===");
  console.log(`Findings insgesamt: ${issues.length}`);
  console.log(`SARIF-Results geschrieben: ${results.length}`);
  console.log(`Ausgelassen (kein Dateipfad): ${skippedNoFile}`);
  console.log("Nach Typ:");
  console.log(formatCountsTable(byType) || "  (keine)");
  console.log("Nach Severity:");
  console.log(formatCountsTable(bySeverity) || "  (keine)");
  console.log("Nach Sprache:");
  console.log(formatCountsTable(byLanguage) || "  (keine)");
  console.log(`Ausgabepfad: ${env.sarifOutput}`);
  if (warnings.length > 0) {
    console.log("\nWarnungen:");
    for (const warning of warnings) console.log(`  - ${warning}`);
  }

  const summaryPath = process.env.GITHUB_STEP_SUMMARY;
  if (summaryPath) {
    await appendFile(
      summaryPath,
      buildMarkdownSummary({ issues, results, skippedNoFile, sarifOutput: env.sarifOutput, warnings }),
    );
    console.log(`\nBericht zusätzlich nach GITHUB_STEP_SUMMARY (${summaryPath}) geschrieben.`);
  }
}

const isDirectRun = process.argv[1] && import.meta.url === `file://${process.argv[1]}`;
if (isDirectRun) {
  main().catch((error) => {
    console.error(`Abbruch mit Fehler: ${error.message}`);
    process.exitCode = 1;
  });
}

export {
  parseCeTaskId,
  htmlToPlainText,
  ruleLanguage,
  stripProjectPrefix,
  buildRegion,
  buildResult,
  severityToSarifLevel,
  securitySeverityScore,
  buildRuleEntries,
  buildSecuritySeverityByRule,
};
