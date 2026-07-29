# Umsetzung des Reviews vom 2026-07-29 — Fortschritt

Begleitdokument zu [`Review-2026-07-29.md`](Review-2026-07-29.md). Hält fest, welche der 81 Befunde umgesetzt sind, wo bewusst vom vorgeschlagenen Fix abgewichen wurde und was noch offen ist. Die Wellen-Einteilung folgt Abschnitt 8 des Reports.

| Welle | Inhalt | Status |
|---|---|---|
| **A** | Quick Wins | ✅ **abgeschlossen** (2026-07-29) |
| **B** | Sicherheit & Korrektheit (S1/S2) | ✅ **abgeschlossen** (2026-07-30) |
| **C** | Refactorings | ⬜ offen |
| **D** | Tests | ⬜ offen |
| **E** | Infra & Launch | ⬜ offen |

---

## Welle A — umgesetzt am 2026-07-29

18 Befunde. Umgesetzt in drei parallelen Arbeitsströmen mit getrennter Datei-Eigentümerschaft (7TV/Shared/i18n · Feature-Seiten · Backend/Doku).

### Kritisch

**S1-4 — „Löschen starten" war klickbar, bevor die Shared-Set-Prüfung antwortete**
`web/src/app/shared/seven-tv/delete-confirm-dialog.ts`: Bestätigen-Button hat jetzt `[disabled]="warningLoading()"` plus `disabled:opacity-50 disabled:cursor-not-allowed`. `warningLoading` war bereits als `input()` vorhanden, keine Signaturänderung nötig. Damit kann kein Löschlauf mehr gestartet werden, während die Ownership-Prüfung (1–3 s bei vielen moderierten Channels) noch läuft.

**S1-3 Sofortteil — Auswahl driftete still von der angezeigten Liste ab**
`selection.clear()` an den zwei fehlenden Stellen: `features/usage-stats/usage-stats-page.ts` in `toggleSort()` (Sortierwechsel verschiebt jeden Positionsindex des Shift-Ankers) und `features/voting/vote-session-detail-page.ts` im `next`-Handler von `load()` (Neu-Deserialisierung bricht die Objektidentität).
**Abweichung:** Der Report nennt für die Voting-Seite zwei Stellen (`refresh()` *und* den `getResults`-Handler); im Ist-Code ist `refresh()` nur ein Aufruf von `load()`, und `load()` hat genau einen `next`-Handler, den auch `vote()` durchläuft. Ein `clear()` deckt beide Aufrufer ab.
**Bewusst in Kauf genommene Nebenwirkung:** Auf der Voting-Detailseite verliert man die Auswahl jetzt bei *jedem* Reload, also auch nach jedem einzelnen Vote. Das ist die korrekte, datensichere Richtung, aber schlechtere UX als vorher-scheinbar. Die dauerhafte Lösung ist die keyed `ListSelection` (Welle B, s. u.) — erst danach lässt sich S2-16 (Auswahl über Reloads/Filterwechsel *erhalten*) überhaupt sicher bauen.

### Hoch

**S2-15 — Namensfilter war Exakt-Match statt Teilstring-Suche**
`web/src/app/shared/emotes/emote-usage-filter.ts`: `globToRegExp` verankert nur noch, wenn die Eingabe tatsächlich `*` oder `?` enthält. `peepo` findet jetzt `peepoHappy`; die Glob-Semantik bleibt vollständig erhalten, sobald ein Wildcard getippt wird. Placeholder auf „Name suchen…" vereinfacht, der Glob-Hinweis ist in ein `title`-Attribut gewandert (neuer Key `usageStats.filterNameTitle`).

**S2-18 — Dialog behauptete „Set gehört nicht diesem Channel", obwohl die Prüfung nur fehlgeschlagen war**
`delete-confirm-dialog.ts`: `hasSharedSetWarning` gated jetzt zusätzlich auf `available` — der rote Alarm erscheint nur noch bei `available && !isOwnSet`. Für `available === false` gibt es einen eigenen, gelben, neutral formulierten Block (`massDelete.ownershipCheckUnavailable`). Damit produziert ein 7TV-Ausfall keinen falschen Alarm mehr, der über Alarm-Fatigue genau den echten Fall entwertet hätte.
Im Fehler-Fallback von `mass-delete-panel.ts` bleibt `isOwnSet: false` bewusst stehen (Agent hatte auf `true` gedreht, zurückkorrigiert): Der Wert ist bei `available: false` bedeutungslos, aber sollte ihn künftiger Code ohne `available`-Prüfung lesen, ist „nicht als eigen verifiziert" die sichere Richtung.

**S2-19 — „Beenden" und „Löschen" als gleichfarbige Nachbarn; Löschbestätigung nannte die Abstimmung nicht**
`features/voting/vote-session-list-page.*`: „Beenden" ist jetzt neutral (`text-slate-300`) — es ist keine destruktive Aktion. „Löschen" bleibt rot, ist aber als Outline-Button (`border-red-800`) mit zusätzlichem Abstand abgesetzt. Die Bestätigung nennt den Session-Titel und den Stimmenverlust (`voting.list.deleteConfirm` mit `{{ title }}`-Interpolation).
**Abweichung:** `deleteSession(sessionId: number)` → `deleteSession(session: VoteSessionSummary)`, weil für die Interpolation der Titel gebraucht wird.

