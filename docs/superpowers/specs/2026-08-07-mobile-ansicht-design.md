# Mobile-Ansicht: Zeigermodus als Vertrag

**Datum:** 2026-08-07
**Betrifft:** `web/src/app/core/`, `web/src/app/shared/ui/`, `web/src/app/shared/emotes/`, `web/src/app/shared/seven-tv/`, `web/src/app/features/{overview,usage-stats,voting}/`, `web/src/styles.css`, `docs/UI-Designsprache.md`, `docs/DECISIONS.md`
**Backend:** nicht betroffen. Keine Migration, keine API-Änderung.

---

## 1. Ausgangslage

Die Mobile-Ansicht war beim Redesign nie Fokus. Vier Beobachtungen aus der Nutzung, plus zwei Befunde, die bei der Untersuchung dazukamen:

| # | Beobachtung | Ursache im Code |
|---|---|---|
| 1 | Nicht getrackte Kanäle brechen in der Channelzeile unschön um | `li` ist ein einziges `flex-wrap` ohne Breakpoint (`overview-page.html:55-140`); die rechte Gruppe hat kein `min-w-0`, der Hinweistext kein `truncate`. Der deutsche Satz ist mit 58 Zeichen 26 % länger als der englische, also fällt die ganze rechte Gruppe rechtsbündig auf eine zweite Zeile und bricht dort erneut |
| 2 | Die Ersatzzeile unter den Filtern ist nutzlos | Sie ist `lg:hidden` (`usage-stats-page.html:128-191`), die Sticky-Bar ist per `max-sm:static` auf dem Phone aber gar nicht gepinnt — sie scrollt weg. Gefüttert wird sie von `mouseenter`/`focus`/`click`; auf Touch bleibt nur der Klick, der zugleich selektiert |
| 3 | Der Drilldown ist auf dem Handy kaum zu treffen | Der Trigger ist 20 × 20 px (`usage-stats-page.html:428-453`) gegen 44 px Mindestgröße; die restliche 64-px-Zelle ist Selektion |
| 4 | Selektieren und Löschen ergibt auf Mobile keinen Sinn | Das 7TV-Write-Token ist nur über DevTools → Local Storage auf `7tv.app` zu bekommen (`docs/Untersuchung-7TV-Token-Login-2026-07-30.md`). Auf dem Handy gibt es die nicht |
| 5 | *(dazugekommen)* Das Sidecar zeigt beim schnellen Hovern das alte Bild zu neuen Zahlen | Das `<img>` hängt an `[ngSrc]` auf einem dauerhaft montierten Knoten; `@if (inspected(); as emote)` ist nur eine Null-Prüfung und fällt wegen des `order[0]`-Fallbacks nie auf `null`. Beim Hover wird nur das Attribut umgebunden, der Browser zeichnet weiter das alte Bitmap, die Textknoten springen synchron um. Im Repo existiert kein einziger Bild-Ladezustand |
| 6 | *(dazugekommen)* Dialoge können unerreichbar überlaufen | Weder Panel noch `DialogShell` noch Inhalt setzen `overflow-y`, während CDK den Dokument-Scroll blockiert und das Pane auf `100vh` deckelt. Betrifft Desktop und Touch |

---

## 2. Leitentscheidung

> **Kein 7TV-Schreibzugriff ohne Maus.**

Mobile ist **Lesen und Abstimmen**. Selektion, Massenlöschen und Protokoll-Reimport sind Desktop-Tätigkeiten. Damit verliert der Tap auf ein Emote seine Doppelbelegung und bedeutet eindeutig: *Detail öffnen*.

Das Gate hängt an der **Zeigerart** (`pointer: coarse`), nicht an der Viewport-Breite. Begründung: Ein halbiertes Desktop-Fenster mit Maus hat Hover, den Group-Hover-Trigger und präzise Klicks — dort ist nichts kaputt. Kaputt ist es dort, wo mit dem Finger gezeigt wird und keine DevTools existieren. Die Breite bleibt zuständig für Platzfragen (Sidecar ab `lg`), die Zeigerart für Fähigkeitsfragen.

Die Breakpoint-Frage der Channelzeile (Abschnitt 6) ist dagegen eine reine Platzfrage und hängt weiterhin an `sm`.

---

