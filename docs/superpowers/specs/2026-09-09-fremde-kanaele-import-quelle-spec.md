# Fremde Kanäle als Import-Quelle — Spec

**Datum:** 2026-09-09 · **Status:** vorgelegt, nicht freigegeben · **Zweitmeinung:** Codex Sol, adversarial, 2026-09-09 — 5 Befunde (3 hoch), **alle im Code nachgeprüft und alle eingearbeitet** (Abschnitt 17) · **Konzept:** [Fremde-Kanaele-als-Import-Quelle-2026-09-09.md](../../designs/Fremde-Kanaele-als-Import-Quelle-2026-09-09.md) (APPROVED, `b3f09c2`) · **Epic:** #118 (messungsneutral)

Diese Spec **zerlegt** das freigegebene Konzept. Sie entwirft es nicht neu. Ansatz B, die
verworfene Export-Seite (C), die zurückgestellte Multi-Kanal-Palette (D), „fremd ist die Quelle,
nie das Ziel", P4' und P5' stehen mit Begründung im Konzept und werden hier **nicht** wieder
aufgerollt. Wer eine davon umwerfen will, braucht einen Befund, keine Präferenz.

Neu gegenüber dem Konzept ist nur dies: **sechs** im Code nachgeprüfte Fallen (Abschnitt 3) — vier
aus dem Konzept, zwei beim Nachprüfen gefunden —, die sieben offenen Fragen als verbindliche
Festlegungen (Abschnitt 2), und die Aufgabenreihenfolge.

---

## 1. Auftrag in einem Satz

Ein eingeloggter Nutzer **ohne jede Rolle im Quellkanal** kann dessen aktives 7TV-Emote-Set
ansehen, **einzelne** Emotes daraus auswählen und in ein Set übertragen, in dem er 7TV-Rechte
hat — Ziel bleibt ein getrackter Kanal aus `listMine()`.

Der heutige Umweg, den das ablöst: einen Kanal joinen, nur um seine Liste zu lesen
(`ChannelAccessService.cs:17-39`, Admin-Durchgriff über `IsGlobalAdmin`).

---

## 2. Entscheidungen dieser Spec

Das Konzept listete sieben offene Fragen. Alle sieben sind hier entschieden und ab jetzt
verbindlich.

| # | Frage | Entscheidung | Begründung |
|---|---|---|---|
| E1 | Wollen wir dafür bekannt sein, fremde Sets bequem abgreifbar zu machen? | **Bauen, nüchtern gerahmt.** Der Fremdkanal ist eine Quelle unter anderen im Import-Dialog, gleichwertig neben „Datei" und „getrackter Kanal". Keine eigene Seite, kein Katalog-Gefühl, keine Bewerbung | Nutzer-Wertung am 2026-09-09. Deckungsgleich mit dem Grund, aus dem Ansatz C verworfen wurde |
| E2 | Sichtbare Warnung vor dem Import und Melde-/Takedown-Weg? | **Weder noch.** Kein Hinweistext im Fluss, kein Beschwerdeweg | Nutzer-Wertung am 2026-09-09: die Sets sind auf `7tv.app` öffentlich einsehbar und kopierbar; wir machen einen vorhandenen Weg bequemer, nicht einen neuen auf. **Hängt nicht an S2-20** und blockiert die Auslieferung nicht |
| E3 | Cache-TTL, reicht ein „neu laden"-Knopf? | **Redis, 60 s, Schlüssel auf dem normalisierten Login** (Präfix `7tvforeign:`), gecacht wird die **fertige Antwortform**, nicht die 7TV-Rohantwort. Plus „neu laden", das den Cache übergeht | Die Auflösungskette (Helix + `userByConnection`) ist selbst zwei Requests; sie mitzucachen ist gratis. 60 s deckt „Dialog schließen und wieder öffnen" ab. Ein Setwechsel beim Quellkanal innerhalb von 60 s unsichtbar zu lassen ist folgenlos |
| E4 | Circuit-Breaker-Schwellen | **Zwei Auslöser, nicht einer.** (a) Ein **erkanntes 429** — ob HTTP 429 oder `extensions.status: 429` — öffnet den Breaker **sofort**, ohne Zählschwelle. (b) Sonstige Upstream-Fehler öffnen ihn nach **5 aufeinanderfolgenden**. Die Offenzeit ist **nicht fix**: liegt ein `Retry-After` oder ein Reset-Hinweis vor, gilt dieser, sonst 60 s. Danach genau ein Probe-Request. Handgeschriebene, **pure** Policy-Klasse, kein Polly | **Korrigiert nach Codex.** Die erste Fassung ließ nach einem eindeutigen 429 noch vier weitere Versuche zu und hätte nach starren 60 s erneut angeklopft, obwohl 7TVs Suchsperre ~1 h läuft (`x-ratelimit-search-reset: 3583`) — das Feature hätte die Sperre verschärft, die es vermeiden soll. `ProviderRequestTelemetryHandler.cs:61-77` liest `Retry-After` bereits in beiden Formen; die Information ist da. Kein Polly, weil das Haus-Muster pure Policy-Klassen sind: `TwitchReconnectBackoffPolicy`, `SevenTvBackoffPolicy`, `TwitchWatchdogPolicy` |
| E5 | Form und Partitionsschlüssel der Rate-Limit-Policy | **Zwei Schranken, nicht eine.** (a) Pro Nutzer: neue benannte Policy `ForeignEmoteLookup`, Fixed-Window, **10 Requests/Minute**, partitioniert über den **vorhandenen** `ResolveUserKey` (`RateLimitRejection.cs:247-250`: `NameIdentifier`-Claim → Remote-IP → `"unknown"`). (b) **Providerweit**, über alle Nutzer hinweg: höchstens **2 gleichzeitige** Fremdset-Abrufe und **60 Upstream-Requests/Minute** aus diesem Feature. Über der Schranke wird gewartet, nicht abgelehnt; nur bei Zeitüberschreitung wird der `Unavailable`-Code geliefert | Es gibt **keinen** literalen `twitch-user`-Schlüssel; der Konzeptvorschlag beschrieb das vorhandene Verhalten unter falschem Namen. Die zweite Schranke ist **nach Codex ergänzt** und der eigentliche Punkt: 7TV sieht **eine** Server-IP, nicht N Nutzer. Zehn verschiedene Logins umgehen Cache und Koaleszierung vollständig und kosten je bis zu 1 Helix + 1 `userByConnection` + 10 Set-Seiten — per-Nutzer-Limits begrenzen davon nichts. 10/min pro Nutzer liegt zwischen `ChannelResync` (5/min) und `Bookkeeping` (120/min). Der „neu laden"-Knopf braucht keine eigene Policy |
| E6 | Name der dritten `SourceKind`-Vokabel | **`"seventv-channel"`** | Wie im Konzept vorgeschlagen. Steht dauerhaft im Audit-Log und liest sich dort richtig. Eine Abweichung vom bereits vorgeschlagenen Wert bräuchte einen Grund |
| E7 | Alias-Kollisionen | **Nichts zu bauen — das gibt es bereits.** `buildImportPreview` erzeugt `nameCollisions` (`import-preview.ts:12,38-59`), der Bestätigungsdialog zählt und zeigt sie mit Pluralschlüssel (`import-confirm-dialog.ts:192-197,303-304`), Specs existieren (`import-preview.spec.ts:41-109`). Warnen ohne blockieren ist bereits das Verhalten: die Zeilen bleiben in `toAdd`, 7TV entscheidet. **Vorgabe hier ist nur: das muss für die neue Quelle genauso greifen** | **Korrigiert nach Codex.** Die erste Fassung entschied etwas, das seit #72 im Bestand steht, und leitete daraus einen eigenen Task ab. Der Task entfällt (Abschnitt 8), die Anforderung bleibt als Regressionsfall |