### Mittel

**S3-14 — Log-Zeile beim Recreate war im proaktiven Pfad sachlich falsch**
`src/EmotePurge.Worker/TwitchChatManager.cs`: `RecreateClientAsync(string reason)`; beide Aufrufer übergeben einen zutreffenden Grund, die vorher doppelten (teils widersprüchlichen) `LogWarning`-Aufrufe sind entfallen. Das Log behauptet nicht mehr „0 aufeinanderfolgende Verbindungsfehler", wenn der proaktive Pfad greift — relevant, weil Logs beim nächsten Prod-Ausfall wieder das einzige Forensik-Werkzeug sind.

**S3-16 — Modale Dialoge ohne Fokusfalle, Escape und Beschriftung; Statusmeldungen ohne `aria-live`**
`delete-confirm-dialog.ts` und der Token-Fallback-Dialog in `mass-delete-panel.ts`: `cdkTrapFocus` + `cdkTrapFocusAutoCapture`, `cdkFocusInitial` auf den **Abbrechen**-Button (bewusst nicht auf den destruktiven), `(keydown.escape)`, `aria-labelledby` bzw. `aria-label`. `role="alert"` auf alle Fehlerbanner (`overview-page`, `usage-stats-page`, `vote-session-list-page`, `vote-session-detail-page`, `channel-workspace-layout`), `role="status"` auf Kopier-Feedback, Fortschritt (`delete-progress-panel.ts`) und die „X von Y Emotes"-Zeilen.
**Abweichung:** Der Report nannte `mass-delete-panel.ts:54` als Ort eines Banners — dort liegt tatsächlich ein zweiter Modal-Dialog. Dieser hat denselben A11y-Fix bekommen; die echten Fortschritts-/Fehlermeldungen liegen in `delete-progress-panel.ts` und wurden dort ausgezeichnet.