## 3. Zeigermodus als erstklassiges Gate

### 3.1 `PointerModeService`

Neuer Service in `web/src/app/core/`, die einzige Stelle im Frontend, die `matchMedia` anfasst.

- Kapselt `matchMedia('(pointer: coarse)')` als Signal (`isCoarse`).
- Hört auf `change`, damit ein angestecktes Trackpad oder die Browser-Emulation sofort greift.
- Für rein visuelles Ausblenden bleibt Tailwinds `pointer-coarse:`-Variante (im Repo bereits in Gebrauch). Wo ein Klick-Handler, ein Service-Aufruf oder ein ARIA-Attribut verschwinden muss, entscheidet das Signal im `@if`. Beides wird gebraucht: CSS kann keinen Handler entfernen.

### 3.2 Was auf `coarse` verschwindet

| Element | Ort |
|---|---|
| Selektions-Dock inkl. Mass-Delete-Panel | `usage-stats-page.html:586-643` |
| Mass-Delete-Panel (inline) | `vote-session-detail-page.html:149-158` |
| Restore-Panel (Protokoll-Reimport) | `usage-stats-page.html:578-583` |
| „alle auswählen" im Band `dead` | `usage-stats-page.html:321-329` |
| Selektionsverhalten der Zelle (`aria-pressed`, Shift-Range, Auswahl-Wash) | beide Seiten |
| Ersatzzeile unter den Filtern | zusätzlich `pointer-coarse:hidden` zum bestehenden `lg:hidden` |

Token-Dialog, Lösch-/Restore-Bestätigung und Vote-Session-Anlage brauchen **keine eigene Sperre** — sie hängen alle an einem dieser Einstiege und sind damit unerreichbar.

Die Slot-Budget-Leiste (`usage-stats-page.html:208-214`) **bleibt**: sie informiert, sie schreibt nichts. Ihr `pendingRemoval` ist auf Touch konstant 0.

### 3.3 Vote-Session-Erstellung

Der Button „Vote-Session erstellen" (`usage-stats-page.html:622-638`) ist per `ngProjectAs` ins Dock eingeschossen und verschwindet mit ihm. Das ist beabsichtigt: Das Kuratieren einer Session — Zeitraum wählen, Emotes durchgehen — ist Bildschirmarbeit. Das **Abstimmen** danach bleibt mobil voll nutzbar.

Damit gibt es auf Touch keinen verbleibenden Zweck für die Selektion, und sie kann vollständig entfallen statt einen Auswahlmodus zu brauchen.

### 3.4 Zustandshygiene

Ein `effect()` leert die `ListSelection` (`shared/selection/list-selection.ts`), wenn `isCoarse()` von `false` auf `true` kippt. Ohne das bliebe eine im Desktop-Fenster getroffene Auswahl unsichtbar bestehen und tauchte beim Zurückwechseln wieder auf.

### 3.5 Kein Hinweistext auf Touch

