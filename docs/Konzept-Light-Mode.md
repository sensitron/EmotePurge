# Konzept: Heller Modus (Light Mode)

**Status:** Entwurf zur Freigabe, 2026-08-02 · **Umsetzung: noch keine.**
**Betrifft:** `web/src/styles.css`, alle Templates und Varianten-Maps unter `web/src/app/`, `web/src/index.html`, `web/public/manifest.webmanifest`, `docs/UI-Designsprache.md`, `web/e2e/audit/`.

---

## 0. Kurzfassung

- Heute gibt es **kein Theming-Fundament**: 0 `dark:`-Varianten, 0 eigene Farb-Tokens, 0 `@theme`-Block. Farbe steht als Tailwind-Paletten-Utility direkt in 36 Dateien (**414 Treffer**) plus in **9 hartkodierten Farb-Maps in TypeScript**. Das ist Greenfield — es gibt nichts abzureißen, aber auch nichts, woran man andocken könnte.
- Empfohlener Mechanismus: **semantische Tokens** (`bg-surface`, `text-muted`, `border-subtle`, …), definiert als CSS-Variablen unter `:root` / `[data-theme='light']` und über Tailwind v4 `@theme inline` als Utilities registriert. **Kein** `dark:`-Variantenansatz.
- Der Umbau ist ein **Ersetzungs-, kein Verdopplungsvorgang**: die 11 häufigsten Utilities decken 65 % aller Treffer ab und fallen auf ~5 Tokens zusammen.
- Vier Dinge lassen sich **nicht** sauber themen und brauchen eine Entscheidung von dir: **7TV-Emote-Bilder**, **die Logo-Assets** (nachgemessen — weniger schlimm als angenommen, §2.2), **der Radial-Glow**, **das PWA-Manifest**. Details in §2.
- Nebenbefund: der Dark-Mode verfehlt heute an **zwei** Stellen AA (`text-slate-500` mit 3,7:1, Input-Ränder mit 1,7:1). Ein „einfach invertieren" würde beide Fehler mitnehmen. Siehe §5.3.
- Aufwand gesamt: **~20–26 h** über 5 Wellen, kleinste benutzbare Auslieferung nach Welle 2 (~11–15 h).

---

## 1. Inventur

### 1.1 Welche Farben, wo, wofür

Sechs Familien, sonst nichts. `rose`, `yellow`, `orange`, `green`, `teal`, `sky`, `cyan`, `fuchsia`, `violet`, `indigo`: **je 0 Treffer**. `pink` existiert ausschließlich als Gradient-Stop.

| Familie | Treffer | Rolle im System |
|---|---:|---|
| **slate** | 275 | Trägt die gesamte Struktur: Seitenfläche (950), Kartenfläche (900), Inset-Panel/Badge-neutral/Skeleton (800), Ränder + Divider (800/700), sowie die komplette **fünfstufige Textleiter** 100 → 200 → 300 → 400 → 500 |
| **purple** | 52 | Der einzige Akzent: Fokusring (500), aktive Tab-Unterkante (500), Fortschrittsbalken (500), Primär-Button (600), ausgewählter Zustand (600/700), Akzent-Linktext (400/300), getönte Flächen „Wash" (950) |
| **amber** | 25 | Warnung/degradiert: Banner + Badge (`950`/`300`), Statustexte (400/300/200), Ampel-Füllung (500) |
| **red** | 21 | Gefahr: Feldfehler (`text-red-400`), Banner/Badge (`950`/`300`), destruktiver Button (`border-red-800` bzw. `bg-red-800`), Vote-Zustand „delete" (`bg-red-700`), Ampel (500) |
| **emerald** | 11 | Erfolg/„läuft": Badge (`950`/`300`), Health-Punkt (500), Vote-Zustand „keep" (`bg-emerald-700`), Bestätigungstexte (400) |
| **blue** | 3 | Genau eine Bedeutung: Rolle „Moderator" bzw. „eingeschränkte Sichtbarkeit" (Badge `950`/`300`, `text-blue-300`) |
| `text-white` | 15 | Text auf gefüllten Akzent-/Gefahrflächen |
| Gradient/accent/shadow | 12 | `accent-purple-600` (8, native Radios/Checkboxen), `shadow-purple-950` (2), `from-purple-400`/`to-pink-400` (je 1) |

**Stufen-Aufschlüsselung slate** (die eigentliche Migrationslast): `text-400` **84** · `text-200` 46 · `bg-800` 27 · `text-300` 23 · `text-100` 19 · `bg-900` 18 · `border-800` 14 · `text-500` 13 · `bg-700` 10 · `border-700` 9 · `bg-950` 8 · Rest 4.
→ **Die 11 häufigsten Ausprägungen sind 271 von 414 Treffern (65 %) und fallen auf fünf Tokens zusammen.**

Farbe als **Domänenwert** existiert an genau einer Stelle: `SlotBudgetTone = 'emerald' | 'amber' | 'red'` in `shared/emotes/slot-budget.ts` mit Schwellen bei 80 %/95 %. Die restlichen acht Maps sind reine Präsentations-Records.

Die neun Farb-Maps in TypeScript (Sammelpunkte, an denen der Umbau billig ist):
`shared/ui/button.ts:6-17` · `shared/ui/status-badge.ts:5-12` · `shared/ui/notice-banner.ts:5-9` · `shared/emotes/slot-budget-bar.ts:6-10` · `features/shell/app-shell.ts:11-15` · `features/admin/admin-monitoring-page.ts:20-33` · `features/admin/admin-roster-card.ts:17` · `features/admin/admin-channel-detail-page.ts:22-30` · `shared/datetime/datetime-picker.ts:276-287`.

Duplikate dieser Maps, die beim Umbau mitmüssen, weil sie die Primitives inline nachbauen statt sie zu benutzen: `datetime-picker.ts:143` (kopiert `appButton="primary"`), `datetime-picker.ts:134` (kopiert `.app-input`), `back-link.ts:24` (Purple-Outline-Button ohne Map), `delete-progress-panel.ts:16` und `seven-tv-token-prompt-dialog.ts:19` (Text-Link-Buttons ohne Primitive).

### 1.2 Wo Farbe hart an „dunkel" gekoppelt ist