**S3-17 — Formularfelder ohne Label; Kontrast unter AA**
Jedes Filter-/Eingabefeld in `usage-stats-page`, `vote-session-detail-page`, `vote-session-list-page`, `overview-page` und `seven-tv-token-input` hat jetzt ein `<label class="sr-only">` bzw. `[attr.aria-label]` (bestehende Placeholder-Keys wiederverwendet, Muster von `language-switcher.ts`). Die beiden Datumsfelder haben sichtbare Labels „von"/„bis" (neue Keys `usageStats.fromLabel`/`toLabel`; der bisherige `dateRangeTo`-Span ist dort zum „bis"-Label geworden, der Key bleibt in `vote-session-detail-page` in anderem Kontext in Gebrauch).
Kontrast (projektweit freigegeben): `placeholder:text-slate-600` → `text-slate-400` (≈2,5:1 → ≈7:1, betrifft auch das 7TV-Token-Feld) und `text-slate-500` → `text-slate-400` für Textinhalte. Icon-/Zustandsfarben blieben bewusst unangetastet (u. a. die Keep/Delete-Button-Icons in `vote-session-detail-page`).

**S3-18 — Ausgewählte Emote-Karten ohne sichtbaren Auswahlzustand**
`usage-stats-page.html` und `vote-session-detail-page.html`: Karten-Container von statischem `class` auf `[class]`-Bindung — ausgewählt → `ring-2 ring-purple-500 bg-purple-950/40`. „Was ist markiert?" ist damit vor einem irreversiblen Löschlauf auf einen Blick erkennbar statt über 36 winzige Checkboxen.

**S3-22 — Logout ohne Fehlerbehandlung**
`web/src/app/core/auth/auth.service.ts`: gemeinsame private `resetClientSession()`, die aus `next` **und** `error` läuft (und von `handleSessionExpired()` mitgenutzt wird). Ein serverseitiger Logout-Fehler lässt jetzt nicht länger Session-State und — sicherheitsrelevant — das 7TV-Schreib-Token im `sessionStorage` stehen. Test dafür in `auth.service.spec.ts` ergänzt.

**S3-30 Akutteil — doppelter Request pro Seitenwechsel**
`vote-session-list-page.ts`: das explizite `this.load()` in `onPageChange` entfernt; der `effect()` erledigt den Reload bereits, weil `load()` `page()` im reaktiven Kontext liest. `my-votings-page.ts` bleibt korrekt unverändert (dort läuft `load()` aus dem Konstruktor, kein Dirty-Effect-Pfad).

### Niedrig

- **S4-1** — Klassenkommentar von `UsageStatsAccessAuthorizationFilter` auf den Ist-Zustand korrigiert: **fünf** Endpoints über zwei Gruppen (`usage-stats`, `usage-stats/totals`, `sync-deleted`, `set-warning`, `active-set`), mit expliziter Begründung, warum der Schreibpfad `sync-deleted` bewusst dazugehört, plus dem Satz, dass echte Management-Semantik hinter den strengeren Filter gehört. `CLAUDE.md` hat einen datierten Nachtrag bekommen, der historische Log-Eintrag selbst blieb unverändert. Gegengeprüft: der Filter lässt tatsächlich 7TV-Editoren zu (`CanViewUsageStatsAsync`).
- **S4-3** — `form-action 'self'` in die CSP (`src/EmotePurge.Api/Program.cs`). Die Direktive fällt nicht auf `default-src` zurück; damit ist die letzte skriptlose Exfiltrations-/Phishing-Lücke der Kette geschlossen. `style-src 'unsafe-inline'` bleibt bewusst (nicht Teil von Welle A).
- **S4-8** — `formatScore()` per `Intl.NumberFormat` statt `DecimalPipe` in `vote-session-detail-page`; reagiert jetzt korrekt auf den Laufzeit-Sprachwechsel („12,5" statt „12.5").
- **S4-9** — letzter hartkodierter nutzersichtbarer String übersetzt (`overview.admin.channelNamePlaceholder`).
- **S4-12** — `SevenTvEmoteJsonMapper.MapFromJsonElement` samt Stale-Kommentar gelöscht (verifiziert aufruferlos); `Program.cs`-Verweise in `IVoteSessionQueryService.cs`, `vote-session-detail-page.ts` und `web/.claude/CLAUDE.md` auf `Endpoints/*.cs` korrigiert; `CLAUDE.md:166` um den Klammerzusatz *(heute `usage-stats-access.guard.ts`)* ergänzt, ohne den Log-Text umzuschreiben; `web/src/app/core/shared/` → `web/src/app/core/models/` umbenannt (einziger Import angepasst).
- **S4-13** — die `BackgroundService`-Ausnahme in `CLAUDE.md` von der Aufzählung auf ein Kriterium umgestellt („ausschließlich per `AddHostedService<T>()`, nirgends injiziert"), Beispielliste jetzt inklusive `Worker` (verifiziert: fünf registrierte Hosted Services).
- **S4-14** — neuer Abschnitt **Sprache** in `CLAUDE.md`, nach Nutzerentscheidung: Bezeichner/Typen/öffentliche APIs englisch · Kommentare in **neuem** Code englisch (Bestand im Worker bleibt deutsch, wird nur nicht fortgeführt) · Log-/`throw`-Messages deutsch · Doku deutsch, Commit-Messages englisch. Kein Bestandscode umgeschrieben.

### Verifikation

| Prüfung | Ergebnis |
|---|---|
| `dotnet build EmotePurge.slnx` | grün, 0 Warnungen |
| `dotnet test EmotePurge.slnx` | 39/39 grün |
| `ng build --configuration production` | grün |
| `npm --prefix web test -- --watch=false` | 79/79 grün (78 vorher, +1 Logout-Fehlerpfad) |
| `npm --prefix web run e2e` | 4/4 grün |
| Locale-Parität de/en | 188/188 Keys, keine Lücke in beiden Richtungen |

Noch **nicht** live gegen echte Twitch-/7TV-Accounts getestet — Welle A enthält mit S2-15 (Filtersemantik), S2-18/S1-4 (Löschdialog) und S3-17 (Kontrast/Labels) genug UI-Verhalten, das im Browser geprüft werden sollte.

---

## Welle B — umgesetzt am 2026-07-30

22 Befunde in drei Blöcken. Die Worker-Zustandsmaschine (B1) wurde Fix für Fix von Hand gebaut, mechanisch umrissene Teile (Backup-Skript, keyed `ListSelection`, Filter-Validierung) liefen über Sub-Agents mit disjunkter Datei-Eigentümerschaft und wurden anschließend gegengelesen.

### Vorab: Verifikation von S2-1 gegen den echten TwitchLib-Quelltext

Der Befund war der einzige mit ausdrücklichem Restvorbehalt (gestützt auf XML-Doku, Strings und Reflection). Gegengelesen wurden `ReconnectionPolicy.cs`, `ClientBase.cs`, `ClientOptions.cs` und `ConnectionWatchDog.cs` in `TwitchLib.Communication`, Commit `d1904be` — und `TwitchLib.Communication 2.0.1` ist auch tatsächlich die aufgelöste Version. **Der Befund trägt vollständig:**

- `ClientOptions(reconnectionPolicy: null)` → `new ReconnectionPolicy(3_000, maxAttempts: 10)`.
- `internal void Reset(bool isReconnect) { if (isReconnect) return; … }` — ein Reconnect setzt `_attemptsMade` nachweislich **nicht** zurück, `ReconnectInternalAsync` ruft immer mit `isReconnect: true`.
- `ProcessValues()` erhöht bei **jedem** Versuch, `AreAttemptsComplete()` ist `_attemptsMade == _maxAttempts`. Ist das Kontingent verbraucht, läuft die `while`-Schleife in `OpenPrivateAsync` null Mal → `RaiseFatal()` → dauerhaft `"Fatal network error."`.
- `ConnectionWatchDog` bricht bei einem gescheiterten Reconnect wirklich per `break` aus seiner Monitor-Schleife und läuft nie wieder.
- Der parameterlose Ctor setzt `_maxAttempts = null`; `_attemptsMade == null` ist nie wahr → tatsächlich unbegrenzt, mit 3s→30s-Backoff.

**Zwei Korrekturen am Report**, beide ohne Auswirkung auf den Fix: (1) Die Default-Policy hat ein **konstantes** 3-Sekunden-Intervall (`min == max == 3000`), das Kontingent ist also nach ~30 s aufgebraucht, nicht nach den im Report genannten 165 s — diese Rampe (3s→30s) gehört zur **neuen** Policy. (2) `TwitchClient.ConnectAsync()` liefert zwar wie beschrieben `Task<bool>`, **`ReconnectAsync()` aber nur `Task`** (verifiziert an `ITwitchClient`, Tag `4.0.1`). S2-4 ist damit nur auf dem Connect-Pfad umsetzbar; beim Reconnect zeigt sich der Ausgang ausschließlich über `OnReconnected`/`OnConnectionError`.

**Eine vom Report nicht gesehene Folge:** Mit unbegrenzten Versuchen verlässt `OpenPrivateAsync` seine Schleife erst, wenn die Verbindung steht — `ConnectAsync()`/`ReconnectAsync()` kehren also nicht mehr mit `false` zurück, sondern blockieren beliebig lange. Ungebremst würde ein Twitch-Ausfall damit den Worker-Start aufhängen (keine Redis-Subscription, keine Join-/Leave-Kommandos). Deshalb sind beide Aufrufe jetzt mit `OpenWaitTimeout` (30 s) **im Warten** begrenzt, nicht im Versuch: der Retry-Loop läuft im Hintergrund weiter und feuert `OnConnected`, sobald er durchkommt.

### B1 — Worker-Cluster

**S2-1 — Verbindungslimit war client-seitig, nicht Twitch-seitig**
`TwitchChatManager.CreateClient()` erzeugt den `TwitchClient` jetzt über einen expliziten `WebSocketClient` mit `new ClientOptions(new ReconnectionPolicy())`. `TwitchLib.Communication 2.0.1` ist dafür als direkte `PackageReference` aufgenommen. `MaxReconnectsBeforeProactiveRecreate` ist **ersatzlos entfallen** — die Prämisse („zählbasiertes Twitch-Limit pro justinfan-Identität") ist widerlegt. `MaxConsecutiveConnectionErrorsBeforeRecreate` bleibt als Sicherheitsnetz.

**S2-4 — Rückgabewert des Connects wird ausgewertet**
`OpenAsync()` wertet den `bool` aus und loggt einen Fehlschlag als `LogError`; ein gescheiterter Connect ist damit im Log vom Erfolg unterscheidbar, und `_isConnected` bleibt korrekt `false`, sodass der Watchdog greift. `ReconnectClientAsync()` ist bewusst getrennt, weil es dort keinen Rückgabewert gibt.

**S2-7 — Health-Key kann nicht mehr „connected" auf einem toten Client melden**
`RecreateClientAsync` setzt `_isConnected = false` direkt nach `UnwireClient` (das `OnDisconnected` unterdrückt). Zusätzlich kennt `GET /api/worker/health` jetzt einen dritten Zustand `stale` (verbunden, aber seit über 5 Minuten keine Chat-Daten). **Abweichung/Ergänzung:** Der Report schlug nur `LastMessageReceivedUtc` als Bezug vor; das hätte einen frisch gestarteten Worker sofort als `stale` gemeldet. Das Payload trägt deshalb zusätzlich `connectAttemptedUtc` als Ersatzbezug — live verifiziert: direkt nach dem Start `connected`, erst nach 5 Minuten Stille `stale`. Das Frontend bildet backend-`stale` und `disconnected` auf denselben Warn-Punkt ab (für den Betrachter ist die Unterscheidung nicht handlungsrelevant).

**S2-2 — absorbierender Totzustand beseitigt**
`ITwitchChatManager` hat `ConnectAttemptedUtc`; der Watchdog nutzt es als Ersatzbezug, wenn nie eine Nachricht ankam. Zusätzlich gibt es einen eigenen Zweig für `!IsConnected` mit kürzerem Cooldown (60 s statt 5 min) — ein Client, der sich selbst als getrennt meldet, ist kein Abuse-Risiko und braucht keine Stille-Schwelle.

**S2-3 — Join-Absicht statt bestätigter Joins**
`_joinedChannels` ist zu `_desiredChannels` (`ConcurrentDictionary<string, bool>`, Wert = von Twitch bestätigt) geworden. Die Absicht wird **vor** dem Versuch eingetragen, `OnJoinedChannel` bestätigt per `TryUpdate` (fügt bewusst nicht ein, damit eine spät eintreffende Bestätigung einen bereits verlassenen Channel nicht wiederbelebt). `EnsureJoinedAsync` ist neu auf dem Interface und wird vom Resync-Worker im Minutentakt aufgerufen — bestätigte Channels werden dabei übersprungen, es gibt also keinen JOIN-Sturm für gesunde Channels. `OnConnected` rejoint jetzt ebenfalls (nicht nur `OnReconnected`), womit der explizite Rejoin nach einem Recreate entfallen konnte, der sonst jeden JOIN doppelt abgesetzt hätte.

**S2-5 — Boot-Recovery kann den Host nicht mehr mitreißen**
`try/catch` pro Channel **und** um die ganze Schleife. Neu ist `BootRecoveryGate` (Worker-internes Singleton): der Resync-Worker wartet darauf, bevor sein Tick startet, damit Boot-Sync und periodischer Sync nicht auf demselben Channel kollidieren. Das Gate wird in einem `finally` freigegeben — eine gescheiterte Boot-Recovery darf den Konvergenzpfad nicht dauerhaft blockieren.

**S2-6 — Resync-Worker vollständig abgesichert**
`ResyncOnceAsync` ist komplett in `try/catch`, inklusive Scope-Erzeugung und Channel-Abfrage. `OperationCanceledException` beim Shutdown wird getrennt und still behandelt.

**S2-8 — Reihenfolgegarantie zurückgeholt**
`RedisSubscriber` nutzt `ChannelMessageQueue.OnMessage(async …)` statt des fire-and-forget-Callbacks; Handler werden wieder streng sequenziell awaited. Für die zweite Kollisionsquelle (Resync-Tick × JOIN auf demselben Channel) gibt es `ChannelSyncGate` — ein Singleton mit einem `SemaphoreSlim` pro Channelname, das `SevenTvSyncService.SyncChannelAsync` serialisiert.

**S3-12 — leeres 7TV-Set wird nicht mehr angewendet**
Meldet 7TV null aktive Emotes, obwohl bereits welche bekannt sind, bricht der Sync mit `LogWarning` ab, statt alles zu archivieren und den Match-Cache zu leeren.

**S3-13 — atomarer Upsert statt Read-then-Insert**
`UsageStatFlushService` schreibt jetzt per `INSERT … SELECT FROM UNNEST(…) ON CONFLICT ("EmoteId","Date") DO UPDATE`, was nebenbei den 1.000er-`IN`-SELECT alle 30 Sekunden spart. `UsageFlushWorker.StopAsync` ruft `base.StopAsync` **zuerst** (beendet die Timer-Schleife) und flusht erst danach.
**Über den Report hinaus:** Ein fehlgeschlagener Flush wird nicht mehr verworfen, sondern über das neue `IEmoteUsageCounter.Merge` zurückgestellt — begrenzt auf fünf aufeinanderfolgende Versuche. Der Grund für die Grenze ist **nicht** Speicher (die Zahl der Emotes ist beschränkt), sondern Zuordnung: `UsageStat.Date` ist der Tag, an dem der Flush *gelingt*, sodass über einen langen Ausfall mitgeschleppte Counts sonst auf dem falschen Kalendertag landen.

### B2 — Datenverlust

**S1-1 — Channel-Leave löscht nichts mehr**
`ChannelService.LeaveAsync` setzt `IsBotActive = false` und publisht weiterhin `LEAVE:`. Neu ist `PurgeAsync` plus `DELETE /api/channels/{channelName}/purge` hinter `GlobalAdminAuthorizationFilter` — bewusst **nicht** hinter `ChannelManagementAuthorizationFilter`, weil der Moderatoren zulässt und ein positiv gecachter Mod-Status ein `/unmod` bis zu zehn Minuten überlebt. Kein UI dafür (Nutzerentscheidung). Die Leave-Bestätigung im Frontend versprach bisher wörtlich die Löschung aller Daten und ist in beiden Locales korrigiert.
**Testanpassung:** `LeaveAsync_RemovesChannel_AndPublishesLeaveCommand` kodierte das alte Verhalten und ist zu `LeaveAsync_DeactivatesChannel_ButKeepsTheRow` geworden; dazu drei neue Tests (Rejoin reaktiviert dieselbe Zeile, Purge löscht, Purge auf unbekanntem Channel).

**Nachtrag zu S1-1 — Rejoin-Möglichkeit im Frontend (beim Live-Test gefunden)**
Der Soft-Deactivate hat eine Lücke aufgerissen, die es vorher nicht geben konnte: Solange ein Leave die Zeile löschte, tauchte der Channel danach wieder als *ungetrackt* auf und bekam den „Hinzufügen"-Button. Jetzt bleibt er getrackt (nur `IsBotActive=false`), fällt also nie in diesen Zweig — ein Nicht-Admin sah „Bot inaktiv" und **hatte nirgends im UI einen Weg zurück**; nur Admins konnten sich über die manuelle Namenseingabe behelfen. Behoben an beiden Stellen: In der Übersicht steht bei getrackten, inaktiven Channels jetzt ein „Bot reaktivieren"-Button (in der Nutzer-Sektion für Broadcaster/Mods, in der Admin-Sektion generell), der die Zeile ohne Navigation an Ort und Stelle umschaltet. Im Channel-Workspace ersetzt derselbe Button den „Channel verlassen"-Button, solange der Bot inaktiv ist, plus ein Hinweisbanner — der Bot-Status kam dabei gratis aus der ohnehin laufenden `canManage`-Probe, die ihn bisher verwarf. Backend unverändert: `JoinAsync` reaktiviert die bestehende Zeile bereits.

**S1-2 — Backup**
`scripts/backup-postgres.sh` plus `docs/Backup-und-Restore.md`. Das Skript vermeidet die `pg_dump | gzip`-Falle (gzip liefert auch bei abgebrochenem Dump Exit 0) über Dump in eine `.tmp`-Datei, Prüfung von Exit-Code **und** Dateigröße, dann atomares `mv`. Rotation greift nur auf das eigene Namensmuster, nie blind aufs Zielverzeichnis. Off-Site (`rclone`) ist optional und darf niemals ein erfolgreiches lokales Backup als Fehlschlag melden. **Was du selbst auf dem VPS tun musst, steht in `docs/Backup-und-Restore.md`** — Kurzfassung: Skript ablegen, `chmod +x`, Zielverzeichnis anlegen, Cron-Eintrag setzen, einmal manuell testen, und mindestens einmal einen **Restore** proben.

**S1-3 — keyed `ListSelection`**
Auswahl liegt jetzt in einem `signal<ReadonlySet<string>>` über `keyFn(item)`, der Shift-Anker ist das Item (über seinen Key), aufgelöst gegen die **aktuelle** Liste zur Klickzeit; ist der Anker nicht mehr sichtbar, fällt es sauber auf Einzel-Toggle zurück. Zwei getrennte Sichten: `selectedKeys` (autoritativ, überlebt Refetch/Sortierung) und `selectedItems` (für Anzeigezwecke, nur aktuell sichtbare Zeilen). Damit entfallen zwei `clear()`-Notbehelfe aus Welle A — insbesondere geht **die Auswahl auf der Voting-Detailseite nicht mehr bei jedem Vote verloren**. Bewusst stehen geblieben: das `clear()` im Ladepfad der Usage-Stats-Seite (dort wechseln mit Channel/Zeitraum auch die Zahlen, gegen die ausgewählt wurde) und die Filter-`clear()`s — Letztere zu entfernen wäre S2-16 und ist ausdrücklich nicht Teil dieser Welle.

**S2-12 — `sync-deleted` schluckt keine Fehler mehr**
`SevenTvDeleteService` hat ein `syncReport`-Signal (`idle`/`pending`/`succeeded`/`partial`/`failed`), zwei automatische Retries mit 2s/4s (bei `401`/`403` bewusst keine — eine abgelaufene Session heilt nicht durch Warten) und `retrySyncReport()` für den manuellen Versuch. `archivedCount`/`notFoundIds` werden ausgewertet: kommen alle IDs in `notFoundIds` zurück, ist das jetzt `partial` statt scheinbarem Vollerfolg. Das Panel emittiert `deleted` **erst nach** bestätigter Rückmeldung; scheitert sie, feuert das neue Output `reloadRequested`, und die Host-Seite lädt neu, statt optimistisch zu filtern.
**Abweichung:** Der ursprünglich beauftragte Sub-Agent brach an einem Sessionlimit ab, nachdem er nur Imports und Konstanten geschrieben hatte; der Rest ist von Hand entstanden. Die Panel-Schnittstelle hat dabei ein zweites Output bekommen — beide Host-Seiten sind entsprechend verdrahtet.

### B3 — Session und Transport

**S2-9 + S2-10 — gemeinsam, wie in Z2 gefordert**
Data-Protection-Keys werden persistiert (`DataProtection__KeyPath=/keys`, Volume `dataprotection-keys` in beiden Compose-Dateien, `mkdir`/`chown` in der `final`-Stage des Api-Dockerfiles, da `appuser` mit `--no-create-home` angelegt ist). Lokales `dotnet run` bleibt unberührt, weil die Persistenz nur bei gesetztem Pfad aktiviert wird. Gleichzeitig — nicht später — der Widerruf: neue Spalte `User.SessionsValidFromUtc` (Migration `AddUserSessionsValidFrom`), Claim `twitch:session_issued_at` beim Callback, Prüfung in `OnValidatePrincipal`, und `logout` setzt den Zeitstempel, wirkt also serverseitig. Cookies **ohne** den neuen Claim werden abgelehnt statt geduldet, sonst bliebe eine dauerhafte Umgehung der Prüfung offen.
Transport: `Cookie.SecurePolicy = Always` (unabhängig von `X-Forwarded-Proto`), `SameSite = Lax`, `HttpOnly`, plus `Strict-Transport-Security` als echter Response-Header. `UseHttpsRedirection()` ist mit Begründung entfernt — im Container ein No-Op, der Schutz vortäuschte.
**Abweichung:** **kein** `includeSubDomains` und kein `preload`. Beides ist nach dem ersten Ausliefern ein Jahr lang praktisch unumkehrbar und würde Subdomains binden, über die diese App nichts weiß (auf dem VPS läuft eine zweite Anwendung). Nachträglich ergänzen ist gefahrlos, zurücknehmen nicht.
**Kosten, bewusst in Kauf genommen:** `OnValidatePrincipal` macht pro authentifiziertem Request einen Primärschlüssel-Lookup (auf eine Spalte projiziert, ungetrackt). Ein Redis-Cache dafür wäre der naheliegende nächste Schritt, falls das je auffällt.

**S3-3 — Validierung in den Filtern + Redis-Grenze**
Alle vier Autorisierungsfilter prüfen `ChannelNameValidation.IsValid()` statt nur auf „nicht leer", bevor irgendein Redis-Key geschrieben wird. Gegengeprüft: `IsValid` normalisiert intern, gemischt geschriebene Namen wie `HandOfBlood` bestehen die Prüfung also unverändert — genau die Falle, in die das Projekt beim Frontend-Validator schon einmal gelaufen ist. Beide Compose-Dateien setzen jetzt `--maxmemory 256mb --maxmemory-policy allkeys-lru`.

**S2-11 — Policy-Schnitt und Caches**
Aus `ExpensiveOps` sind zwei Policies geworden: `ExternalApi` (20/min) für alles, was Helix oder 7TV anfasst — inklusive `GET /api/channels/mine`, der ganzen `EmoteEndpoints`- und `UsageStatsEndpoints`-Gruppe (deren **Autorisierungsfilter selbst** zwei ungecachte 7TV-Calls macht) sowie der Vote-Session-Liste — und `Bookkeeping` (120/min) allein für `sync-deleted`. Dazu zwei neue Caches in `IModRoleCache` mit derselben TTL wie der Mod-Check: 7TV-Editor-Grants (`7tveditor:{uid}:{channel}`) und Sub-Status (`subcheck:{uid}:{broadcasterId}`). Ein `null` von Helix bzw. 7TV wird bewusst **nicht** gecacht, sonst sperrte ein transientes 429 legitime Nutzer für die volle TTL aus.

**S3-1 — eine Manager-Definition statt zweier**
`VoteEligibilityService` prüft nicht mehr selbst Broadcaster-Login und Mod-Status, sondern delegiert an `IChannelAccessService.CanManageChannelAsync`. Damit ist der Admin-Zweig automatisch enthalten (er fehlte in der Kopie), und die Frage „wer verwaltet diesen Channel?" ist nur noch an einer Stelle beantwortet. Das `IsActive`-Gate in `EvaluateAsync` blieb unberührt.

**S3-4 — Rohnutzung nur noch für Manager**
`GetResultsAsync` nimmt `includeRawUsage`; für Nicht-Manager ist `TotalUseCount` 0, während `NormalizedUsageScore`, `Score` und die Sortierung unverändert aus den echten Zahlen berechnet werden. Zusätzlich ist das rückdatierbare `StartedAt` auf 366 Tage begrenzt — ohne die Grenze umfasste eine einmalig weit zurückdatierte `Everyone`-Session die gesamte Historie.

**S3-7 — Doppelklick auf „Daumen hoch" ergibt keinen 500 mehr**
`CastVoteAsync` fängt `DbUpdateException`, lädt neu und behandelt den Konflikt als das Update, das er in Wahrheit ist. Dazu global `UseExceptionHandler` mit neuem Fehlercode `unexpected_error` (in `ApiErrorCodes`, `api-error.ts` und beiden Locales) — bewusst ohne Exception-Details im Body.

**S3-2 — Broadcaster-Identität an der Twitch-ID**
`CanManageChannelAsync` und der 7TV-Editor-Abgleich vergleichen die unveränderliche `Channel.TwitchChannelId` gegen `principal.TwitchUserId`; der Login-Vergleich bleibt nur als Fallback für Zeilen ohne aufgelöste ID. Stimmt der Login überein, die ID aber nicht, wird abgelehnt **und geloggt** — sonst wäre das eine Ablehnung, die niemand erklären könnte.

### Verifikation

| Prüfung | Ergebnis |
|---|---|
| `dotnet build EmotePurge.slnx` | grün, 0 Warnungen |
| `dotnet test EmotePurge.slnx` | 47/47 grün (39 vorher: +5 Flush-Upsert, +3 Leave/Purge-Semantik, −1 ersetzt) |
| `ng build --configuration production` | grün |
| `npm --prefix web test -- --watch=false` | 90/90 grün (79 vorher: +7 Sync-Report, +4 keyed Selection) |
| `npm --prefix web run e2e` | 4/4 grün |
| Locale-Parität de/en | 197/197 Keys, keine Lücke in beiden Richtungen |
| `docker compose up -d --build` | Stack läuft, Worker verbindet und joint alle vier Channels inkl. 7TV-Sync |
| Smoke-Test | `Strict-Transport-Security` gesetzt, CSP unverändert, `401` ohne Cookie, Health `connected` direkt nach Start |

Der neue `INSERT … ON CONFLICT`-Pfad war der einzige handgeschriebene SQL-Code der Welle und ließ sich anders als durch einen Test nicht prüfen — `UsageStatFlushServiceTests` (5 Tests gegen echtes Postgres) belegt insbesondere, dass die Arbiter-Inferenz auf dem Unique-Index trotz dessen `INCLUDE`-Spalte greift.

**Noch nicht live gegen echte Twitch-/7TV-Accounts getestet.** Besonders prüfenswert: Login/Logout (der Widerruf ist neu und meldet **alle** bestehenden Sessions einmalig ab), ein Leave mit anschließendem Rejoin, ein Löschlauf inklusive Rückmeldung, und das Verhalten des Health-Punkts.

### Was beim Deployment auf Prod zu tun ist

1. **Migration anwenden**: `AddUserSessionsValidFrom` (additiv, eine nullable Spalte auf `Users`). Lokal bereits angewendet.
2. **Alle Nutzer werden einmalig ausgeloggt** — sowohl weil der Data-Protection-Schlüsselring wechselt als auch weil Cookies ohne den neuen Claim abgelehnt werden. Ab dann überleben Sessions Redeploys.
3. Der Portainer-Stack braucht das neue Volume `dataprotection-keys` (steht in `docker-compose.prod.yml`).
4. Backup-Cronjob einrichten, siehe `docs/Backup-und-Restore.md`.

---

## Was noch offen ist

### Direkte Anschlussarbeiten aus den Wellen A und B

Diese Punkte sind Teil eines Befunds, dessen Rest bewusst einer späteren Welle zugeordnet ist:

- **S2-16 (Auswahl über Filterwechsel erhalten)** ist durch die keyed `ListSelection` jetzt sicher baubar — aber nicht automatisch fertig: beide Host-Seiten leiten ihre Löschliste über `selectedItems()` ab, sodass eine über einen Filterwechsel erhaltene Auswahl beim Löschen still auf die sichtbaren Zeilen zusammenschrumpfen würde. Die Richtung ist ungefährlich (es würde weniger gelöscht, nie das Falsche), widerspräche aber der vom Befund geforderten Anzeige „50 ausgewählt (12 ausgeblendet)". Wer S2-16 baut, muss die Zählung auf `selectedKeys()` umstellen und eine Key→Zeile-Zuordnung für ausgeblendete Einträge mitführen.
- **`ReconnectPolicy` extrahieren (S3-6, Welle D)** — die Entscheidungslogik in `ForceReconnectAsync` ist jetzt eine reine Fallunterscheidung über vier Zustände (kein Open läuft + Fehler < 3 → Reconnect; Fehler ≥ 3 → Recreate; Open läuft < 10 min → warten; Open läuft ≥ 10 min → Recreate). Sie herauszuziehen und ohne TwitchLib zu testen ist damit ein kurzer Handgriff geworden.
- **Redis-Cache für `OnValidatePrincipal`** — der Widerruf kostet einen Primärschlüssel-Lookup pro authentifiziertem Request. Sauber, aber cachebar, falls es je auffällt.
- **S3-30 `rxResource`-Pilot** (Welle C) — nur der doppelte Request ist behoben, das strukturelle Muster „`effect()` als Datenlader" existiert an fünf Stellen weiter.
- **S3-16 `@angular/cdk/dialog`** (mittelfristig) — Fokusfalle/Escape sind nachgerüstet, aber weiter handgebaut.
- **S4-3 `style-src` ohne `unsafe-inline`** — braucht `ngCspNonce`, mit `MapFallbackToFile("index.html")` nicht ohne Weiteres möglich; im Report ausdrücklich kein Blocker.
- **S3-17 Restfall** — `my-votings-page.ts` hat denselben `text-slate-500`-Statustext, war im Report aber nicht als Fundort genannt und blieb unangetastet. Trivialer Nachzug.

### Offene Wellen

**Welle C — Refactorings.** S2-13 (HTTP-Interceptor) → S3-26 → S3-32 → `/permissions`-Endpoint → S3-31 → S3-30 → S3-33 (`strict`) → S4-11 → S3-6 → S3-27 (CLAUDE.md-Umbau; die Datei ist auf ~93 KB gewachsen).

**Welle D — Tests.** In dieser Reihenfolge, die ersten beiden ohne Container: `ChannelAccessServiceTests` → `VoteEligibilityServiceTests` → `VoteSessionService.CastVoteAsync` → `SevenTvSyncService` → `UsageStatFlushService` → `ReconnectPolicy`/`EmoteUsageCounter` → zwei Struktur-Tests (Core-Assembly-Referenzen, Fehlercode-Key-Parität beider Locale-Dateien).

**Welle E — Infra & Launch.** S2-21 (Ressourcenlimits, vor dem Stresstest) → Z1-Aufteilung der Health-Endpoints + S3-35 → S3-36 → S3-34 → S3-38 → S3-37 (`pull_request`-Trigger) → S4-15/S4-16 → S4-17/S4-18 → S2-20 (Rechtstexte) → `robots.txt` öffnen.

### Offene Fragen, die weiterhin unbeantwortet sind

Abschnitt 10 des Reports listet 21. Diese blockieren oder verbilligen konkret die nächsten Wellen:

1. ~~**Setzt der Host-Reverse-Proxy `X-Forwarded-Proto`?**~~ — durch S2-10 gegenstandslos: `CookieSecurePolicy.Always` hängt nicht mehr davon ab. **Aber:** Setzt der Proxy den Header *nicht*, ist der Login nach dem Deploy sichtbar kaputt statt unsicher funktionierend. Nach dem Prod-Deploy einmal prüfen.
2. ~~**`ReconnectionPolicy.Reset(bool)`-Verhalten**~~ — am Quelltext verifiziert, s. Welle B oben.
3. ~~**Existiert außerhalb des Repos schon ein Backup?**~~ — beantwortet: nein, keins. S1-2 ist damit der erste überhaupt.
4. **Wie viele Vote-Sessions hat ein Channel typisch?** `SELECT "ChannelId", COUNT(*) FROM "VoteSessions" GROUP BY 1` — durch den Sub-Cache aus S2-11 entschärft, aber weiterhin relevant für die Kosten der Listen-Route.
5. **Kosten von `strictTemplates`** — ein `ng build` mit gesetzter Flagge beantwortet es in zwei Minuten (S3-33).
6. **Wird `VoteSession.IsActive` je automatisch beendet?** Produktentscheidung. Durch die 366-Tage-Grenze auf rückdatiertes `StartedAt` entschärft, aber eine nie beendete Session erweitert ihr Auswertungsfenster weiter laufend.

Die restlichen (Stresstest-Messungen, 7TV-Editor-Permissions-Bitfeld, GHCR-Sichtbarkeit, Branch-Protection, Docker-Log-Rotation) sind in Abschnitt 10 des Reports unverändert nachlesbar.

### Nicht erneut untersuchen

Die **Multi-Tenant-Isolation** wurde über eine vollständige Endpoint-×-Rollen-Matrix geprüft (Anhang A) und ist **intakt** — ein Mod oder 7TV-Editor von Channel A kommt nicht an Daten von Channel B.
