# Branding-Quelldateien

KI-generierte Logo-Originale (ChatGPT, 2026-07-31), 1254×1254, dunkler Hintergrund eingebrannt.
Liegen bewusst **nicht** in `public/` — sie werden nicht ausgeliefert, sondern sind die Quelle,
aus der die ausgelieferten Assets generiert wurden.

- `logo-full.png` — Hauptversion mit wegfliegenden Pixel-Quadraten → Quelle für `public/logo-hero.png`.
- `logo-mark.png` — vereinfachte Icon-Version → Quelle für `public/favicon.ico`, `logo.png`,
  `apple-touch-icon.png`, `icon-192.png`, `icon-512.png`.

Die Ableitung (Freistellung per Flood-Fill vom Rand, quadratischer Zuschnitt, Downscaling,
ICO-Container mit PNG-Einträgen) macht [make-icons.ps1](make-icons.ps1) — bei neuen Quelldateien
einfach erneut ausführen, es überschreibt die generierten Assets in `public/`.
Padding-Konventionen: Favicon/`logo.png`/`logo-hero.png` transparent mit 3 % Rand,
App-Icons (`apple-touch-icon`, `icon-192/512`) auf `#020617` mit 10–12 % Rand.