| # | Kopplung | Fundstelle(n) | Bricht im Hellen weil |
|---|---|---|---|
| 1 | `color-scheme: dark` | `styles.css:14` | Native Controls (`input[type=time]` im DateTime-Picker), Scrollbars, Autofill bleiben dunkel |
| 2 | Lila Glow-Schatten | `styles.css:90` (`rgb(88 28 135 / .55)`), `landing-page.html:77,183` (`shadow-purple-950/50`) | Ein farbiger, additiv gedachter Schein; auf Weiß wird er zu einem schmutzigen Fleck statt zu Tiefe |
| 3 | `color-mix` gegen `transparent` | `styles.css:88` (Hover-Rand), `styles.css:102` (Sticky-Bar) | Das Ergebnis hängt vom darunterliegenden Dunkel ab, nicht vom Token |
| 4 | Halbtransparente Sticky-Bars + Blur | `styles.css:99-104`, `app-shell.ts:44`, `landing-page.html:10` (5 Verwendungsstellen) | Funktioniert *mechanisch* auch hell, sobald die Basisfarbe ein Token ist — die Alpha-Stufe (85 %/80 %) muss aber pro Modus anders sein, hell braucht mehr Deckung |
| 5 | `rgb(0 0 0 / .6)` Dialog-Backdrop | `styles.css:128` | Ein schwarzer 60-%-Schleier über einer hellen Seite ist zu hart |
| 6 | Fixe helle Overlay-Schrift | `styles.css:134` (`color: slate-100`) | Der CDK-Overlay-Container hängt **außerhalb** der App-Shell-DOM. Konsequenz fürs Konzept: das Theme-Attribut muss auf `<html>` sitzen, nicht auf dem Shell-`<div>` |
| 7 | Badge-/Banner-System | `status-badge.ts:5-12`, `notice-banner.ts:5-9` | Durchgehend `bg-<farbe>-950` + `text-<farbe>-300`. Auf hellem Grund liest sich eine fast schwarze Fläche als „Fehler", egal welche Farbe |
| 8 | `notice-banner` `info` = `bg-slate-900` | `notice-banner.ts:6` | Identisch mit der Kartenfläche. Sichtbar wird der Banner heute nur, weil die *Seite* dunkler ist. Im Hellen (Karte weiß) verschwindet er auf einer Karte spurlos → braucht ein eigenes Token, nicht nur einen anderen Wert |
| 9 | SegmentedControl-Divider | `segmented-control.ts:21` | Trennlinien entstehen durch `gap-px` über einer `bg-slate-700`-Containerfläche, die **heller** als die Segmente (`slate-800`) ist. Im Hellen dreht sich die Richtung um |
| 10 | Alpha-Flächen auf Dunkel gerechnet | 13 Treffer / 7 Ausprägungen: `bg-amber-950/40` (3), `bg-slate-950/80` (2), `bg-slate-900/60` (2), `bg-slate-800/70` (2), `bg-purple-950/40` (2), `bg-red-950/50`, `bg-purple-500/50` | Alle mischen eine dunkle Farbe *auf* eine dunklere Fläche. Im Hellen muss jede einzelne neu gemischt werden |
| 11 | Skeleton | `styles.css:109-113` + `@keyframes { 50% { opacity:.5 } }` | Der Puls funktioniert in beiden Modi, die Fläche (`slate-800`) nicht |
| 12 | `disabled:opacity-50` | `button.ts:39`, `admin-audit-log-page.ts:164` | Weiß auf 50 % `purple-600` über Weiß ergibt **2,3:1** — der Button sieht kaputt aus, nicht deaktiviert (im Dunkeln fällt das nicht auf) |
| 13 | Radial-Glow | `app-shell.ts:37`, `landing-page.html:5`, `login-page.ts:19` — **dreimal wortgleich kopiert** | `rgba(147,51,234,.14)` + `rgba(236,72,153,.1)` sind additiv gedacht; auf Weiß ein Schleier |
| 14 | Gradient-Text | `landing-page.html:66` (`from-purple-400 to-pink-400` + `bg-clip-text`) | Deutlich unter AA auf hellem Grund |
| 15 | Kalender-Helligkeitsleiter | `datetime-picker.ts:276-287` (`slate-700` disabled < `slate-600` außerhalb < `slate-200` normal) | Eine rein dunkel gedachte Leiter; jede Stufe kippt |
| 16 | `theme-color` / Manifest | `index.html:11` (`#020617`), `manifest.webmanifest` (`background_color`, `theme_color`) | Beides dunkel festgenagelt |
| 17 | Logo-Assets | `logo.png`, `logo-hero.png` (`app-shell.ts:54`, `landing-page.html:16,52`, `login-page.ts:26`); App-Icons laut `branding/README.md` mit `#020617` **eingebrannt** | Nur teilweise — der Marken-Korpus ist ein gesättigter Verlauf und bleibt sichtbar, die Gesichtszüge sind aber deckendes Fast-Schwarz. Siehe §2.2, gemessen |
| 18 | 7TV-Emote-Bilder | `usage-stats-page.html:234-240`, `vote-session-detail-page.html:206-213` | Fremd-Content, für dunkle Chats gestaltet: weiße Schrift, helle Outlines, weiße Glow-Kanten. **Nicht themebar** — siehe §2.1 |

**Nicht** problematisch, und das ist eine gute Nachricht: alle Inline-Styles sind farbfrei (`[style.width.%]`, `[style.grid-template-columns]`, ein `rotate(180deg)`); alle Inline-SVGs nutzen `currentColor`; es gibt **keine** Canvas- oder Chart-Bibliothek — jeder „Chart" ist ein CSS-Balken; **kein** Spec prüft eine Farbklasse, ein Token-Refactor bricht also keinen Test.

### 1.3 Größenordnung

| Kennzahl | Wert |
|---|---:|
| Dateien unter `web/src/` gesamt | 120 (6 `.html`, 113 `.ts`, 1 `.css`) |
| Davon mit mindestens einer Farb-Utility | **36** (30 %) |
| Farb-Utility-Treffer gesamt | **414** |
| Davon in den 5 größten Dateien | 155 (37 %) |
| Farb-Maps in TypeScript | 9 |
| Alpha-Utilities (`…-950/40` etc.) | 13 in 7 Ausprägungen |
| `.app-*`-Klassen in `styles.css` | 8 (`card`, `card-interactive`, `card-link`, `input`, `input-sm`, `sticky-bar`, `skeleton`, `dialog-*`) |
| Vorhandene `dark:`-Varianten | **0** |
| Vorhandene eigene Farb-Tokens | **0** |

Top-Dateien: `landing-page.html` 52 · `vote-session-detail-page.html` 28 · `usage-stats-page.html` 28 · `datetime-picker.ts` 26 · `admin-monitoring-page.ts` 26 · `admin-channel-detail-page.ts` 23 · `admin-roster-card.ts` 22 · `app-shell.ts` 21 · `admin-layout.ts` 17 · `button.ts` 15.

---

## 2. Vier Funde, die das Konzept prägen — und die ich nicht glattbügle

### 2.1 7TV-Emote-Bilder lassen sich nicht themen

Die Emote-Kacheln im Usage-Stats-Grid und in der Voting-UI zeigen transparente PNG/WebP vom 7TV-CDN. Ein großer Teil dieser Emotes ist für dunkle Chat-Hintergründe gezeichnet: weiße Schrift, weiße Outlines, helle Glow-Kanten. Auf einer weißen Karte verschwinden sie schlicht. Wir haben keinen Einfluss auf das Material und können es nicht pauschal invertieren (das zerstört farbige Emotes).

**Vorschlag:** Die Emote-Kachel bekommt ein eigenes, **themefestes** Token `--emote-canvas` (in beiden Modi `slate-800`), die Bildfläche bleibt also in beiden Modi dunkel — als bewusst gesetzte, abgerundete „Leinwand", nicht als Versehen. Der Rest der Karte (Name, Zahlen, Rand, Selektionszustand) folgt dem Theme. Das ist in hellen UIs ein etabliertes Muster für Fremd-Assets mit Alpha-Kanal.
> **Revidiert am 2026-08-02, nach dem Ansehen im Browser.** Umgesetzt wurde zuerst genau das hier Vorgeschlagene; die dunkle Leinwand ist danach zurückgenommen worden, weil sie auf **jeder** Kachel steht und ein fast schwarzer Balken quer über jede Karte im Hellen das lauteste Element der Seite ist — während das Material, das er schützt, die Minderheit ist. `--ep-emote-canvas` gibt es jetzt pro Modus (hell `slate-200`). Begründung im Entscheidungslog, Regel in der Designsprache §2.4. Der Rest dieses Abschnitts — eigenes Token statt `surface-inset`, Selektions-Wash weg vom Bild — gilt unverändert.

