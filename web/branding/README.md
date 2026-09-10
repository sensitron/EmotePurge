# Branding source files

## As of 2026-08-06: the source is a vector

**`web/public/logo.svg` is the brand mark.** One file, flat Guide turquoise, works equally well on
graphite and on paper white — there is no longer a separate light version and no simplified icon
version alongside a hero version. All shipped raster files are derived from it.

The vector is **not redrawn from scratch**, but derived from `logo-full.png`: per-pixel
classification into silhouette and near-black facial features, contours via marching squares,
simplification with Ramer-Douglas-Peucker at ~1 px on a 512 grid. It is the same swirl, the same
wink, the same three tiles flying off — only the gradient from violet to magenta has given way to
a flat colour.

## Shipped assets and how they are produced

| File | Size | Margin | Background |
|---|---|---|---|
| `logo.svg` | vector | 6% (in the path) | transparent |
| `icon-192/512.png` | 192 / 512 | 10% | `#0d0f12` |
| `icon-maskable-192/512.png` | 192 / 512 | 21% | `#0d0f12` |
| `apple-touch-icon.png` | 180 | 13% | `#0d0f12`, **no alpha channel** |
| `favicon.ico` | 48 / 32 / 16 | 0 | transparent |
| `og-image.png` | 1200 × 630 | — | `#0d0f12` |

They were produced by rendering the SVG in a headless Chromium instance onto a canvas of the
target size (`drawImage` with `imageSmoothingQuality: 'high'`), with `favicon.ico` as a
PNG-embedded ICO container built from the three small sizes. There is deliberately **no**
checked-in script for this: the derivation happens once per brand change, and a script that needs
a browser engine as its rasterizer is more maintenance burden than the manual step is worth. The
assets are the artefact, the SVG is the source.

**On the maskable size, honestly:** the 21% is a fixed, conservative value. The mark thereby fills
around 58% of the edge length and sits safely within the 80% safe-zone circle of every launcher
mask. The earlier derivation instead measured the largest distance from the centre to an opaque
pixel and arrived at 78.8% — that worked because the mark is a disc with empty box corners. Since
the vector version, the three tiles sit further out to the right, so the margin is smaller; it is
still not maxed out, though. Anyone who wants the icon bigger should measure the radius, not
guess.

## Historical sources

The AI-generated originals (ChatGPT, 1254×1254, background baked in) still live here and are
**not** shipped:

- `logo-full.png` — main version with the flying-off pixel squares. **Source of the vector.**
- `logo-mark.png` — simplified icon version. No longer used, since one file now covers all sizes.
- `logo-full-light.png`, `logo-mark-light.png` — the light pair from 2026-08-02. No longer used,
  since the mark is now a single colour and no longer needs a mode twin.

`make-icons.ps1` has been **removed** (2026-08-06). The script derived the assets from the violet
pair and, on a run, would have overwritten the new files with the old mark again — a script that
silently reverts the current state is more dangerous than none at all. It remains in history in
case the flood-fill background removal is ever needed again: `git log -- web/branding/make-icons.ps1`.
