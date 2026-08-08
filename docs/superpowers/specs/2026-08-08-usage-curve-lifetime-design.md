# Nutzungskurve sagt nur, was sie weiß — Design

**Stand:** 2026-08-08 · **Status:** bestätigt am Prototyp (`web/public/prototype-usage-curve.html`),
Umsetzung geplant

Der Prototyp führt die hier vorgeschlagene Logik aus — die dortigen Kurven sind gerechnet, nicht
gezeichnet. Beim Bauen kam heraus, dass „An 1 von 1 Live-Tagen" grammatisch schief ist; die
`.one`-Formen unten sind daraus entstanden.

## Ziel

Kurve und Beschriftung sprechen über den Zeitraum des **Channels**, ihre Aussagekraft endet aber an
der Lebenszeit des **Emotes**. Am Drilldown von `GAMBA` beobachtet (2026-08-07): Emote am selben Tag
ins Set gekommen, Zeitraum 26.07.–07.08. Die Kurve läuft zwölf Tage flach auf 0 und steigt am
letzten Tag auf 1, daneben steht „Live an 12 von 13 Tagen". Beides liest sich als „13 Tage lang
praktisch ungenutzt" — tatsächlich gab es das Emote zwölf dieser Tage nicht.

**Die tragende Begründung steht schon im eigenen Code.** `usage-stats-page.html:198-205` blendet
genau dann eine Warnung ein, wenn der gewählte Zeitraum vor den Trackingbeginn zurückreicht, und
begründet das im Kommentar so: *„a silently empty leading stretch reads as ‚this emote is dead'
instead of ‚we weren't counting yet'"*. Dasselbe Argument gilt eine Ebene tiefer für jedes einzelne
Emote, ist dort aber nie angewendet worden. Der Modus der Seite ist Operate — sie ist Werkzeug für
genau eine Entscheidung („kann das weg?"), und eine Nulllinie, die eine ungemessene Abwesenheit
behauptet, verfälscht diese Entscheidung in die gefährliche Richtung.

Zweitens: die Zeile „Live an 12 von 13 Tagen" ist **für jedes Emote identisch**. Sie beantwortet
eine Frage über den Stream, nicht über das Emote, steht aber an einem Emote. Und ihr Nenner ist ein
Überclaim, s. „Der Nenner muss weg" weiter unten.

## Vorab geprüft: `firstSeenAt` trägt

Die offene Frage aus der Notiz vom 2026-08-07 war, ob `firstSeenAt` für Emotes taugt, die schon vor
dem Trackingbeginn im Set waren — zeigte es dort auf den Trackingbeginn, würde der Entwurf denselben
Fehler eine Ebene tiefer machen. **Tut es nicht:**

| Befund | Belegstelle |
|---|---|
| Der Wert ist 7TVs eigenes `addedAt` des Set-Eintrags, aus der v4-GraphQL-API — nicht unser Sync-Zeitpunkt | `SevenTvApiClient.cs:27-28`, `SevenTvSyncService.cs:332` |
| Jeder REST-Sync korrigiert einen abweichenden Wert nach | `SevenTvSyncService.cs:298-301` |
| Kein Pfad stempelt den Sync-Zeitpunkt ein. Die einzige Systemzeit-Stelle ist das EventAPI-`push`, und ein `push` **ist** der Moment des Hinzufügens | `SevenTvSyncService.cs:156-161` |
| Scheitert der v4-Lookup, bleibt `null` statt „jetzt" | `SevenTvApiClient.cs:96-107` |
| Der Vorgänger-Wert (v3-`timestamp`) war das **Upload**-Datum und ist per Migration global auf `null` zurückgesetzt worden | `20260803110452_ResetMisattributedFirstSeenAt.cs:18`, `DECISIONS.md:642-652` |

Der Rest-Fall ist `null` = „7TV hat nichts geliefert" (darunter alle archivierten Zeilen nach der
Reset-Migration). `Emote.cs:23-25` schreibt fest: *„consumers must read that as ‚unknown', never as
‚new'."* Dieser Entwurf hält sich daran — bei `null` wird nichts verkürzt und nichts behauptet.

