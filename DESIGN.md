---
name: EmotePurge
description: Ein Leuchttisch für fremdes Bildmaterial — Graphit oder Papier, eine Leitlinie, sonst nichts.
colors:
  leuchtlinie: "#00d3bc"
  tiefes-teal: "#0a6f64"
  tiefes-teal-tiefer: "#085a51"
  markierung-flaeche: "#06231f"
  markierung-schrift: "#57e8d8"
  aufdruck: "#ffffff"
  glasplatte: "#0d0f12"
  auflage: "#15181c"
  vertiefung: "#1f232a"
  vertiefung-beruehrt: "#282d35"
  eingelassenes-feld: "#0a0c0f"
  kante: "#262b32"
  kante-betont: "#353b43"
  fassung: "#6b747e"
  schrift: "#e9ecee"
  schrift-fliesstext: "#d4d9dd"
  schrift-zweitrangig: "#b2b9c0"
  schrift-still: "#8b939b"
  schrift-gesperrt: "#565d65"
  bildtraeger: "#1f232a"
typography:
  display:
    fontFamily: "Archivo, ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "2.25rem"
    fontWeight: 800
    lineHeight: "2.5rem"
    letterSpacing: "-0.025em"
  headline:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "1.5rem"
    fontWeight: 700
    lineHeight: "2rem"
    letterSpacing: "-0.025em"
  title:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "1.125rem"
    fontWeight: 600
    lineHeight: "1.75rem"
  subtitle:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "1rem"
    fontWeight: 600
    lineHeight: "1.5rem"
  body:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: "1.25rem"
  label:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "0.6875rem"
    fontWeight: 600
    lineHeight: "1rem"
    letterSpacing: "0.13em"
  data:
    fontFamily: "Azeret Mono, ui-monospace, Cascadia Code, Courier New, monospace"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: "1.25rem"
rounded:
  sm: "0.25rem"
  md: "0.375rem"
  lg: "0.5rem"
  sheet: "1rem"
  full: "9999px"
spacing:
  "1": "0.25rem"
  "2": "0.5rem"
  "3": "0.75rem"
  "4": "1rem"
  "6": "1.5rem"
  "8": "2rem"
components:
  button-primary:
    backgroundColor: "{colors.tiefes-teal}"
    textColor: "{colors.aufdruck}"
    rounded: "{rounded.md}"
    padding: "0.375rem 0.75rem"
    typography: "{typography.body}"
  button-primary-hover:
    backgroundColor: "{colors.tiefes-teal-tiefer}"
    textColor: "{colors.aufdruck}"
  button-neutral:
    backgroundColor: "{colors.vertiefung}"
    textColor: "{colors.schrift-fliesstext}"
    rounded: "{rounded.md}"
    padding: "0.375rem 0.75rem"
  button-neutral-hover:
    backgroundColor: "{colors.vertiefung-beruehrt}"
    textColor: "{colors.schrift-fliesstext}"
  button-outline:
    textColor: "{colors.schrift-zweitrangig}"
    rounded: "{rounded.md}"
    padding: "0.375rem 0.75rem"
  button-lg:
    rounded: "{rounded.md}"
    padding: "0.5rem 1rem"
    height: "2.75rem"
  input:
    backgroundColor: "{colors.eingelassenes-feld}"
    textColor: "{colors.schrift}"
    rounded: "{rounded.md}"
    padding: "0.5rem 0.75rem"
    typography: "{typography.body}"
  badge-accent:
    backgroundColor: "{colors.markierung-flaeche}"
    textColor: "{colors.markierung-schrift}"
    rounded: "{rounded.full}"
    padding: "0.125rem 0.5rem"
  segment-selected:
    backgroundColor: "{colors.tiefes-teal}"
    textColor: "{colors.aufdruck}"
    padding: "0.375rem 0.75rem"
  segment-idle:
    backgroundColor: "{colors.vertiefung}"
    textColor: "{colors.schrift-zweitrangig}"
    padding: "0.375rem 0.75rem"
  popover-panel:
    backgroundColor: "{colors.auflage}"
    rounded: "{rounded.md}"
    width: "16rem"
  emote-cell:
    backgroundColor: "{colors.bildtraeger}"
    rounded: "0"
