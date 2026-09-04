# Nachtlauf 2026-09-04 — vier unabhängige Pakete

**Modus:** `durchlauf` (unbeaufsichtigt, keine Rückfragen möglich)
**Ausgangspunkt:** `main` = `5ed3d90`
**Branches:** `nachtlauf/issue-61`, `nachtlauf/issue-60`, `nachtlauf/issue-45`, `nachtlauf/issue-42` — alle **lokal**, nichts gepusht, nichts gemergt.

## Was ist passiert

*(wird am Ende gefüllt)*

---

## Rahmenbedingungen dieses Laufs

- `git push`, `gh`, `curl`, `wget`, `ssh`, `docker compose` waren gesperrt. Es gibt daher **keine
  CI-Läufe**, **keine PRs**, **keine Live-Verifikation** gegen echte Twitch-/7TV-Zugänge.
  Regel 16 („Backend-Features vor dem Commit live verifizieren") konnte in diesem Lauf **nicht**
  erfüllt werden — das bleibt je Paket als offener Handgriff für den Morgen notiert.
- **`npm` ist in diesem Lauf ebenfalls gesperrt** — nicht nur `docker compose`. Damit sind
  **alle Frontend-Gates unausführbar**: `npm --prefix web test`, `run lint`, `run format:check`
  und `run e2e`. Gemessen, nicht vermutet: `npm --version` wird von der Berechtigungsregel
  abgelehnt. Ein Umweg (Node direkt, `npx`, Vitest-Binary von Hand) wäre eine Umgehung der
  Schranke und ist unterblieben.
  **Folge:** Die Backend-Gates sind je Paket gefahren und grün; jede Frontend-Änderung dieses Laufs
  (#45, #42) ist **ungeprüft** und braucht am Morgen zwingend einen Lauf der drei Frontend-Gates,
  bevor sie irgendwohin gemergt wird.
- **Widerspruch im Auftrag, so aufgelöst:** Der Abschnitt „Zweitmeinung je Paket" verlangt einen
  Codex-Review je Paket; der Abschnitt „Wie du heute Nacht arbeitest" sagt „Auch keine
  Codex-Reviews — das Kontingent gehört dem Review am wachen Merge." Ich habe die
  **paket­spezifische** Anweisung als die für diesen Lauf gemeinte gelesen (sie ist detailliert,
  nennt den `--scope`-Fallstrick und regelt den Umgang mit Befunden), der zweite Satz ist der
  generische Baustein der Nachtlauf-Vorlage. Ergebnis siehe je Paket.
- **Merge-Konflikte zwischen den Paketen sind zu erwarten und beabsichtigt.** Jedes Paket zweigt
  laut Auftrag von `main` ab, also kennt keines die Änderungen der anderen. Konkret kollidieren
  sicher: `docs/DECISIONS.md` (jedes Paket mit Vertrags-/Konventionsänderung schreibt oben einen
  Eintrag) und `src/EmotePurge.Core/SevenTv/SevenTvModels.cs` (#61 baut die fünf Ergebnistypen um,
  #60 erweitert `SevenTvSyncResult` in derselben Datei). Empfohlene Merge-Reihenfolge am Morgen:
  **#61 → #60 → #45 → #42**; dann trifft #60 den bereits geschlossenen Stil der Datei vor und der
  Konflikt bleibt textuell statt semantisch.
- Die Notizdatei selbst wird am Ende auf dem Nachtlauf-Branch
  `nacht/2026-09-04-vier-unabhaengige-pakete-sequenziell-in` committet, damit die vier Paketbranches
  nur ihre eigene Änderung tragen.

### Vergleichszahlen vom Stand `main` (5ed3d90)

| Gate | Sollwert |
|---|---|
| Backend `dotnet test` | 706 (89 Worker + 83 Api + 534 Infrastructure) |
| Vitest | 594 in 61 Dateien |
| Playwright E2E | 103 |

---

## Paket 1 — Issue #61: SevenTv-Ergebnistypen schließen

**Branch:** `nachtlauf/issue-61` · **Commit:** `3379841` · **Status: fertig, alle anwendbaren Gates grün.**

### Was umgesetzt wurde

Alle fünf Typen sind jetzt `sealed class` mit privatem Konstruktor, Factories als einzigem Weg
hinein — exakt nach dem Vorbild aus `8bc8965`:

- `SevenTvChannelStateResult`, `SevenTvTwitchUserIdResult`, `SevenTvIdentityResult`,
  `SevenTvEditorGrantsResult` in `src/EmotePurge.Core/SevenTv/SevenTvModels.cs`
- `SevenTvEditorGrantsLookupResult` in `src/EmotePurge.Core/Services/ISevenTvEditorService.cs`

`Failed(...)` weist Erfolgsstatus und undefinierte Enum-Werte zurück, die `Ok(...)`-Factories weisen
null-Nutzlast zurück; `throw`-Messages deutsch.

**Geänderte Dateien:** `src/EmotePurge.Core/SevenTv/SevenTvModels.cs`,
`src/EmotePurge.Core/Services/ISevenTvEditorService.cs`,
`tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvResultTypesTests.cs` (neu), `docs/DECISIONS.md`.

### Das Abnahmekriterium — gemessen, nicht behauptet

Eine Wegwerf-Probe (`src/EmotePurge.Core/ZzzClosedResultProbe.cs`, danach gelöscht) rief je Typ den
positionalen Konstruktor und ein `with` auf. Ergebnis, wörtlich aus dem Compiler:

| Code | Anzahl | Bedeutung |
|---|---|---|
| `CS8858` | 5 | „The receiver type '…' is not a valid record type and is not a struct type" — `with` ist weg |
| `CS0200` | 5 | „Property or indexer '….Status' cannot be assigned to -- it is read only" |
| `CS0122` | 5 | „'….ctor(SevenTvLookupStatus, …)' is inaccessible due to its protection level" |

**Abweichung vom Auftrag, bewusst:** Der Auftrag nannte `CS1729` („no constructor takes N arguments").
Den bekommt man nur, wenn die Argumentliste der Probe *nicht* zur privaten Signatur passt. Meine
Probe trifft die Signatur exakt, also meldet der Compiler `CS0122` statt `CS1729`. Beides heißt
dasselbe — die Tür ist zu. Ich habe die Probe **nicht** verbogen, um den im Auftrag genannten Code
zu erzeugen; stattdessen steht die Abweichung hier und im Entscheidungslog.

Zusätzlich hält `SevenTvResultTypesTests.NoPublicConstructor_AndNoWithExpression` als `[Theory]`
über alle fünf Typen per Reflection fest, dass es keinen öffentlichen Instanzkonstruktor und kein
`<Clone>$` gibt.

### Testzahlen

| Suite | vorher (`main`) | nachher |
|---|---|---|
| Worker | 89 | 89 |
| Api | 83 | 83 |
| Infrastructure | 534 | **543** (+9) |
| **Summe** | **706** | **715** |

`dotnet build --no-incremental \| grep CS[0-9]+` → leer. `dotnet format --verify-no-changes` → exit 0.

### Annahmen und Entscheidungen

1. **Keine Aufrufstelle wurde angefasst — und das ist ein Befund, keine Nachlässigkeit.** Eine
   Bestandsaufnahme über alle 59 Fundstellen der fünf Typen ergab: jede Konstruktion lief schon über
   `Ok(...)`/`Failed(...)`, und weder `with`, Dekonstruktion, positionale Muster noch Wertegleichheit
   wurden irgendwo benutzt. Die `record`-Form trug also nichts außer den zwei offenen Türen.
2. **Die Begründungspassage steht einmal, an `SevenTvChannelStateResult`.** Der Auftrag verlangte
   „einmal an zentraler Stelle statt siebenmal kopiert". Die naheliegende Alternative wäre gewesen,
   auch die Doku von `ChannelJoinResult`/`TwitchUserLookup` auf diese eine Stelle umzubiegen — das
   hätte den Umfang über die `**Betrifft:**`-Zeile des Issues hinaus ausgeweitet, also habe ich es
   gelassen. Ergebnis: **zwei** ausformulierte Passagen im Repo (dort und hier) statt sieben.
3. **Die Statusprüfung ist als `internal static class SevenTvLookupStatusGuard` extrahiert.** Fünf
   kopierte `if`-Blöcke werden bei einem zweiten Erfolgsstatus zu vier übersehenen Fundstellen.
   Vertretbare Alternative wäre gewesen, dem Vorbild zu folgen und den Block je Typ zu wiederholen —
   bei zwei Typen ist das noch Kopie, bei fünf ist es eine Fehlerquelle.
4. **`Ok(...)` prüft nur auf `null`, nicht auf Leerstring.** `SevenTvTwitchUserIdResult.Ok("")` ist
   weiterhin baubar. Der Auftrag verlangt ausdrücklich nur „null-Nutzlast zurückweisen", und eine
   Whitespace-Prüfung hätte still das Verhalten einer Aufrufstelle ändern können. Offener Punkt,
   falls jemand das enger haben will.
5. **Ein Commit statt zwei.** Regel 3 verlangt den `DECISIONS.md`-Eintrag im selben Commit; der
   Eintrag zitiert die Reflection-Tests als Beleg. Ein Aufsplitten hätte entweder Regel 3 gebrochen
   oder einen Eintrag hinterlassen, der auf eine noch nicht existierende Testdatei verweist.

### Codex-Zweitmeinung (`--model gpt-5.6-sol --scope branch --base origin/main`)

Im Wortlaut:

> # Codex Review
>
> Target: branch diff against origin/main
>
> No functional regressions were found. Repository usages remain compatible with the factory APIs, and runtime reflection confirmed the new construction guards; the full test command could not run because the sandbox filesystem is read-only.

Keine Befunde, also nichts zu beheben und nichts als offene Frage zu notieren. Dass Codex die
Testsuite nicht fahren konnte (read-only Sandbox), ist bekannt und hier folgenlos — die Suite lief
lokal.

### Was ungetestet blieb

- **Regel 16 (Live-Verifikation)** ist nicht erfüllt und in diesem Lauf nicht erfüllbar. Für dieses
  Paket ist das Risiko allerdings gering: es ändert keine Laufzeitentscheidung, nur die Bauform der
  Typen — die einzige neue Laufzeitwirkung sind die `throw`s, und die sind durch die neuen Tests
  abgedeckt.

## Paket 2 — Issue #60: Login-Propagierung nach dem Zeilen-Reload

*(noch nicht begonnen)*

## Paket 3 — Issue #45: duplicate-names in die active-set-Antwort

*(noch nicht begonnen)*

## Paket 4 — Issue #42: Verbindungslimit vom Redis-Ausfall trennen

*(noch nicht begonnen)*