## Entschiedene Punkte

| Frage | Entscheidung |
|---|---|
| Vorlauf im Graphen | Die Linie beginnt später; keine Ersatzfläche, keine Schraffur |
| x-Achse | bleibt der Channel-Zeitraum |
| Live-Bänder | laufen unverändert über die ganze Breite |
| Zeile pro Emote | wird ersetzt, nicht entfernt — sie sagt künftig etwas Emote-Spezifisches |
| Channelweite Live-Zahl | einmalig an der Tracking-Zeile, ohne Nenner |
| Backend | unberührt, keine Migration |

## 1. Die Linie beginnt, wenn das Emote beginnt

`toPolylinePoints` bekommt einen vierten, optionalen Parameter `drawFrom` (ISO-Tag,
`yyyy-MM-dd`), und die Sparkline ein gleichnamiges Input. Punkte mit `date < drawFrom` werden
**nicht gezeichnet**; ohne `drawFrom` bleibt alles beim heutigen Verhalten.

Der Name sagt, was die Funktion tut, nicht warum — der Grund (`firstSeenAt`) bleibt beim Aufrufer.
Das hält die Komponente frei von Domänenwissen; im Brief hieß der Parameter noch `startsAt`.

**Die x-Positionen der gezeichneten Punkte dürfen sich dabei nicht verschieben.** `stepX` bleibt
`width / (points.length - 1)` über das **volle** Array, und jeder Punkt behält seinen Index als
x-Faktor (`usage-series.ts:82-88`). Nur der `.map()` wird zu „ab dem ersten sichtbaren Index". Sonst
wäre die Kurve gestaucht und die Live-Bänder darunter würden nicht mehr zu ihr passen — `liveBands`
rechnet mit demselben `stepX` (`usage-series.ts:118`).

**Der y-Maßstab wird nur über die gezeichneten Punkte gebildet.** In der Praxis ändert das nichts
(Vorlauf-Tage sind 0), aber es ist die richtige Regel, und der Aufrufer der y-Achsen-Beschriftung
muss denselben Maßstab sehen — dazu unten.

**Ein einzelner gezeichneter Tag braucht einen Stummel.** Eine SVG-`polyline` mit einer Koordinate
zeichnet nichts. Der bestehende Sonderfall für `points.length === 1` (`usage-series.ts:77-80`) malt
eine Linie über die **volle** Breite — hier wäre das genau die Falschaussage, die der Umbau
beseitigt. Stattdessen: bleibt genau ein Punkt sichtbar, läuft die Linie von `x - 0,5 · stepX` bis
`x + 0,5 · stepX`, auf `[0, width]` geklemmt — also genau einen Tagesschritt breit, um den Tag
zentriert, dieselbe Halbschritt-Regel, die `liveBands` für seine Bänder benutzt
(`usage-series.ts:127-128`). Beim heute hinzugefügten Emote ist das ein kurzer Strich am rechten
Rand — sichtbar, und an der richtigen Stelle.

Verglichen wird `date` als Zeichenkette. Bei `yyyy-MM-dd` ist die lexikografische Ordnung die
chronologische, und die ganze Datei rechnet ohnehin in UTC-Tagen (`rangeDates`,
`usage-series.ts:150-162`) — kein `Date.parse` pro Punkt.

Der Sonderfall „das ganze Array hat nur einen Punkt" (Ein-Tages-Zeitraum) behält sein heutiges
Verhalten, weil es dort keine falsche Spanne gibt.

### Wer `drawFrom` liefert

| Ort | Quelle | Bei fehlendem Wert |
|---|---|---|
| Sidecar (`usage-stats-page.html:516-521`) | `inspected()!.firstSeenAt` aus `EmoteUsageTotal` (`usage-stats-page.ts:387-392`) | `undefined` → wie heute |
| Drilldown von der Usage-Seite (`emote-drilldown-dialog.ts:106-111`) | `data.firstSeenAt`, wird bereits übergeben (`usage-stats-page.ts:812`) | — |
| Drilldown vom Stimmzettel | `data.firstSeenAt` fehlt dort (`vote-session-detail-page.ts:495-508`) | `undefined` → wie heute |