Es wird **nicht** angezeigt, dass Löschen am Rechner passiert. Visuell fehlt nichts, und ein Dauer-Hinweis für einen Fall, den die meisten nie suchen, widerspricht der Zurückhaltungsregel („braucht das der Erstbesuch?"). Wer die Funktion vom Desktop kennt, findet sie dort wieder.

---

## 4. Tap ist Detail

Auf `coarse` verliert die Zelle ihre Doppelbelegung. Der 20-px-Trigger oben links entfällt ersatzlos; die **ganze Zelle** öffnet den Drilldown — 64 px im Usage-Atlas (`ATLAS_CELL_PX`), 96 px im Ballot unter 600 px Containerbreite (`CELL_NARROW_PX`). Beide deutlich über 44 px.

**Usage-Atlas** (`usage-stats-page.html:354-453`): Der Selektions-Button bleibt derselbe `<button>` und tauscht nur die Bedeutung — `aria-pressed` entfällt, `aria-label` wird zum Drilldown-Label, `(click)` geht auf `openDrilldown`. `(mousedown)` mit dem Shift-Guard entfällt.

Der **Roving-Tabindex und `onAtlasKeydown` bleiben unangetastet.** Sie kosten auf Touch nichts und sind auf `fine` weiterhin der Tastaturpfad. Auf `fine` gilt wie heute: Enter öffnet den Drilldown (mit `preventDefault`, damit der native Klick nicht selektiert), Space selektiert. Auf `coarse` öffnen **beide** den Drilldown — der Enter-Zweig behält seinen `preventDefault`, sonst feuerte zusätzlich der native Klick und der Dialog ginge doppelt auf.

**Ballot** (`vote-session-detail-page.html:218-264`): Die Sprite-Fläche ist heute nur Klickziel, wenn `canSelectForDelete()`. Auf `coarse` tritt an dessen Stelle `hasUsageData()` — dieselbe Bedingung, die schon heute den Drilldown-Trigger gatet. Ohne Usage-Daten bleibt die Fläche wie bisher tot (kein `role`, kein `tabindex`).

Der Vote-Strip (Keep/Delete) ist davon unberührt und wächst unter 600 px Containerbreite bereits auf 44 px (`STRIP_NARROW_PX`).

---

## 5. Bottom-Sheet auf Touch

Der Drilldown wird auf Touch von der Nebenrolle zur **einzigen** Detailansicht. Er erscheint dort als Bottom-Sheet.

**CDK-Dialog bleibt der einzige Overlay-Stack.** Ein zweites Overlay-System würde dauerhaft synchron gehalten werden müssen; das ist der Preis, den dieser Entwurf vermeidet. Was sich ändert, ist das Pane, die Shell und ein neues Verhaltensbauteil.

### 5.1 Pane-Wahl

`openAppDialog()` (`shared/ui/dialog.ts:32-43`) liest den `PointerModeService` und wählt beim Öffnen:

- `fine` → `panelClass: 'app-dialog-panel'`, zentriert, wie heute.
- `coarse` → `panelClass: 'app-sheet-panel'` plus eine unten verankerte `GlobalPositionStrategy`.

Eine Stelle, alle Dialoge. Der Modus wird **beim Öffnen** eingefroren; ein Moduswechsel bei offenem Dialog wird nicht behandelt (praktisch nicht auslösbar, und der Dialog ist per Escape/Backdrop jederzeit zu schließen).

`.app-sheet-panel` in `styles.css`, neben den bestehenden `.app-dialog-*`-Regeln und wie diese unlayered und doppelt qualifiziert (`.cdk-overlay-pane.app-sheet-panel`, Spezifität 0,2,0): volle Breite, `max-width: none`, `max-height: 85dvh`.

### 5.2 `DialogShell`-Sheet-Modus

`shared/ui/dialog-shell.ts:22-39` bekommt einen Sheet-Modus:

- Nur oben abgerundet, unten bündig.
- Griffbalken (36 × 4 px, `aria-hidden`) über dem Header-Slot.
- Einfahr-Animation von unten; `prefers-reduced-motion` überspringt sie.

### 5.3 Scroll-Container — beide Modi

Der Inhaltsbereich der Shell bekommt `overflow-y: auto` und `overscroll-behavior: contain`; das Panel bekommt eine `max-height` — im Sheet-Modus `85dvh`, im zentrierten Modus `calc(100dvh - 2rem)`. **Das gilt auch im `fine`-Modus** — Befund 6 ist kein Mobile-Thema, sondern ein bestehender Bug, der bei kurzem Browserfenster genauso zuschlägt.

Der Schließen-Button geht auf 44 px Mindesthöhe, und zwar über `min-h-11` an der Größe `lg` in `shared/ui/button.ts:55` statt punktuell am Dialog. Blast-Radius: alle `buttonSize="lg"`-Vorkommen werden mindestens 44 px hoch. Das ist beabsichtigt — `lg` ist im Bestand genau die Größe der handlungstragenden Buttons, und §7.1 der Designsprache fordert 44 px bereits für Popover-Zeilen.

### 5.4 `SheetDragDirective`

Neues Bauteil in `shared/ui/`, aktiv nur im Sheet-Modus.

- Pointer-Events (`pointerdown`/`pointermove`/`pointerup`/`pointercancel`) mit `setPointerCapture`, damit die Geste das Element verlassen darf.
- Die Geste **startet nur**, wenn der Druckpunkt auf dem Griff liegt **oder** der Scroll-Container auf `scrollTop === 0` steht. Sonst scrollt der Nutzer Inhalt, und das Sheet darf nicht mitwandern.
- Während der Geste: `transform: translateY(max(0, dy))`. Kein Widerstand nach oben.
- Beim Loslassen entweder entlassen oder zurückfedern; `prefers-reduced-motion` macht beides ohne Animation.
- Entlassen ruft `dialogRef.close()`. **Kein neuer Schließweg** — Backdrop-Tap und Escape bleiben unverändert die anderen beiden.

Die Entscheidung „entlassen oder zurück?" liegt als **reine Funktion** daneben:

```ts
shouldDismiss(dy: number, velocityPxPerMs: number): boolean
```

Damit ist sie ohne DOM testbar — dieselbe Trennung, die im Worker `ReconnectPolicy` und `TwitchWatchdogPolicy` von ihren Transportklassen trennt.

Startwerte als benannte Konstanten neben der Funktion: **Weg ≥ 96 px** *oder* **Geschwindigkeit ≥ 0,5 px/ms** entlässt. Beim Live-Test auf einem echten Gerät nachjustiert; die Spec-Fälle prüfen das Verhalten der Funktion, nicht die konkreten Zahlen.

---

## 6. Channelzeile

`overview-page.html:55-140`:

- `li` wird `flex-col sm:flex-row`.
- Die rechte Gruppe verliert unterhalb `sm` ihr `ml-auto` (wird `sm:ml-auto`) und bekommt `min-w-0`.
- Unterhalb `sm`: Zeile 1 = `#name` + Live-Status, Zeile 2 = Rollen · Aktion, **linksbündig** statt rechtsgedrängt.
- Ab `sm` bleibt alles wie heute.

Der Umbruch ist damit Absicht statt Unfall, alle Zeilen sehen gleich aus, und lange Kanalnamen oder Rollenketten können nichts mehr kaputtmachen. Der Hinweistext `overview.notTrackedYet` bleibt **unverändert** — er passt jetzt.

---

## 7. `EmoteSprite`

Neue Komponente in `shared/emotes/`. Behebt Befund 5 strukturell statt an drei Stellen von Hand.

- Inputs: URL, Kantenlänge (alle sechs Aufrufstellen sind quadratisch — 64, 96, 56, 56, 28, 40 — ein Wert genügt für die von NgOptimizedImage geforderten `width`/`height`), `alt`, optionaler Dimm-Zustand (für `isArchived` auf der Voting-Seite).
- Vereinheitlicht nebenbei `disableOptimizedSrcset`, das heute an zwei von sechs Stellen steht und mangels konfiguriertem `IMAGE_LOADER` ohnehin wirkungslos ist.
- Hält den zuletzt **geladenen** URL in einem Signal. Das `<img>` ist verborgen, solange `(load)` nicht für den *aktuellen* URL gefeuert hat; bei `(error)` bleibt es verborgen.
- Sichtbar ist bis dahin die `app-sprite-cell`-Fläche (`styles.css:495-497`), die heute nie zu sehen ist, weil das alte Bild sie deckt.
- Behält `ngSrc`/NgOptimizedImage bei — der Rest des Repos nutzt es ebenfalls.

Ersetzt alle sechs Sprite-Stellen (Atlas-Zelle, Ballot-Zelle, beide Sidecars, mobile Readout-Zeile, Drilldown-Header). Betroffen vom Fehler sind zwar nur die drei mit persistentem Knoten, aber eine einzige Sprite-Komponente ist billiger zu pflegen als drei Sonderfälle plus drei Normalfälle.

Randnotiz zur Diagnose: Alle Aufrufstellen fordern **dieselbe URL** an (serverseitig fest `2x.webp`, `SevenTvEmoteJsonMapper.cs:21-32`, und mangels konfiguriertem `IMAGE_LOADER` ist die srcset-Erzeugung ohnehin aus). Der Browser-Cache greift also, sobald ein Sprite einmal geladen ist. Der Fehler tritt genau im Fenster davor auf — und der Atlas ist virtualisiert und lazy, also ist dieses Fenster real.

---

## 8. Ausdrücklich nicht im Umfang

| | Begründung |
|---|---|
| **Drilldown-Dialog entfernen** | Auf der Voting-Seite ist er die **einzige** Quelle für Kurve, Peak, Live-Tage und First/Last-Used — die Seite ruft keinen Usage-Endpunkt auf. `firstUsedDate` existiert zudem nur in `/usage-stats/daily`, weder `/totals` noch `/series` liefern es. Und er nur auf Touch existieren zu lassen ergäbe ein Bauteil mit einer einzigen Plattform |
| **Sidecar um Y-Achse und Live-Tage-Legende anreichern** | Rollenteilung bleibt scharf: **Sidecar = Einordnung** (Rang, Band, Anteil, Beobachtungs-Badge — hat der Dialog alle nicht), **Drilldown = Verlauf**. Bei 48 px Kurvenhöhe bringt eine Achsenbeschriftung wenig |
| **Ersatzzeile ganz entfernen** | Am schmalen Desktop-Fenster (640–1024 px, Maus) tut sie, was sie soll — Hover füttert sie, die Sticky-Bar klebt dort. Kaputt ist sie nur auf Touch |
| **Bottom-Sheet als eigener Overlay-Stack** | Zwei Overlay-Wege müssten dauerhaft synchron gehalten werden. Das Sheet ist eine andere Erscheinung desselben Dialogs, kein zweites System |
| **Backend** | Keine API-Änderung, keine Migration |

---

## 9. Absicherung

**Vitest** (Regel 12 — neue Services/Utilities in `core/` und `shared/` bekommen einen co-located Spec):

- `PointerModeService` gegen ein `matchMedia`-Mock, inklusive `change`-Ereignis.
- `shouldDismiss` als reine Funktion: unter der Wegschwelle, über der Wegschwelle, unter der Wegschwelle aber schnell, negatives `dy`.
- `EmoteSprite`: URL-Wechsel ⇒ Bild verborgen, bis `load` für den neuen URL feuert; `error` ⇒ bleibt verborgen.

**Playwright** (`/api/**` gemockt, wie im Bestand):

- Ein Kontext mit `hasTouch` — Dock, Mass-Delete-Panel und Restore-Panel sind abwesend; ein Tap auf eine Atlas-Zelle öffnet das Sheet; das Sheet lässt sich per Backdrop-Tap schließen.
- Die Drag-Geste selbst wird **nicht** per E2E geprüft — die Entscheidung liegt in `shouldDismiss` und ist dort abgedeckt; die Pointer-Mechanik wird live verifiziert.

**Audit-Harness** (`web/e2e/audit/ui-audit.audit.ts`, rendert bereits auf 360 × 800): Fälle für das Sheet und die zweizeilige Channelzeile.

**Live-Test** (Regel 16 sinngemäß fürs Frontend): Drag-to-dismiss auf einem echten Gerät, weil Schwellen und Momentum sich im Emulator nicht ehrlich beurteilen lassen.

---

## 10. Dokumentationspflichten

- **`docs/DECISIONS.md`** im selben Commit (Regel 3): Der Zeigermodus wird zum erstklassigen Gate — das ist ein neuer Vertrag, kein Detail. Ebenso die Begründung, warum das Signal die Zeigerart und nicht die Breite ist.
- **`docs/UI-Designsprache.md`**: §7 (Dialoge) bekommt das Sheet als zweite Erscheinung desselben Overlays. §7.1 beschreibt heute den 20-px-Drilldown-Trigger, der auf Touch entfällt.
- **i18n**: Ein neuer Key für das `aria-label` des Griffbalkens, sofern der Griff nicht `aria-hidden` bleibt. Neue Codes brauchen nach Regel 7 Einträge in **beiden** Locale-Dateien.

---

## 11. Reihenfolge

1. `PointerModeService` + Spec — alles Weitere hängt daran.
2. Scroll-Container und `min-h-11` in `DialogShell` — eigenständiger Bugfix, unabhängig vom Rest, sofort nützlich.
3. `EmoteSprite` + Spec, sechs Aufrufstellen umgestellt — ebenfalls unabhängig.
4. Channelzeile — unabhängig, klein.
5. Gate in `usage-stats` und `voting`: Dock, Panels, Selektionsverhalten, Ersatzzeile.
6. Tap-ist-Detail in beiden Zellen.
7. Sheet: Pane-Wahl, Shell-Modus, `SheetDragDirective` + `shouldDismiss`.
8. Doku-Einträge.

Die Schritte 2, 3 und 4 sind eigenständig und können vorab landen. Ab Schritt 5 hängt alles an Schritt 1.
