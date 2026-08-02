# Branding-Quelldateien

KI-generierte Logo-Originale (ChatGPT), 1254×1254, Hintergrund eingebrannt.
Liegen bewusst **nicht** in `public/` — sie werden nicht ausgeliefert, sondern sind die Quelle,
aus der die ausgelieferten Assets generiert wurden.

Dunkles Paar (2026-07-31), Grund `#020617`, Gesichtszüge fast-schwarz:

- `logo-full.png` — Hauptversion mit wegfliegenden Pixel-Quadraten → Quelle für `public/logo-hero.png`.
- `logo-mark.png` — vereinfachte Icon-Version → Quelle für `public/favicon.ico`, `logo.png`,
  `apple-touch-icon.png`, `icon-192.png`, `icon-512.png`.

Helles Paar (2026-08-02) für den Light Mode, Grund `#FFFFFF` (gemessen `#FDFDFD`–`#FFFFFF`),
Gesichtszüge und Sichel-Aussparungen fast-weiß, Verlauf tiefer abgestimmt (`#931CD1` → `#E32F81`,
das Violett liegt praktisch auf `purple-600`):

- `logo-full-light.png` → Quelle für `public/logo-hero-light.png`.
- `logo-mark-light.png` → Quelle für `public/logo-light.png`.

**Nur diese zwei Ausgabedateien** hat das helle Paar. Favicon und App-Icons bleiben dunkel: ein
PWA-Manifest trägt genau eine `theme_color`, das installierte Icon folgt also der Marke und nicht
dem Seiten-Theme.

Die Ableitung (Freistellung per Flood-Fill vom Rand, quadratischer Zuschnitt, Downscaling,
ICO-Container mit PNG-Einträgen) macht [make-icons.ps1](make-icons.ps1) — bei neuen Quelldateien
einfach erneut ausführen, es überschreibt die generierten Assets in `public/`.
Padding-Konventionen: Favicon/`logo.png`/`logo-hero.png` und die hellen Zwillinge transparent mit
3 % Rand, App-Icons (`apple-touch-icon`, `icon-192/512`) auf `#020617` mit 10–12 % Rand.
Weil der Zuschnitt relativ zur Bounding-Box rechnet, füllt die Marke in hell wie dunkel dieselben
94,3 % der Kantenlänge — die Varianten sind an derselben `<img>`-Box austauschbar, ohne dass
etwas springt.

Der Flood-Fill nimmt Pixel (1,1) als Referenz und läuft mit Toleranz 30. Das trägt beide Paare
mit großem Abstand: der am weitesten vom Grund entfernte Negativraum-Punkt der hellen Quellen
liegt bei einem Distanzquadrat von 29, die Schwelle bei 2700. Eingeschlossene Flächen (Augen,
Mund) bleiben dabei bewusst deckend — im dunklen Paar fast-schwarz, im hellen fast-weiß —, nur
die nach außen offenen Sicheln werden transparent.