Jeweils `firstSeenAt?.slice(0, 10)` — die Zeitreihe rechnet in UTC-Tagen, `firstSeenAt` ist ein
Timestamp.

**Der Stimmzettel-Drilldown bleibt also bewusst unverändert.** `VoteSessionResult` trägt kein
`firstSeenAt`; es dorthin zu bringen wäre eine Backend-Änderung und ist **nicht** Teil dieser
Arbeit. Der Dialog verhält sich dort genau wie heute, was konsistent ist: er zeigt aus demselben
Grund schon jetzt keine „Im Set"-Zeile (`emote-drilldown-dialog.ts:284-287`).

## 2. Die Zeile pro Emote sagt etwas über das Emote

`usageStats.drilldown.liveDays` („Live an {{live}} von {{total}} Tagen") verschwindet. An derselben
Stelle, mit demselben grünen Legenden-Tupfer, steht künftig eine von drei Formen:

| Lage | Key | DE |
|---|---|---|
| Live-Tage vorhanden, ungenutzte darunter | `usageStats.chart.unusedOnLiveDays.{one,other}` | An {{unused}} von {{live}} Live-Tagen nicht benutzt · *one:* Am einzigen Live-Tag nicht benutzt |
| Live-Tage vorhanden, alle genutzt | `usageStats.chart.usedOnAllLiveDays.{one,other}` | An allen {{live}} Live-Tagen benutzt · *one:* Am einzigen Live-Tag benutzt |
| keine zählbaren Live-Tage, aber Bänder sichtbar | `usageStats.chart.liveLegend` | Grün: Stream war live |
| keine Live-Tage im Zeitraum | — | keine Zeile, wie heute |

Der Plural richtet sich nach `live`, nicht nach `unused` — im Deutschen wie im Englischen regiert der
Nenner das Substantiv („von 1 Live-Tag" / „von 3 Live-Tagen"). Über den bestehenden `pluralKey`
(`core/i18n/plural.ts`).

Die dritte Form ist die, die man leicht vergisst: ein Emote, das nach dem letzten Live-Tag des
Zeitraums hinzukam, hat null zählbare Live-Tage — die grünen Bänder im Graphen stehen aber trotzdem
da und brauchen weiter ihre Legende. Ohne diese Form wäre die Beschriftung genau in dem Fall stumm,
in dem sie am nötigsten ist.

### Der neue Helfer

```
liveDayCoverage(points, liveDays, drawFrom?) → { live: number; unused: number }
```

Rein, in `usage-series.ts` neben den übrigen. `points` ist das Tages-Universum und damit automatisch
auf den Zeitraum geklemmt; gezählt werden nur Punkte mit `date >= drawFrom` (ohne `drawFrom`: alle),
davon `live` = im `liveDays`-Set enthalten, `unused` = zusätzlich `useCount === 0`.

Dass der Nenner die Lebenszeit respektiert, ist der ganze Punkt: ein Live-Tag, an dem es das Emote
nicht gab, ist kein Tag, an dem es benutzt werden konnte.

### Der Nenner muss weg

Die alte Zeile setzte `total = points().length`, also die Länge des Zeitraums. Das behauptet für
jeden Tag ohne `ChannelLiveDay`-Zeile, der Stream sei offline gewesen. `ChannelLiveDay.cs:8-9`
verbietet genau diese Lesart: *„Rows only exist since the poll shipped: an absent row means ‚no
data', never ‚offline'."* Zeilen entstehen erst seit dem 300-s-Poll (`TwitchLivePollWorker.cs:95`
schreibt ausschließlich für **heute**), es gibt keinen Backfill im Repo, und der VOD-Nachtrag vom
2026-08-03 lief einmalig von Hand.

Die neue Zeile hat als Nenner nur Tage, für die wir eine Live-Zeile **haben**. Damit ist der
Überclaim weg — die Umstellung räumt einen bestehenden Fehler mit ab, sie fügt nicht nur etwas hinzu.

