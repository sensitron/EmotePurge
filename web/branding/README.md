# Branding-Quelldateien

## Stand seit 2026-08-06: die Quelle ist ein Vektor

**`web/public/logo.svg` ist die Marke.** Eine Datei, flaches Guide-Türkis, trägt auf Graphit und auf
Papierweiß gleichermaßen — es gibt keine helle Zweitfassung mehr und keine vereinfachte Icon-Version
neben einer Hero-Version. Alle ausgelieferten Rasterdateien werden daraus abgeleitet.

Der Vektor ist **nicht neu gezeichnet**, sondern aus `logo-full.png` gewonnen: Klassifikation je
Pixel in Silhouette und fast-schwarze Gesichtszüge, Konturen per Marching Squares, Vereinfachung mit
Ramer-Douglas-Peucker bei ~1 px auf einem 512er-Raster. Es ist derselbe Wirbel, dasselbe Zwinkern,
dieselben drei abgeworfenen Kacheln — nur der Verlauf von Violett nach Magenta ist einer flachen
Farbe gewichen.

## Ausgelieferte Assets und wie sie entstehen

| Datei | Größe | Rand | Fläche |
|---|---|---|---|
| `logo.svg` | vektoriell | 6 % (im Pfad) | transparent |
| `icon-192/512.png` | 192 / 512 | 10 % | `#0d0f12` |
| `icon-maskable-192/512.png` | 192 / 512 | 21 % | `#0d0f12` |
| `apple-touch-icon.png` | 180 | 13 % | `#0d0f12`, **ohne Alphakanal** |
| `favicon.ico` | 48 / 32 / 16 | 0 | transparent |
| `og-image.png` | 1200 × 630 | — | `#0d0f12` |

Erzeugt wurden sie durch Rendern des SVG in einem headless Chromium auf ein Canvas der Zielgröße
(`drawImage` mit `imageSmoothingQuality: 'high'`), das `favicon.ico` als PNG-eingebetteter
ICO-Container aus den drei kleinen Größen. Es gibt dafür bewusst **kein** eingechecktes Skript: die
Ableitung passiert einmal pro Markenänderung, und ein Skript, das eine Browser-Engine als
Rasterisierer braucht, ist mehr Wartungslast als der Handgriff wert. Die Assets sind das Artefakt,
das SVG die Quelle.

**Zur maskable-Größe, ehrlich:** die 21 % sind ein fester, konservativer Wert. Der Mark füllt damit
rund 58 % der Kantenlänge und liegt sicher im 80-%-Safe-Zone-Kreis jeder Launcher-Maske. Die frühere
Ableitung hat stattdessen den größten Abstand vom Mittelpunkt zu einem deckenden Pixel gemessen und
kam damit auf 78,8 % — das ging, weil die Marke eine Scheibe mit leeren Box-Ecken ist. Seit der
Vektorfassung stehen die drei Kacheln rechts weiter außen, der Vorteil ist also kleiner; ausgereizt
ist er trotzdem nicht. Wer das Icon größer haben will, misst den Radius nach, statt zu raten.

## Historische Quellen

Die KI-generierten Originale (ChatGPT, 1254×1254, Hintergrund eingebrannt) liegen weiterhin hier
und werden **nicht** ausgeliefert:

- `logo-full.png` — Hauptversion mit den wegfliegenden Pixel-Quadraten. **Quelle des Vektors.**
- `logo-mark.png` — vereinfachte Icon-Version. Ohne Funktion, seit eine Datei alle Größen trägt.
- `logo-full-light.png`, `logo-mark-light.png` — das helle Paar vom 2026-08-02. Ohne Funktion, seit
  die Marke einfarbig ist und keinen Modus-Zwilling mehr braucht.

`make-icons.ps1` ist **entfernt** (2026-08-06). Das Skript leitete die Assets aus dem violetten Paar
ab und hätte bei einem Lauf die neuen Dateien wieder mit der alten Marke überschrieben — ein Skript,
das den Bestand still zurückdreht, ist gefährlicher als keins. Es steht in der Historie, falls die
Flood-Fill-Freistellung je wieder gebraucht wird: `git log -- web/branding/make-icons.ps1`.
