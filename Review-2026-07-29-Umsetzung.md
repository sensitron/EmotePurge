# Umsetzung des Reviews vom 2026-07-29 — Fortschritt

Begleitdokument zu [`Review-2026-07-29.md`](Review-2026-07-29.md). Hält fest, welche der 81 Befunde umgesetzt sind, wo bewusst vom vorgeschlagenen Fix abgewichen wurde und was noch offen ist. Die Wellen-Einteilung folgt Abschnitt 8 des Reports.

| Welle | Inhalt | Status |
|---|---|---|
| **A** | Quick Wins | ✅ **abgeschlossen** (2026-07-29) |
| **B** | Sicherheit & Korrektheit (S1/S2) | ⬜ offen |
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

## Was noch offen ist

### Direkte Anschlussarbeiten aus Welle A

Diese Punkte sind Teil eines Befunds, dessen Rest bewusst einer späteren Welle zugeordnet ist:

- **S1-3 keyed `ListSelection`** (Welle B) — `ListSelection` auf `keyFn`/`Set<string>` umstellen und den Shift-Anker als *Item* statt Positionsindex speichern. Erst danach ist S2-16 (Auswahl über Filter-/Reload-Wechsel erhalten) sicher baubar (Zielkonflikt **Z5** des Reports), und erst danach entfällt die oben beschriebene Nebenwirkung „Auswahl geht bei jedem Vote verloren".
- **S3-30 `rxResource`-Pilot** (Welle C) — nur der doppelte Request ist behoben, das strukturelle Muster „`effect()` als Datenlader" existiert an fünf Stellen weiter.
- **S3-16 `@angular/cdk/dialog`** (mittelfristig) — Fokusfalle/Escape sind nachgerüstet, aber weiter handgebaut.
- **S4-3 `style-src` ohne `unsafe-inline`** — braucht `ngCspNonce`, mit `MapFallbackToFile("index.html")` nicht ohne Weiteres möglich; im Report ausdrücklich kein Blocker.
- **S3-17 Restfall** — `my-votings-page.ts` hat denselben `text-slate-500`-Statustext, war im Report aber nicht als Fundort genannt und blieb unangetastet. Trivialer Nachzug.

### Offene Wellen

**Welle B — Sicherheit & Korrektheit.** Reihenfolge laut Report:
- *B1, vor dem HandOfBlood-Stresstest:* **S2-1** (TwitchLib `ReconnectionPolicy` — zuerst, macht zwei bestehende Heuristiken obsolet) → S2-4 → S2-7 → S2-2 → S2-3 → S2-5 → S2-6 → S2-8, danach S3-12 und S3-13.
- *B2, Datenverlust schließen:* **S1-1** (Channel-Leave auf Soft-Deactivate — heute löscht ein Leave kaskadierend die komplette Channel-Historie) → **S1-2** (Postgres-Backup, unabhängig davon sofort) → S1-3 keyed `ListSelection` → S2-12.
- *B3, Session und Transport:* S2-9 + S2-10 gemeinsam (Zielkonflikt Z2) → S3-3 → S2-11 → S3-1, S3-4, S3-7 → S3-2.

⚠️ **Vor dem Umbau von S2-1**: `ReconnectionPolicy.cs` in `github.com/TwitchLib/TwitchLib.Communication`, Tag `2.0.1`, Commit `d1904be` gegenlesen (~10 Min). Der Befund stützt sich auf XML-Doku, Strings und Reflection — die Methodenkörper waren nicht dekompilierbar. Es ist der wertvollste Befund des Reports und gleichzeitig der mit dem größten Restvorbehalt.

**Welle C — Refactorings.** S2-13 (HTTP-Interceptor) → S3-26 → S3-32 → `/permissions`-Endpoint → S3-31 → S3-30 → S3-33 (`strict`) → S4-11 → S3-6 → S3-27 (CLAUDE.md-Umbau; die Datei ist auf ~93 KB gewachsen).

**Welle D — Tests.** In dieser Reihenfolge, die ersten beiden ohne Container: `ChannelAccessServiceTests` → `VoteEligibilityServiceTests` → `VoteSessionService.CastVoteAsync` → `SevenTvSyncService` → `UsageStatFlushService` → `ReconnectPolicy`/`EmoteUsageCounter` → zwei Struktur-Tests (Core-Assembly-Referenzen, Fehlercode-Key-Parität beider Locale-Dateien).

**Welle E — Infra & Launch.** S2-21 (Ressourcenlimits, vor dem Stresstest) → Z1-Aufteilung der Health-Endpoints + S3-35 → S3-36 → S3-34 → S3-38 → S3-37 (`pull_request`-Trigger) → S4-15/S4-16 → S4-17/S4-18 → S2-20 (Rechtstexte) → `robots.txt` öffnen.

### Offene Fragen, die weiterhin unbeantwortet sind

Abschnitt 10 des Reports listet 21. Diese blockieren oder verbilligen konkret die nächsten Wellen:

1. **Setzt der Host-Reverse-Proxy `X-Forwarded-Proto`?** → `curl -sI https://emotepurge.app/api/auth/twitch/login | grep -i set-cookie`; ohne `secure` ist die Annahme verletzt. S2-10 macht die Frage gegenstandslos.
2. **`ReconnectionPolicy.Reset(bool)`-Verhalten** — s. Warnung zu S2-1 oben.
3. **Existiert außerhalb des Repos schon ein Backup?** Entscheidet die Schärfe von S1-1 und S1-2.
4. **Wie viele Vote-Sessions hat ein Channel typisch?** `SELECT "ChannelId", COUNT(*) FROM "VoteSessions" GROUP BY 1` — bestimmt die Priorität des Sub-Check-Teils von S2-11.
5. **Kosten von `strictTemplates`** — ein `ng build` mit gesetzter Flagge beantwortet es in zwei Minuten (S3-33).
6. **Wird `VoteSession.IsActive` je automatisch beendet?** Produktentscheidung, verschärft S3-4.

Die restlichen (Stresstest-Messungen, 7TV-Editor-Permissions-Bitfeld, GHCR-Sichtbarkeit, Branch-Protection, Docker-Log-Rotation) sind in Abschnitt 10 des Reports unverändert nachlesbar.

### Nicht erneut untersuchen

Die **Multi-Tenant-Isolation** wurde über eine vollständige Endpoint-×-Rollen-Matrix geprüft (Anhang A) und ist **intakt** — ein Mod oder 7TV-Editor von Channel A kommt nicht an Daten von Channel B.