Nebenwirkung: der Selektionszustand `bg-purple-950/40` liegt heute **unter** dem Bild; er muss im Hellen auf den Kartenrahmen wandern (`inset-ring` bleibt, Wash wechselt auf `--accent-wash`), sonst kämpft er mit der dunklen Leinwand.

### 2.2 Das Logo ist weniger kaputt als gedacht — aber tonal falsch

**Nachgemessen an `web/public/logo.png` (128×128, `Format32bppArgb`), nicht geschätzt:**

| Messpunkt | Alpha | Farbe |
|---|---:|---|
| Ecke (außerhalb der Marke) | **0** | — (freigestellt) |
| Gesichtsfläche | 255 | `#b435e1` (Verlauf Violett → Pink) |
| Linkes Auge | **255** | `#000017` |
| Mund | **255** | `#000009` |

Die Marke ist also **kein** helles Line-Art-Logo, sondern ein gesättigter Violett-Pink-Verlauf. Nur der *äußere* Grund ist transparent; die Gesichtszüge sind **deckendes Fast-Schwarz**. Auf Weiß bleibt der Korpus damit klar sichtbar und das Gesicht lesbar — das Logo verschwindet nicht.

Was trotzdem nicht stimmt: `#000017` ist ein Ton, den der helle Modus sonst nirgends führt (dunkelste Textstufe wäre `slate-900` `#0f172a`). Die Augen- und Mundflächen wirken als schwarze Löcher in einer hellen Oberfläche, und die Sichel-Aussparungen des Wirbels lesen sich im Hellen invertiert.

**Konsequenz für den Plan:** Eine helle Asset-Variante ist **Feinschliff, kein Blocker.** Welle 4 kann ohne sie ausgeliefert werden; das Logo sieht dann leicht fremd aus, nicht kaputt. Wenn die Variante vorliegt, wird sie per `<picture>`/`[ngSrc]` am Theme umgeschaltet.
`filter: invert()` scheidet aus — die Marke ist farbig, nicht monochrom.

### 2.3 Der Radial-Glow ist ein Dark-Mode-Gerät, kein Design-Element

Der Hero-Schein auf Shell, Landing und Login ist additive Lichtsimulation. Im Hellen gibt es dafür kein Äquivalent — ein heller Schein auf hellem Grund ist unsichtbar, ein dunkler wäre ein Schatten aus dem Nichts. Ich schlage vor, ihn im hellen Modus auf eine **sehr schwache Tönung** zu reduzieren (`.06`/`.04` statt `.14`/`.10`), womit er zur Papierfärbung wird statt zum Leuchten. Alternativ im Hellen ganz abschalten. Beides ist vertretbar; „gleich aussehen" ist es nicht.

Unabhängig davon: der Gradient ist **dreimal wortgleich kopiert**. Er gehört vor dem Theming in eine Klasse `.app-page-glow` in `styles.css` — das ist Voraussetzung, nicht Kür.

### 2.4 Der Dark-Mode verfehlt heute schon zweimal AA

Bei der Kontrastrechnung ist mir aufgefallen, dass zwei bestehende Paare unter dem Repo-Anspruch (AA-Minimum, §10 der Designsprache) liegen:

- **`text-slate-500`** auf Kartenfläche: **3,7:1** (auf der Seitenfläche 4,2:1) — Anforderung 4,5:1. 13 Vorkommen (Hinweistexte, Wochentagsköpfe, Meta-Zeilen).
- **`.app-input`-Rand `slate-700`** auf Kartenfläche: **1,7:1** — Anforderung 3:1 (WCAG 1.4.11, ein Eingabefeld wird durch seinen Rand als Bedienelement erkennbar).

Eine mechanische Invertierung würde beide Fehler in den hellen Modus mitnehmen. Der Tokensatz unten behebt sie in beiden Modi (siehe `--text-muted` und `--border-field`). Das ist eine sichtbare Änderung am *bestehenden* dunklen Modus — Ränder werden etwas heller, `text-slate-500` verschwindet als eigene Stufe. Ob das mit hineingehört, ist offene Entscheidung Nr. 4.

Gegenprobe zu deiner Erwartung: **der lila Fokusring fällt nicht durch.** `purple-500` erreicht auf Weiß 4,0:1 und auf `slate-50` 3,8:1, beides über der 3:1-Schwelle. Viel Luft ist das nicht, deshalb nehme ich im Hellen trotzdem `purple-600` (5,2–5,4:1). Die tatsächlichen Wackelkandidaten sind die Ampel-Balkenfüllungen (§5.2) und `disabled:opacity-50` (§1.2 Nr. 12).

---

## 3. Technischer Mechanismus

### 3.1 Die drei realistischen Optionen

**Option A — Semantische CSS-Custom-Properties, pro Theme umgeschaltet.**
Jede Farbe bekommt einen Rollennamen (`--surface`, `--text-muted`, `--border`, `--accent`). Die Werte stehen an genau einer Stelle, einmal pro Theme. Templates schreiben `bg-surface`, `text-muted`, `border-subtle`.

- *Dafür:* Jede Farbentscheidung existiert einmal statt zweimal. Der Umbau ist eine **Ersetzung** (414 Treffer → ~414 Treffer), keine Verdopplung. Ein dritter Modus (High-Contrast) wäre später eine weitere Wertetabelle, kein weiterer Durchlauf durch alle Templates. Die 9 TS-Farb-Maps bleiben einzeilig. Passt exakt zur Regel „es gibt genau **eine** Kartenfläche" (§2.1 Designsprache): `bg-surface` *ist* diese eine Fläche, unabhängig vom Modus.
- *Dagegen:* Man verliert die direkte Ablesbarkeit — `bg-surface` sagt nicht mehr, welches Grau da steht. Erfordert Disziplin plus ein Lint-Gate, sonst schleichen sich Paletten-Utilities zurück ein (§7.3). Neue Farbwünsche kosten einen Token-Eintrag statt eines Utilities.

**Option B — Tailwinds `dark:`-Varianten.**
`class="bg-white dark:bg-slate-900 text-slate-900 dark:text-slate-100"`, plus `@custom-variant dark (&:where([data-theme=dark], [data-theme=dark] *))` in Tailwind v4.