---

# Design System: EmotePurge

> **Abgeleitetes Dokument.** Die normativen Werte stehen in `web/src/styles.css`; hier stehen sie
> als Momentaufnahme, damit Werkzeuge sie maschinell lesen können. Wer den Tokenblock ändert,
> erzeugt dieses Dokument neu (`$impeccable document`).
>
> **Verträge, Muster und die Bauen-Checkliste stehen nicht hier**, sondern in
> [docs/UI-Designsprache.md](docs/UI-Designsprache.md) — Sticky-Ebenen und z-Leiter, der
> `h-14`-Höhenvertrag, der Stretched-Link-Kontrakt, ARIA-Muster, i18n-Pflichten, die
> Audit-Gates. Jenes Dokument ist verbindlich; dieses beschreibt die Farbwelt.

## Overview

**Creative North Star: „Der Leuchttisch"**

Diese Oberfläche zeigt fremdes Material. Mehrere hundert 7TV-Emotes je Kanal, vollgesättigt, von
anderen Leuten gezeichnet, für dunklen Chat gemacht — und dieses Werkzeug entscheidet, welche
davon gelöscht werden. Daraus folgt alles Weitere: Der Grund hält sich raus. Er ist Graphit und
nicht Slate, weil ein blaustichiger Grund jedes einzelne dieser Bilder einfärbt. Er ist im hellen
Modus Papier auf einem Tisch statt reinem Weiß, weil Flächen die größte Fläche jeder Seite sind
und reines Weiß auf hellen Displays geblendet hat.

Genau eine Farbe darf laut sein, und sie ist ein **Guide**, kein Leuchtmittel: das Türkis, das
Auswahl, Fokus und Markierung trägt. Pack- und Layoutwerkzeuge reservieren eine solche Signalfarbe
für Overlays, die nie mit dem Material verwechselt werden dürfen — hier tut sie dasselbe. Sie
zieht Haarlinien. Sie glüht nicht, und sie liegt nie unter einem Bild.

Der dritte Zug ist Zurückhaltung mit Ansage. Die Oberfläche schweigt im Normalbetrieb: keine
Karten um jede Zeile, keine Pillen für Selbstverständlichkeiten, keine Bewegung ohne Zustand. Was
gesund ist, ist still — damit das eine Ding, das gerade nicht in Ordnung ist, ohne Zutun das
lauteste Element auf dem Schirm wird. Lautstärke gibt es, aber sie ist an Unwiderruflichkeit
gekoppelt, nicht an Wichtigkeit.

**Key Characteristics:**

- Neutraler Graphit- bzw. Papiergrund, der fremdes Bildmaterial nicht einfärbt
- Eine einzige Leitfarbe, eingesetzt als Guide — Auswahl, Fokus, Markierung, nie Dekoration
- Zwei vollständige, gleichwertig gepflegte Modi; jede Farbrolle hat in beiden einen Wert
- Flächen statt Rahmen: geriffelte Zeilen, randlose Abschnitte, getönte Blöcke
- Dichte Listen bis ~900 Zeilen, Zahlen im Monospace
- Bewegung nur als Antwort auf einen Zustand

## Colors

Eine nahezu neutrale Rampe mit einer Spur Grün, dazu ein Türkis als einzige gesättigte Farbe. Die
Rollen sind in beiden Modi identisch besetzt; nur die Werte kippen.

### Primary

- **Leuchtlinie** (`#00d3bc`): Das Guide-Türkis. Fokusring, Auswahlkante, aktiver Tab, Links.
  Es markiert Stellen — es füllt keine Flächen und trägt nie Schrift, denn Weiß erreicht darauf
  nur rund 2,5:1. Im hellen Modus kann es diese Rolle nicht spielen (1,7:1 auf Weiß); dort
  übernimmt sie **Tiefes Teal**, das dieselbe Aufgabe dunkel statt hell erfüllt.
- **Tiefes Teal** (`#0a6f64`): Dieselbe Farbe als *Fläche*, überall dort, wo Schrift darauf steht:
  gefüllte Hauptaktionen, das gewählte Segment, der gedrückte Schalter. Im hellen Modus ist es
  zusätzlich die Leitfarbe selbst.