## 3. Die channelweite Live-Zahl wandert an die Tracking-Zeile

`usage-stats-page.html:194-197` trägt heute einen Satz. Er bekommt einen zweiten, im selben `<p>`,
nur wenn `liveDayDates().length > 0`:

> Wir zählen für diesen Channel seit dem 26.07.2026. Im gewählten Zeitraum war der Stream an 12
> Tagen live.

Neuer Key `usageStats.liveDaysInRange.{one,other}` als Geschwister von `usageStats.trackedSince`,
das selbst unverändert bleibt.

Drei Dinge sind an der Formulierung Absicht:

- **Kein Nenner** — aus demselben Grund wie oben.
- **„Im gewählten Zeitraum"** — die Tracking-Zeile ist channelfest, die Live-Zahl hängt am
  Zeitraum-Filter. Ohne diesen Einschub liest sich die Zahl als feststehende Eigenschaft des
  Channels und ändert sich beim Umschalten scheinbar grundlos.
- **Bei null Live-Tagen entfällt der Satz ganz**, statt „0" zu sagen — „0 Live-Tage" wäre wieder die
  Aussage, die wir nicht belegen können.

**Der Dialog bekommt diese Zahl nicht.** Er ist ein Modal, die Seitenzeile liegt dahinter — aber
seine eigene, emote-spezifische Zeile trägt Tupfer und das Wort „Live-Tage" und ist damit Legende
genug. Die channelweite Zahl zusätzlich ins Modal zu holen, wäre genau die Redundanz, die dieser
Umbau beseitigt.

## Berührte Dateien

Reine Frontend-Arbeit. **Kein Backend-Eingriff, keine Migration** — alle vier benötigten Daten
liegen in beiden Oberflächen schon vor: `firstSeenAt`, die Live-Tage (Sidecar als Offsets über
`liveDayDates()` in `usage-stats-page.ts:463-467`, Dialog als ISO-Daten in `series()!.liveDays`),
die Tagesreihe pro Emote und der Zeitraum.

| Datei | Änderung |
|---|---|
| `web/src/app/shared/emotes/usage-series.ts` | `toPolylinePoints` bekommt `drawFrom` samt Stummel-Regel, `seriesPeak` denselben Parameter; neuer Helfer `liveDayCoverage`. **`liveBands` bleibt unangetastet** — die Bänder laufen weiter durch (im Brief stand das noch falsch) |
| `web/src/app/shared/emotes/usage-sparkline.ts` | optionales Input `drawFrom`, an `toPolylinePoints` durchgereicht |
| `web/src/app/features/usage-stats/usage-stats-page.html` | Tracking-Zeile (`:194-197`), Sidecar-Zeile (`:534-545`), `drawFrom` am Sparkline-Aufruf (`:516-521`) |
| `web/src/app/features/usage-stats/usage-stats-page.ts` | `drawFrom`-Computed aus `inspected()`, `liveDayCoverage`-Computed, Key-Auswahl |
| `web/src/app/shared/emotes/emote-drilldown-dialog.ts` | dieselben zwei Computeds, dieselbe Zeile (`:128-139`), `drawFrom` am Aufruf (`:106-111`) |
| `web/public/i18n/de.json`, `en.json` | s. i18n |

### Die y-Achse muss mitziehen

Beide Oberflächen beschriften die y-Achse als HTML **neben** dem SVG, mit dem Peak als Obergrenze:
`inspectedPeak()` im Sidecar (`usage-stats-page.html:507-515`), `yMax()` im Dialog
(`emote-drilldown-dialog.ts:97-105`). Beide leiten aus `seriesPeak(points)` ab — über **alle**
Punkte, also auch den Vorlauf.

Das ist heute harmlos, weil der Vorlauf nur Nullen enthält und ein Maximum daran nichts ändert.
Es bleibt harmlos, aber es ist eine stille Kopplung: sobald der y-Maßstab der Kurve nur die
gezeichneten Punkte berücksichtigt, müssen Beschriftung und Kurve dieselbe Menge sehen, sonst passt
die Zahl an der Achse nicht zur Kurvenhöhe. `seriesPeak` bekommt deshalb denselben optionalen
`drawFrom`-Parameter, und beide Aufrufstellen übergeben ihn. Das ist keine Verhaltensänderung,
sondern der Schutz dagegen, dass es später eine wird.