- *Dafür:* Lokal ablesbar, kein Indirektionsschritt, kein Lint-Gate nötig, Tailwind-idiomatisch.
- *Dagegen:* Verdoppelt **414 Utilities auf ~800** und bläht die 9 TS-Farb-Maps auf doppelte Stringlänge. Jede spätere Farbänderung ist eine Suche über 36 Dateien statt eine Zeile. Vor allem aber: es verletzt den Geist der Ein-Kartenflächen-Regel — die Kartenfläche wäre nicht mehr *eine* Entscheidung, sondern zwei, an jeder Verwendungsstelle erneut getroffen. Genau diese Divergenz („warum ist diese Karte hell `bg-slate-50`, jene `bg-white`?") ist der typische Verfall von `dark:`-Codebasen. Und es löst die harten Punkte aus §1.2 gar nicht: `styles.css` braucht für Glow, Backdrop und Sticky-Bar ohnehin Variablen, weil das keine Utilities sind.
- *Ausnahme:* Für Fälle, in denen sich nicht der *Wert*, sondern die *Struktur* unterscheidet (Glow ja/nein, Schatten ja/nein, Logo-Datei), ist die Variante das richtige Werkzeug.

**Option C — Mischform: Tokens als Regel, Variante als Ausnahme.** ← **Empfehlung**

Alles Flächige, Textliche, Randliche, Akzentuierte läuft über Tokens (Option A). Zusätzlich wird **eine** Variante `light:` registriert, die ausschließlich dort benutzt wird, wo sich die beiden Modi *strukturell* unterscheiden statt nur im Wert — nach heutiger Zählung sind das **9 Stellen**: die 3 Glow-Kopien (nach Dedup: 1), die 2 Logo-Einbindungen, `shadow-xl` auf Popover/Dialogen (im Dunkeln praktisch wirkungslos, im Hellen tragend), das Gradient-Heading der Landing-Page, die Kalender-Helligkeitsleiter.

**Kosten für den Bestand (Option C):** 414 Utility-Ersetzungen in 36 Dateien, davon 65 % rein mechanisch (die 11 häufigsten Ausprägungen), plus 9 TS-Maps, plus eine Neufassung der 8 `.app-*`-Klassen, plus ~15 Stellen mit echter Entscheidung (Alpha-Flächen, Divider-Trick, Ampelfarben, Emote-Kachel). Kein Test bricht dabei, weil kein Spec Farbklassen prüft.

### 3.2 Skizze der Token-Ebene (Tailwind v4)

Wichtig ist die Zweiteilung: die **rohen** Werte als normale CSS-Variablen unter Selektoren, die **Registrierung** über `@theme inline`. `@theme inline` sorgt dafür, dass ein Utility zu `var(--ep-surface)` kompiliert statt den Wert einzubacken — nur so greift die Umschaltung per Selektor.

```css
/* 1. Rohwerte, einmal pro Modus */
:root,
:root[data-theme='dark'] {
  --ep-page:    var(--color-slate-950);
  --ep-surface: var(--color-slate-900);
  --ep-text-muted: var(--color-slate-400);
  --ep-accent:  var(--color-purple-500);
  --ep-backdrop: rgb(0 0 0 / 0.6);
  --ep-card-hover-shadow: 0 8px 24px -12px rgb(88 28 135 / 0.55);
  color-scheme: dark;
}

:root[data-theme='light'] {
  --ep-page:    var(--color-slate-50);
  --ep-surface: #ffffff;
  --ep-text-muted: var(--color-slate-600);
  --ep-accent:  var(--color-purple-600);
  --ep-backdrop: rgb(15 23 42 / 0.35);
  --ep-card-hover-shadow: 0 8px 24px -12px rgb(15 23 42 / 0.18);
  color-scheme: light;
}

/* 2. Registrierung als Utilities — `inline` ist Pflicht, sonst friert Tailwind den Wert ein */
@theme inline {
  --color-page:       var(--ep-page);
  --color-surface:    var(--ep-surface);
  --color-text-muted: var(--ep-text-muted);
  --color-accent:     var(--ep-accent);
}
```

Danach existieren `bg-page`, `bg-surface`, `text-muted`, `border-accent`, `ring-accent` usw., inklusive Alpha-Modifier (`bg-surface/70` kompiliert zu `color-mix(in oklab, var(--ep-surface) 70%, transparent)` — funktioniert mit Variablen).

Drei Konsequenzen, die im Konzept festgehalten gehören:

1. **Das `data-theme`-Attribut sitzt auf `<html>`**, nicht auf dem Shell-`<div>`. Grund: der CDK-Overlay-Container hängt als Geschwister von der App außerhalb der Shell (`styles.css:131-135` musste deshalb die Textfarbe explizit setzen). Auf `<html>` erben Dialoge und Popover die Tokens automatisch, und die explizite `color`-Krücke wird zu `color: var(--color-text)`.
2. **`color-scheme` wird pro Theme mitgesetzt** — damit ändert das native `<input type="time">` im DateTime-Picker, die Scrollbars und Autofill-Hintergründe ihre Erscheinung mit. Das ist eine Zeile und ersetzt eine Menge Handarbeit.
3. Die drei unlayered CDK-Regeln in `styles.css` bleiben unangetastet — sie werden nur wertseitig auf Tokens umgestellt. Der Kommentarblock zur Spezifitätsfalle bleibt gültig und wird nicht „aufgeräumt".

---

## 4. Farbsystem

### 4.1 Wie die Tiefenwirkung im Hellen ersetzt wird

Die Prämisse „im Hellen liegen Karten dunkler als die Seite" ist verbreitet, aber nicht die einzige Möglichkeit — und für dieses Projekt die schlechtere. Ich schlage stattdessen eine **richtungsstabile Regel** vor:

> Die Seitenfläche ist der neutrale Grund. Eine **erhöhte** Fläche entfernt sich vom Grund (dunkel: heller / hell: weißer), eine **eingelassene** Fläche geht zum Grund zurück und darüber hinaus.

Konkret:

| Ebene | Dunkel | Hell | Beziehung |
|---|---|---|---|
| Seite | `slate-950` | `slate-50` | Grund |
| Karte (erhöht) | `slate-900` | `#ffffff` | in beiden Modi **weiter vom Grund weg** |
| Panel in einer Karte (eingelassen) | `slate-800` | `slate-100` | in beiden Modi **eine Stufe zurück Richtung Grund** |

Damit bleibt `.app-card` in beiden Modi „die Fläche, die sich vom Grund abhebt", und das mentale Modell der Designsprache dreht sich nicht um. Nur die vierte Ebene (Panel *in* einer Karte, z. B. `delete-progress-panel`) kehrt ihre physikalische Richtung um — im Dunkeln heller, im Hellen dunkler. Genau dafür sind Rollennamen da: `surface-inset` beschreibt die Rolle, nicht die Richtung. Das ist das stärkste einzelne Argument gegen Option B.

Die **Tiefenwirkung** kommt im Dunkeln fast vollständig vom Rand (`slate-800` auf `slate-950` = 1,4:1, also kaum sichtbar — die Karte trennt sich in Wahrheit über die Flächenhelligkeit). Im Hellen ist das umgekehrt schwächer: Weiß auf `slate-50` sind 1,05:1, und der Rand `slate-200` auf `slate-50` nur 1,2:1. **Das reicht nicht.** Deshalb bekommt der helle Modus zwei Ersatzmittel:

- einen echten, neutralen **Elevationsschatten** auf `.app-card` (`0 1px 2px rgb(15 23 42 / .06), 0 1px 3px rgb(15 23 42 / .10)`), im Dunkeln `none`;
- den Hover-Schatten aus `.app-card-interactive` als **neutralen** statt lila Schatten (`0 8px 24px -12px rgb(15 23 42 / .18)`), zusammen mit dem Akzentrand, der bleibt.

Das ist der Punkt, an dem die beiden Modi bewusst **nicht** gleich aussehen: dunkel trennt über Helligkeit, hell über Schatten.

### 4.2 Tokensatz

Flächen und Ränder:

| Token | Dark | Light | Verwendung |
|---|---|---|---|
| `--color-page` | `slate-950` `#020617` | `slate-50` `#f8fafc` | `body`, Basis der Sticky-Bar-Mischung |
| `--color-surface` | `slate-900` `#0f172a` | `#ffffff` | `.app-card`, Dialogpanel, Popover, `notice-banner` info-Rahmen |
| `--color-surface-inset` | `slate-800` `#1e293b` | `slate-100` `#f1f5f9` | Panel in einer Karte, neutrales Badge, Skeleton, Balkenspur, `neutral`-Button, `<code>` |
| `--color-surface-inset-hover` | `slate-700` `#334155` | `slate-200` `#e2e8f0` | Hover auf Inset-Flächen, SegmentedControl-Divider-Träger |
| `--color-field` | `slate-950` `#020617` | `#ffffff` | `.app-input`, `.app-input-sm`, DateTime-Trigger |
| `--color-border` | `slate-800` | `slate-200` | Kartenrand, Divider, `border-b` der Tab-Leisten |
| `--color-border-strong` | `slate-700` | `slate-300` | Popover-Rand, `outline`-Button, EmptyState-Strichrand |
| `--color-border-field` | `slate-500` `#64748b` | `slate-500` `#64748b` | Eingabefeld-/Control-Ränder — **einziges Token mit identischem Wert in beiden Modi**, weil es in beiden 3:1 erreicht (§5.3) |
| `--color-emote-canvas` | `slate-800` | ~~`slate-800`~~ `slate-200` | Bildfläche der Emote-Kacheln; die Themefestigkeit ist am 2026-08-02 zurückgenommen worden (§2.1) |

Text:

| Token | Dark | Light | Verwendung |
|---|---|---|---|
| `--color-text` | `slate-100` | `slate-900` | Überschriften, aktive Tabs, Kartentitel |
| `--color-text-body` | `slate-200` | `slate-800` | Fließtext, `<dd>`-Werte |
| `--color-text-secondary` | `slate-300` | `slate-700` | Labels, Listentext, Badge-Neutraltext |
| `--color-text-muted` | `slate-400` | `slate-600` | Meta, Hinweise, Placeholder, `<dt>`-Begriffe. **Ersetzt auch alle heutigen `text-slate-500`** |
| `--color-text-disabled` | `slate-600` | `slate-400` | Deaktivierte Kalendertage, deaktivierte Labels (WCAG-1.4.3-befreit) |

Akzent:

| Token | Dark | Light | Verwendung |
|---|---|---|---|
| `--color-accent` | `purple-500` | `purple-600` | Fokusring, aktive Tab-Unterkante, Fortschrittsfüllung, Input-Fokusrand |
| `--color-accent-solid` | `purple-600` | `purple-600` | `appButton="primary"`, gewählter Kalendertag |
| `--color-accent-solid-hover` | ~~`purple-500`~~ `purple-700` | `purple-700` | Hover darauf. Die hier vorgeschlagene Richtungsumkehr ist am 2026-08-02 zurückgenommen worden: `purple-500` gibt weißer Schrift nur 4,1:1, der Hover wäre also unter AA gefallen. Gefüllte Buttons dunkeln jetzt in **beiden** Modi ab |
| `--color-accent-selected` | `purple-700` | `purple-700` | SegmentedControl/Filter-Toggle „ausgewählt" |
| `--color-accent-text` | `purple-400` | `purple-700` | Akzent-Linktext, „heute", BackLink |
| `--color-accent-wash` | `purple-950` | `purple-50` `#faf5ff` | EmptyState-Icon-Kachel, Selektions-Wash, BackLink-Hover |
| `--color-on-accent` | `#ffffff` | `#ffffff` | Text auf gefüllter Akzentfläche |

Semantisch — je Ton drei Rollen (`wash` = Fläche, `text` = Schrift darauf, `solid` = gefüllte Fläche/Punkt/Balken):

| Ton | Rolle | Dark | Light | Verwendung |
|---|---|---|---|---|
| success | wash / text | `emerald-950` / `emerald-300` | `emerald-50` / `emerald-700` | Badge „läuft", „Token gesetzt" |
| success | solid | `emerald-700` | `emerald-700` | Vote-Zustand „keep" (mit `on-accent`) |
| success | dot | `emerald-500` | `emerald-600` | Health-Punkt, Ampelbalken |
| warning | wash / text | `amber-950` / `amber-300` | `amber-50` / `amber-700` | Warn-Banner, degradiert-Badge, Drift-Meldungen |
| warning | dot | `amber-500` | **`amber-700`** | Health-Punkt, Ampelbalken — `amber-600` verfehlt 3:1 auf heller Spur (§5.2) |
| danger | wash / text | `red-950` / `red-300` | `red-50` / `red-700` | Fehler-Banner, Feldfehler, Fehlerlisten |
| danger | solid | `red-800` | `red-700` | `danger-solid`-Button, Vote-Zustand „delete" |
| danger | dot | `red-500` | `red-600` | Ampelbalken, Status-Punkt |
| info | wash / text | `blue-950` / `blue-300` | `blue-50` / `blue-700` | Badge „Moderator", „eingeschränkt" |
| neutral | wash / text | `slate-800` / `slate-300` | `slate-100` / `slate-700` | Badge „inaktiv", `notice-banner` info |

Nicht-Farb-Tokens:

| Token | Dark | Light |
|---|---|---|
| `--ep-shadow-card` | `none` | `0 1px 2px rgb(15 23 42 / .06), 0 1px 3px rgb(15 23 42 / .10)` |
| `--ep-shadow-card-hover` | `0 8px 24px -12px rgb(88 28 135 / .55)` | `0 8px 24px -12px rgb(15 23 42 / .18)` |
| `--ep-shadow-overlay` (Popover/Dialog) | `0 10px 30px -12px rgb(0 0 0 / .6)` | `0 10px 30px -10px rgb(15 23 42 / .22)` |
| `--ep-backdrop` | `rgb(0 0 0 / .6)` | `rgb(15 23 42 / .35)` |
| `--ep-sticky-alpha` | `85 %` | `92 %` (hell braucht mehr Deckung, sonst „schmutzt" der Text durch den Blur) |
| `--ep-page-glow` | `rgba(147,51,234,.14)` / `rgba(236,72,153,.10)` | `rgba(147,51,234,.06)` / `rgba(236,72,153,.04)` |
| `color-scheme` | `dark` | `light` |

**Vier Akzentstufen bleiben bewusst erhalten** (`accent`, `accent-solid`, `accent-selected`, `accent-text`). Heute existieren fünf `purple`-Stufen ohne Namen, was zu der Inkonsistenz `bg-purple-600` (Button) vs. `bg-purple-700` (SegmentedControl, DateRange) für dieselbe Bedeutung „ausgewählt/primär" geführt hat. Die Token-Ebene macht diese Inkonsistenz sichtbar; sie **im selben Zug zu vereinheitlichen** wäre eine sichtbare Änderung am dunklen Modus und ist Teil von offener Entscheidung Nr. 4.

---

## 5. Kontrast-Nachweis

Gerechnet nach WCAG 2.1 Relativluminanz (sRGB), Kontrast = (L₁+0,05)/(L₂+0,05). Schwellen: **4,5:1** normaler Text, **3:1** große Schrift (≥ 18,66 px fett / 24 px) und UI-Ränder/Fokusringe/Bedeutungsträger-Grafiken (1.4.11).

### 5.1 Textpaare

| Paar | Dark | Light | Gate |
|---|---:|---:|---|
| `text` auf `surface` | 16,2:1 | 17,9:1 | 4,5 ✅ |
| `text` auf `page` | 18,9:1 | 17,1:1 | 4,5 ✅ |
| `text-body` auf `surface` | 14,3:1 | 14,7:1 | 4,5 ✅ |
| `text-secondary` auf `surface` | 12,0:1 | 10,4:1 | 4,5 ✅ |
| `text-muted` auf `surface` | 7,0:1 | 7,6:1 | 4,5 ✅ |
| `text-muted` auf `page` | 7,9:1 | 7,2:1 | 4,5 ✅ |
| `text-muted` auf `surface-inset` | 5,7:1 | 6,9:1 | 4,5 ✅ |
| `accent-text` auf `surface` | 6,8:1 | 7,0:1 | 4,5 ✅ |
| `accent-text` auf `page` | 7,7:1 | 6,7:1 | 4,5 ✅ |
| `on-accent` (weiß) auf `accent-solid` | 5,4:1 | 5,4:1 | 4,5 ✅ |
| `on-accent` auf `accent-selected` (`purple-700`) | 7,0:1 | 7,0:1 | 4,5 ✅ |
| `on-accent` auf success-solid (`emerald-700`) | 5,5:1 | 5,5:1 | 4,5 ✅ |
| `on-accent` auf danger-solid (`red-700`) | 6,5:1 | 6,5:1 | 4,5 ✅ |
| Badge success (text auf wash) | 9,9:1 | 5,2:1 | 4,5 ✅ |
| Badge warning | 10,4:1 | 4,8:1 | 4,5 ✅ |
| Badge danger | 8,5:1 | 5,9:1 | 4,5 ✅ |
| Badge accent | 8,5:1 | 6,5:1 | 4,5 ✅ |
| Badge info | 8,2:1 | 6,1:1 | 4,5 ✅ |
| Badge neutral | 9,9:1 | 9,5:1 | 4,5 ✅ |

Die hellen Badge-Werte liegen erwartbar niedriger (4,8–6,5 statt 8,2–10,4), weil ein `-50`-Wash weniger Spielraum lässt als ein `-950`-Wash. Alle bleiben über AA; für **AAA** (7:1) wäre im Hellen durchgehend `-800` statt `-700` als Badge-Text nötig. Offene Entscheidung Nr. 6.

### 5.2 Ränder, Ringe, Grafiken (3:1)

| Element | Dark | Light | Gate |
|---|---:|---:|---|
| Fokusring `accent` auf `page` | 5,1:1 | 5,2:1 | 3,0 ✅ |
| Fokusring `accent` auf `surface` | 4,5:1 | 5,4:1 | 3,0 ✅ |
| Fokusring `accent` auf `surface-inset` | 3,7:1 | 4,9:1 | 3,0 ✅ |
| `border-field` (`slate-500`) auf `surface` | 3,7:1 | 4,8:1 | 3,0 ✅ |
| `border-field` auf `field` | 3,6:1 | 4,8:1 | 3,0 ✅ |
| Aktive Tab-Unterkante `accent` auf `page` | 5,1:1 | 5,2:1 | 3,0 ✅ |
| Selektions-`inset-ring` `accent` auf `surface` | 4,5:1 | 5,4:1 | 3,0 ✅ |
| Ampel success (dot/Balken) auf `surface-inset` | 5,8:1 | 3,4:1 | 3,0 ✅ |
| Ampel warning auf `surface-inset` | 6,1:1 | **4,6:1** mit `amber-700` | 3,0 ✅ |
| Ampel danger auf `surface-inset` | 4,3:1 | 4,4:1 | 3,0 ✅ |
| `border` (Kartenrand) auf `page` | 1,4:1 | 1,2:1 | — dekorativ, kein Gate; im Hellen trägt der Schatten (§4.1) |

**Der einzige echte Ausrutscher:** Im Hellen erreicht `amber-500` gegen die helle Balkenspur nur **2,0:1** und `amber-600` **2,9:1** — beide fallen durch. Erst `amber-700` (#b45309) erreicht 4,6:1. Amber ist von Natur aus mittelhell und hat auf hellem Grund keine Reserve. `amber-700` liest sich allerdings eher bräunlich als warnend; die Alternative wäre `orange-600` (#ea580c) — das führt eine neue Farbfamilie ein, die es im Projekt bisher nicht gibt. Offene Entscheidung Nr. 6.

### 5.3 Bestandsverstöße, die der Umbau nebenbei behebt

| Heutiges Paar | Ist | Soll | Behebung im Konzept |
|---|---:|---:|---|
| `text-slate-500` auf `bg-slate-900` (13×) | **3,7:1** | 4,5 | Stufe entfällt, geht in `--color-text-muted` (`slate-400`, 7,0:1) auf |
| `text-slate-500` auf `bg-slate-950` | **4,2:1** | 4,5 | dito (7,9:1) |
| `.app-input`-Rand `slate-700` auf `bg-slate-900` | **1,7:1** | 3,0 | `--color-border-field` = `slate-500` → 3,7:1 |
| `disabled:opacity-50` auf `primary` im Hellen | 2,3:1 | (befreit, aber wirkt kaputt) | Eigener Disabled-Zustand: `surface-inset` + `text-disabled` statt Opazität |

Beides sind sichtbare Änderungen am *bestehenden* dunklen Modus. Ränder werden minimal präsenter, die schwächste Textstufe verschwindet.

---

## 6. Umschaltung & Persistenz

**Zustandsmodell — drei Werte, nicht zwei:** `'system' | 'light' | 'dark'`. Ein reiner Zweizustand-Toggle kann „folge dem System" nicht ausdrücken; ein Nutzer, der einmal umgeschaltet hat, säße für immer fest.

**Default: `'system'`.** Es gibt keinen guten Grund, `prefers-color-scheme` zu ignorieren, und ohne gespeicherte Wahl ist die Systempräferenz das beste verfügbare Signal. Praktischer Nebeneffekt: Bestandsnutzer mit dunklem System sehen keine Änderung.

**Ort:** In der Shell-Kopfzeile rechts, unmittelbar neben dem `LanguageSwitcher` — dieselbe Kategorie (persönliche Darstellungspräferenz, keine Domänenaktion). Auf schmalen Viewports wandert er in die bestehende Mobile-Disclosure, wo der Sprachumschalter schon liegt; die `h-14`-Kopfzeile hat mobil keinen Platz für ein weiteres Element (§8.5 Höhenvertrag).

**Form:** `<app-theme-menu>` auf Basis des vorhandenen `<app-popover>` (§7.1 Designsprache), Trigger ist ein Icon-Button mit `aria-haspopup="menu"`, das Panel enthält drei Zeilen (System / Hell / Dunkel) mit `role="menuitemradio"` + `aria-checked`, `min-h-11 sm:min-h-9`. Bewusst **kein** `SegmentedControl`: der ist laut §10 für Wechsel gedacht, die neben der Auswahl nichts auslösen — inhaltlich träfe das zu, aber drei Segmente mit Textlabels kosten in der Kopfzeile zu viel Breite, und die Popover-Variante hat mit `date-range-menu.ts` bereits ein Vorbild. Bewusst **kein** durchklickender Icon-Button: der nächste Zustand ist dann nicht ansagbar.

**Persistenz:** `localStorage['emotepurge.theme']`. Unkritisch — es ist eine Darstellungspräferenz, keine Sitzungsinformation; die Regel „Auth-Session gehört nicht in `localStorage`" bleibt unberührt. Ohne Eintrag gilt `'system'`. Zusätzlich hört der Service auf `matchMedia('(prefers-color-scheme: dark)').addEventListener('change')`, damit ein Systemwechsel bei geöffneter App sofort durchschlägt, solange `'system'` aktiv ist.

**Flash of wrong theme:** Angular bootet erst nach dem ersten Paint; würde der Service das Attribut setzen, blitzte bei hellem System kurz die dunkle Seite auf. Gegenmittel ist ein **synchrones Inline-Skript im `<head>` von `index.html`**, vor dem Stylesheet-Link, ~8 Zeilen: `localStorage` lesen, sonst `matchMedia`, Ergebnis als `document.documentElement.dataset.theme` setzen. Der `ThemeService` liest dieses Attribut danach als Ausgangszustand, statt es neu zu bestimmen.
Zwei Randbedingungen: (a) `body { background-color }` liegt bereits korrekt auf `body` und wird mit umgeschaltet — der Kommentar in `styles.css:8-12` bleibt gültig; (b) falls die Api für `wwwroot` eine Content-Security-Policy mit `script-src` ohne `'unsafe-inline'` ausliefert, braucht das Skript eine Nonce oder einen Hash. **Das ist vor Welle 0 zu prüfen** — ich habe es nicht verifiziert.

**Native Controls:** `color-scheme` wird pro Theme im selben Selektor gesetzt (§3.2). Damit folgen `input[type="time"]` im DateTime-Picker, Scrollbars, Autofill-Hintergründe und die Standard-Formularelemente automatisch.

**`theme-color`:** Das `<meta name="theme-color">` in `index.html:11` wird durch **zwei** Tags mit `media="(prefers-color-scheme: light|dark)"` ersetzt — das deckt den `'system'`-Fall ohne JavaScript ab. Bei expliziter Nutzerwahl aktualisiert der `ThemeService` das passende Tag zusätzlich per DOM. Das **PWA-Manifest** kann grundsätzlich nur einen Wert tragen; `background_color`/`theme_color` bleiben dunkel (offene Entscheidung Nr. 8).

---

## 7. Migrationsplan

### 7.1 Wellen

**Welle 0 — Fundament (kein sichtbarer Unterschied im Dunkeln)**
Token-Ebene in `styles.css` (Rohwerte + `@theme inline`), alle 8 `.app-*`-Klassen auf Tokens, `data-theme` auf `<html>`, Anti-FOUC-Skript, `ThemeService` + `<app-theme-menu>`, `.app-page-glow` als Klasse aus den drei Kopien extrahiert, `light:`-Variante registriert. Der Umschalter existiert danach, aber die Feature-Seiten sind noch nicht themefähig — er bleibt bis Ende Welle 2 **unsichtbar** (Service-seitig vorhanden, Trigger nicht gerendert), damit niemand in einen halb migrierten Zustand schaltet.
*Ergebnis:* Dark 1:1 unverändert, Light technisch schaltbar.

**Welle 1 — Primitives**
Die 9 TS-Farb-Maps plus die ~15 Komponenten unter `shared/`. Enthält die echten Entscheidungen: Badge-/Banner-Inversion, `info`-Banner bekommt ein eigenes Token, SegmentedControl-Divider, Disabled-Zustand ohne `opacity-50`, DateTime-Picker-Leiter, `shadow-xl` → `--ep-shadow-overlay`, Alpha-Flächen in `seven-tv/*`.
*Ergebnis:* Alle wiederverwendbaren Bausteine sind themefähig — das ist der Hebel, weil sie in jedem Feature vorkommen.

**Welle 2 — Die eingeloggte App** ← *kleinste benutzbare Auslieferung*
`app-shell.ts`, `overview-page.html`, `usage-stats-page.html`, `vote-session-list-page.html`, `vote-session-detail-page.html`, `my-votings-page.ts`, `channel-workspace-layout.ts`, `login-page.ts`, `create-vote-session-dialog.ts`. Enthält die Emote-Kachel-Entscheidung (§2.1) und die Selektionszustände.
*Ergebnis:* **Hier wird der Umschalter sichtbar geschaltet.** Die komplette angemeldete Nutzung ist in beiden Modi benutzbar.

**Welle 3 — Admin**
`admin-layout.ts`, `admin-monitoring-page.ts`, `admin-channels-page.ts`, `admin-users-page.ts`, `admin-channel-detail-page.ts`, `admin-roster-card.ts`, `admin-audit-log-page.ts` (~110 Treffer). Darf nachziehen: der Bereich ist Allowlist-beschränkt und für Betreiber, nicht für Endnutzer.

**Welle 4 — Landing, Login-Chrome, Assets**
`landing-page.html` (52 Treffer, Gradient-Heading, Hero-Glow, `shadow-purple-950/50`), Logo-Varianten, `theme-color`-Tags, Manifest-Entscheidung.
Darf am längsten warten: die Landing-Page ist in sich geschlossen hart dunkel und wirkt bis dahin nicht kaputt, sondern schlicht dunkel — auch wenn der Rest hell steht. Das ist ein bewusst hingenommener Stilbruch auf Zeit, keine Panne.

### 7.2 Kann eine Welle stecken bleiben?

**Nein.** Alle fünf Wellen sind reine Codearbeit ohne externe Abhängigkeit. Die hellen Logo-Varianten (§2.2) sind ein nachreichbarer Feinschliff und hängen keiner Welle vor der Tür — kommen sie später, ist es ein Einzeiler pro Einbindungsstelle plus ein Lauf von `make-icons.ps1`.

### 7.3 Wie neue UI nicht wieder hart dunkel wird

**Neuer Abschnitt in `docs/UI-Designsprache.md` als §2.0** (vor „2.1 Kartenfläche", weil er der Kartenregel logisch vorgelagert ist):

> **2.0 Farbe kommt aus Tokens, nicht aus der Palette.**
> **Was gilt:** Kein Template, keine Varianten-Map und keine Komponentenklasse schreibt eine Tailwind-Paletten-Farbe (`slate-*`, `purple-*`, `red-*`, `amber-*`, `emerald-*`, `blue-*`, `white`, `black`) direkt. Erlaubt sind ausschließlich die semantischen Utilities aus dem Tokensatz (`bg-page`, `bg-surface`, `bg-surface-inset`, `text-*`, `border-*`, `accent-*`, die Ton-Tripel `success|warning|danger|info-{wash,text,solid}`). Paletten-Namen stehen an genau einer Stelle: im Tokenblock von `web/src/styles.css`.
> **Wann anwenden:** Immer. Braucht eine neue UI eine Farbe, die es als Token nicht gibt, wird **das Token ergänzt** — mit Wert für **beide** Modi und mit gerechnetem Kontrastnachweis im Commit — nicht die Palette benutzt. Wo sich die Modi strukturell und nicht nur im Wert unterscheiden (Schatten statt Leuchten, anderes Bildasset), ist die `light:`-Variante das Mittel; sie bleibt die Ausnahme.
> **Referenz:** `web/src/styles.css` (Tokenblock), `docs/Konzept-Light-Mode.md` §4.

**Erzwungen wird das über ein CI-Gate**, sonst ist es eine Bitte: ein `rg`-Schritt (oder eine ESLint-Regel `no-restricted-syntax` auf Template-/String-Literale) verbietet `(bg|text|border|ring|divide|from|via|to|accent|shadow)-(slate|purple|red|amber|emerald|blue|pink)-[0-9]{2,3}` unterhalb von `web/src/app/`. Allowlist: `web/src/styles.css`. Das ist ein einzeiliger Check, läuft in Sekunden und macht den Rückfall unmöglich statt unwahrscheinlich. Er wird am Ende von **Welle 2** scharf geschaltet, mit den noch offenen Admin-/Landing-Dateien als befristeter Ausnahmeliste, die Welle 3 und 4 abräumen.

---

## 8. Verifikation

**UI-Audit-Harness (§12 Designsprache).** Die Matrix ist heute ~20 Szenarien × 3 Viewports × 2 Locales = ~120 Zustände. Theme als vierte Dimension verdoppelt das auf ~240.
- *In der Welle, die ein Theme ausliefert:* volle Matrix × 2 Themes, Screenshots beider Modi sichten.
- *Danach dauerhaft:* Dark voll (3 Viewports × 2 Locales), Light nur bei 1280 × 2 Locales — das hält die Laufzeit bei ~1,3× statt 2×, und Layoutbrüche sind ohnehin themeunabhängig (die Farbe ändert keine Kastengrößen). Die vorhandenen Gates (`horizontalOverflowPx` = 0, `beyondRightEdge` = 0, keine neuen `smallTargetsUnder24`) bleiben unverändert.

**Neu: automatisierter Kontrast-Gate.** §10 fordert „AXE-pass", geprüft wird das heute aber nur manuell. Mit `@axe-core/playwright` im Audit-Harness lässt sich pro Zustand die Regel `color-contrast` laufen; neue Metrik `contrastViolations`, **Gate: 0 auf `serious`/`critical`**. Das ist der höchste Einzelnutzen im ganzen Verifikationsteil — es hätte die beiden Bestandsverstöße aus §5.3 gefunden, bevor ich sie von Hand nachgerechnet habe, und es sichert jedes künftige Token ab. Einschränkung, die man kennen muss: axe rechnet nur, was es als Text über einer bestimmbaren Fläche erkennt — halbtransparente Stapel und Grafik-Kontrast (1.4.11) fallen durch das Raster und bleiben Handarbeit.

**Playwright-E2E (`web/e2e/`).** Ein neuer Spec `theme.spec.ts`: (1) Default folgt `prefers-color-scheme` — Playwright kann das per `colorScheme`-Kontextoption setzen; (2) explizite Wahl überlebt einen Reload; (3) kein FOUC — per `addInitScript` prüfen, dass `document.documentElement.dataset.theme` schon vor dem Angular-Bootstrap gesetzt ist; (4) Systemwechsel bei geöffneter App schlägt durch, solange `'system'` gewählt ist (`page.emulateMedia`). Die bestehenden E2E-Flows laufen weiter nur in einem Modus — sie prüfen Verhalten, nicht Farbe.

**Vitest.** `theme.service.spec.ts` nach Regel 12: `localStorage`-Lesen/Schreiben, Fallback auf `matchMedia`, Attributsetzung, `matchMedia`-Change-Listener, Aufräumen. `<app-theme-menu>` wird nicht isoliert getestet (Komponententests sind bewusst nicht Teil der Konvention).

**Manuell, unersetzbar:** die Emote-Kachel mit echten 7TV-Assets im hellen Modus (§2.1) und das Logo (§2.2) — beides hängt an Fremd-/Binärmaterial, das kein Harness bewerten kann.

---

## 9. Aufwandsschätzung

| Welle | Inhalt | Aufwand | Risiko |
|---|---|---:|---|
| **0** | Tokenebene, `.app-*`-Neufassung, ThemeService, Menü, FOUC-Skript, Glow-Dedup, `light:`-Variante | **5–7 h** | CSP-Frage beim Inline-Skript (§6). Sonst gering — Dark bleibt bitgleich, Regressionen fallen sofort auf |
| **1** | 9 Farb-Maps + ~15 `shared/`-Komponenten (~140 Treffer) | **3–4 h** | Mittel: hier liegen die echten Entscheidungen (Badge-Inversion, Divider, Disabled) |
| **2** | Shell + 8 Feature-Dateien (~130 Treffer), Emote-Kachel, Selektionszustände | **4–5 h** | Mittel: Emote-Kachel braucht eine Sichtprüfung mit echtem Material |
| **3** | Admin, 7 Dateien (~110 Treffer) | **2–3 h** | Gering, mechanisch |
| **4** | Landing + Login-Chrome + Assets (~60 Treffer) | **3–4 h Code** | Gering. Helle Logo-Varianten sind nachreichbar (§2.2), +0,5 h wenn sie vorliegen |
| **V** | Audit-Harness um Theme-Dimension + axe-Gate, `theme.spec.ts`, Service-Spec | **3–4 h** | Gering |
| | **Summe** | **20–27 h** | |

Kleinste benutzbare Auslieferung (Wellen 0–2 + der Kontrast-Teil von V): **12–17 h**. Danach ist die gesamte angemeldete Anwendung in beiden Modi benutzbar; Admin und Landing sind dann noch dunkel-fest, aber in sich stimmig.

Die Zahlen setzen voraus, dass die Ersetzungen mit Kopf statt per `sed` passieren: 65 % der Treffer sind mechanisch, aber die restlichen 35 % sind genau die Stellen, an denen heute eine Farbe zwei Rollen gleichzeitig spielt (`bg-slate-800` ist Inset-Fläche *und* Neutral-Button *und* Skeleton *und* Balkenspur *und* Hover-Ziel).

---

## 10. Offene Entscheidungen — hier brauche ich deine Antwort

| # | Frage | Meine Empfehlung |
|---|---|---|
| 1 | ~~**Logo im hellen Modus**~~ — **erledigt.** Nachgemessen (§2.2): kein Blocker, der Verlaufskorpus trägt auch auf Weiß. Helle Varianten werden separat per ChatGPT erstellt und nachgereicht | Welle 4 läuft ohne sie an; Umschaltung per `<picture>`/`[ngSrc]`, sobald die Dateien da sind |
| 2 | **Emote-Kachel** (§2.1): dauerhaft dunkle Leinwand in beiden Modi, oder helle Kachel mit Karomuster/Rand? | Dunkle Leinwand — **am 2026-08-02 nach dem Ansehen zurückgenommen**, s. §2.1 |
| 3 | **Default-Modus**: `'system'` oder weiterhin fest dunkel bis zur expliziten Wahl? | `'system'` |
| 4 | **AA-Fixes am Bestand** (§5.3: `text-slate-500` 13×, Input-Ränder 1,7:1, dazu die drei uneinheitlichen Purple-Stufen für „ausgewählt"): im Zuge des Umbaus mitnehmen oder als eigener, vorgelagerter `fix:`-Commit? | Vorgelagerter eigener Commit vor Welle 0. Dann bleibt das Theming-Diff frei von „warum sieht Dark plötzlich anders aus?" |
| 5 | **Landing-Page**: mitziehen (Welle 4) oder bewusst dauerhaft dunkel als Marketing-Fläche? | Mitziehen. Eine helle App hinter einer dunklen Landing wirkt wie ein Bruch, nicht wie Absicht |
| 6 | **Warnfarbe im Hellen** (§5.2): `amber-700` (4,6:1, wirkt bräunlich) oder `orange-600` (neue Farbfamilie im Projekt)? Analog: Badge-Texte im Hellen auf `-800` heben, um auch AAA zu erreichen? | `amber-700`, keine neue Familie. AAA nicht anstreben — der Anspruch im Repo ist AA |
| 7 | **Audit-Harness-Matrix**: dauerhaft beide Themes über alle 3 Viewports (~240 Zustände) oder Light nur bei 1280? | Light nur bei 1280 nach der Auslieferungswelle; volle Matrix nur in der Welle selbst |
| 8 | **PWA-Manifest**: `background_color`/`theme_color` dunkel lassen (Splash-Screen bleibt dunkel), oder auf hell umstellen? | Dunkel lassen — der Splash gehört optisch zum Icon, und das Icon hat `#020617` eingebrannt |
| 9 | **CSP** (§6): Liefert die Api für `wwwroot` eine `script-src`-Policy aus? Falls ja, braucht das Anti-FOUC-Skript eine Nonce | Vor Welle 0 verifizieren — habe ich nicht geprüft |