---

## 3. Sechs Fallen, im Code nachgeprüft am 2026-09-09

Jede davon ist eine **prüfbare Vorgabe**, keine Fußnote. Ein naiver Umsetzer läuft in jede.
F1–F4 standen im Konzept, F5 und F6 sind beim Nachprüfen dazugekommen — F6 nach Codex' Einwand.

### F1 — Die Auflösungskette ist verbindlich, `users(query:)` ist verboten

Vorgeschrieben, in dieser Reihenfolge:

1. `IChannelIdentityService.LookupByLoginAsync` (`Core/Services/IChannelIdentityService.cs:119`,
   Implementierung `ChannelIdentityService.cs:137-165`) — erledigt Normalize, App-Token, Helix und
   den Dreizustand `TwitchUserLookupStatus { Found, NotFound, Unavailable }` in **einem** Schritt.
   Die App-Token-Behandlung **darf nicht ein zweites Mal geschrieben werden**.
2. `ISevenTvApiClient.ResolveSevenTvIdentityAsync` (`ISevenTvApiClient.cs:21`, Implementierung
   `SevenTvApiClient.cs:178-230`) — `userByConnection(platform: TWITCH, id:)` →
   `SevenTvIdentity(SevenTvUserId, ActiveEmoteSetId)`.
3. Neue v4-GQL-Set-Abfrage auf der Set-Id.

**Verboten:** `GqlUsersQuery` (`SevenTvApiClient.cs:12-13`) und damit `ResolveTwitchUserIdAsync`.
Das ist 7TVs **Suchendpunkt**; er hängt am 100er-Suchlimit, dessen Überziehung ~1 Stunde sperrt
und sich als HTTP 200 mit `extensions.status: 429` tarnt. Einziger heutiger Aufrufer ist
`SevenTvSyncService.cs:75` — der neue Pfad berührt ihn nicht, **solange niemand einen dritten,
unnötigen Aufruf einbaut**.

**Prüfbar:** ein Test, der belegt, dass der neue Pfad `ResolveTwitchUserIdAsync` nicht aufruft.

### F2 — `SevenTvLookupStatus.NoActiveEmoteSet` tritt auf diesem Pfad nie ein

`NoActiveEmoteSet` wird an **genau einer** Stelle gesetzt: `SevenTvApiClient.cs:149`, innerhalb
`GetChannelStateForTwitchUserAsync` — dem v3-REST-Pfad, den diese Spec **nicht** benutzt.
`ResolveSevenTvIdentityAsync` kann nur `Ok`, `NoSevenTvAccount` und `Unavailable` liefern.

„Konto vorhanden, aber ohne aktives Set" ist auf dem vorgeschriebenen Pfad:
**`Ok` mit `ActiveEmoteSetId is null`**. Wer `NoActiveEmoteSet` abfragt, prüft eine Bedingung, die
nie eintritt, und liefert dem Nutzer den falschen Fehler.

`GetChannelStateForTwitchUserAsync` wird **nicht angefasst**.

### F3 — Paginierung ist Pflicht, und die Decke darf nicht still abschneiden

`SetEntriesPerPage = 500`, `MaxSetEntryPages = 10` (`SevenTvApiClient.cs:36-37`), Kommentar dort:
„capacity can exceed 1000". `totalCount > items.length` ist bei Sub-Sets der **Normalfall**.

**Korrektur am Konzept:** Diese Konstanten gehören heute **nicht** zum Set-Abruf. Der heutige
Abruf ist v3-REST und unpaginiert; die Konstanten bedienen allein die optionale
`addedAt`-Anreicherung in `GetSetEntryAddedAtAsync` (`SevenTvApiClient.cs:374-429`). Für die
**neue** v4-Abfrage gilt die Paginierung trotzdem — die Konstanten werden dort wiederverwendet.