## Zustände, die tragen müssen

| Fall | Erwartung |
|---|---|
| Emote heute hinzugefügt | kurzer Strich am rechten Rand, kein Vorlauf; Zeile nennt nur Live-Tage ab heute |
| Emote lange vor Trackingbeginn im Set | `drawFrom` liegt vor `from`, also kein sichtbarer Unterschied zu heute |
| `firstSeenAt = null` (archiviert, v4-Lookup gescheitert) | volle Kurve, voller Nenner — wie heute, keine Behauptung |
| Zeitraum ohne jede Live-Abdeckung | keine Bänder, keine Zeile |
| Emote nach dem letzten Live-Tag hinzugekommen | Bänder sichtbar, Zeile in der Form `liveLegend` |
| Zeitraum ohne jede Nutzung | `noUsage`-Satz wie heute (unverändert), Live-Zeile trägt die Aussage |
| Ein-Tages-Zeitraum | bestehender `points.length === 1`-Pfad, unverändert |
| 900er-Set im Atlas | 900 Kurven mit je eigenem `drawFrom`; gezeichnet wird nur die inspizierte, `liveDayCoverage` läuft nur für sie |
| Stimmzettel-Drilldown | unverändert, weil kein `firstSeenAt` vorliegt |

## Absicherung

**Unit (Regel 12 — reine Utilities in `shared/`):** `usage-series.spec.ts` wächst um

- `toPolylinePoints` mit `drawFrom`: Punkte davor fehlen, die x-Positionen der übrigen sind
  unverändert gegenüber demselben Aufruf ohne `drawFrom` (das ist der Test, der die Stauchung
  ausschließt); `drawFrom` vor `from` ändert nichts; `drawFrom` nach `to` ergibt eine leere Linie;
  genau ein sichtbarer Punkt ergibt den halben-Schritt-Stummel an der richtigen x-Position.
- `liveDayCoverage`: Vorlauf-Live-Tage zählen nicht in den Nenner; `unused` zählt nur Nullen;
  `drawFrom` fehlt → alle Tage; keine Live-Tage → `{live: 0, unused: 0}`.
- `seriesPeak` mit `drawFrom`.

Es gibt kein `usage-sparkline.spec.ts` und soll keines geben — isolierte Komponententests sind
bewusst nicht Teil der Konvention (Regel 12).

**E2E** in `web/e2e/usage-atlas.e2e.spec.ts`:

- Der bestehende Fall `'the curve states its scale, and the green bands say what they are'` prüft
  `/Live an 4 von \d+ Tagen/` und **muss umgeschrieben werden**.
- Neu: ein Emote, das mitten im Zeitraum hinzukam, zeichnet davor keine Linie. Prüfbar am
  `points`-Attribut der `polyline` — die erste x-Koordinate ist größer als 0. Dafür braucht
  `mockUsageTotals` in `web/e2e/support/mocks.ts` ein `firstSeenAt` mitten im gemockten Zeitraum.
- Neu: die Tracking-Zeile nennt die Live-Zahl, und nennt sie nicht, wenn der Mock keine Live-Tage
  liefert.

**Lücke, die bleibt:** kein Test deckt den `rangeBeforeTracking`-Banner ab. Das ist Bestand und
**nicht** Teil dieser Arbeit.

## i18n

| Aktion | Key |
|---|---|
| **entfällt** | `usageStats.drilldown.liveDays` |
| **zieht um** | `usageStats.drilldown.chartLabel` → `usageStats.chart.label` |
| **neu** | `usageStats.chart.unusedOnLiveDays.{one,other}` |
| **neu** | `usageStats.chart.usedOnAllLiveDays.{one,other}` |
| **neu** | `usageStats.chart.liveLegend` |
| **neu** | `usageStats.liveDaysInRange.{one,other}` |