- **Tiefes Teal, tiefer** (`#085a51`): Der Hover-Schritt darunter. Siehe *Die Dunkler-Regel*.
- **Markierungsfläche / Markierungsschrift** (`#06231f` / `#57e8d8`): Getönte Fläche plus ihre
  Schrift, für hervorgehobene Eigenschaften in Badges. Zwei eigene Rollen, weil Schrift *auf* der
  getönten Fläche einen anderen Grund hat als Schrift auf einer normalen Fläche.

### Neutral

Die Rampe trägt die ganze Oberfläche. Dunkel ist sie Glas, hell ist sie Papier auf einem Tisch;
die *Richtung* bleibt identisch — erhöht entfernt sich vom Grund, eingelassen geht zu ihm zurück.

- **Glasplatte** (`#0d0f12`, hell: `#eef0f2` „Tischplatte"): der Seitengrund, auf dem alles liegt.
- **Auflage** (`#15181c`, hell: `#fafbfc` „Papierbogen"): die normale Fläche — Panels, Dialoge,
  Overlays.
- **Vertiefung** (`#1f232a`, hell: `#e5e8eb`): eingelassene Blöcke, neutrale Buttons, Skeletons.
- **Vertiefung, berührt** (`#282d35`, hell: `#dadde1`): deren Hover.
- **Eingelassenes Feld** (`#0a0c0f`, hell: `#fafbfc`): Eingabefelder. Dunkel gehen sie *unter* den
  Seitengrund, weil ein Feld in die Fläche gedrückt wirkt.
- **Kante** (`#262b32`, hell: `#d7dade`) und **Kante, betont** (`#353b43`, hell: `#bec3ca`):
  Trennlinien und die kräftigere Variante für Outline-Buttons und Overlay-Ränder.
- **Fassung** (`#6b747e`): der Rand, der ein Bedienelement als Bedienelement erkennbar macht.
  **Das einzige Farbtoken mit identischem Wert in beiden Modi** — es muss gegen hellen wie dunklen
  Grund funktionieren.
- **Schrift** (`#e9ecee` → `#14171b`), **Fließtext** (`#d4d9dd` → `#23282e`), **Zweitrangig**
  (`#b2b9c0` → `#3f464e`), **Still** (`#8b939b` → `#5d656e`), **Gesperrt** (`#565d65` →
  `#a2a9b1`): fünf Stufen. *Still* ist die schwächste zulässige Textstufe; ihr engster Fall ist
  still auf Vertiefung — hell 4,81:1, dunkel 5,06:1.

### Tertiary

- **Bildträger** (`#1f232a`, hell: `#e6e8eb`): die Platte, auf der ein Emote gezeichnet wird. Ein
  **eigenes** Token, nicht die Vertiefung, weil das Material fremd ist. Sie folgt dem Modus statt
  fest dunkel zu bleiben; bewusst in Kauf genommener Preis: ein weiß umrandetes Emote verliert im
  Hellen seine Kontur.

### Semantische Töne

`success`, `warning`, `danger` und `info` binden je Modus an Tailwinds Paletten (`emerald`,
`amber`, `red`, `blue`) und stehen deshalb **nicht** im Frontmatter — dort stünden sonst
Näherungswerte neben der echten Quelle. Jeder Ton hat bis zu fünf Rollen: `wash` (getönte Fläche),
`fg` (Schrift darauf), `solid` (gefüllte Fläche), `solid-hover`, `dot` (bedeutungstragende
Kleingrafik, die 3:1 schuldet statt 4,5:1). Der helle Modus setzt `warning-dot` als einzigen Ton
zwei Stufen dunkler, weil Amber auf hellem Grund keine Reserve hat.

### Named Rules

**Die Guide-Regel.** Die Leitfarbe markiert, sie leuchtet nicht. Sie erscheint als Haarlinie,
Fokusring, Auswahlkante und Link — nie als Verlauf, nie als farbiger Schatten, nie als Fläche
unter einem Emote. Genau eine Fläche der App trägt eine Linie in ihr: der Auswahl-Dock, und dort
markiert sie die Grenze eines lebenden, umkehrbaren Zustands.

**Die Dunkler-Regel.** Gefüllte Flächen werden im Hover **dunkler — in beiden Modi**. `*-solid-hover`
liegt immer eine Stufe unter `*-solid`. Das ist keine Optik: der Aufdruck ist in beiden Modi weiß,
ein hellerer Hover kann Kontrast also nur wegnehmen. Kein Werkzeug fängt einen Verstoß, weil ein
Hover nie gerendert geprüft wird.

**Die Rollen-Regel.** Tone-Namen sind Bedeutungen, keine Farben. Wer `red` verlangt, verlangt einen
Wert — und den gibt es nicht, weil hinter `danger` je Modus ein anderer steht. Neue Farbe heißt
neues Token mit Wert für **beide** Modi, nie ein Griff in die Palette.

## Typography

**Display/Body Font:** Archivo (mit `ui-sans-serif`, `system-ui`, `Segoe UI`, Roboto)
**Data Font:** Azeret Mono (mit `ui-monospace`, `Cascadia Code`, `Courier New`)

Beide sind Variable Fonts und werden aus `/fonts/*.woff2` selbst ausgeliefert, nicht von einem
CDN. Archivo ist eine grotesk mit engen Kurven und aufrechtem Charakter — sie trägt dichte Listen,
ohne technisch zu posieren. Azeret Mono steht daneben für **Daten**, nie als Kostüm für
„technisch".

### Hierarchie

Vier Ebenen mit festen Klassenketten, plus zwei Sonderrollen.

- **Display** (800, 2,25rem, `sm:` 3rem, `tracking-tight`): ausschließlich der Landing-Hero. Die
  öffentliche Fläche ist bewusst Marketing-skaliert und folgt der Tabelle unten nicht.
- **Headline** (700, 1,5rem, Zeilenhöhe 2rem, `tracking-tight`): Seitentitel. `<h1>` in Layouts,
  `<h2>` auf Seiten ohne eigenes Layout-`<h1>`.
- **Title** (600, 1,125rem, 1,75rem): Sektionstitel, `<h2>`.
- **Subtitle** (600, 1rem, 1,5rem): Blocktitel, `<h3>`.
- **Body** (400, 0,875rem, 1,25rem): die Arbeitsgröße. Rund zwei Drittel aller Textklassen im
  Projekt sind diese eine.
- **Label** (600, 11px, `letter-spacing: 0.13em`, versal): Kleinüberschriften über Datenpaaren und
  Bandtrennern. In der Mono-Variante zusätzlich als Stufen-Marker auf Landing und Login.
- **Data** (Azeret Mono, 0,875rem): jede Zahl, die in einer Spalte steht.

### Named Rules

**Die Vier-Ebenen-Regel.** Ein `<h3>` trägt **nie** die Sektionsgröße. Zwei Ebenen, die gleich
aussehen, sind eine Ebene. Das Heading-*Level* folgt der Dokumentstruktur, die *Optik* folgt der
Tabelle — beides unabhängig einzuhalten.

**Die Mono-für-Daten-Regel.** Monospace steht für Zahlen, IDs und Marker, nie für Fließtext und
nie, um etwas technisch aussehen zu lassen. Ausrichtung in Spalten läuft über die Schrift, nicht
über `tabular-nums` — im Projekt existiert diese Utility bewusst nirgends.

## Layout

Die Seite scrollt als **ein Dokument**, nicht als App-Rahmen mit innerem Scroll-Container. Die
Inhaltsspalte hat app-weit **eine** Breite: `max-w-5xl` (64rem), gesetzt an der Kopfzeile und an
`<main>`, mit `px-4 py-8` als Seitenrahmen.

Drei Ebenen bleiben beim Scrollen stehen und haben feste Höhen, weil `sticky` für gestapelte
Ebenen exakte Offsets braucht: Kopfzeile 3,5rem bei `top-0`, Tab-Leisten 2,5rem bei `top-14`,
Filter-Toolbars variabel bei `top-24` (= 14 + 10). Diese Zahlen sind Berechnungsgrundlage, keine
Optik.

Der Abstandsrhythmus ist knapp und wiederholt sich: Zeilen `px-3 py-3`, Abschnitte `gap-3` über
einer Haarlinie mit `pt-4`, Seitenwurzeln `gap-4` bis `gap-8`, Dialoge `p-6`. Gruppen werden mit
`gap` gesetzt, nicht mit Rändern an den Kindern.

Von den Breakpoints trägt `sm:` (640px) die Last — 62 Vorkommen gegenüber 11 für `md:` und 9 für
`lg:`. Responsives Verhalten ist strukturell: aus einer Spalte werden zwei, aus einem
Sidecar-Raster ein Stapel. Schriftgrößen bleiben fest, außer im Landing-Hero.

### Named Rules

**Die Eine-Breite-Regel.** Keine Route setzt ihre eigene Inhaltsbreite. Beim Wechsel zwischen
einer Blatt- und einer Listenseite spränge sonst der Rahmen, und ein Layout, das bei jeder
Navigation seine Breite ändert, ist unruhiger als eine Blattseite, die Platz verschenkt. Braucht
ein Blatt mehr Breite, holt es sie *innerhalb* der konstanten Spalte.

**Die Höhenvertrag-Regel.** Wer 3,5rem oder 2,5rem ändert, zieht jeden `top`- und
`scroll-mt`-Wert der App nach. Diese Höhen sind ein Vertrag.

## Elevation & Depth

**Das System ist flach.** Tiefe entsteht durch gestapelte Flächenhelligkeit, nicht durch
Schatten — Glasplatte unter Auflage unter Vertiefung, in derselben Richtung in beiden Modi.

Es gibt genau **ein** Schattentoken, und es gehört der einzigen wirklich erhöhten Fläche der App:
dem Overlay. Alles andere — Panels, Zeilen, Abschnitte, Blöcke — liegt auf der Ebene, auf der es
gezeichnet ist. Eine Kartenklasse mit Schatten existierte bis 2026-08-06 und wurde mitsamt ihrer
beiden Schattentoken entfernt.

Die Sticky-Ebenen lösen ihre Überlagerung nicht über Schatten, sondern über einen abgedunkelten
Blur: `backdrop-filter: blur(8px)` über einer teiltransparenten Seitenfarbe. Die Deckung ist selbst
ein Token — hell braucht 92 %, dunkel kommt mit 85 % aus, weil hell sonst den Text darunter
durchscheinen lässt.

### Shadow Vocabulary

- **Overlay** (`box-shadow: 0 10px 30px -12px rgb(0 0 0 / 0.6)` dunkel, `0 10px 30px -10px rgb(20 23 27 / 0.22)` hell):
  Popover-Panels und Dialoge. Der einzige Schatten im System.

### Named Rules

**Die Ein-Schatten-Regel.** Wer eine neue Fläche „absetzen" will, wählt eine andere Flächenfarbe,
keinen Schatten. Es gibt nur den Overlay-Schatten, und er ist an das Überlagern gebunden.

## Shapes

Knappe Radien, keine weichen Formen. `0.375rem` ist der Arbeitsradius für praktisch alles —
Buttons, Panels, Eingabefelder, getönte Blöcke, Skeletons. `0.5rem` bleibt dem Dialogfenster
vorbehalten, `1rem` als Oberkante dem Sheet auf Touch-Geräten, `9999px` den Badges und
Statuspunkten.

Abgrenzung entsteht bevorzugt durch Fläche und Haarlinie, nicht durch umlaufende Rahmen. Zeilen
werden durch `divide-y` getrennt und atmen mit einem negativen Außenrand über den Text hinaus,
damit der Hover-Wisch breiter ist als die Inhaltskante. Abschnitte tragen eine einzelne Linie
oben, keinen Kasten.

**Emote-Zellen haben bewusst keinen Radius.** Ein Sprite-Blatt hat keine abgerundeten Zellen; die
gerade Kante ist das, was das Raster als Bogen lesbar macht statt als Sammlung von Kacheln.

### Named Rules

**Die Keine-Karte-Regel.** Es gibt keine Kartenklasse, und es kommt keine zurück. Die Prüffrage
vor jeder neuen Abgrenzung: Eine Karte ist eine Grenze gegen einen **andersartigen** Nachbarn. Ist
jeder Nachbar dieselbe Sorte Ding, zeichnet ein Rand acht Rechtecke, wo eine Linie „Liste"
deutlicher sagt — und konkurriert mit dem Einzigen, das auffallen muss.

**Die Quadratische-Zelle-Regel.** Was Bildmaterial trägt, bleibt eckig und flach: kein Radius,
kein Alpha-Karo, kein Wash unter dem Bild. Auswahl malt als `inset-ring` **innerhalb** der
Zellfläche, damit sie das Raster nicht aufbläht.

## Components

Der Charakter ist **zurückhaltend, bis es ernst wird**: Der Normalfall ist leise, und Lautstärke
ist an Unwiderruflichkeit gekoppelt, nicht an Wichtigkeit.

### Buttons

- **Shape:** Arbeitsradius (0,375rem), zwei Größen — `md` (`0.375rem 0.75rem`) und `lg`
  (Mindesthöhe 2,75rem, `0.5rem 1rem`) als Komfortziel für Primäraktionen und Touch.
- **Primary:** Tiefes Teal mit weißem Aufdruck, halbfett. Die *eine* Hauptaktion eines Kontexts.
- **Neutral:** Vertiefung mit Fließtext-Schrift. Sekundäraktionen mit Fläche.
- **Outline:** nur betonte Kante plus zweitrangige Schrift, Hover füllt mit Vertiefung. Leise
  Sekundäraktionen und Abbrechen in Dialogen.
- **Hover / Focus:** Füllungen gehen eine Stufe dunkler (siehe *Die Dunkler-Regel*), der
  Fokusring ist global und überall derselbe: 2px Leitfarbe mit 2px Versatz.
- **Disabled:** Rahmen fällt weg, Fläche wird Vertiefung, Schrift gesperrt.
- **Destruktiv, drei Stufen** — sie kodieren die **Position im Bestätigungs-Flow**, nicht die
  Schwere: Outline löst aus, Solid vollzieht, Quiet ist Outline in Serie.

### Chips

Badges sind vollrunde Pillen (`0.125rem 0.5rem`, 0,75rem Schrift) in sechs Tönen, jeweils getönte
Fläche plus zugehörige Schrift. Zustände dagegen sind **keine** Pillen, sondern Punkt plus Wort:
ein 6px-Kreis in Erfolgs- oder Kante-Farbe neben stiller Schrift.

### Cards / Containers

Es gibt keine. Flächen entstehen aus drei Formen: der geriffelten Zeilenliste (Haarlinien oben,
unten und zwischen den Zeilen, negativer Außenrand), dem randlosen Abschnitt (eine Linie oben,
`pt-4`, Titel links und Statusmarker rechts) und dem getönten Block (Vertiefung, Arbeitsradius,
`0.75rem`) für das, was tatsächlich gegen Andersartiges grenzt.

### Inputs / Fields

- **Style:** Eingelassenes Feld als Fläche, 1px Fassung als Rand, Arbeitsradius, `0.5rem 0.75rem`.
  Eine kompakte Variante mit `0.375rem 0.5rem` für Filter-Toolbars.
- **Focus:** Der Rand wechselt auf die Leitfarbe, zusätzlich zum globalen Fokusring.
- **Error:** Der Rand ändert sich **nicht**. Fehler tragen einen eigenen Absatz in Danger-Schrift
  unter dem Feld, verbunden über `aria-describedby`.

### Navigation

Tab-Leisten sind Router-Links mit 2px Unterkante: aktiv in der Leitfarbe mit voller Schriftfarbe,
inaktiv transparent mit stiller Schrift. Kein ARIA-Tabs-Muster — es sind echte Navigationen.
Rücknavigation ist ein einzelner Up-Link auf den Elternknoten der *Informations*-Hierarchie, mit
dem Eigennamen des Ziels als Label, nie „Zurück".

### Das Sprite-Blatt

Die Signaturfläche. Nutzungsseite und Stimmzettel sind keine Listen, sondern ein Bogen
gleichartiger Zellen, gruppiert in vier Bändern, die aus dem Set selbst geschnitten werden statt
aus festen Schwellen: die Emotes, die zusammen die erste Hälfte der Nutzung ausmachen, dann bis
80 %, dann der Rest mit mindestens einem Treffer, dann die toten. Bandüberschriften sind Haarlinie
plus Label. Der Füllbalken einer Zelle misst gegen die Spitze ihres *Bandes*, nicht gegen die des
Sets — sonst wäre in den unteren Bändern jeder Balken leer.

### Named Rules

**Die Bemerkenswert-wie-oft-Regel.** Eine Pille markiert eine *bemerkenswerte* Eigenschaft, nicht
jede Eigenschaft. Was in jeder Zeile steht, markiert nichts mehr und wird stiller Text. Subsysteme,
die ihre eigene Gesundheit melden, werden bei `ok` zum Punkt und erst bei Warnung zur Pille —
damit trägt eine gesunde Übersicht **keine einzige farbige Pille**, und das erste auffällige
Subsystem ist ohne Zutun das lauteste Element.

**Die Rahmen-schweigt-Regel.** Für die App-Kopfzeile gilt das eine Stufe strenger als für eine
Seite: Was dort steht, steht auf jedem Bildschirm in jeder Sitzung. Und was der Rahmen sagt, sagt
keine Seite ein zweites Mal.

**Die Auslösen-Vollziehen-Regel.** Jede destruktive Aktion hat Auslöser **und** Vollzug: Outline
oder Quiet öffnet einen Dialog, Solid bestätigt darin. Ein destruktiver Button ohne
Bestätigungsdialog ist nicht vorgesehen. Wiederholt sich der Auslöser je Zeile, wird er still —
zwanzig rot umrandete Knöpfe untereinander machen die seltenste Aktion einer Seite zu ihrem
lautesten Element.

## Do's and Don'ts

### Do:

- **Do** jede Farbe aus dem Tokensatz nehmen. Fehlt eine Rolle, wird das **Token** ergänzt — mit
  Wert für beide Modi — statt in Tailwinds Palette zu greifen. `npm run lint` erzwingt das
  unterhalb `web/src/app/`.
- **Do** gefüllte Flächen im Hover **dunkler** machen, auch im Dunkelmodus.
- **Do** Abgrenzung über Fläche und Haarlinie lösen: geriffelte Zeile, randloser Abschnitt,
  getönter Block.
- **Do** den globalen Fokusring stehen lassen. Wer ihn entfernt, schuldet gleichwertigen Ersatz.
- **Do** Primäraktionen und Touch-Ziele auf `lg` (2,75rem Mindesthöhe) setzen — automatisch ist
  daran nichts, der Default ist `md`.
- **Do** Zahlen im Monospace setzen, wenn sie in einer Spalte stehen.
- **Do** die vorhandenen Primitives benutzen statt Utility-Ketten nachzubauen; die vollständige
  Liste steht in der Bauen-Checkliste der UI-Designsprache.

### Don't:

- **Don't** die Leitfarbe glühen lassen. Keine farbigen Schatten, keine Verläufe, keine
  gesättigten Flächen als Effekt — Guides sind Haarlinien, keine Lichtquellen.
- **Don't** den Grund blaustichig machen. Slate-getönte Flächen färben mehrere hundert fremde
  Bilder mit; das ist der Grund für Graphit, und er gilt weiter.
- **Don't** eine Kartenklasse einführen. Kein Rechteck um jede Zeile, kein `.app-card` unter
  anderem Namen.
- **Don't** Bewegung als Zierde einsetzen. Keine Eingangsanimationen, keine gestaffelten Reveals,
  kein `behavior: 'smooth'` beim Seitenwechsel. Bewegung antwortet auf einen Zustand oder findet
  nicht statt.
- **Don't** einen Hover auf etwas legen, das nicht klickbar ist — er verspricht einen Klick, den
  es nicht gibt.
- **Don't** eine Pille für etwas vergeben, das auf den meisten Zeilen dasselbe Wort ist.
- **Don't** ein Emoji als Icon verwenden. Der Leerzustand hat bewusst keinen Icon-Slot.
- **Don't** eine Farbe fest verdrahten, weil sie „in beiden Modi geht". Es gibt genau eine
  themefeste Farbrolle, und das ist die Fassung eines Bedienelements.