**Und hier die eigentliche Falle:** Die heutige Schleife bricht bei Erreichen von
`MaxSetEntryPages` **ohne Fehler und ohne Warnung** ab (`:379`, `:409-412`). Für `addedAt` ist das
tolerabel („AddedToSetAt unknown", `:313-324`). Für eine Vorschau wäre es das nicht: der Nutzer
bekäme eine **zu kurze Liste, ohne zu erfahren, dass sie zu kurz ist**, und würde einen Import
für vollständig halten, der es nicht ist.

**Vorgabe:** Wird die Seitendecke erreicht, während `totalCount` mehr Einträge ansagt, ist das ein
**expliziter Zustand** — nicht Stille. Die Antwort trägt `truncated: true` samt `totalCount`, die
Oberfläche sagt es, und der Fall wird geloggt. Kein leises Kürzen.

### F4 — Das Auswahlraster muss neu gebaut werden

Generisch ist **nur** `ListSelection<T>` (`web/src/app/shared/selection/list-selection.ts:29-157`:
`selectedKeys`, `selectedItems`, `isSelected`, `onRowClick` mit Shift-Bereich, `selectMany`,
`clear`, `retainVisible`, `retainAmong`). Über eine `keyFn` ist es direkt wiederverwendbar.

Das **Raster** ist es nicht. Es steht inline in `usage-stats-page.html` (~485-560), wird mit
`emote.emoteId` instanziiert (`usage-stats-page.ts:516`) — unserer **DB-internen Guid**, nicht der
7TV-ObjectID (Regel 8) — und hängt an `EmoteUsageTotal`: Bänder, Sparkline und Füllbalken lesen
`totalUseCount`. Für einen ungetrackten Kanal existiert nichts davon.
`mass-delete-panel.ts:174` sagt es selbst: „the host page owns the ListSelection".

**Vorgabe:** eigenes, schlankes Raster über den Zeilentyp `{ sevenTvEmoteId, name, imageUrl,
scores }`, mit eigener `ListSelection<ForeignEmoteRow>` auf `sevenTvEmoteId`. Das ist ein
**eigener Task**, keine Wiederverwendung. Auf dieser Anforderung hat der Nutzer ausdrücklich
bestanden: der heutige Datei-Pfad hat keine Einzelauswahl (`ImportConfirmDialog` übergibt
`preview.toAdd` als Ganzes), und „**muss die möglichkeit geben dass man NICHT alle direkt
übernimmt**".

### F5 — Die dritte Vokabel muss an **drei** Stellen ankommen, nicht an einer

Neu gegenüber dem Konzept, beim Nachprüfen gefunden. `POST /sync-imported` nimmt heute nur
`("channel" | "file")` (`EmoteEndpoints.cs:136-138`). Die Vokabel `"seventv-channel"` allein dort
einzutragen reicht **nicht**:

1. **Endpunkt-Validierung** (`EmoteEndpoints.cs:136-138`): Vokabelmenge erweitern.
2. **Kind/Name-Abgleich** (`EmoteEndpoints.cs:155`):
   `IsNullOrWhiteSpace(SourceChannelName) != (SourceKind == "file")`. Für `"seventv-channel"` mit
   gesetztem Namen trägt die Bedingung — aber **zufällig**, weil sie binär „file gegen nicht-file"
   fragt. Sie bleibt so und wird mit einem Test festgenagelt, statt sich darauf zu verlassen.
3. **Audit-Log-Rendering** (`AuditLogQueryService.cs:145`): der Zweig prüft
   `sourceKind is "channel" or "file"`. Ein unbekannter Wert fällt laut Kommentar dort „to the
   branches below" durch — also auf den nackten `EmoteCount`-Zweig. Der Kommentar direkt darüber
   nennt genau das als den Schaden, den diese Reihenfolge verhindern soll: „silently drop the one
   thing that row can't be reconstructed from otherwise — where the emotes came from".
   **Ohne diesen dritten Schritt verliert jede Fremdkanal-Zeile ihre Herkunft, dauerhaft und
   unbemerkt** — Audit-Zeilen sind write-once.

Dazu die Frontend-Seite: `ImportOrigin` (`core/seven-tv/import-source.ts:16-24`) ist heute eine
Union aus zwei Zweigen und braucht einen dritten. Der bestehende `kind: 'channel'`-Zweig liest über
`GET /api/channels/{name}/emotes` → `EmoteListQueryService.ListActiveAsync`, das
`LoadChannelReadOnlyAsync` braucht und für **jeden ungetrackten Kanal 404** liefert — er ist also
nicht umbaubar, es kommt ein Zweig **daneben**. Der Docstring zu `ImportSource.discardedRows`
(`:26-33`) nennt heute nur zwei Ursprünge und wird mitgezogen.

### F6 — Die dritte Vokabel muss auch über die Leitung, und die Union wird nirgends erschöpfend geprüft

Nach Codex' Einwand ergänzt und im Code bestätigt. Die Spec erklärte `SevenTvImportService` für
unverändert. Das ist falsch, und zwar auf die teure Art.

**Der Schaden:** `seven-tv-import.service.ts:239-240` baut den `/sync-imported`-Rumpf so:

```
sourceChannelName: run.origin.kind === 'channel' ? run.origin.channelName : null,
sourceKind: run.origin.kind,
```

Für einen `seventv-channel`-Lauf wäre `sourceChannelName` also **`null`**. Der Kind/Name-Abgleich
(`EmoteEndpoints.cs:155`) verlangt für jeden Nicht-Datei-Ursprung einen Namen und antwortet mit
**400** — und zwar **nachdem die 7TV-Mutationen bereits gelaufen sind**. Die Emotes sind kopiert,
die Meldung scheitert, die Herkunft ist weg. Das ist schlimmer als der stille Anzeigeverlust aus
F5.3 und die einzige Falle, die nach unumkehrbarer Arbeit zuschlägt.

**Was der Compiler abfängt und was nicht** — die Unterscheidung ist die eigentliche Vorgabe:

| Stelle | Verhalten bei einem dritten Union-Zweig |
|---|---|
| `emote-admin.service.ts:30` (`sourceKind: 'channel' \| 'file'`) | **Laut.** Typfehler beim Build, kann nicht durchrutschen |
| `import-confirm-dialog.ts:72` (`origin.kind === 'channel'`) | **Laut**, aber nur zufällig: der `@else`-Zweig liest `origin.fileName`, das es auf dem neuen Zweig nicht gibt |
| `seven-tv-import.service.ts:239` (`=== 'channel' ? … : null`) | **Still.** Typkorrekt, semantisch falsch — der 400 oben |
| `import-confirm-dialog.ts:333` (`origin.kind !== 'file'`) | **Still**, und zufällig richtig: behandelt den neuen Zweig als Kanal |
| `import-confirm-dialog.ts:376` (`origin.kind === 'file'`) | **Still**, und richtig |
| `import-source.ts:30` (Docstring) | Still, veraltet |

**Vorgabe:** `=== 'channel'` und `!== 'file'` sind ab dem dritten Union-Mitglied **nicht mehr
gleichbedeutend**. Alle sechs Fundstellen werden einzeln entschieden, keine per Suchen-und-Ersetzen.
Wo eine erschöpfende Behandlung sinnvoll ist, wird sie erzwungen (`switch` über den Diskriminanten
mit `never`-Zweig), damit der nächste Ursprung nicht wieder still danebenfällt.

---

## 4. Vertrag: Endpunkt

```
GET /api/seventv/channels/{channelName}/emotes[?refresh=true]
```

Eigene `MapGroup`. Es gibt heute **keine** `/api/seventv/…`-Gruppe; sie entsteht mit dieser Spec.

**Tatsächliche Reihenfolge** (Vertrag, wird getestet, Regel 11). **Korrigiert nach Codex** — die
erste Fassung listete den Namensfilter vor dem Rate-Limiter, was die Pipeline nicht hergibt:

| Position | Läuft wo | Wirkung |
|---|---|---|
| 1 | `UseAuthentication`/`UseAuthorization`, Middleware (`Program.cs:262-263`) | nicht eingeloggt → **401** |
| 2 | `UseRateLimiter`, Middleware (`Program.cs:270`) | über Budget → **429** |
| 3 | `ChannelNameValidationFilter`, **Endpoint**-Filter | ungültiger Name → **400** |

`RequireRateLimiting` ist **kein** nachgelagerter Endpoint-Filter: Endpoint-Filter laufen im
Endpoint-Delegate und damit **nach** der gesamten Middleware. Zwei Folgen, die ausdrücklich so
gewollt sind und getestet werden, statt sie zu entdecken:

- **Ein ungültiger Kanalname über Budget bekommt 429, nicht 400.** Der 400-Vertrag der
  Namensvalidierung gilt, solange Budget da ist — nicht darüber hinaus.
- **Ungültige Namen verbrauchen Permits.** Akzeptiert: die Alternative wäre, die Namensprüfung in
  eigene Middleware vor den Limiter zu ziehen, und dafür ist der Schaden zu klein.

**Ausdrücklich kein** `UsageStatsAccessAuthorizationFilter` — das ist der Kern des Features. Das
nächstliegende Bestandsmuster für „jeder eingeloggte Nutzer, kanalübergreifend" sind
`LiveEndpoints.cs:69-78` und `:92-105`.

**Antwortform** (Vertrag, keine Implementierung):

```
ForeignEmoteSetResponse {
  channelName: string        // normalisiert
  sevenTvUserId: string
  emoteSetId: string
  totalCount: int            // was 7TV ansagt
  truncated: bool            // true wenn die Seitendecke griff (F3)
  emotes: ForeignEmoteRow[]
}

ForeignEmoteRow {
  sevenTvEmoteId: string     // 7TV-ObjectID, nicht unsere Guid (Regel 8)
  name: string               // Alias im Quellset
  defaultName: string        // globaler Basisname, kann abweichen
  imageUrl: string
  topAllTime: int?           // 7TV-weit, siehe Abschnitt 6
  trending: int?
}
```

Der Endpunkt schreibt **nichts** in unsere DB. Keine Migration.

---

## 5. Vertrag: Zustände und Fehlercodes

| Zustand | Quelle | Antwort |
|---|---|---|
| Kanal existiert nicht auf Twitch | `TwitchUserLookupStatus.NotFound` | 404, **vorhandener** `ChannelNotOnTwitch` (`ApiErrorCodes.cs:35`) |
| Helix nicht erreichbar / kein App-Token | `TwitchUserLookupStatus.Unavailable` | 503, **neuer** Code + Retry-Hinweis. **Nicht** als Ablehnung behandeln |
| Kein 7TV-Konto | `SevenTvLookupStatus.NoSevenTvAccount`, erkannt an fehlender TWITCH-Connection (nicht am Sentinel) | 404, **neuer** Code |
| Konto ohne aktives Set | `Ok` mit `ActiveEmoteSetId is null` (**nicht** `NoActiveEmoteSet`, F2) | 404, **neuer** Code |
| Aktives Set mit 0 Emotes | Set-Abruf, leeres `items` | **200**, Leerzustand, kein Fehler, kein Code |
| 7TV nicht erreichbar / 429 / Breaker offen | `Unavailable`, `extensions.status: 429` | 503, **neuer** Code + Retry-Hinweis |

**Vier neue Codes**, nicht fünf: `ChannelNotOnTwitch` existiert bereits und wird wiederverwendet.
Jeder neue Code braucht denselben Eintrag in `ApiErrorCodes.cs`, in
`web/src/app/core/i18n/api-error.ts` (`KNOWN_API_ERROR_CODES`) **und** in beiden Locales
(`web/public/i18n/de.json`, `web/public/i18n/en.json`, Schlüssel `errors.api.<code>`) — Regel 7.
`api-error-locales.spec.ts` erzwingt die beiden hinteren Schritte; der Schritt von
`ApiErrorCodes.cs` nach `api-error.ts` bleibt Disziplin.

**429 aus dem GraphQL-Payload:** 7TV meldet Überlast als HTTP **200** mit `extensions.status: 429`.
Ohne diese Erkennung sieht ein Fehlschlag aus wie ein **leeres Set** — der Zustand, der laut
Tabelle oben ausdrücklich kein Fehler ist. Diese Verwechslung ist der teuerste Einzelfehler in der
ganzen Spec.

---

## 6. Vertrag: Härtung

| Maßnahme | Festlegung |
|---|---|
| Cache | Redis, Präfix `7tvforeign:`, Schlüssel = normalisierter Login, TTL 60 s, gecacht wird die fertige `ForeignEmoteSetResponse` (E3) |
| Koaleszierung | Parallele identische Abrufe teilen sich einen Upstream-Abruf |
| `refresh=true` | Übergeht den Cache, unterliegt derselben Rate-Limit-Policy und demselben Breaker |
| 429-Erkennung | Aus `extensions.status` bei HTTP 200, siehe Abschnitt 5 |
| Breaker | 5 aufeinanderfolgende Upstream-Fehler → 60 s offen → ein Probe-Request. Pure Policy-Klasse, kein Polly (E4). Das Öffnen wird **einmal** geloggt, nicht je Request |
| Telemetrie | Eigene `RateLimitCallSources`-Quelle, sichtbar in `/api/admin/rate-limits`, **mit semantischem Status** — siehe Vertrag unten. Ein Feature, dessen Begründung „7TV nicht verärgern" lautet, muss seinen eigenen Verbrauch zeigen |

### Telemetrievertrag (korrigiert nach Codex)

Die erste Fassung verlangte eine eigene Call-Source, ohne zu prüfen, ob das geht. Es geht so nicht:
`ProviderRequestTelemetryHandler` bekommt seine Call-Source **fest bei der Registrierung des
gesamten typisierten Clients** (`ServiceCollectionExtensions.cs:77`,
`RateLimitCallSources.SevenTvRest`). Eine neue Methode auf `ISevenTvApiClient` erschiene damit
weiter unter `seventv-rest`. Schlimmer: der Handler sieht bei `extensions.status: 429` nur HTTP
200, und der Store wertet nur einen **HTTP**-429 als Rate-Limit — ausgerechnet der wichtigste
Fehler dieses Features bliebe im Admin-Monitoring unsichtbar.

**Festlegung:** Für diesen Pfad **meldet die Client-Methode selbst**, nicht der Message-Handler.
Der Handler wird für diese Requests über einen `HttpRequestOptions`-Schlüssel stillgelegt; die
Beobachtung entsteht **nach** dem GraphQL-Parsing und trägt damit den semantischen Status (ein
`extensions.status: 429` zählt als Rate-Limit-Ereignis) unter der neuen Call-Source.

**Genau eine Beobachtung je Upstream-Request.** Nicht null (Handler stillgelegt, Client vergisst
es) und nicht zwei (beide melden). Das ist die Bedingung, die der Test nachweist.

**Bekannte Grenze, bewusst in Kauf genommen:** Breaker-Zustand, Koaleszierung und das
providerweite Budget aus E5 liegen **in-process**. Bei mehr als einer Api-Replica bräuchte es
verteilten Zustand — dieselbe Grenze wie beim Twitch-Token-Refresh, und heute gibt es eine
Replica. Gehört in `docs/DECISIONS.md`.

### Bedeutungsvertrag der Score-Spalte (P5', unverändert aus dem Konzept)

`topAllTime`/`trending` kommen im Set-Batch **gratis** mit — gemessen am 2026-09-09 gegen
HandOfBloods Set: 956 Emotes, 174.547 Bytes, 0,72 s, Query-Complexity 16 von 5000. Ihre
**Bedeutung** kostet trotzdem:

- Sie werden als **netzwerkweit** beschriftet („7TV global"), in beiden Locales.
- Sie sind **nie** die Standardsortierung, nur eine wählbare.
- Die Spalte heißt **nie** bloß „Beliebtheit".

Der Beleg für das Missverständnisrisiko steht im Konzeptgespräch selbst: der Betreiber schrieb
„Beliebtheit im Quellkanal", bevor er eine Oberfläche gesehen hatte. Eine kanalbezogene
Beliebtheit für fremde Kanäle gibt es nicht und kann es nicht geben — `EmoteSetEmote` trägt keine
Nutzungsdaten.

**`channels.totalCount` wird nicht geholt.** Es hängt an einem eigenen Such-Eimer mit Limit 100
und sperrt bei Überziehung ~1 h. Nur denkbar beim Klick auf ein **einzelnes** Emote, und das ist
nicht Teil dieser Runde.

---

## 7. Vertrag: Frontend

Fluss unverändert: **Quelle → Auswahl → Ziel → Bestätigung.**

| Baustein | Was passiert |
|---|---|
| Import-Dialog | Bekommt neben „Datei" und „getrackter Kanal" eine dritte Quelle: Kanalname eingeben, Set laden |
| Auswahlraster | **Neu** (F4), Zeilentyp `ForeignEmoteRow`, eigene `ListSelection` auf `sevenTvEmoteId` |
| Score-Spalte | Optionale Sortierung, Beschriftung „7TV global", nie vorausgewählt |
| `ImportOrigin` | Dritter Zweig `{ kind: 'seventv-channel'; channelName: string }`; **alle sechs `origin.kind`-Fundstellen einzeln entscheiden** (F6); Docstring zu `discardedRows` mitziehen |
| `ImportTargetDialog` | **`forcedScope` auf `'selection'`** (`import-target-dialog.ts:20-25`). Die Radiogruppe „Auswahl vs. alle sichtbaren" ist sinnlos, wenn im Raster bereits einzeln gewählt wurde |
| `buildImportPreview` / `ImportConfirmDialog` | Unverändert. Diff, Kapazität **und** der Kollisionshinweis (`nameCollisions`) existieren bereits — sie müssen für die neue Quelle nur genauso greifen (E7) |
| `SevenTvImportService` | **Muss angefasst werden** (F6): `reportImported` sendet den Quellnamen heute nur für `kind === 'channel'` und schickte für die neue Quelle `null` — 400 nach erfolgter Mutation |
| `emote-admin.service.ts` | `sourceKind`-Union um die dritte Vokabel erweitern (Typfehler, fällt beim Build auf) |
| Wiederverwendet, nicht anfassen | `ImportRow`, `ImportSource`, `dedupeImportRows`, `startImportFlow`, `SevenTvRunEngine`, Token-Prompt |
| `truncated: true` | Wird in der Oberfläche gesagt, nicht verschluckt (F3) |

Das Schreiben läuft weiterhin **vollständig im Browser** über den 7TV-Token aus dem
`sessionStorage`; das Backend sieht ihn nie. Diese Spec fügt eine **Lese**fähigkeit hinzu, keine
Schreibfähigkeit.

---

## 8. Aufgaben in Reihenfolge

Jeder Task läuft als eigener Subagent mit frischem Kontext (globale Regel).

**Nach Codex neu geschnitten.** Die erste Fassung trennte die 429-Erkennung (T2) vom Parser und
Zustands-Mapping (T1), obwohl T1 ohne sie den teuersten Fehler der Spec nicht vom Leerzustand
unterscheiden kann — die beiden Tasks hätten sich gegenseitig blockiert. Und T6 baute etwas, das
es schon gibt.

| # | Task | Warum diese Position |
|---|---|---|
| T1 | Endpunktbereich, Auflösungskette (F1/F2), paginierter v4-Set-Abruf inkl. `truncated` (F3), **429-Erkennung aus `extensions.status`**, Zustands-Mapping, die vier neuen Fehlercodes samt Locales | Alles Weitere hängt an der Antwortform. Die 429-Erkennung gehört hierher, weil sie Teil des Parsers ist: ohne sie ist ein Fehlschlag nicht vom Leerzustand zu trennen |
| T2 | Härtung: Cache, Koaleszierung, Breaker (E4), providerweites Budget (E5), Telemetrievertrag (Abschnitt 6) | Setzt auf T1s Parser auf. Muss **vor** der UI stehen: eine Oberfläche, die ungehärtet gegen 7TV läuft, ist genau der verworfene Ansatz A |
| T3 | Auswahlraster (neu, F4) samt Specs | Größter Frontend-Brocken, unabhängig von T1/T2 |
| T4 | Verdrahtung in den Import-Flow: dritter `ImportOrigin`-Zweig, alle sechs `origin.kind`-Fundstellen (F6), dritte `SourceKind`-Vokabel an **allen drei** Backend-Stellen (F5), `forcedScope`, Regressionsfall für `nameCollisions` (E7) | Braucht T1 und T3 |
| T5 | Score-Spalte samt Beschriftung und Übersetzungen (P5') | Aufsatz auf T3 |

**Abhängigkeiten:** T1 → T2 (Parser). T1 + T3 → T4. T3 → T5. **Parallel möglich:** T3 gegen
T1/T2, sobald die Antwortform aus Abschnitt 4 steht. Ein Task ist erst abgenommen, wenn er für
sich prüfbar ist — T1 also inklusive 429-Fall.

**Vor dem Merge:** `/codex:review --model gpt-5.6-sol` über das fertige Arbeitspaket (Regel 22).
Den Merge fährt der Nutzer.

---

## 9. Akzeptanzkriterien

Nummeriert, pass/fail.

1. Ein eingeloggter Nutzer **ohne jede Rolle** im Quellkanal erhält dessen Emote-Liste über
   `GET /api/seventv/channels/{name}/emotes` mit HTTP 200.
2. Derselbe Nutzer wählt **einzelne** Emotes im Raster und überträgt nur diese; die nicht
   gewählten erscheinen nicht in `toAdd`.
3. Das Ziel stammt aus `listMine()`; ein ungetrackter Zielkanal ist nicht wählbar.
4. Ein Set mit mehr als 500 Einträgen wird vollständig geladen.
5. Wird die Seitendecke erreicht, meldet die Antwort `truncated: true` mit `totalCount`, und die
   Oberfläche sagt es. **Kein stilles Kürzen** (F3).
6. Zweites Öffnen desselben Quellkanals innerhalb von 60 s erzeugt **null** weitere 7TV-Abrufe;
   `refresh=true` erzeugt genau einen Satz.
7. Ein 7TV-429 (HTTP 200 mit `extensions.status: 429`) erzeugt eine erklärende Meldung, **nicht**
   ein leeres Set. Das ist der teuerste Einzelfehler der Spec.
8. Ein erkanntes 429 öffnet den Breaker **sofort**, nicht erst nach fünf Fehlern, und die
   Offenzeit folgt einem vorhandenen `Retry-After`/Reset-Hinweis statt starrer 60 s (E4).
9. Das providerweite Budget greift **über Nutzergrenzen hinweg**: mehr als zwei gleichzeitige
   Fremdset-Abrufe warten, statt zusätzlich gegen 7TV zu laufen (E5).
10. Die vier neuen Fehlerzustände haben je einen Code in `ApiErrorCodes.cs`, in `api-error.ts` und
    in **beiden** Locales. Der Leerzustand „aktives Set mit 0 Emotes" bekommt **keinen**.
11. Der Pfad ruft `ResolveTwitchUserIdAsync` / `GqlUsersQuery` **nicht** auf (F1).
12. Ein Import aus einem Fremdkanal erreicht `/sync-imported` mit **gesetztem**
    `sourceChannelName` und wird nicht mit 400 abgewiesen — geprüft **nach** einem erfolgreichen
    Mutationslauf (F6).
13. Derselbe Import erscheint im Audit-Log **mit** Herkunft und Kanalnamen, nicht als nackte
    Emote-Zahl (F5).
14. Die neue `MapGroup` und ihre Filterreihenfolge haben ihren Fall in `tests/EmotePurge.Api.Tests`
    (Regel 11): 401 anonym, 200 eingeloggt ohne Rolle, 400 bei ungültigem Namen mit Budget, **429
    bei ungültigem Namen über Budget** — der Vorrang des Limiters vor dem Namensfilter ist Teil
    des Vertrags (Abschnitt 4).
15. Der neue 7TV-Pfad erscheint als eigene Call-Source in `/api/admin/rate-limits`, und ein
    HTTP 200 mit `extensions.status: 429` erscheint dort als **Rate-Limit-Ereignis**, nicht als
    Erfolg. Je Upstream-Request genau **eine** Beobachtung.
16. Die Score-Spalte ist in beiden Locales als netzwerkweit erkennbar und **nicht** vorausgewählt.
17. Der bestehende Kollisionshinweis (`nameCollisions`) greift für die neue Quelle genauso wie für
    die beiden alten und blockiert nichts (E7, Regression — nicht neu zu bauen).
18. **Kein Worker-Code angefasst, kein zusätzlicher Channel-Join, keine Migration** — das
    Messfenster aus Epic #118 bleibt unberührt.
19. Gates grün: `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`,
    `npm --prefix web run e2e`, plus `node scripts/coverage-local.mjs` vor dem PR.
20. Live gegen echte 7TV-/Twitch-Zugänge verifiziert (Regel 16), mindestens: ein Kanal mit großem
    Set, ein Kanal ohne 7TV-Konto, ein nicht existierender Kanal — und ein vollständiger
    Import-Durchlauf, damit AK 12 nicht nur in Mocks gilt.

---

## 10. Testpyramide

| Ebene | Was | Anzahl |
|---|---|---|
| Unit (`Infrastructure.Tests/Unit/`) | Breaker-Policy: sofortiges Öffnen bei 429, Fünferschwelle sonst, `Retry-After` schlägt die 60 s, Probe, Schließen, Rückfall (E4) | +7 |
| Unit | Zustands-Mapping der Auflösungskette auf die sechs Zustände aus Abschnitt 5, inkl. `Ok`+`EmoteSetId is null` (F2) | +6 |
| Unit | v4-Antwort-Parser: Paginierung, `truncated` bei Deckenkontakt, `extensions.status: 429` bei HTTP 200 klar getrennt vom Leerzustand | +5 |
| Integration (`Infrastructure.Tests/Integration/`) | Cache und Koaleszierung gegen echtes Redis (Testcontainers): TTL, `refresh=true`, parallele Abrufe teilen einen Upstream | +4 |
| Integration | Providerweites Budget: zwei Nutzer mit **verschiedenen** Quellkanälen überschreiten die Nebenläufigkeitsgrenze nicht (E5) | +2 |
| Integration | Telemetrie: genau eine Beobachtung je Upstream-Request, neue Call-Source, `extensions.status: 429` als Rate-Limit-Ereignis | +3 |
| `Api.Tests` | Filter-Matrix der neuen `MapGroup`: 401/200/400/429 **inklusive 429-vor-400 über Budget** (Regel 11, Abschnitt 4) | +6 |
| `Api.Tests` | `SourceKind`-Vertrag: `"seventv-channel"` akzeptiert, Kind/Name-Abgleich in beiden Richtungen, unbekannte Vokabel abgelehnt (F5) | +4 |
| `Infrastructure.Tests` | Audit-Rendering für `"seventv-channel"` behält die Herkunft (F5, Punkt 3) | +2 |
| Vitest (`web/`) | Raster-Verhalten: Einzelauswahl, Shift-Bereich, Auswahl überlebt Sortierwechsel; Score-Sortierung nicht vorausgewählt; `truncated`-Meldung | +9 |
| Vitest | **F6-Wire:** `reportImported` sendet für `seventv-channel` Kind **und** Quellnamen; Bestätigungsdialog zeigt die Kanal-Herkunft, nicht die Datei-Herkunft; `nameCollisions`-Regression für die neue Quelle | +5 |
| Playwright E2E | Ein Durchlauf Quelle → Auswahl → Ziel → Bestätigung mit gemocktem `/api/**`; ein Fehlerfall (429) | +2 |

Nur **Verhalten**, keine Vorlage (Regel 12): keine CSS-Klassen, keine Tailwind-Ketten, keine
Snapshots. Übersetzungswortlaut darf eine Meldung identifizieren, ist aber nicht der
Prüfgegenstand.

**Coverage:** T1–T3 legen überwiegend **neue** Dateien an — dort ist `coverage-local.mjs` nah an
Sonars Messung, und die 80-%-Schwelle auf neuem Code beißt (`analyze` ist required check). Vor dem
PR laufen lassen, aber keine Alibi-Tests schreiben.

---

## 11. Aufwand

| Task | Mensch | CC |
|---|---|---|
| T1 Endpunkt, Auflösungskette, Set-Abruf, 429-Erkennung, Zustände | ~5 h | ~30 min |
| T2 Härtung inkl. providerweitem Budget und Telemetrievertrag | ~6 h | ~35 min |
| T3 Auswahlraster | ~5 h | ~30 min |
| T4 Verdrahtung, dritte Vokabel (3 Backend-Stellen), sechs `origin.kind`-Fundstellen | ~4 h | ~25 min |
| T5 Score-Spalte | ~2 h | ~15 min |
| Tests über alle Ebenen (+53) | ~6 h | ~35 min |
| Live-Verifikation (Regel 16) | ~1 h | — |
| **Summe** | **~29 h** | **~3 h** |

Gegenüber der ersten Fassung (~25 h): T6 entfällt (E7 existiert), aber E4/E5 und der
Telemetrievertrag kosten mehr, als der gestrichene Task einbringt.

---

## 12. Rollback

Keine Migration, kein Worker, kein persistierter Zustand außer dem 60-s-Redis-Cache (läuft von
selbst ab). Ein Revert des PR entfernt den Endpunkt und die dritte Quelle im Import-Dialog.

**Eine Nachwirkung bleibt:** bereits geschriebene Audit-Zeilen tragen `sourceKind:
"seventv-channel"` dauerhaft — Audit-Zeilen sind write-once. Nach einem Revert fiele der
Renderer für diese Zeilen wieder auf den nackten `EmoteCount`-Zweig zurück (F5, Punkt 3). Das ist
Anzeigequalität, kein Datenverlust, und es ist der Grund, warum die Vokabel vor dem ersten
Produktionslauf feststehen muss (E6) und nicht später umbenannt wird.

---

## 13. Dateireferenz

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` | **neu** — die `MapGroup` aus Abschnitt 4 |
| `src/EmotePurge.Api/RateLimiting/RateLimitingOptions.cs` | Policy `ForeignEmoteLookup` (E5) |
| `src/EmotePurge.Api/RateLimiting/RateLimitPolicyNames.cs` | Name der Policy |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | vier neue Codes (Abschnitt 5) |
| `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs:136-138` | Vokabelmenge um `"seventv-channel"` (F5.1) |
| `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs:155` | Kind/Name-Abgleich bleibt, wird getestet (F5.2) |
| `src/EmotePurge.Core/Services/` | Interface für den neuen Dienst (Regel 4/5) |
| `src/EmotePurge.Core/SevenTv/ISevenTvApiClient.cs` | neue v4-Set-Abfrage mit `alias`, `defaultName`, `scores` |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:32-37` | Paginierungskonstanten wiederverwenden; **`:12-13` nicht anfassen** |
| `src/EmotePurge.Infrastructure/SevenTv/` | Breaker-Policy (pure), Cache, Koaleszierung |
| `src/EmotePurge.Infrastructure/Services/AuditLogQueryService.cs:145` | dritte Vokabel im Herkunftszweig (F5.3) |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` | Registrierung, einziger DI-Punkt |
| `web/src/app/core/seven-tv/import-source.ts:16-24` | dritter `ImportOrigin`-Zweig, Docstring `:26-33` |
| `web/src/app/core/seven-tv/seven-tv-import.service.ts:239-240` | **F6** — Quellname für die neue Vokabel senden, sonst 400 nach erfolgter Mutation |
| `web/src/app/core/emotes/emote-admin.service.ts:29-30` | `sourceKind`-Union erweitern (Typfehler beim Build) |
| `web/src/app/shared/seven-tv/import-confirm-dialog.ts:72,333,376` | **F6** — drei `origin.kind`-Fundstellen einzeln entscheiden |
| `src/EmotePurge.Infrastructure/Telemetry/ProviderRequestTelemetryHandler.cs` | Stilllegung per `HttpRequestOptions` für den neuen Pfad (Telemetrievertrag) |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs:77` | Beleg, warum die Call-Source nicht am Client hängen kann |
| `web/src/app/shared/seven-tv/` | neues Auswahlraster, Quellenwahl im Import-Dialog |
| `web/src/app/shared/seven-tv/import-target-dialog.ts:20-25` | `forcedScope: 'selection'` |
| `web/src/app/core/i18n/api-error.ts` | vier neue Codes in `KNOWN_API_ERROR_CODES` |
| `web/public/i18n/de.json`, `web/public/i18n/en.json` | vier Fehlertexte, Score-Beschriftung, `truncated`-Meldung, Kollisionshinweis |
| `docs/DECISIONS.md` | neue `MapGroup`, dritte Vokabel, In-Process-Grenze des Breakers (Regel 3) |
| `tests/EmotePurge.Api.Tests/` | Filter-Matrix und `SourceKind`-Vertrag |
| `tests/EmotePurge.Infrastructure.Tests/` | Unit- und Integrationsfälle aus Abschnitt 10 |

`GetChannelStateForTwitchUserAsync`, `ResolveTwitchUserIdAsync`, `GqlUsersQuery`, der bestehende
`kind: 'channel'`-Importzweig und das Raster in `usage-stats-page.*` werden **nicht** angefasst.

---

## 14. Nicht in dieser Runde

Aus dem Konzept übernommen und hier verbindlich: Worker-Änderungen, zusätzliche Joins,
kanalbezogene Beliebtheit für fremde Kanäle, persistierte Quellsnapshots, Multi-Kanal-Palette
(Ansatz D), automatische Auswahl anhand von Scores, serverseitige 7TV-Mutationen, Import in ein
Set eines **ungetrackten** Zielkanals, `channels.totalCount`, eine eigene Fremdkanal-Seite
(Ansatz C).

Dazu aus E2: keine Warnzeile, kein Melde-/Takedown-Weg.

---

## 15. Folge-Issue: 7TVs Bestenliste als dritte Quelle

Eigenes Issue, **nicht** Teil dieses Pakets, ebenfalls unter den messungsneutralen Tickets in
Epic #118. Dreistufig: (1) feste Bestenliste ohne Suche, (2) Blättern und Sortierwechsel,
(3) Suchfeld und Filter. Stufe 3 berührt 7TVs Such-Eimer und braucht eine eigene Betrachtung.

---

## 16. Risiken

| Risiko | Umgang |
|---|---|
| 7TV empfindet den Fan-out als Last | Genau dagegen ist Abschnitt 6 gebaut. Der eigene Call-Source-Eintrag macht den Verbrauch sichtbar, statt ihn zu vermuten |
| Ein 429 wird als leeres Set gelesen | Akzeptanzkriterium 7, eigener Testfall |
| Stille Kürzung bei sehr großen Sets | F3, Akzeptanzkriterium 5 |
| Herkunft geht im Audit-Log verloren | F5, Akzeptanzkriterium 13 |
| 400 nach bereits erfolgter 7TV-Mutation | F6, Akzeptanzkriterium 12. Der einzige Fehler der Spec, der nach unumkehrbarer Arbeit zuschlägt — deshalb ist die Live-Verifikation in AK 20 kein Formalismus |
| Ein 429 im Monitoring unsichtbar | Telemetrievertrag Abschnitt 6, Akzeptanzkriterium 15 |
| Breaker, Koaleszierung und Providerbudget in-process | Bewusst in Kauf genommen, eine Replica; in DECISIONS festhalten |
| Nutzer liest „7TV global" als Kanal-Beliebtheit | P5'-Bedeutungsvertrag, Akzeptanzkriterium 16. Widerlegung nicht per A/B-Test, sondern: ausliefern und schauen, ob trotzdem jemand nach Kanal-Beliebtheit fragt |

---

## 17. Zweitmeinung Codex Sol, 2026-09-09

`/codex:adversarial-review --model gpt-5.6-sol` über die erste Fassung. Verdikt
**needs-attention**, fünf Befunde, davon drei hoch. **Alle fünf einzeln im Code nachgeprüft, alle
bestätigt, alle eingearbeitet** — keiner abgelehnt, keine Schiedsinstanz nötig (es waren
Ergänzungen, keine Widersprüche zu einem Opus-Review).

| Befund | Wo eingearbeitet |
|---|---|
| **hoch** — Frontend-Wire schickt für die neue Vokabel keinen Quellnamen; 400 nach erfolgter Mutation | **F6** (neu), Abschnitt 7, T4, AK 12a, `+5` Vitest-Fälle |
| **hoch** — Per-Nutzer-Limit begrenzt keinen Provider, der eine Server-IP sieht; Breaker reagiert zu spät und zu starr auf 429 | **E4** und **E5** neu gefasst, AK 6a/6b, `+2` Integrationsfälle |
| **hoch** — Call-Source hängt am typisierten Client, semantisches 429 bleibt unsichtbar | Telemetrievertrag in Abschnitt 6, AK 12, `+3` Integrationsfälle |
| **mittel** — Deklarierte Filterreihenfolge widerspricht der Pipeline | Abschnitt 4 korrigiert, AK 11 |
| **mittel** — T1/T2 gegenseitig blockierend, T6 baut Vorhandenes | Abschnitt 8 neu geschnitten, T6 gestrichen, **E7** als Bestandsbefund umgeschrieben |

Was das über die erste Fassung sagt: die vier Fallen aus dem Konzept und die eine, die ich beim
Nachprüfen fand (F5), waren richtig — aber alle fünf lagen auf der **Backend**-Seite. Die teuerste
Falle (F6) und drei der fünf Befunde saßen dort, wo die Spec „wiederverwenden, nicht anfassen"
sagte. Eine Wiederverwendungsbilanz ist eine Behauptung und gehört genauso nachgeprüft wie eine
Änderung.