Der Umzug von `chartLabel` ist Aufräumen im Vorbeigehen: der Key wird von Sidecar **und** Dialog
benutzt, lag aber unter `drilldown.`. Ohne den Umzug stünde der neue `chart.`-Block neben einem
Geschwister, das dorthin gehört — aus einer Schönheitsfehler-Stelle würden zwei. Kostet zwei Zeilen
in zwei Templates.

`usageStats.drilldown.range` bleibt: der Dialog hat keine Zeitraum-Toolbar über sich und nennt den
Zeitraum deshalb selbst (`emote-drilldown-dialog.ts:76`). Nur die **Sidecar**-Variante dieser Zeile
war am 2026-08-07 verworfen worden.

Der Locale-Paritätstest greift nur für `ApiErrorCodes` — hier bleibt es Disziplin, beide Dateien zu
pflegen.

## Dokumentation (Regel 3, im selben Commit)

- `docs/UI-Designsprache.md:107` — der Satz zählt auf, was der Drilldown „zusätzlich" trägt:
  „Y-Achse, Spitzensatz, Live-Tage-Legende und Erstnutzung". Seit `a2d2602` hat der Sidecar Y-Achse
  und Live-Tage-Zeile ebenfalls; der Satz ist also **bereits** überholt und wird durch diese
  Änderung falscher. Nachziehen.
- `docs/DECISIONS.md` — ein Eintrag mit den drei Begründungen: die Linie schweigt im Vorlauf statt
  Null zu behaupten; die Zeile pro Emote wird zur Kehrseite (ungenutzte Live-Tage) statt eine
  channelweite Zahl zu wiederholen; Live-Zahlen tragen keinen Nenner, weil eine fehlende
  `ChannelLiveDay`-Zeile „keine Daten" heißt.

## Nach der Umsetzung

`context.mjs` hat gemeldet, dass in dieser Session kein automatischer Impeccable-Hook läuft. Wenn
die UI steht, **einmal** — nicht früher, nicht in einer Schleife — den mechanischen Detektor über
die geänderten Dateien laufen lassen:

```
node C:\Users\admin\.claude\skills\impeccable\scripts\detect.mjs --json <geänderte Dateien>
```

Außerdem: die E2E-Suite läuft nur, wenn auf `:5151` **keine** Api lauscht (CLAUDE.md, Abschnitt
Tests). Vorher `dotnet run` beenden.

## Nicht Teil dieser Arbeit

- **Keine Schraffur und kein zweites Hintergrundmuster** für den Vorlauf. Die schweigende Linie ist
  wirksamer als jede Fläche dahinter, und ein zweites Muster in einem 100 × 40-Kasten neben den
  Live-Bändern würde zwei Hintergrundebenen mit unterschiedlicher Bedeutung übereinanderlegen.
- **Keine emote-eigene x-Achse.** Der Sidecar-Atlas vergleicht Kurven untereinander; eine je Emote
  verschobene Zeitachse würde genau das kaputt machen.
- **Keine dritte Sidecar-Zeile.** Die Zeilenzahl bleibt gleich, eine Zeile wird ersetzt. Der
  Zeitraum-Caption ist am 2026-08-07 bewusst verworfen worden und kommt nicht zurück.
- **Kein `firstSeenAt` auf `VoteSessionResult`.** Das wäre eine Backend-Änderung; der
  Stimmzettel-Drilldown behält sein heutiges Verhalten.
- **Kein Live-Tage-Backfill** und keine Änderung an `ChannelLiveDay` oder am Poll-Worker. Der Umbau
  arbeitet mit der Datenlage, die es gibt, und sagt genau das.
- **Keine Änderung am `rangeBeforeTracking`-Banner**, an den Bandfarben, an der Peak-Zeile, am
  „Neu"-Badge, an `Im Set` / `Erstmals benutzt` / `Zuletzt benutzt` oder an der Zeitraum-Auswahl.
- **Kein neues Dauer-Control.** Nichts an diesem Entwurf ist ein- oder ausschaltbar.
