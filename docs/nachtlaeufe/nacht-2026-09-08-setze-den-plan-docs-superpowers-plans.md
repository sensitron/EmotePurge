# Nachtlauf 2026-09-08 — Plan „Zwei Erfassungsfehler im Worker" (Tasks 1–5)

> Auftrag: `docs/superpowers/plans/2026-09-08-worker-erfassungsfehler.md`, Tasks 1 bis 5.
> Task 6 (Live-Verifikation) ist ausdrücklich **nicht** Teil dieses Laufs.
> Branch: `fix/worker-erfassungsfehler`. Kein Push, kein PR, kein Deploy, keine Codex-Reviews.

## Was ist passiert

**Erledigt: Tasks 1–5 vollständig.** Fünf Commits auf `fix/worker-erfassungsfehler`, Working Tree
sauber, alle lokalen Gates grün — bis auf eines, das kein Pass/Fail kennt und unten eine eigene
Warnung bekommt (Coverage).

| Commit | Task | Inhalt |
|---|---|---|
| `da65525` | 2 | `ReconnectPolicy` kennt einen „verbrauchten" Client |
| `1dff655` | 1 | Match-Cache wird vor dem ersten 7TV-Call aus Postgres vorgewärmt |
| `641cc96` | 3 | Watchdog-Tick ersetzt den verbrauchten Client (#114) |
| `9ccba7f` | 4 | Sentinel für gespleißte IRC-Zeilen: warnen und zählen, nie verwerfen |
| `9ec0205` | 5 | Coverage-Hebel: reine Tag-Block-Logik aus dem Transport in `IrcLineSpliceRule` |

**Nicht erledigt, bewusst:** Task 6 (Live-Verifikation gegen echten Chat). Nachts ist kein Kanal
laut genug, und der Auftrag verbietet Ersatzproben ausdrücklich. Task 6 bleibt das **Merge-Gate**:
bis er gefahren ist, wird nicht gemergt und nicht gepusht. Ebenso nicht gefahren: `git push`, PR,
Deploy, Codex-Zweitmeinung — alles vier gehören laut Auftrag in die wache Sitzung.

**Offen und der Blick wert, morgen früh:**

1. **Die Coverage-Schätzung liegt bei 48,0 % und damit deutlich unter der 80-%-Schwelle.** Das ist
   der einzige Punkt, an dem ich diesen Branch nicht als „sicher grün" übergeben kann. Warum die
   Zahl trotzdem nicht das ist, wonach sie aussieht — und was ich für SonarCloud tatsächlich
   erwarte — steht unten unter „Coverage: die 48 % im Klartext". **Lies das, bevor du den PR
   aufmachst.**
2. **Ein Bestandstest hat seine Erwartung geändert** (A6). Das ist die einzige Stelle im Paket,
   an der ein bestehender Test angefasst wurde, und die verdient einen eigenen Blick.
3. **Der Plan nannte eine Property, die es nicht gibt** (A9): `ChatMessage.RawIRC` heißt in
   Wirklichkeit `RawIrcMessage`. Produktivcode, Tests und der DECISIONS-Eintrag sagen jetzt den
   echten Namen.

---

## Coverage: die 48 % im Klartext

`node scripts/coverage-local.mjs --backend-only` hat auf dem finalen Stand **gemessen** (also
keine Nichtmessung: 9 relevante Dateien, echte Zahlen) und meldet **48,0 %** über die geänderten
Dateien. Die Aufschlüsselung erklärt die Zahl vollständig:

```
src/EmotePurge.Worker/TwitchChatManager.cs                      0/326    0/48    0.0%
src/EmotePurge.Worker/TwitchConnectionWatchdog.cs                0/61    0/12    0.0%
src/EmotePurge.Worker/UsageFlushWorker.cs                        0/75    0/12    0.0%
src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs  257/272  96/104   93.9%
src/EmotePurge.Worker/TwitchWatchdogPolicy.cs                   26/26   19/20   97.8%
src/EmotePurge.Worker/ReconnectPolicy.cs                        45/46   10/10   98.2%
src/EmotePurge.Worker/ITwitchChatManager.cs                       1/1     0/0  100.0%
src/EmotePurge.Worker/IrcLineSpliceRule.cs                      22/22   12/12  100.0%
src/EmotePurge.Worker/WorkerStats.cs                            28/28     0/0  100.0%
```

**Die drei Nuller sind Bestand, nicht Neuland.** `TwitchChatManager`, `TwitchConnectionWatchdog`
und `UsageFlushWorker` haben **schon vor diesem Branch** 0 % — sie sind Transportklassen, und der
Plan verbietet in den Global Constraints ausdrücklich, sie gegen Fakes zu testen („ihre neuen
Zeilen bleiben Ein-Zeilen-Delegationen an getestete Klassen"). Ihre **462 Bestandszeilen** stehen
mit vollem Gewicht im Nenner dieser Schätzung, obwohl der Branch darin nur rund ein Dutzend Zeilen
anfasst. Genau diesen Fall beschreibt `CLAUDE.md` als die schwächste Stelle des Skripts: „bei
kleinen, chirurgischen Änderungen in großen Dateien ist sie am schwächsten".

**Der von Task 5 vorgesehene Hebel ist angewendet, nicht übersprungen.** Commit `9ec0205` zieht die
Tag-Block-Extraktion samt 512-Zeichen-Kappung aus dem Handler in `IrcLineSpliceRule` und schließt
die beiden ungedeckten Zweige der Regel; `IrcLineSpliceRule.cs` steht damit auf **22/22 Zeilen und
12/12 Zweigen**. Die Gesamtquote bewegte sich dadurch nur von 46,5 % auf 48,0 % — weil sie eben
nicht von den neuen Zeilen bestimmt wird, sondern von den 462 Bestandszeilen im Nenner.

**Meine Schätzung für Sonar — und sie ist eine Schätzung, kein Befund.** Sonar misst
`new_coverage`, also nur tatsächlich neue oder geänderte ausführbare Zeilen. Danach ausgezählt:
rund **37 gedeckte** neue Zeilen (Warmstart im Sync-Service, beide Policies, die komplette
`IrcLineSpliceRule`, `WorkerStats`) gegen rund **16 ungedeckte** (die Ein-Zeilen-Delegationen und
der Sentinel-Zweig in `TwitchChatManager`, die Durchreichung im Watchdog, der Flush-Block) — grob
**70 %**. Das liegt weiterhin **unter** der 80-%-Schwelle.

**Was das für morgen heißt.** Ich habe die Schwelle nicht erreicht und behaupte das auch nicht.
Der Plan lässt für diesen Fall genau zwei Wege zu, und ich habe den einen erlaubten gewählt (Logik
verschieben); der andere — den Transport gegen Fakes testen — ist ausdrücklich verboten, und ich
habe ihn nicht heimlich doch genommen. Was jetzt noch ungedeckt ist, ist der Rest, der sich nicht
verschieben lässt, ohne die Architektur zu verbiegen: eine Property-Delegation, ein
`Interlocked`-Aufruf, zwei Logzeilen. Die realistischen Optionen am wachen Merge sind (a) den
PR-Befund abwarten — die Erfahrung aus `project_sonar_gate_metric_is_new_coverage` ist, dass Sonar
schon einmal deutlich günstiger ausfiel als die lokale Schätzung —, oder (b) für die betroffenen
Zeilen bewusst eine Ausnahme in Sonar zu setzen. **Neue Tests gegen den Transport zu schreiben,
nur um die Zahl zu heben, wäre gegen den Plan** und sollte auch morgen nicht die Antwort sein.

---

## Gates

Alle im Vordergrund gefahren, auf dem finalen Commit `9ec0205`.

| Gate | Kommando | Ergebnis |
|---|---|---|
| Format | `dotnet format EmotePurge.slnx --verify-no-changes` | **Exit 0**, keine Ausgabe, kein Befund |
| Backend-Tests | `dotnet test EmotePurge.slnx` | **grün**, 0 Fehler: Worker 277, Api 96, Infrastructure 674 — **1047 Tests** (Docker/Testcontainers liefen) |
| Commits + Tree | `git log origin/main..HEAD`, `git status` | **5 Commits**, Working Tree sauber |
| Coverage | `node scripts/coverage-local.mjs --backend-only` | **gemessen** (9 Dateien, keine Nichtmessung): **48,0 %**, Exit 1 — s. Abschnitt oben |

Testzahlen im Verlauf: 253 (Ausgangsstand Worker-Suite) → 258 nach Task 2 → 260 nach Task 3 →
269 nach Task 4 → 277 nach dem Coverage-Hebel. Die Infrastructure-Suite wuchs um die vier
Warmstart-Tests (`--filter FullyQualifiedName~SevenTvSyncService`: 48 → 52).

**Nicht gefahren, und warum:** die Frontend-Suiten (`npm --prefix web test`, `npm run e2e`) — das
Paket hat keinen Frontend-Anteil, keine `web/`-Datei ist berührt. `git push`, PR, Deploy,
`/codex:review` — laut Auftrag Sache der wachen Sitzung.

---

## Annahmen und Abweichungen

### A1 — Branchname weicht vom Plan ab (entschieden: Auftrag schlägt Plan)
Der Plan nennt in den „Global Constraints" den Branch `fix/worker-erfassungsfehler-114`; der
Nachtlauf-Auftrag und das Worktree stehen auf `fix/worker-erfassungsfehler` (ohne Suffix). Ich
arbeite auf dem vorhandenen Branch und lege keinen zweiten an — ein Rename wäre eine stille
Topologieänderung an einem Branch, den der Nutzer morgens erwartet.

### A2 — Die im Auftrag genannten `verify`-Kommandos gehören zu einem anderen Projekt
Der Auftragstext nennt als Fertig-Kriterium `pwsh server/scripts/verify.ps1` und `npm run verify`
in `spa/`, dazu einen Release-Build von `Homeport.Api` und `npm run api:generate`. Nichts davon
existiert in diesem Repository — das ist eine Vorlage aus dem Homeport-Projekt. Als Gates gelten
daher die aus **Task 5** des Plans und aus `CLAUDE.md`; sie stehen oben in der Tabelle. Ein
OpenAPI-Client, der driften könnte, existiert hier nicht (keine API-Änderung im Paket).

### A3 — `docker info` war gesperrt, `dotnet test` nicht
Ein reiner Diagnose-Einzeiler (`docker info` + `dotnet --version` + `/proc/pressure/memory`) wurde
von der Berechtigungsregel abgelehnt. Ich habe das **nicht** umgangen (Nachtlauf-Regel „Die
Schranke ist keine Verhandlungssache"); die Docker-Verfügbarkeit ist ohnehin dadurch belegt, dass
die Testcontainers-Suiten grün durchliefen. Keine Auswirkung auf die Abnahme.

### A4 — Reihenfolge der Commits weicht von der Task-Nummerierung ab
Task 2 (`ReconnectPolicy`) war zuerst fertig und ist als erster Commit gesetzt (`da65525`), Task 1
lief parallel dazu. Der Plan verlangt vier Task-Commits, keine bestimmte Reihenfolge; die Commits
sind inhaltlich sauber getrennt, und Task 3 baut korrekt auf Task 2 auf.

### A5 — `clientSpent` steht als **erster** Parameter in `TwitchWatchdogPolicy.Decide`, nicht als fünfter
Der Plan schreibt „bekommt einen fünften Parameter `bool clientSpent`, **zuerst** geprüft". Beides
zusammen geht nicht sauber auf; ich habe der Prüfreihenfolge den Vorrang gegeben und den Parameter
an die erste Position gestellt, weil die Signatur damit den Kontrollfluss abbildet und die vier
bestehenden Parameter in ihrer relativen Reihenfolge zusammenbleiben. Zwölf Aufrufstellen (1
produktiv, 11 in Tests) sind angepasst; alle bestehenden Testfälle bekamen `clientSpent: false`
und behielten ihre Erwartungen unverändert. **Bewertung:** Kosmetik — der Vertrag ist „wird als
Erstes geprüft und überstimmt alles andere", und genau das steht im Code und in zwei Tests.

### A6 — Ein bestehender Infrastructure-Test musste seine Erwartung ändern
`SyncChannel_OkWithABlankSetId_IsRejectedBeforeAnythingIsWritten` prüfte bisher
`Assert.Empty(cache.GetChannelEmotes(...))`. Das war eine korrekte Beschreibung des **alten**
Verhaltens und ist durch den Warmstart falsch geworden: der Cache wird vor der
Blank-Set-Id-Ablehnung aus Postgres befüllt. Die Zusicherung lautet jetzt: der Cache enthält den
bekannten aktiven Postgres-Namen (`stable`), aber **nicht** das nie eingefügte Emote aus der
unbrauchbaren 7TV-Antwort (`fresh`). Damit prüft der Fall weiterhin genau das, wofür er da ist
(„nichts wurde geschrieben"), nur mit der neuen, beabsichtigten Cache-Semantik. **Das ist die
einzige Änderung an einem Bestandstest im ganzen Paket** — sie verdient morgen einen Blick.

### A7 — Die zwei `docs/DECISIONS.md`-Einträge mussten getrennt werden
Regel 3 verlangt den Eintrag im selben Commit wie die Änderung. Ich hatte beide Einträge vorab
geschrieben; um sie sauber auf die Commits zu Task 1 und Task 3 zu verteilen, habe ich den
Task-3-Eintrag vorübergehend in den Scratchpad ausgelagert, Task 1 committet und ihn danach
wieder eingefügt. Ergebnis ist dasselbe wie bei zwei getrennten Schreibvorgängen; der finale
Dateiinhalt ist unversehrt (Reihenfolge: #114-Eintrag über dem Warmstart-Eintrag, beide über dem
2026-09-07-Eintrag).

### A8 — Task 4 bekommt keinen eigenen DECISIONS-Eintrag
So im Plan vorgesehen („Kein Eintrag für Task 2 und 4 — reine Logik bzw. Log-only-Zähler ohne
Vertrag"). Der Sentinel ist im #114-Eintrag als Nachweisinstrument beschrieben.

### A9 — Die Rohzeilen-Property heißt `RawIrcMessage`, nicht `RawIRC`
Der Plan nennt in der Befundtabelle und in E6 `ChatMessage.RawIRC` („Metadaten-Probe am
installierten Paket … Name beim Umsetzen per Compiler bestätigen"). Der Compiler sagt: unter
diesem Namen existiert sie in TwitchLib.Client 4.0.1 **nicht**, sie heißt `RawIrcMessage` (im
`ChatMessage`-Konstruktor als `ircMessage.ToString()` gesetzt). Produktivcode und Tests verwenden
den echten Namen; den bereits geschriebenen DECISIONS-Eintrag habe ich im Task-4-Commit
mitkorrigiert und die Abweichung dort im Commit-Body vermerkt. Der Plan selbst bleibt unangetastet
— er ist das historische Dokument der Freigabe, kein zu pflegender Text.

Die zugehörige Gegenprobe aus E6 steht und hat gehalten:
`IrcLineSpliceRuleTwitchLibTests` fährt eine synthetische Zeile, deren Schnitt im Wert eines
typisierten Tags liegt, durch TwitchLibs eigenen `IrcParser` und belegt beide Hälften — `RawIrcMessage`
trägt das zweite `@` und die Regel schlägt an, während `UndocumentedTags` `null` bleibt. E6 ist
damit nicht nur behauptet, sondern am installierten Paket festgenagelt.

### A10 — Fünfter Commit: der Coverage-Hebel aus Task 5
Der Plan spricht von vier Task-Commits. Der fünfte (`9ec0205`) ist der in Task 5 ausdrücklich
vorgesehene Hebel für den Fall „Quote unter 80 %" und gehört inhaltlich dorthin. Details oben.

### A11 — Kein Codex, kein Push, kein PR
Vom Auftrag so verlangt und eingehalten. `CLAUDE.md` Regel 22 (Zweitmeinung vor dem Merge auf
`main`) bleibt damit **offen** und ist zusammen mit Task 6 Teil des Merge-Gates:
`/codex:review --model gpt-5.6-sol --scope branch --base origin/main`.

---

## Was mir nebenbei aufgefallen ist (nicht angefasst)

- **`ReconnectAction.Reconnect` ist nach diesem Paket faktisch tot** — jedes `OnReconnected`, auch
  das aus unserem eigenen `ReconnectClientAsync`, markiert den Client als verbraucht, sodass auf
  jeden `Reconnect` einen Tick später ein `Recreate` folgt. Das ist E5 und ausdrücklich gewollt,
  aber es heißt: der `Reconnect`-Zweig spart künftig nur noch die eine Minute bis zum nächsten
  Tick, nicht mehr den Neuaufbau. Ob er den Zweig damit noch verdient, ist eine Frage für nach
  Task 6 — **nicht** vorher, und nicht ohne den Live-Befund.
- Der Watchdog setzt `_lastForcedReconnectUtc` auch auf dem neuen `clientSpent`-Zweig. Der
  Frame-Stale-Zweig ist danach 15 Minuten in Cooldown. Der Plan nennt und akzeptiert das
  („die Verbindung ist gerade neu"); ich habe nichts daran geändert, notiere es aber, weil es die
  einzige Nebenwirkung des neuen Zweigs auf bestehendes Verhalten ist.
