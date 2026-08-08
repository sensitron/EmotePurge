# UI-Designsprache

Verbindliche Vorgabe für alle UI-Arbeit unter `web/`.

**Dieses Dokument beschreibt ausschließlich den geltenden Stand.** Was einmal anders war, welche Regel eine frühere abgelöst hat und warum, steht in [DECISIONS.md](DECISIONS.md) — hier steht keine Vorgeschichte, keine Zwischenstufe und kein „bis dahin galt". Wer wissen will, wie eine Regel entstanden ist, sucht sie dort per `grep`; wer wissen will, was heute zu tun ist, liest nur hier.

Format pro Regel: **Was gilt** · **Wann anwenden** · **Referenz** (Dateipfad der Muster-Implementierung). Dieses Dokument ist das **Wie**, DECISIONS.md das **Warum**-Log. Bei Widerspruch zwischen einem älteren DECISIONS-Wortlaut und diesem Dokument gilt dieses Dokument.

Wer neue UI baut, arbeitet die [Checkliste in Abschnitt 11](#11-checkliste-neue-ui-bauen) ab und verifiziert per [Audit-Harness (Abschnitt 12)](#12-verifikation-per-ui-audit-harness) — dann braucht es kein neues UI/UX-Audit.

---

## 1. Geltung

- Gilt für alles unter `web/` — neue Seiten, neue Komponenten, Änderungen an bestehenden.
- Ergänzt [`web/.claude/CLAUDE.md`](../web/.claude/CLAUDE.md) (Angular-Konventionen, Signals, Standalone) — beide gelten kumulativ.
- Bestandscode wird nicht rückwirkend umgeschrieben (CLAUDE.md-Sprachregel gilt analog): Abweichungen werden bei der nächsten Berührung der Stelle behoben, nicht in Sammel-Refactorings.

## 2. Farbe & Flächen

### 2.0 Farbe kommt aus Tokens, nicht aus der Palette

- **Was gilt:** Kein Template, keine Varianten-Map und keine Komponentenklasse schreibt eine Tailwind-Paletten-Farbe (`slate-*`, `purple-*`, `red-*`, `amber-*`, `emerald-*`, `blue-*`, `pink-*`, `white`, `black`) direkt. Erlaubt sind ausschließlich die semantischen Utilities aus dem Tokensatz. Paletten-Namen stehen an genau **einer** Stelle: im Tokenblock von `web/src/styles.css`.

  | Rolle | Utilities |
  |---|---|
  | Flächen | `bg-page` (der Grund) · `bg-surface` (die eine erhöhte Fläche, s. 2.1) · `bg-surface-inset` · `bg-surface-inset-hover` · `bg-field` |
  | Ränder | `border-border` · `border-border-strong` · `border-border-field` (Bedienelemente, 3:1-Pflicht — s. 5.1) |
  | Text | `text-fg` · `text-fg-body` · `text-fg-secondary` · `text-fg-muted` · `text-fg-disabled` |
  | Akzent | `bg-accent` · `bg-accent-solid` (+`-hover`) · `bg-accent-selected` · `text-accent-fg` (Akzent **als Text**) · `bg-accent-wash` · `text-on-accent` (Text **auf** gefüllter Akzentfläche) |
  | Töne | `{success,warning,danger,info,neutral}-{wash,fg,solid,dot}` — `wash` = getönte Fläche, `fg` = Schrift darauf, `solid` = gefüllte Fläche mit `on-accent`-Schrift, `dot` = kleine bedeutungstragende Grafik (Statuspunkt, Balkenfüllung; schuldet 3:1, nicht 4,5:1) |
  | Sonstiges | `shadow-overlay` (Popover/Dialog) · `bg-emote-canvas` (Bildfläche einer Emote-Zelle) |

- **Wann anwenden:** Immer. Braucht eine neue UI eine Farbe, die es als Token nicht gibt, wird **das Token ergänzt** — mit Wert für **beide** Modi und mit gerechnetem Kontrastnachweis in der Commit-Message — nicht die Palette benutzt. Unterscheiden sich die Modi strukturell statt nur im Wert, ist das zuerst ein Hinweis, dass das Token falsch geschnitten ist; erst danach eine CSS-Variante. **Es gibt keine themefeste Farbe mehr** — `bg-emote-canvas` war die eine dokumentierte Ausnahme und wurde nach dem Ansehen zurückgenommen (s. 2.4). Ein eigenes Token heißt „eigene Rolle", nicht „fester Wert".
- **Tone-Namen sind Bedeutungen, keine Farben.** `StatusBadgeTone` und `SlotBudgetTone` heißen `accent · info · success · neutral · warning · danger`. Ein Aufrufer, der `red` verlangt, verlangt einen Farbwert — und den gibt es nicht, weil hinter `danger` pro Modus ein anderer steht. Das gilt für jede neue Ton-Union.
- **Gefüllte Buttons werden im Hover dunkler — in beiden Modi.** `*-solid-hover` liegt immer eine Stufe unter `*-solid`. Die Regel ist keine Optik, sondern der einzige Weg, den Hover kontrastsicher zu halten: `on-accent` ist in beiden Modi weiß, ein *hellerer* Hover kann Kontrast also nur wegnehmen. **Kein Werkzeug fängt einen Verstoß** — axe wertet nur aus, was gerade gerendert ist, und ein Hover ist das nie.
- **Die Werte stehen bewusst nicht hier**, sondern nur in `web/src/styles.css`. Eine zweite Wertetabelle in Markdown driftet ab dem ersten nachgezogenen Token; der Tokenblock im Code wird bei jeder Farbänderung zwangsläufig angefasst und kann von sich selbst nicht abweichen. Dieses Dokument führt die **Rollen**, der Code die Werte. Der Kontrastnachweis gehört in die Commit-Message der Änderung, die ihn nötig macht; das jeweils engste Paar steht in §10 und wird dort mitgezogen.
- **Erzwungen**, nicht erbeten: `npm run lint` fährt `web/scripts/check-color-tokens.mjs` mit und verbietet Paletten-Utilities unterhalb `web/src/app/`.
- **Referenz:** `web/src/styles.css` (Tokenblock).

### 2.0a Themes: Umschaltung, Persistenz, kein Aufblitzen

- **Drei Zustände, nicht zwei:** `'system' | 'light' | 'dark'` (`THEME_PREFERENCES` in `core/theme/theme.service.ts`), Default `'system'`. Ein Zweizustands-Toggle kann „folge dem System" nicht ausdrücken — wer einmal umschaltet, säße sonst für immer fest.
- **Der aufgelöste Modus steht als `data-theme` auf `<html>`**, nicht auf dem Shell-`<div>`: der CDK-Overlay-Container hängt außerhalb der App-Shell und erbt sonst keine Tokens.
- **`public/theme-init.js` läuft synchron im `<head>`, vor dem Stylesheet.** Angular bootet erst nach dem ersten Paint; ohne dieses Skript blitzte bei heller Systempräferenz die dunkle Seite auf. Der `ThemeService` **liest** das gesetzte Attribut als Ausgangszustand, statt ihn neu zu bestimmen. Das Skript ist bewusst eine eigene Datei und kein Inline-Block — so braucht es unter der CSP der Api keine Nonce.
- **Persistenz: `localStorage['emotepurge.theme']`.** Eine Darstellungspräferenz, keine Sitzungsinformation — die Regel „Auth-Session gehört nicht in `localStorage`" bleibt davon unberührt. Zusätzlich hört der Service auf `matchMedia('(prefers-color-scheme: dark)')`, damit ein Systemwechsel bei geöffneter App sofort durchschlägt, solange `'system'` gilt.
- **`theme-color` sind zwei Metas** (`data-theme-mode="light"|"dark"` + `media`), damit der `'system'`-Fall ohne JavaScript stimmt; bei expliziter Wahl korrigiert der Service zusätzlich per DOM.
- **`color-scheme` wird pro Modus im Tokenblock auf `:root` gesetzt** — dadurch folgen `input[type="time"]`, Scrollbars und Autofill von selbst, ohne eine einzige eigene Regel.
- **Bedienelement ist `<app-display-preferences>`** — zwei beschriftete `SegmentedControl`-Gruppen (Darstellung, Sprache) im Ton `quiet`, die ausschließlich im Panel von `<app-account-menu>` (§7.1) leben. Es gibt keine zweite Stelle im Repo, an der Theme oder Sprache umgestellt werden; Shell, Landing und Login setzen dasselbe Menü an dieselbe Stelle. Bewusst kein durchklickender Icon-Button: dessen nächster Zustand ist nicht ansagbar. Und bewusst `role="radiogroup"` statt `role="menuitemradio"` — das Panel hält gemischte Kinder, für die `role="menu"` nicht gilt.
- **Eingeloggt liegen sie eine Ebene tiefer**, hinter der Zeile „Einstellungen"; ausgeloggt zeigt das Panel sie direkt. Das ist die Bemerkenswert-wie-oft-Regel auf *Platz* statt auf Farbe angewandt: beides wird einmal eingestellt und danach nur noch bestätigt, darf also nicht der größte Posten in einem Panel sein, das mit dem Produkt wächst. Gemessen am Prototyp: die Wurzel schrumpft von 322 auf 190 px, und der Abstand wächst mit jedem weiteren Eintrag. **„Nur kleiner machen" war die verworfene Alternative** — sie brachte 20 px und hätte das 44-px-Daumenziel gekostet. Ausgeloggt gilt die Ebene nicht: eine Zeile, die eine Unteransicht mit ihrem einzigen Inhalt öffnet, ist eine Tür vor einer Tür.
- **Referenz:** `web/src/app/core/theme/theme.service.ts` (+ `theme.service.spec.ts`), `web/src/app/shared/ui/display-preferences.ts`, `web/src/app/shared/ui/account-menu.ts`, `web/public/theme-init.js`, `web/src/index.html`, Flow-Test `web/e2e/theme.spec.ts`.

### 2.1 Flächen: geriffelte Zeile und randloser Abschnitt

- **Es gibt keine Kartenklasse.** `styles.css` führt weder `.app-card` noch eine Entsprechung, und es kommt keine zurück. Flächen entstehen aus geriffelten Zeilen, randlosen Abschnitten und getönten Blöcken — nicht aus umrandeten Rechtecken.
- **Die Prüffrage vor jeder neuen Abgrenzung:** eine Karte ist eine Grenze gegen einen **andersartigen** Nachbarn. Ist jeder Nachbar dieselbe Sorte Ding — in einer Liste ist jede Zeile eine Zeile, auf einer Diagnoseseite ist jede Sektion ein Subsystem, das über sich selbst berichtet —, dann zeichnet ein Rand acht Rechtecke, wo eine Linie „Liste" deutlicher sagt, und konkurriert mit dem Einzigen, was auffallen muss: dem Subsystem, das nicht in Ordnung ist. Wer eine Fläche abgrenzen will, beantwortet zuerst diese Frage.
- **Zeilenliste:** `-mx-3 divide-y divide-border border-y border-border` am `<ul>`, `px-3 py-3` je `<li>`. Klickbare Zeilen zusätzlich `relative transition-colors hover:bg-surface-inset`. Der negative Rand lässt den Hover-Wisch über den Text hinausatmen, während die Inhalte auf der linken Kante der Seite bleiben. Umgesetzt in `overview-page.html`, `vote-session-list-page.html`, `my-votings-page.ts`, `admin-channels-page.ts`, `admin-users-page.ts`, `audit-log-list.ts`.
- **Abschnittsstapel:** `flex flex-col gap-3 border-t border-border pt-4` je Sektion, Überschrift links und Statusmarker rechts. Umgesetzt in `admin-monitoring-page.ts`, `admin-channel-detail-page.ts`.
- **Getönter Block** (`rounded-md bg-surface-inset px-3 py-3`) für das, was tatsächlich gegen Andersartiges grenzt: Panels *in* einer Seite, das 7TV-Token-Formular, der Fortschrittslauf. Fläche statt Rand — die Tönung sagt „hier gilt etwas anderes", ohne ein Rechteck zu zeichnen.
- **Die Tiefenwirkung entsteht pro Modus anders, und das ist Absicht:** dunkel trennt über Flächenhelligkeit, hell über einen echten Elevationsschatten — im Hellen liegen Blatt und Grund nur 1,10:1 auseinander und könnten eine Erhöhung gar nicht tragen. Die *Richtung* bleibt in beiden Modi gleich: erhöht entfernt sich vom Grund, eingelassen (`surface-inset`) geht zu ihm zurück. Das Overlay ist die einzige erhöhte Fläche der App, `--ep-shadow-overlay` deshalb das einzige Schatten-Token.
- **Referenz:** `web/src/styles.css`, `web/src/app/features/overview/overview-page.html`.

### 2.2 Hover nur bei Klickbarkeit

- **Was gilt:** Eine Hover-Reaktion kommt **nur** auf tatsächlich klickbare Zeilen — `relative transition-colors hover:bg-surface-inset`. Statische Zeilen und Abschnitte bleiben stumm: Hover darf nie einen Klick versprechen, den es nicht gibt.
- **Wann anwenden:** Genau dann, wenn die Zeile den Stretched-Link-Kontrakt (2.3) erfüllt. Bedingte Anwendung ist erlaubt und der Normalfall — `[class]`-Binding schaltet den Hover-Teil nur bei Klickbarkeit zu (`overview-page.html` tut das pro Channel über `isTracked`).
- **Referenz:** `web/src/app/features/admin/admin-channels-page.ts` (bedingt), `web/src/app/features/overview/overview-page.html`.

### 2.3 Stretched-Link-Kontrakt (vollflächig klickbare Zeilen)

- **Was gilt:** Klickbare Listenzeilen nutzen das Stretched-Link-Pattern über `.app-card-link` (Inclusive-Components-„Cards"-Muster). Der Klassenname benennt das Pseudoelement, nicht eine Fläche — er gilt unverändert für geriffelte Zeilen. Der Kontrakt hat drei Pflichtteile:
  1. Zeilencontainer ist `relative`.
  2. **Ein** kurzer echter Link (Titel/Name) trägt `app-card-link` — sein `::after` dehnt die Klickfläche über die ganze Zeile; Screenreader hören nur den kurzen Namen.
  3. **Jede** Sekundäraktion in der Zeile (Buttons, weitere Links) liegt in einem Container mit `relative z-10` und bleibt separat klick- und fokussierbar.

  Kanonisches Markup:

  ```html
  <li class="relative flex items-center gap-4 px-3 py-3 transition-colors hover:bg-surface-inset">
    <a [routerLink]="[...]" class="app-card-link max-w-full truncate font-medium">#{{ name }}</a>
    <div class="relative z-10 ml-auto flex gap-2">
      <button type="button" appButton="danger" (click)="...">…</button>
    </div>
  </li>
  ```

- **Wann anwenden:** Jede Listenzeile, deren primäre Aktion „öffnen/ansehen" ist. **Nicht:** die ganze Zeile als `<a>` wrappen (ungültig bei inneren Buttons, aufgeblähter Accessible Name) oder ein JS-Klick-Handler auf dem Container.
- **Referenz:** `web/src/app/features/overview/overview-page.html`, `web/src/app/features/admin/admin-channels-page.ts`, `web/src/app/features/voting/vote-session-list-page.html`.

### 2.4 Bildfläche einer Emote-Zelle

- **Was gilt:** Die Fläche, auf der ein 7TV-Emote gezeichnet wird, ist `bg-emote-canvas` — ein **eigenes Token**, nicht `surface-inset`. Grund: das Bildmaterial ist fremd, für dunkle Chats gezeichnet und enthält weiße Schrift und helle Outlines. Diese Fläche wird deshalb irgendwann anders entschieden werden müssen als „irgendeine eingelassene Fläche", und dann muss es eine Zeile sein.
- **Sie folgt dem Theme**, statt in beiden Modi dunkel zu bleiben. Eine themefeste dunkle Leinwand stünde auf **jeder** Zelle und wäre auf einer hellen Seite deren lautestes Element — während die Emotes, die sie schützen soll, die Minderheit sind. Bewusst in Kauf genommener Preis: ein weiß umrandetes Emote verliert im Hellen seine Kontur. Der Handel steht an genau einer Stelle und ist eine Zeile.
- **Der Selektions-Wash liegt auf der Zelle, nicht unter dem Bild.** Sonst kämpfen Wash und Bildmaterial um dieselben Pixel — der `inset-ring` (8.5) trägt die Auswahl, die Fläche verstärkt sie nur.
- **Die Fläche ist flach — kein Alpha-Karo.** Ein Karo beantwortet eine Frage, die diese Seiten nie stellen (*welche Pixel sind transparent*), und lässt das Nie-benutzt-Band wie eine andere Art Ding aussehen statt wie dieselbe Sache mit einer Null darauf.
- **`.app-sprite-cell-void` ist Stimmzettel-only.** Dort markiert sie ein **archiviertes** Emote mitten in einem gemischten Raster, in dem keine Überschrift das sagt — der Fall, für den eine eigene Platte da ist. Im Atlas hat sie nichts zu suchen: „im Zeitraum nie benutzt" sagen dort bereits die Bandüberschrift, die gedruckte 0 auf jeder Zelle und der fehlende Füllbalken. **Eine Markierung, die auf jedes Mitglied ihrer eigenen Gruppe zutrifft, markiert nichts** — sie kostet nur die Gleichmäßigkeit des Bogens.
- **Referenz:** `web/src/styles.css` (`--color-emote-canvas`, `.app-sprite-cell`), `web/src/app/features/usage-stats/usage-stats-page.html`, `web/src/app/features/voting/vote-session-detail-page.html`.

### 2.5 Das Sprite-Blatt: Bänder, Sidecar, Dock

Nutzungsseite und Stimmzettel sind keine Listen, sondern **ein Bogen gleichartiger Zellen**. Was für sie gilt, gilt für nichts anderes in der App — und umgekehrt.

- **Die Bänder sind Pareto-Schnitte aus dem Set selbst, keine festen Schwellen.** `heavy` = die Emotes, die zusammen die erste Hälfte der Nutzung ausmachen · `regular` = bis 80 % · `rare` = der Rest mit mindestens einem Treffer · `dead` = null. Feste Grenzen („ab 1000 ist es viel") sind für einen großen Channel richtig und für einen kleinen bedeutungslos — dort landete jedes Emote im selben Band und die Gruppierung trüge keine Information. Die Reihenfolge ist fest (`USAGE_BAND_ORDER`, schwer zuerst): der Leser scannt nach unten auf die Kandidaten zu.
- **Geschnitten wird über Werte, nicht über Ränge.** Ein Rangschnitt zerteilt eine Gruppe gleicher Zählerstände willkürlich in der Mitte; ein Wertschnitt hält sie zusammen.
- **Der Füllbalken einer Zelle misst gegen die Spitze ihres *Bandes*, nicht gegen die des Sets.** Sonst wäre im Schwanz jeder Balken leer und das Band ohne Aussage.
- **Bandüberschriften sind Haarlinie plus Label** — dieselbe Form wie beim `EmptyState` (6.2) und den Landing-Stufen, nicht eine fünfte Sorte Überschrift.
- **Der Sidecar ist die Lupe ab `lg`** (`<aside>`, sticky, 16 rem; das Raster wird erst zweispaltig, wenn tatsächlich etwas inspiziert ist). Darunter trägt eine kompakte Meta-Zeile (`lg:hidden`) dieselben Zahlen. **Der Drilldown-Dialog bleibt** — er ist der einzige Weg auf dem Stimmzettel, unterhalb `lg`, per Touch und per Tastatur, und trägt zusätzlich Y-Achse, Spitzensatz, Live-Tage-Legende und Erstnutzung.
- **Der Sidecar lädt nie pro Zelle nach.** Seine Tagesreihe kommt aus **einem** Aufruf pro (Channel, Zeitraum) — `GET /usage-stats/series`. Eine Fläche, die am Mauszeiger hängt, darf keine Requests erzeugen; das Durchfahren eines Bandes wäre sonst ein Lastprofil.
- **`.app-dock` erscheint nur, solange eine Auswahl besteht.** Eine dauerhaft geparkte Aktionsleiste ist ein Bedienelement, an dem der Erstbesuch vorbeilesen muss. Der Dock trägt als einzige Fläche der App eine Linie in der Leitfarbe — sie markiert die Grenze eines lebenden, umkehrbaren Zustands.
- **Selektion, Dock und der 20-px-Verlaufs-Trigger sind zusätzlich hinter `PointerModeService.isCoarse` gegated — kein 7TV-Schreibzugriff ohne Maus.** Das 7TV-Schreib-Token lässt sich nur aus den Entwicklertools auf 7tv.app kopieren, die ein Telefon nicht hat; das Gate ist deshalb die Zeigerart, nicht die Breite (`(pointer: coarse)`, nicht `any-pointer` — ein Desktop mit angestecktem Touchscreen behält alles, weil DevTools bleiben). Auf `coarse` markiert ein Klick auf die Zelle nichts mehr, sondern öffnet direkt den Drilldown-Dialog (§7.1), und Mass-Delete- wie Restore-Panel rendern gar nicht erst.
- **Was auf `coarse` wegfällt, wird nicht erklärt — was auf `coarse` ins Leere zeigt, schon.** Dock, Mass-Delete- und Restore-Panel verschwinden kommentarlos: visuell fehlt nichts, also gibt es nichts zu sagen. Ein *Verweis* auf eine dieser Fähigkeiten ist der andere Fall — er bleibt sichtbar stehen und verspricht etwas, das sein Ziel dort nicht einlösen kann. Einziges Beispiel bisher: der Link „Nur bestimmte Emotes zur Wahl stellen?" unter dem Erstellen-Formular der Voting-Liste, der auf coarse einem Satz weicht (`voting.list.wholeSetHintDesktopOnly`). Rein visuelles Umschalten dieser Art gehört ins Variantenpaar `pointer-coarse:hidden` / `hidden pointer-coarse:inline`, nicht in `PointerModeService` — der Dienst ist für Entscheidungen, die der Code trifft.
- **Tastatur ist gleichwertig, nicht nachgereicht:** Roving-Tabindex über den Bogen, Pfeile bewegen, Leertaste markiert, Enter öffnet den Verlauf, Umschalt+Klick markiert einen Bereich. Der Hinweistext dazu ist übersetzt und steht sichtbar an der Seite — eine Tastaturbedienung, die niemand erwähnt, existiert für die meisten nicht.
- **Hover-Flächen tragen keine Klickziele.** Was nur beim Überfahren erscheint, ist per Touch nicht erreichbar; jede Aktion des Bogens hat einen Weg ohne Mauszeiger.
- **Referenz:** `web/src/app/shared/emotes/usage-bands.ts` (+ `usage-bands.spec.ts`), `usage-series.ts`, `usage-sparkline.ts`, `web/src/styles.css` (`.app-sprite-cell*`, `.app-dock`), `web/src/app/features/usage-stats/usage-stats-page.html`; Flow-Test `web/e2e/usage-atlas.e2e.spec.ts`.

## 3. Typografie-Hierarchie

- **Was gilt:** Vier Ebenen, feste Klassenketten:

  | Ebene | Klassen | Element |
  |---|---|---|
  | Seitentitel | `text-2xl font-bold tracking-tight` | `<h1>` in Layouts, `<h2>` auf Seiten ohne eigenes Layout-`<h1>` |
  | Sektionstitel | `text-lg font-semibold` | `<h2>` |
  | Blocktitel | `text-base font-semibold` | `<h3>` |
  | Listenzeilen-Titellink | `font-medium` (Textgröße erbt vom Kontext) | `<a class="app-card-link">` / `<span>` |

  Ein `<h3>` trägt **nie** die Sektionsgröße `text-lg`: zwei Ebenen, die gleich aussehen, sind eine Ebene.
- **Wann anwenden:** Immer. Das Heading-**Level** folgt der Dokumentstruktur (eine Seite unter einem Layout-`<h1>` beginnt bei `<h2>`), die **Optik** folgt der Tabelle — beides ist unabhängig voneinander einzuhalten.
- **Ausnahme:** Die Landing-Page (`web/src/app/features/landing/landing-page.html`) ist bewusst Marketing-skaliert (`text-4xl`/`sm:text-5xl`-Hero, `sm:text-3xl`-Sektionen) und folgt der Tabelle nicht.
- **Referenz:** `web/src/app/features/admin/admin-layout.ts` (Seitentitel), `web/src/app/features/usage-stats/usage-stats-page.html` (Sektionstitel), `web/src/app/features/admin/admin-monitoring-page.ts` (Blocktitel).

## 4. Buttons, Badges, Banner

### 4.1 Buttons: `appButton`

- **Was gilt:** Jeder Button/Aktions-Link nutzt die Attribut-Direktive `appButton` (`web/src/app/shared/ui/button.ts`) — keine kopierten Utility-Ketten. Varianten `primary`/`neutral`/`outline`/`danger`/`danger-quiet`/`danger-solid`, Größen `md` (Default)/`lg`. Element-spezifisches Layout (`ml-auto`, `relative z-10`, …) bleibt am eigenen `class`-Attribut, Angular merged beides.

  | Variante | Einsatz |
  |---|---|
  | `primary` | die eine Haupt-Aktion eines Kontexts (Login, Erstellen, Speichern) |
  | `neutral` | Sekundäraktionen mit Fläche (Aktualisieren, Kopieren) |
  | `outline` | leise Sekundäraktionen, Abbrechen in Dialogen |
  | `danger` | siehe 4.2 |
  | `danger-quiet` | siehe 4.2 |
  | `danger-solid` | siehe 4.2 |

- **Schalter: `[buttonPressed]` statt einer eigenen Klassenkette.** Ein Button mit `aria-pressed` bekommt seinen Ein-Zustand vom Primitiv; die Variante bleibt daneben stehen und gilt im Aus-Zustand. Die Füllung ist bewusst dieselbe wie beim gewählten Segment des `SegmentedControl`: „dieses hier ist an" soll gleich aussehen, ob allein oder als eines von mehreren. Einziger Unterschied ist der Hover-Schritt — ein einzelner Schalter lässt sich erneut drücken, ein gewähltes Radio nicht. **Ein Zustand, der nur als angehängter Pfeil im Label steht, erreicht Screenreader und sonst niemanden** und ist deshalb kein zulässiger Ersatz.
- **Ein Filter mit benannten Bereichen plus freien Feldern ist ein Popover-Menü**, kein Lauf einzelner Felder: Trigger nennt die aktuelle Einstellung, Panel enthält die Voreinstellungen und tauscht seinen Inhalt gegen die Felder, sobald „eigener Bereich" gewählt ist (`DateRangeMenu` für Zeiträume, `UsageRangeMenu` für Nutzungsspannen). **Kein Schalter, der in Wahrheit Feldwerte setzt:** ein „nur ungenutzte"-Toggle, der still min = 0/max = 0 in die Felder daneben schreibt, ist kein eigener Filter, sondern eine unsichtbare Überschreibung.
- **Eins-aus-N ist kein Schalterpaar, sondern `<app-segmented-control>`.** Zwei Toggles, bei denen ein zweiter Klick auf den aktiven heimlich etwas anderes umschaltet, verstecken eine ganze Einstellung: Sortierschlüssel und Sortierrichtung sind zwei Fragen und bekommen zwei Controls.
- **Referenz:** `web/src/app/shared/ui/button.ts`, `web/src/app/shared/ui/segmented-control.ts`; Schalter-Aufrufstellen `web/src/app/features/usage-stats/usage-stats-page.html`.

### 4.2 Destruktiv-Stufung: Flow-Position, nicht Schwere

- **Was gilt:** Die Destruktiv-Stufen kodieren die **Position im Bestätigungs-Flow**, nicht die Schwere der Aktion:
  - `danger` (Outline): der **auslösende** destruktive Button im Seitenkontext, der neben anderen Controls steht und noch einen Bestätigungsschritt vor sich hat (Channel verlassen, Channel-Purge öffnen).
  - `danger-quiet` (nur Schrift, Wash beim Hover): **derselbe Auslöser, wenn er sich je Listenzeile wiederholt.** Zwanzig rot umrandete „Löschen"-Knöpfe untereinander machen die seltenste Aktion einer Seite zu ihrem lautesten Element. Die Stufe ist an die **Wiederholung** gekoppelt, nicht an geringere Schwere — der Bestätigungsdialog dahinter bleibt unverändert, es fällt allein der dauerhafte rote Kasten weg.
  - `danger-solid` (gefüllt): der **ausführende** Button — der Bestätigen-Button in `ConfirmDialog`/`TypedConfirmDialog`/Mass-Delete-Dialog sowie der Seiten-Haupt-CTA des Mass-Delete-Panels.

  Merksatz: Outline löst aus, Solid vollzieht, Quiet ist Outline in Serie. Dass die unwiderrufliche Purge per Outline **ausgelöst** und das reversible Verlassen per Solid **bestätigt** wird, ist damit korrekt.
- **Wann anwenden:** Jede destruktive Aktion bekommt Auslöser **und** Vollzug: `danger`/`danger-quiet`-Auslöser → Dialog → `danger-solid`-Bestätigung. Ein destruktiver Button ohne Bestätigungsdialog ist nicht vorgesehen.
- **Schwere rechtfertigt keine Ausnahme von der Wiederholungsregel.** Auch Purge und Session-Revoke laufen in den Admin-Listen als `danger-quiet` — je länger die Liste, desto schlimmer die Farbleiter. Abgesichert wird eine unwiderrufliche Aktion durch die typisierte Namensbestätigung, nicht durch einen roten Rahmen, den man fünfundzwanzigmal untereinander sieht.
- **Referenz:** Auslöser: `web/src/app/features/channel-workspace/channel-workspace-layout.ts`; in Serie: `web/src/app/features/voting/vote-session-list-page.html`, `web/src/app/features/admin/admin-channels-page.ts`, `web/src/app/features/admin/admin-users-page.ts`. Vollzug: `web/src/app/shared/ui/confirm-dialog.ts`, `web/src/app/shared/seven-tv/mass-delete-panel.ts`.

### 4.3 StatusBadge

- **Was gilt:** Ein `<app-status-badge>` markiert eine **bemerkenswerte** Eigenschaft, nicht jede Eigenschaft. Der Baustein kennt nur Töne, die Bedeutung liegt beim Aufrufer:

  | Tone | Verwendung im Bestand |
  |---|---|
  | `accent` | hervorgehobene Eigenschaft |
  | `info` | Hinweis-Eigenschaft |
  | `success` | LIVE · „läuft"-Zustände |
  | `neutral` | inaktiv/neutral |
  | `warning` | degradiert/Warnung |
  | `danger` | Fehler/getrennt |

- **Die Prüffrage lautet „bemerkenswert wie oft?"** Eine Pille, die in jeder Zeile steht, markiert nichts mehr — sie ist eine Farbleiter. Was auf den meisten Zeilen dasselbe Wort ist, wird stiller Text: die **Rollen** in der Übersicht (Broadcaster/Moderator/7TV-Editor) sind eine Tatsache über *dich* und wiederholen sich; die **Abstimmungs-Zielgruppe** trägt ihre Einschränkung als eine Kontraststufe statt als blaue Pille; **offline** ist der unauffällige Fall. Die Pille bleibt dem vorbehalten, was *gerade jetzt* gilt und nicht auf jeder Zeile steht — **LIVE** ist der Musterfall.
- **Zustand ≠ Eigenschaft.** Wovon eine Zeile gerade *ist* (Bot misst / Session läuft / Twitch-Token vorhanden), ist `<app-state-dot>` mit `tone="on"|"off"` — Punkt plus Wort statt Pille. Damit konkurriert der Zustand nicht mit den Eigenschaften daneben, und die Farbe trägt keine Bedeutung, die das Wort nicht schon trägt.
- **Subsysteme, die ihre eigene Gesundheit melden, benutzen `<app-health-marker>`.** Der Baustein entscheidet die Darstellung anhand des Tons: `ok`/`idle` werden zum Punkt, `warning`/`danger` zur Pille. Damit trägt eine gesunde Monitoring-Seite **keine einzige farbige Pille**, und das erste auffällige Subsystem ist ohne Zutun das lauteste Element auf ihr. Das ist derselbe Satz wie oben, nur andersherum gelesen: die Aussage entsteht durch **Abwesenheit**, und Abwesenheit funktioniert nur, wenn nichts anderes sie ausgibt. Aufrufer bilden ihren Domänenstatus auf den `HealthTone` ab (`admin-monitoring-page.ts`, `admin-roster-card.ts`, `admin-channel-detail-page.ts`).
- **Der App-Rahmen schweigt im Normalfall ganz.** Für den Kopfbereich gilt die Regel eine Stufe strenger als für eine Seite: was dort steht, steht auf **jedem** Bildschirm in **jeder** Sitzung, und selbst der stille Punkt-plus-Wort-Zustand von `HealthMarker` ist dafür zu viel. Beim Worker-Status steht dort bei `connected` und bei `unknown` **nichts** und nur bei `stale` eine Warn-Pille. `unknown` schweigt mit, weil es der Zustand vor der ersten Antwort des Polls ist: eine Warnung dort blitzt bei jedem Kaltstart auf. **Und was der Rahmen sagt, sagt keine Seite ein zweites Mal** — zwei Aussagen derselben Tatsache auf einem Bildschirm sind genau das, was dieses Kapitel abräumt.
- **Der Text nennt die Folge, nicht das Subsystem.** „Chat wird nicht gezählt" statt „Worker getrennt": wer die Meldung liest, will wissen, was *für ihn* gerade nicht stimmt, nicht welcher Prozess welchen Namen trägt.
- **Ein transienter Zustand ist keine Warnung.** Der SSE-Stream steht beim Laden jeder Admin-Seite kurz auf `connecting`; als `warning` gerendert hätte das bei jedem Seitenaufruf eine gelbe Pille aufblitzen lassen und einem Admin beigebracht, gelbe Pillen zu übersehen. `connecting` ist deshalb `idle`.
- **Das Label geht als `label`-Input hinein, nicht als projizierter Inhalt.** Ein `<ng-content>` in zwei Zweigen eines Control-Flow-Blocks wird von Angular nur einmal befüllt — beim Wechsel zwischen Punkt und Pille wäre der Text sonst still verschwunden.
- **Referenz:** `web/src/app/shared/ui/status-badge.ts`, `web/src/app/shared/ui/state-dot.ts`, `web/src/app/shared/ui/health-marker.ts`; Verwendung `overview-page.html`, `vote-session-list-page.html`, `admin-*`.

### 4.4 NoticeBanner

- **Was gilt:** Jede seitenweite Meldung ist ein `<app-notice-banner>`; keine Ad-hoc-Fehlerboxen oder gefärbten Absätze. `variant="error"` rendert `role="alert"` (wird vorgelesen), `info`/`warning` bleiben `role="status"` (still). Aktions-Button in den `[notice-action]`-Slot (rechtsbündig).
- **Wann anwenden:** `error` = fehlgeschlagener Request (Text via `apiErrorTranslationKey`, s. 9), `warning` = degradierter Zustand (Reauth nötig, Bot inaktiv), `info` = gutartiger Wartezustand (Sync ausstehend).
- **Nicht für etwas, das der App-Rahmen schon sagt** (4.3). Ein Banner ist für das, was **diese Seite** betrifft; alles App-weite trägt der Kopfbereich, und zwar allein.
- **Referenz:** `web/src/app/shared/ui/notice-banner.ts`; Verwendung `overview-page.html`, `usage-stats-page.html`.

## 5. Formulare & Validierung

### 5.1 Inputs

- **Was gilt:** `.app-input` ist der einzige Input-Stil, `.app-input-sm` die kompakte Variante für Filter-Toolbars. Beide bringen expliziten `color` mit (nötig im CDK-Overlay außerhalb der Shell-DOM).
- **Der Rand ist Vertrag, nicht Optik:** Er trägt `border-border-field` und muss gegen die Fläche darunter **mindestens 3:1** erreichen (WCAG 1.4.11 — ein Eingabefeld wird durch seinen Rand überhaupt erst als Bedienelement erkennbar). Dieses Token ist das **einzige mit demselben Wert in beiden Modi**: es liegt bewusst in der Mitte, damit es gegen ein dunkles *und* gegen ein helles Feld reicht. Wer einen input-artigen Trigger von Hand nachbaut statt `.app-input` zu benutzen (der DateTime-Trigger tut das), schuldet denselben Wert — und einen Hover, der den Rand-Kontrast **erhöht**. `hover:border-fg-muted` tut genau das in beiden Modi (im Dunkeln heller, im Hellen dunkler); eine fest verdrahtete Richtung wäre in einem der beiden Modi falsch herum.
- **Referenz:** `web/src/styles.css`, `web/src/app/shared/datetime/datetime-picker.ts` (nachgebauter Trigger).

### 5.2 Label-Pflicht

- **Was gilt:** Jedes Feld hat entweder ein sichtbares `<label for="…">` + `id` am Input, oder — nur in Filter-Toolbars, wo kein sichtbares Label vorgesehen ist — `[attr.aria-label]` (+ `[title]` für den Maus-Tooltip).
- **Referenz:** sichtbares Label: `web/src/app/shared/ui/typed-confirm-dialog.ts`; `aria-label`-Fall: `web/src/app/features/usage-stats/usage-stats-page.html` (Filterleiste).

### 5.3 Feldfehler-Muster

- **Was gilt:** Feldbezogene Validierungsfehler folgen einem festen Muster:

  ```html
  <input
    id="feld-id"
    [formControl]="control"
    class="app-input"
    [attr.aria-invalid]="control.invalid && control.touched ? 'true' : null"
    [attr.aria-describedby]="control.invalid && control.touched ? 'feld-id-error' : null"
  />
  @if (control.invalid && control.touched) {
    <p id="feld-id-error" class="text-sm text-danger-fg">{{ 'x.y.error' | transloco }}</p>
  }
  ```

  Fest: Fehlertext `text-sm text-danger-fg`, Fehler-`<p>` mit `id`, Input mit `aria-invalid` + `aria-describedby` nur im Fehlerfall. Formular-**übergreifende** Fehler (Request fehlgeschlagen) laufen dagegen über `NoticeBanner variant="error"` (4.4), nicht über Feldfehler.
- **Wann anwenden:** Jedes Feld mit Client-Validierung, dessen Fehler sichtbar wird. Ein still bleibendes `invalid` ohne Anzeige (7TV-Token-Input) ist die zu vermeidende Ausnahme.
- **Referenz:** `web/src/app/features/voting/vote-session-list-page.html` (Titel-Feld), `web/src/app/features/admin/admin-channels-page.ts` (Channel-Join).

### 5.4 Validierungs-Fallen

- **Was gilt:**
  - Client-Validatoren denken die serverseitige **Normalisierung** mit, nicht nur die Server-Regex (CLAUDE.md Regel 9): Channel-Namen validieren über `channelNameValidator` aus `web/src/app/core/channels/channel-name.ts`, das den **normalisierten** Wert prüft (Nutzer tippen `HandOfBlood`).
  - `(ngSubmit)` feuert **nie** auf einem `<form>` mit nur einem standalone `[formControl]` — stattdessen `(submit)="onSubmit($event)"` mit `event.preventDefault()`.
  - Deaktivierte Submit-/Bestätigen-Buttons erklären ihren Grund als **Text** daneben, nicht nur per Ausgrauung (WCAG); Enter-Pfade prüfen die Bedingung selbst statt sich auf `disabled` zu verlassen.
- **Referenz:** `web/src/app/core/channels/channel-name.ts`, `web/src/app/shared/ui/typed-confirm-dialog.ts` (Hint + Enter-Pfad).

## 6. Lade- & Leerzustände

### 6.1 Skeleton vs. Spinner (NN/g-Regel)

- **Was gilt:** **Skeleton für Seiten-/Listen-Ladevorgänge, disabled-Button (Label bleibt konstant) für isolierte Aktionen.** Keine „Lädt…"-Textzeilen, keine Spinner für Seitenladen.
  - Geriffelte Listen: `<app-skeleton-rows [count]="3" />`.
  - Abschnittsseiten (Admin-Monitoring, Admin-Channel-Detail): `<app-skeleton-sections [count]="3" />`.
  - Abweichende Formen (Atlas, Stimmzettel): handgerolltes Skeleton nach demselben A11y-Muster — **ein** `role="status"`-Element mit übersetztem `aria-label`, die Schimmer-Blöcke (`.app-skeleton`) in einem `aria-hidden="true"`-Container.
  - Aktionen (Refresh, Join, Purge): Button `[disabled]="isLoading()"`, Label bleibt.
- **Das Skeleton zeichnet den Umriss des echten Inhalts, nicht irgendeinen Platzhalter** — Abstand, Kante und horizontale Ausdehnung inklusive. **Ein Skeleton, dessen Umriss vom Inhalt abweicht, lässt das Eintreffen des Inhalts wie einen Layoutfehler aussehen** und kostet damit genau die Ruhe, für die es da ist; schon ein anderes horizontales Padding zeigt sich als seitlich springender Text. Wer eine Listen- oder Abschnittsform ändert, prüft das zugehörige Skeleton **im selben Commit** — keine Testsuite sieht je ein Skeleton, hier gibt es also nichts, was den Fehler für einen fängt.
- **Referenz:** `web/src/app/shared/ui/skeleton-rows.ts`, `skeleton-sections.ts`, Grid-Variante `web/src/app/features/usage-stats/usage-stats-page.html`.

### 6.2 EmptyState

- **Was gilt:** Jeder Leerzustand ist ein `<app-empty-state>` mit `title` (warum leer) + möglichst `description` und projiziertem CTA (was als Nächstes tun). Kein nackter grauer Satz.
- **Kein Emoji, keine gestrichelte Box, nicht zentriert.** Der Baustein hat bewusst **keinen** `icon`-Input, damit keine Aufrufstelle ein Emoji unterbringen kann: ein Bilderrahmen um 🔍 ist kein Icon-System, sondern dessen Abwesenheit. Und eine zentrierte Textspalte in einer linksbündigen Seite liest sich wie ein vergessener Platzhalter — für einen korrekten und erwarteten Zustand genau die falsche Lesart. Stattdessen dieselbe Haarlinie-plus-Label-Form, die die Atlas-Bänder (2.5) und die Landing-Stufen benutzen.
- **Wann anwenden:** Liste/Grid ohne Einträge, Filter ohne Treffer — aber erst **nach** abgeschlossenem Laden (Skeleton verhindert das Aufblitzen des EmptyState während `rxResource`-Loads mit `defaultValue`).
- **Referenz:** `web/src/app/shared/ui/empty-state.ts`; Verwendung `overview-page.html`, `usage-stats-page.html`.

## 7. Dialoge

- **Was gilt:** **Jeder** Dialog läuft über `@angular/cdk/dialog` — nie `window.confirm`, nie handgebaute Overlays. Fokus-Trap, Escape, Backdrop-Klick, `aria-modal`, Fokus-Rückgabe kommen vom CDK.
- **Öffnen: nie `Dialog.open` direkt.** Jede Dialog-Komponente exportiert ihre eigene `open<X>Dialog(dialog, data)`-Funktion neben sich, die intern `openAppDialog` (`shared/ui/dialog.ts`) aufruft — dort sitzen `backdropClass`, `panelClass` und die Benennung. Der Grund ist gemessen: der Dreizeiler stand an zwölf Aufrufstellen von Hand, fünf davon hatten `ariaLabelledBy` vergessen. Ein neuer Dialog bekommt seine `open…()`-Funktion im selben Commit wie die Komponente.
- **Innen: `<app-dialog-shell>`** (`shared/ui/dialog-shell.ts`) — Fläche, Padding, Überschrift, Body, Aktionszeile. Abstände macht die Shell (Flex-Spalte), **nicht** `mb-*` an jedem Kind; enger zusammengehörender Inhalt wickelt sich in ein eigenes `flex flex-col gap-1`. Die Breite **und die Höhe** gehören dem Pane (`.cdk-overlay-pane.app-dialog-panel`), nie dem Inhalt.
- **Auf einem groben Zeiger ist derselbe Dialog ein Bottom-Sheet — dieselbe Instanz, zweite Erscheinung, kein zweiter Overlay-Stack.** Zwei getrennte `@media (pointer: coarse)`-Regeln tragen das, nicht eine: die Andockung sitzt auf dem **Wrapper**, nicht auf der Pane — `.cdk-global-overlay-wrapper:has(> .app-dialog-panel) { align-items: flex-end !important; }`, das `!important` Pflicht, weil CDKs `GlobalPositionStrategy` `align-items` als Inline-Style auf denselben Wrapper schreibt und Inline nur von `!important` geschlagen wird. Die zweite Regel bleibt auf der Pane-Klasse und ändert `max-width` (`none` statt `min(28rem, calc(100vw - 2rem))`) und `max-height` (`85dvh` statt `calc(100dvh - 2rem)`) — `width: 100%` steht schon in der Basisregel und ändert sich nie, es ist also nie „volle Breite statt 28rem", sondern „keine Kappung mehr". Wer die Andockung auf eine neue Panel-Klasse überträgt und dabei nur die Pane-Regel kopiert, bekommt einen zentrierten Dialog — derselbe Fehlertyp wie die beiden CDK-Fallen unten, nur an dieser Stelle noch nicht gelistet. Die Geometrie kommt ohne `openAppDialog()` und ohne eine Änderung an der Pane-Wahl aus; `PointerModeService.isCoarse` entscheidet live statt einmalig beim Öffnen. Die Sheet-**Chrome** dagegen sitzt in `dialog-shell.ts`: `DialogShell` rendert den Ziehbereich mit dem Attribut `data-sheet-handle` — die `SheetDrag`-Direktive sucht exakt danach, um eine Ziehgeste zuzulassen; ändert sich der Name auf einer Seite ohne die andere, bricht Drag-to-Dismiss lautlos. **Der Ziehbereich ist die ganze Oberkante des Sheets, nicht der sichtbare Balken:** die sticky Leiste ist auf `min-h-11` bemessen (44 px, das Komfortziel aus §10 — der Balken selbst ist 4 px hoch), und der Kopfblock darunter trägt dasselbe Attribut, weil ein Zug, der auf der Überschrift beginnt, das Sheet meint und nicht den Inhalt. Die beiden müssen **lückenlos** aneinanderstoßen: der Kopfblock schluckt das `gap-4` der Hülle per `-mt-4 pt-4` und gibt es als eigenes Padding zurück. Bleibt der Abstand bei der Hülle, ist er weder Griff noch `touch-none` — ein Streifen, in dem eine Geste gegen das Scrollen des Browsers antreten muss, und zwar genau dort, wohin ein Daumen zielt. `web/e2e/touch-mobile.e2e.spec.ts` pinnt davon die eine Hälfte (`toHaveCount(2)` auf `[data-sheet-handle]` innerhalb der Pane, Höhe der Leiste ≥ 44 px, und die beiden Kästen berühren sich) — dass ein echter Dialog die Griffe unter diesem Namen überhaupt rendert; für die andere Hälfte, den literalen String in `SheetDrag`, steht allein die Spec der Direktive. Die Geometrie sagt zu beidem nichts, sie kommt aus der Media-Query und hält auch ganz ohne Griff. Der Griff trägt das `rounded-t-2xl` der Hülle ein zweites Mal: seine negativen Ränder blenden ihn in die Ecken, die der Radius der Hülle frei lässt — ohne das liest sich das Sheet oben eckig. `overflow-hidden` an der Hülle wäre der andere Weg und ist verboten, es machte die Hülle zum eigenen Scroll-Container und bräche die Sticky-Verankerung des Griffs. Die Pane bleibt auch hier der Scroll-Container, siehe die CDK-Fallen unten.
- **Drag-to-dismiss** (`shared/ui/sheet-drag.ts` + `sheet-drag-policy.ts`) schließt über `DialogRef.close()`, ist also kein vierter Ausgang neben Backdrop-Tap und Escape. Die Geste beginnt nur am Griff oder wenn die Pane oben angeschlagen ist — sonst bedeutet ein Zug nach unten „Inhalt hochscrollen". Der zweite Weg ist dabei kein Ersatz für den ersten: sobald der Inhalt länger ist als das Sheet, nimmt der Browser eine Abwärtsbewegung als Scroll für sich und bricht den Pointer ab. Verlässlich ist allein, was `data-sheet-handle` trägt und damit `touch-none` ist. **Und sie beginnt erst mit der ersten Bewegung über 4 px: ein bloßes Drücken nimmt keinen Pointer-Capture und schreibt keinen Inline-Style.** Das ist keine Feinheit — `setPointerCapture` beim `pointerdown` verlegt den folgenden `click` auf das Capture-Element, also auf die Hülle, unter der jeder Knopf des Dialogs liegt. Bei Zeigertyp `touch` fällt das nicht auf, bei Zeigertyp `mouse` ist damit jeder Knopf in jedem Dialog tot — und genau diese Kombination (`pointer: coarse` bei Mauseingabe) ist die DevTools-Emulation, mit der diese Ansicht von Hand geprüft wird. Die Schwelle des Entlassens hat **drei** Zahlen, nicht zwei: `distancePx ≥ 72` **oder** `velocityPxPerMs ≥ 0,5`, aber erst ab einer Mindeststrecke von `24 px` — ohne die Untergrenze schlösse ein Vier-Pixel-Zucken beim Tippen das Sheet, weil eine kurze Bewegung per Definition schnell ist. **Beide Größen werden am Ende der Geste gemessen, nicht über ihren Verlauf**, und das ist der Teil, der zweimal falsch war: Geschwindigkeit über die ganze Geste gemittelt teilt jede Millisekunde ruhenden Fingers in das Ergebnis hinein (deshalb `VELOCITY_WINDOW_MS`, die letzten 100 ms), und der Weg, aus der letzten *gemeldeten* Bewegung gelesen, hinkt hinterher, weil der Browser `pointermove` zusammenfasst und verwirft — je hastiger die Geste, desto mehr. Eine hastige Geste ist eine kurze, sie steht und fällt also mit genau diesen beiden Zahlen.
- **Benennung (jeder Dialog braucht einen Accessible Name):** entweder eine sichtbare Überschrift — dann `[dialogTitle]` bzw. ein `[dialog-header]`-Slot mit `id="app-dialog-title"`, und `openAppDialog` verdrahtet `ariaLabelledBy` selbst — **oder** ein `ariaLabel` mit einer kurzen Aktionsphrase. Ein Dialog ohne beides ist ein Fehler.
- **Aktionszeile: Abbrechen steht immer zuerst.** Damit landet der `first-tabbable`-Default des CDK auf dem harmlosen Knopf; ein explizites `cdkFocusInitial` erübrigt sich. Eine *Wahl* (Format, Bereich) gehört in den Body als Radiogruppe, nicht als zweiter Ausgangsknopf in die Fußzeile — sonst konkurrieren gleichrangige Optionen als Buttons und eine muss willkürlich leiser gestuft werden.
- **Farbe im Dialog meint „dieser Fall ist ungewöhnlich".** Hinweise, die für *jeden* Durchlauf gelten (»unwiderruflich«, »nicht alle Channels erkennbar«), sind still — `fg-secondary`/`fg-muted`. Nur der Befund, der diesen Durchlauf von den anderen unterscheidet, wird `<app-notice-banner>`. Vier gestapelte Warnfarben im Lösch-Dialog hießen faktisch: keine davon ist wichtig.
- **Namenslisten** (»das wird gelöscht«) über `<app-name-preview-list>` — Kappung bei 50 plus gezählter Rest, geriffelte Zeilen nach §2.1, voll blutend gegen das `p-6` der Shell.
- **Wahlkriterium:**
  - `ConfirmDialog` (`shared/ui/confirm-dialog.ts`): destruktive Aktion, die eine Ja/Nein-Bestätigung braucht (Channel verlassen, Session löschen). Aufrufer übergibt fertig übersetzte `message`/`confirmLabel`. Bewusst **ohne** Überschrift — jede seiner Meldungen beginnt schon mit der Aktion; benannt wird er per `ariaLabel`.
  - `TypedConfirmDialog` (`shared/ui/typed-confirm-dialog.ts`): Aktion ist unwiderruflich **und** zeilenbezogen (Channel-Purge) — Nachtippen beweist, *welche* Zeile gemeint war. Vergleich getrimmt, aber case-sensitiv. `title` ist Pflicht.
  - Eigener Dialog nur, wenn keiner der beiden passt (z. B. Mass-Delete mit Fortschritt) — dann trotzdem `DialogShell` + eigene `open…()`.
- **Gesperrter Bestätigen-Knopf braucht seinen Grund als Text** neben sich (`mr-auto` in der Aktionszeile, per `aria-describedby` verbunden). Ausgegraut allein ist keine wahrnehmbare Erklärung (WCAG) — gilt für das Nachtipp-Feld genauso wie für den laufenden Shared-Set-Check.
- **CDK-Fallen (beide live gefunden):**
  1. Der Overlay-Container hängt **außerhalb** der App-Shell-DOM — er erbt keine Textfarbe. Dialog-Panel und `.app-input` brauchen explizites `color` (haben sie; bei neuen Overlay-Styles daran denken).
  2. CDK injiziert seine Overlay-Styles zur Laufzeit **hinter** allen Bundle-Stylesheets. Panel-Chrome muss deshalb unlayered und mit erhöhter Spezifität definiert werden (`.cdk-overlay-pane.app-dialog-panel`) — neue Panel-Regeln nach demselben Muster.
- **Referenz:** `web/src/app/shared/ui/dialog.ts`, `dialog-shell.ts`, `name-preview-list.ts`, `confirm-dialog.ts`, `typed-confirm-dialog.ts`, `web/src/styles.css` (Dialog-Klassen), Aufrufer `channel-workspace-layout.ts`, `admin-channels-page.ts`.

### 7.1 Popover (nicht-modal)

- **Was gilt:** Nicht-modale Dropdowns laufen über **`<app-popover>`** (`shared/ui/popover.ts`) — nie ein weiteres handgebautes `relative`-Wrapper-plus-`absolute`-Panel. Das Primitive bringt Panel-Chrome, `max-w-[calc(100vw-2rem)]`, Außenklick- und Escape-Dismiss mit. Der Host setzt den `position: relative`-Wrapper mit dem Marker `data-popover-anchor` um Trigger **und** Popover; Klicks darin zählen nie als Außenklick (sonst schließt der öffnende Klick das Panel im selben Dispatch wieder).
- **Vertrag:** gerendert = offen. Das Panel versteckt sich nie selbst, es emittiert `closed`; das Sichtbarkeits-Signal **und** die Fokus-Rückgabe an den Trigger gehören dem Host. Padding bringt der Inhalt mit — das Panel ist padding-frei, damit Full-Bleed-Menüzeilen und gepolsterte Formulare beide hineinpassen.
- **Abgrenzung zu §7:** Popover ≠ Dialog. Kein Fokus-Trap, kein `aria-modal`, kein Backdrop. Sobald die Interaktion den Rest der Seite blockieren soll, ist es ein CDK-Dialog. Und **kein** CDK-Overlay für den Popover-Fall: diese Panels öffnen aus Sticky-Leisten heraus und müssen deren Stacking-Kontext erben (§8.5), was ein an `<body>` gehängter Overlay-Container nicht kann.
- **Mobil:** Menüzeilen `min-h-11 sm:min-h-9` (§10, 44-px-Komfortziel bei Touch). Ein geöffnetes Popover gehört mit `afterLoad` in den Audit-Harness (§12) — geschlossen sagen die Overflow- und Touch-Target-Metriken nichts über es aus.
- **Das 44-px-Komfortziel ist inzwischen kein Popover-Sonderfall mehr, sondern an der Button-Größe `lg` verankert** (`shared/ui/button.ts`, `SIZE_CLASSES.lg = 'min-h-11 …'`): ein flow-tragender Button, der sich für `lg` entscheidet — Dialog-Bestätigungen, der Mass-Delete-Auslöser, der Schließen-Weg des Sheets —, bekommt die 44-px-Höhe darüber, ohne die Klassenkette selbst nachzubauen. Automatisch ist daran nichts: `buttonSize` ist standardmäßig `md` (23 Aufrufstellen setzen `lg` bewusst), und `md` bleibt unverändert für dichte Toolbars, wo eine Maus der realistische Zeiger ist.
- **Der 20-px-Verlaufs-Trigger auf einer Atlas-Zelle (§2.5) entfällt auf `coarse` ganz** — dort ist die ganze 64-px-Zelle das Ziel, was ein 20-px-Overlay in jeder Hinsicht schlägt und ohnehin die einzige Aktion ist, die der Tap auf coarse noch auslöst.
- **Referenz:** `web/src/app/shared/ui/popover.ts`; Verwendung `shared/datetime/date-range-menu.ts`.

## 8. Navigation

### 8.1 Tab-Leisten (Router-Link-Muster)

- **Was gilt:** Tab-Leisten sind Router-Links, **kein** ARIA-Tabs-Pattern (`role="tablist"`/`aria-selected` sind hier falsch, da echte Navigationen). Ein Tab ist `<app-tab-link>`; die Leiste bleibt beim Aufrufer, weil ihre Sticky-Position pro Ebene verschieden ist:

  ```html
  <nav class="app-sticky-bar top-14 mb-6 flex h-10 gap-2 border-b border-border">
    <app-tab-link link="usage-stats" [label]="'x.tab' | transloco" />
  </nav>
  ```

  **Der Tab selbst ist eine Primitive** (`shared/ui/tab-link.ts`) und wird nie als Klassenkette nachgebaut — ein Vertrag, der in kopierten String-Literalen lebt, driftet beim ersten Edit. `ariaCurrentWhenActive="page"` steckt in der Primitive und ist damit nicht vergessbar. `display: contents` auf dem Host: der Anker muss selbst das Flex-Kind sein, sonst zentriert er in einer eigenen Box statt die `h-10` der Leiste zu tragen.

  `h-10` und `flex items-center` (statt `py-2`) sind Teil des Sticky-Vertrags aus §8.5 — die Tab-Leisten-Höhe ist der `top`-Offset der Filter-Toolbars.
- **Referenz:** `web/src/app/shared/ui/tab-link.ts`; Leisten in `admin-layout.ts`, `channel-workspace-layout.ts`.

### 8.2 In-Page-Anker

- **Was gilt:** Anker laufen über `routerLink` + `fragment`, **nie** über nackte `href="#…"` (löst gegen `<base href="/">` auf und bricht). Der Router scrollt selbst (`withInMemoryScrolling` + `onSameUrlNavigation: 'reload'` sind in `app.config.ts` konfiguriert — nicht entfernen, der Reload-Teil macht den zweiten Klick auf denselben Anker funktionsfähig).
- **Referenz:** `web/src/app/app.config.ts`, `web/src/app/features/landing/landing-page.html`.

### 8.3 Rollen-Sichtbarkeit

- **Was gilt:** Sichtbarkeit von Navigation/Bereichen entscheidet ein **gelesenes Feld** (z. B. `isGlobalAdmin` aus dem gecachten `/me`), nie ein provozierter Fehler (403-Probing). Guards für Rollen-Bereiche leiten auf `/` (nicht `/login`) und stashen keine Return-URL. Autoritativ bleibt immer der serverseitige Filter.
- **Referenz:** `web/src/app/core/auth/admin.guard.ts`, `web/src/app/features/shell/app-shell.ts`.

### 8.4 Pagination

- **Was gilt:** Seitenweise Listen nutzen `<app-pager [page] [totalPages] [scrollTarget] (pageChange)>` gegen ein `PagedResult<T>` vom Backend (`items/page/pageSize/totalCount/totalPages`); der Pager versteckt sich bei einer Seite selbst. `create`/`delete` laden die Liste neu statt optimistisch zu patchen (verschiebt sonst die Paginierung); reine In-Place-Änderungen dürfen lokal patchen.
- **Ein Seitenwechsel repositioniert den Viewport — `[scrollTarget]` ist Pflicht, sobald die Liste länger als ein Bildschirm werden kann.** Der Pager steht unter seiner Liste, der Klick passiert also immer am unteren Dokumentende; ohne Reposition kommen die Zeilen der nächsten Seite komplett *oberhalb* des Viewports an und der Leser schaut weiter auf das Ende einer Liste, durch die er nie gescrollt ist. Übergeben wird eine Template-Referenz auf den Kopf der Ergebnisregion:

  ```html
  <h2 #resultsTop tabindex="-1" class="scroll-mt-24 text-lg font-semibold">…</h2>
  …
  <app-pager [page]="page()" [totalPages]="totalPages()" [scrollTarget]="resultsTop" … />
  ```

  Drei Teile, alle notwendig:
  - **`tabindex="-1"`** — der Pager fokussiert das Ziel (WCAG 2.4.3). Ohne Fokusverschiebung fällt der Fokus beim Seitenwechsel auf `<body>`, weil der geklickte Button samt Liste in den Lade-Zweig verschwindet.
  - **`scroll-mt-*` = Höhe des Sticky-Stapels über der Seite** (§8.5): `scroll-mt-24` unter einer Tab-Leiste (14 + 10), `scroll-mt-14` direkt unter der Shell. Ohne das parkt `scrollIntoView` das Ziel hinter dem Header. Die Filter-Toolbar bekommt keinen Anteil — sie ist selbst sticky und pinnt an ihrem eigenen `top`.
  - **Reposition vor dem `pageChange`-Emit**, im Pager gekapselt: der Emit setzt das Page-Signal, die Resource fällt auf ihren `defaultValue` zurück, und Liste wie Pager werden durch das Skeleton ersetzt. Alles danach liefe gegen ein totes Element.

  Instant, nie `behavior: 'smooth'`: das ist ein Inhaltsaustausch, kein geführter Rundgang, und mehrere Bildschirmhöhen zu animieren ist langsamer als der Leser und ein Motion-Trigger. Die Seitenzeile des Pagers ist `role="status"` und damit eine Live-Region — die Fokusverschiebung sagt *was* auf dem Schirm steht, nie *welche Seite*.
- **Der Pager bleibt bewusst im Lade-`@else` und wird nicht während des Ladens weitergerendert.** Angulars `resource` gibt bei einem Params-Wechsel `value()` auf den `defaultValue` zurück (nur ein `reload()` mit gleichen Params behält den Stream), `totalPages()` ist während des Ladens also `0` und der Pager würde sich ohnehin selbst verstecken. Ihn stehen zu lassen bräuchte in jeder Seite einen klebrigen Zweitzustand — und der Fokus ist durch die Reposition bereits gerettet.
- **Seite und Filter stehen in der URL, nicht in lokalen Signals** — über `listQueryState()` aus `core/routing/list-query-state.ts`. Ohne das ist „Zeile öffnen, zurück" ein Reset auf Seite 1, und ein geteilter Link zeigt eine andere Liste als die, die gemeint war. **Eine neue paginierte Seite nimmt den Helper, statt `page = signal(1)` zu schreiben.** Was er festlegt:

  | | Navigation | Effekt |
  |---|---|---|
  | `goToPage(n)` | `replaceUrl: false` | ein echter History-Schritt — dafür ist der Zurück-Button da |
  | `setParams(patch)` | `replaceUrl: true` | ersetzt den Eintrag, springt auf Seite 1 zurück, leert die Drafts der geschriebenen Keys |

  Filter dürfen **keinen** History-Schritt erzeugen: sie greifen pro Tastenschlag, der Zurück-Button würde sonst „einen Buchstaben rückgängig" bedeuten. Der Preis ist, dass Zurück die Filterung in einem Schritt verlässt statt sie aufzudröseln — dafür gibt es den Zurücksetzen-Button.

  Beide Navigationen tragen **`scroll: 'manual'`**. Das ist der Per-Navigation-Ausstieg aus der Scroll-Restoration und der Grund, warum die Anker-Reposition oben überhaupt noch greift: der Router würde sonst auf `[0, 0]` springen — bei jedem Tastenschlag, und beim Seitenwechsel *nach* dem Pager, unter Missachtung des `scroll-mt-*`.

  Default-Werte werden aus der URL **entfernt** statt leer geschrieben (`?page=3`, nicht `?page=3&action=&channel=&actor=`); Seite 1 steht nie drin.
- **Textfilter laufen über `query.textFilter(key, debounceMs)`, nie über ein eigenes `signal()` + `debounceTime`.** Das Tippen bleibt lokal und sofort sichtbar, die URL bekommt erst den ausgeruhten Wert — eine Router-Navigation zwischen Taste und Zeichen frisst Eingaben, und ein Wert, der mitten im Wort aus der URL zurückkommt, überschreibt den Cursor. Dass `setParams` die Drafts der Keys mitleert, die es schreibt, ist Teil des Vertrags und kein Detail: ein Wert, der noch im Debounce-Fenster hängt, hat die URL nie erreicht und würde sich sonst 300 ms später selbst zurückschreiben — unter einer Filterkombination, die die Seite gerade ausgeschlossen hat.
- **Nicht abgedeckt:** eine Seitennummer außerhalb des Bereichs (`?page=9999`) wird durchgereicht — das Backend antwortet mit einer leeren Seite und die Liste zeigt ihren Leerzustand. Clamping bräuchte `totalPages`, das es erst *nach* der Antwort gibt, die mit diesem Zustand angefordert wird.
- **Referenz:** `web/src/app/shared/pagination/pager.ts` (+ `pager.spec.ts`), `web/src/app/core/routing/list-query-state.ts` (+ `list-query-state.spec.ts`); Verwendung `admin-audit-log-page.ts` (drei Filter), `channel-activity-page.ts` (zwei), `admin-users-page.ts`, `my-votings-page.ts` (`scroll-mt-14`), `vote-session-list-page.html` (Anker ist die `<ul>` — die Seite hat über den Zeilen keine eigene Überschrift, nur das Anlege-Formular, und genau dorthin darf ein Seitenwechsel *nicht* zurückspringen). Flow-Test `web/e2e/channel-activity.e2e.spec.ts`.

### 8.4a Inhaltsbreite (eine, bewusst)

- **Was gilt:** Die Inhaltsspalte hat **eine** Breite, app-weit: `max-w-5xl` (64 rem) an der Header-Zeile und an `<main>`. Keine Seite und keine Route setzt eine eigene.
- **Der Rahmen darf pro Route nicht springen.** Eine zweite Breite für die Sprite-Blätter (2.5) ist sachlich begründbar — dort ist Breite keine Dekoration, sondern Emote-Spalten —, aber **beim Wechsel zwischen einer Blatt- und einer Listenseite springt dann der Rahmen**, und ein Layout, das bei jeder Navigation seine Breite ändert, ist unruhiger als eine Blattseite, die 500 px verschenkt. Eine routengesteuerte Shell-Breite (`data.wideLayout` o. ä.) ist deshalb ausgeschlossen, nicht offen.
- **Wenn ein Blatt mehr Breite braucht**, holt es sie *innerhalb* der konstanten Spalte — die Blattfläche bricht aus, die Shell-Spalte bleibt stehen.
- **Referenz:** `web/src/app/features/shell/app-shell.ts` (Kommentar an der Header-Zeile).

### 8.5 Sticky-Ebenen (Header · Tabs · Filter)

- **Was gilt:** Die Seite scrollt als **ein Dokument** (kein App-Frame mit innerem Scroll-Container — der bräche CDK-Virtual-Scroll, Router-Scroll-Restoration und das Einklappen der Mobile-Browserleiste). Drei Ebenen bleiben dabei per `position: sticky` sichtbar, mit **festen Höhen als Vertrag**:

  | Ebene | Höhe | `top` | z |
  |---|---|---|---|
  | Shell-Header (`app-shell.ts`) | `h-14` | `top-0` | `z-30` |
  | Tab-Leisten (§8.1) | `h-10` | `top-14` | `z-20` (via `.app-sticky-bar`) |
  | Filter-Toolbars | variabel (darf wrappen) | `top-24` | `z-20` (via `.app-sticky-bar`) |

  Sticky-Leisten nutzen die Primitive **`.app-sticky-bar`** (`styles.css`): sticky + `z-20` + abgedunkelter Blur-Hintergrund; nur der `top`-Offset kommt als Tailwind-Klasse an der Verwendungsstelle. Filter-Toolbars bekommen zusätzlich `py-2`, damit der Blur eine Fläche hat. **Neue Seite mit Filter-Toolbar ⇒ `app-sticky-bar top-24 py-2`**, neue Tab-Leiste ⇒ Snippet aus §8.1.
- **Virtualisierte Emote-Grids scrollen mit dem Dokument:** `<cdk-virtual-scroll-viewport scrollWindow>` — kein innerer Scroll-Container, keine feste Viewport-Höhe, kein Rahmen um das Grid. Die Zeilen laufen beim Scrollen bewusst unter den transluzenten Sticky-Leisten durch. Zum Vertrag gehört die Regel `cdk-virtual-scroll-viewport[scrollWindow] { overflow-anchor: none; }` in `styles.css` (bei `scrollWindow` wendet CDK `.cdk-virtual-scrollable` nicht an, das Scroll-Anchoring des Browsers würde sonst auf dem Dokument jittern) sowie `minBufferPx`/`maxBufferPx` ≥ 1×/2× Zeilenhöhe (die CDK-Defaults sind kleiner als eine Grid-Zeile). Die `ROW_HEIGHT_PX`-Konstanten der Seiten müssen die Zellhöhen-Arithmetik als Kommentar nennen.
- **z-Leiter (verbindlich):** Zeilen-Action-Container/Stretched-Link `z-10` (§2.3) < Sticky-Leisten `z-20` < Shell-/Landing-Header und `.app-dock` `z-30` (das Panel von `<app-account-menu>` liegt als `z-30` **im** Header-Kontext und damit über allem). Dropdowns, die aus einer Sticky-Leiste heraus öffnen (z. B. das Zeitraum-Menü der Usage-Stats, `shared/ui/popover.ts` via `shared/datetime/date-range-menu.ts`), erben deren `z-20`-Kontext und liegen damit über dem Content; Dropdowns im Content (Datetime-Picker im Create-Formular, `z-30` im `z-10`-Zeilen-Kontext) bleiben unter den Leisten — sie öffnen nach unten, weg davon.
- **Das Panel des Account-Menüs darf nicht beschnitten werden.** Es ist rund 320 px hoch und hängt absolut positioniert aus einem 56-px-Header, liegt also **innerhalb** des Header-Stacking-Kontexts. Das ist Absicht (`popover.ts:16-19`: Panels öffnen aus Sticky-Leisten und müssen deren Kontext erben, was ein an `<body>` gehängter CDK-Overlay-Container nicht kann). Der Preis: bekommt irgendein Vorfahr des Headers `overflow: hidden` oder `overflow: auto`, wird das Panel abgeschnitten. Heute trägt keiner davon eines. **Wer am Shell-Layout arbeitet, prüft das am Gerät nach.**
- **Warum feste Höhen:** `sticky` braucht für gestapelte Ebenen exakte `top`-Offsets. `h-14`/`h-10` sind deshalb keine Optik, sondern Berechnungsgrundlage (`top-24` = 14 + 10) — wer sie ändert, zieht alle `top`-Werte nach. Die Filter-Toolbar selbst darf beliebig hoch wrappen, ihr eigener `top` hängt nur von den Ebenen **über** ihr ab.
- **Selektions- und Hover-Zustände malen ausschließlich *innerhalb* der Zellfläche** — konkret `inset-ring-2 inset-ring-accent` statt `ring-2` (Tailwind v4; `ring-inset` gibt es dort nicht mehr). Grund ist genau diese Sticky-Konstruktion: Scroll-Container und Sticky-Leisten sind beide exakt die Content-Box von `<main class="mx-auto max-w-5xl px-4">`, und ein *outset* `ring-2` malt 2 px **außerhalb** der Border-Box. Bei den Zellen der ersten und letzten Grid-Spalte lägen diese 2 px links und rechts neben dem Hintergrundkasten der Leiste und schimmerten beim Durchscrollen durch. Die Leiste breiter zu machen ist keine Lösung — das verdeckt den Randeffekt, statt ihn zu vermeiden, und jede künftige Leiste müsste mitziehen. **Neuer Selektionszustand auf einer Zelle ⇒ `inset-ring-*`.**
- **Die Fokus-Outline bleibt bewusst outset** (globaler `:focus-visible`-Ring, §10) — sie ist transient und darf an den Randspalten nicht abgeschnitten werden. Genau deshalb steht in `styles.css` weiterhin `contain: layout style` statt CDKs `contain: content` auf `.cdk-virtual-scroll-content-wrapper`: Paint-Containment würde sie kappen. Diese Regel nicht „aufräumen".
- **Referenz:** `web/src/styles.css` (`.app-sticky-bar`, CDK-Containment-Block), `web/src/app/features/admin/admin-audit-log-page.ts` (Toolbar), `web/src/app/features/shell/app-shell.ts` (Header), `usage-stats-page.html` + `vote-session-detail-page.html` (Selektionsring).

### 8.6 Rücknavigation (hierarchischer Up-Link)

- **Was gilt:** Jede Seite, die kein Wurzelknoten ist, trägt oben links genau **einen** Up-Link auf ihren Elternknoten der *Informations*-Hierarchie — als Primitive `<app-back-link [link] [label] />` (`shared/ui/back-link.ts`), nie als handgebauter Anker und nie als `history.back()`.
- **Warum kein Verlaufs-Zurück:** Die tiefen Seiten werden regelmäßig per Deep-Link betreten und überspringen dabei ihre Liste — eine Vote-Session wird direkt nach dem Anlegen aus den Usage-Stats geöffnet, außerdem aus My-Votings und aus dem Admin-Bereich. Der Browser-Verlauf zeigt dort also gerade *nicht* nach oben; ein „Zurück" wäre sachlich falsch. Zielgenaue Up-Links sind überall konstruierbar, weil `paramsInheritanceStrategy: 'always'` (`app.config.ts`) jeder Kindroute `channelName` und `sessionId` mitgibt.
- **Warum kein Breadcrumb:** NN/g empfiehlt Breadcrumbs ab drei Ebenen — hier bildet aber die Tab-Leiste (§8.1) die Ebenen 2/3 bereits dauerhaft ab, und nur *eine* Seite (`vote-sessions/:sessionId`) liegt überhaupt auf Ebene 4. Ein Breadcrumb hätte die Tabs verdoppelt und als vierte Leiste den Höhen-Vertrag aus §8.5 gebrochen. Der Up-Link scrollt bewusst mit dem Inhalt weg, ist also keine Sticky-Ebene.
- **Wo er steht:** als erstes Element der Seite, in einer Zeile mit deren Überschrift (`flex flex-wrap items-center gap-x-4 gap-y-2`) — so wie es `ChannelWorkspaceLayout` vorgibt. Auf einer Detailseite steht er **außerhalb** des Lade-`@if`, damit der Ausweg auch beim Laden und nach einem Fehler existiert. Seiten unterhalb eines Layouts mit Up-Link (Usage-Stats, Vote-Session-Liste) bekommen **keinen zweiten** — sie erben den des Layouts.
- **Label:** der Eigenname des Ziels, nicht „Zurück" — und wo das Ziel bereits einen Key hat, **derselbe** Key (die Vote-Session-Detailseite beschriftet ihren Up-Link mit `channelWorkspace.tabs.voting`, dem Namen des Tabs, auf dem sie landet). Generische Ziele stehen unter `nav.*`.
- **Barrierefreiheit:** echter `<a routerLink>` (in neuem Tab öffenbar, kein `<button (click)="navigate()">`), der Pfeil `←` ist `aria-hidden` und damit dekorativ, der zugängliche Name wird in der Primitive auf `nav.backTo` („Zurück zu {{target}}") verbreitert — enthält den sichtbaren Text und erfüllt damit WCAG 2.5.3. Fokus über den globalen `:focus-visible`-Ring.
- **Referenz:** `web/src/app/shared/ui/back-link.ts` (+ `back-link.spec.ts`); Verwendung `channel-workspace-layout.ts`, `admin-layout.ts`, `my-votings-page.ts`, `vote-session-detail-page.html`; Flow-Test `web/e2e/back-navigation.e2e.spec.ts`.

## 9. i18n-Pflichten

- **Was gilt:**
  - Jeder sichtbare Text (auch `aria-label`, Skeleton-Labels, `title`-Tooltips) ist ein Transloco-Key mit Eintrag in **beiden** Locales (`web/public/i18n/de.json` + `en.json`). Keine hartkodierten Strings im Template.
  - Request-Fehlertexte kommen **ausschließlich** aus `apiErrorTranslationKey(error)` (`web/src/app/core/i18n/api-error.ts`): bekannter Backend-`errorCode` → `errors.api.<code>`, sonst Status-Fallback (`errors.status.*`), sonst `errors.generic`. Ein neuer Backend-`ApiErrorCode` braucht den Eintrag in `api-error.ts` **und** beiden Locales — `api-error.spec.ts` erzwingt das (CLAUDE.md Regel 7). 401 wird nie auf Seitenebene behandelt (globaler `apiAuthInterceptor`).
  - Button-/Toggle-Labels bleiben **konstant**; Zustand kodiert `aria-pressed` + Optik, nicht ein Label-Tausch (verhindert Breiten-Sprünge und Toolbar-Umbrüche).
  - Ungenutzte Keys werden beim Entfernen eines Features aus beiden Locales gelöscht.
- **Referenz:** `web/src/app/core/i18n/api-error.ts` + `api-error.spec.ts`; Toggle-Muster `web/src/app/shared/emotes/emote-usage-filter.ts`.

## 10. Accessibility-Checkliste

Basis: `web/.claude/CLAUDE.md` — **AXE-pass und WCAG-AA-Minimum sind Pflicht.** Konkret in diesem Projekt:

- [ ] **Fokus:** Der globale `:focus-visible`-Ring (`outline: 2px solid var(--ep-accent)` plus Offset — `styles.css`) gilt für alles Interaktive. Er trägt die Leitfarbe und folgt damit dem Modus von selbst. Nie `outline-none`/`focus:outline-none` ohne gleichwertigen Ersatz.
- [ ] **Touch-Targets:** ≥ 24 × 24 px für alles Interaktive (WCAG 2.5.8 AA), 44 px als Komfortziel für Primäraktionen — die Vote-Buttons erreichen es unterhalb `sm` (`min-h-11 sm:min-h-6`). Ausnahme „equivalent target" (kleines Control in einer vollflächig klickbaren Zeile) ist zulässig, wird aber **im Template als Kommentar dokumentiert**.
- [ ] **ARIA-Rollen:** `role="alert"` nur für Fehler (NoticeBanner `error`), `role="status"` für stille Meldungen und Skeleton-Wrapper; `radiogroup` + Roving-Tabindex für SegmentedControl (`shared/ui/segmented-control.ts` wiederverwenden, nicht nachbauen) — es selektiert bei Fokus und taugt deshalb nur, wenn ein Wechsel billig ist; hängt am Wechsel ein Refetch, gehört die Auswahl in ein Popover-Menü (`shared/datetime/date-range-menu.ts`), wo Pfeile nur den Fokus bewegen und Enter/Space committet (von APG für genau diesen Fall erlaubt); `ariaCurrentWhenActive="page"` auf aktiven Nav-/Tab-Links.
- [ ] **Feldfehler:** Muster aus 5.3 (`aria-invalid` + `aria-describedby` + Fehler-`id`).
- [ ] **Disabled erklärt sich:** Grund als sichtbarer Text neben dem Button (TypedConfirm-Hint-Muster), nicht nur Ausgrauung.
- [ ] **Dekoratives versteckt:** Emoji-Icons und Skeleton-Schimmer `aria-hidden="true"`.
- [ ] **Accessible Names kurz:** Stretched-Link-Zeilen lassen den Screenreader nur den kurzen Titel hören (2.3), nie die ganze Zeile als Linktext.
- [ ] **Kontrast, in beiden Modi:** Text 4,5:1, Ränder/Fokusringe/bedeutungstragende Grafiken 3:1 — **gerechnet für hell UND dunkel, nicht geschätzt** (2.0: ein neues Token bringt beide Werte plus den Nachweis in der Commit-Message mit). Die schwächste zulässige Textstufe ist `text-fg-muted`. **Ihr engster Fall ist `text-fg-muted` auf `surface-inset`: hell 4,81:1, dunkel 5,06:1** — der Wert, der bei der nächsten Verschiebung einer Fläche zuerst reißt. Wer eine Fläche verschiebt, rechnet ihn neu und zieht ihn hier nach. Input-Ränder: `border-border-field`, s. 5.1.
- [ ] **Hover-Zustände mitrechnen:** Ein Hover ist ein eigener Zustand und schuldet denselben Kontrast wie der Ruhezustand. **Kein Werkzeug prüft das** — axe kennt nur, was gerade gerendert ist. Für gefüllte Buttons erledigt die Regel aus 2.0 das (`*-solid-hover` immer eine Stufe dunkler); alles Handgebaute rechnet selbst nach.
- [ ] **axe-Kontrastgate:** Der Audit-Harness (12) fährt `@axe-core/playwright` mit der Regel `color-contrast` pro Zustand und schreibt `contrastViolations`. **Gate: 0 auf `serious`/`critical`.** Grenze, die man kennen muss: axe rechnet nur, was es als Text über einer bestimmbaren Fläche erkennt — halbtransparente Stapel verweigert es, und Grafik-Kontrast (1.4.11: Ampelpunkte, Balkenfüllungen, Ränder) deckt die Regel gar nicht ab. Beides bleibt Handarbeit.
- [ ] **Kontrast/nativ:** `color-scheme` wird **pro Theme** im Tokenblock auf `:root` gesetzt (nicht mehr fest auf `body`) — dadurch folgen `input[type="time"]`, Scrollbars und Autofill dem Modus von selbst. Farbpaare der Primitives (Badge-Tones, Banner-Varianten) nicht ad hoc neu mischen — sie sind Tokens (2.0).

## 11. Checkliste „Neue UI bauen"

Vor dem Abschluss jeder UI-Änderung abhaken:

1. [ ] **Primitives statt Utility-Ketten** — nichts davon nachbauen: `appButton` · `SegmentedControl` · `StatusBadge` · `StateDot` · `HealthMarker` · `NoticeBanner` · `EmptyState` · `SkeletonRows`/`SkeletonSections` · `Pager` · `TabLink` · `BackLink` · `Popover` · `DialogShell` + `ConfirmDialog`/`TypedConfirmDialog` + `NamePreviewList` · `DateRangeMenu`/`UsageRangeMenu` · `AccountMenu` + `DisplayPreferences` + `Avatar` · `.app-input*`.
2. [ ] **Farbe aus Tokens** (2.0): keine Paletten-Utility unter `web/src/app/` — `npm run lint` erzwingt das. Fehlt ein Token, wird es **ergänzt**, mit Werten für **beide** Modi und gerechnetem Kontrast in der Commit-Message. Und: **beide Modi angesehen**, nicht nur den, in dem gerade gearbeitet wurde.
3. [ ] **Flächen:** geriffelte Zeile bzw. randloser Abschnitt (2.1); `.app-card-link` + Stretched-Link-Kontrakt nur bei echter Klickbarkeit (2.3).
4. [ ] **Typo-Skala** eingehalten (Abschnitt 3), Heading-Level folgt Dokumentstruktur.
5. [ ] **Destruktiv-Flow:** `danger`-Auslöser → Dialog → `danger-solid`-Vollzug (4.2).
6. [ ] **Ladezustand:** Skeleton (Seite/Liste) bzw. disabled-Button (Aktion) — kein Lade-Text (6.1).
7. [ ] **Leerzustand:** `EmptyState` mit Warum + CTA (6.2).
8. [ ] **Fehlerpfad:** `apiErrorTranslationKey` + `NoticeBanner error`; Feldfehler nach 5.3.
9. [ ] **i18n:** alle Keys in **beiden** Locales, konstante Labels (9).
10. [ ] **A11y-Checkliste** (10) durchgegangen; neue Formulare mit Label + Feldfehler-ARIA.
11. [ ] **Audit-Harness** gelaufen, Gates grün inkl. `contrastViolations` (12); bei neuen Seiten: Szenario ergänzt.
12. [ ] Konvention geändert/präzisiert? → DECISIONS-Eintrag im selben Commit (CLAUDE.md Regel 3) und dieses Dokument aktualisieren.

## 12. Verifikation per UI-Audit-Harness

- **Was er ist:** Playwright-Harness getrennt von der e2e-Suite: rendert die UI-Zustands-Matrix (~30 Szenarien × 3 Viewports 360/768/1280 × de/en × **dunkel/hell**, API gemockt) und schreibt pro Zustand einen Full-Page-Screenshot + JSON-Metriken.
- **Theme ist die vierte Dimension, und sie läuft bewusst nicht voll.** Dunkel deckt alle drei Viewports ab, **hell nur 1280** — dieselbe Abwägung, die die `en`-Locale schon macht. Begründung: Layoutbrüche sind theme-unabhängig (Farbe ändert keine Kastengrößen), und was am hellen Modus wirklich zu prüfen ist, ist der Kontrast — den misst der axe-Gate in *jedem* Zustand. Das hält die Laufzeit bei ~1,3× statt 2×. **Ausnahme:** wer die Grundflächen eines Modus verschiebt oder einen Modus neu ausliefert, nimmt den Skip heraus und sieht die volle Matrix in beiden Modi durch.
- **Wann laufen lassen:** Nach jeder Layout-/Style-Änderung mit Flächenwirkung, bei jeder neuen Seite (vorher als Szenario in `web/e2e/audit/ui-audit.audit.ts` ergänzen — Route mocken, Edge-Case-Daten mit langen Namen verwenden) und vor Abschluss jeder größeren UI-Runde.
- **Wie:**

  ```
  cd web
  npx playwright test --config=playwright.audit.config.ts
  ```

  (Kein npm-Script; startet selbst `ng serve` auf Port 4300.) Output unter `web/.audit-out/` (gitignored): `shots/<szenario>--<viewport>--<locale>--<theme>.png`, `metrics/<szenario>--<viewport>--<locale>--<theme>.json`. **Vor einem Lauf, dessen Metriken man auswertet, `.audit-out/` leeren** — sonst bleiben Dateien früherer Läufe liegen (auch solche zu inzwischen entfernten Szenarien) und verfälschen jede Auszählung über das Verzeichnis.
- **Metriken lesen:** Pro JSON-Datei:
  - `horizontalOverflowPx` — horizontaler Seiten-Overflow in px. **Gate: muss 0 sein.**
  - `smallTargetsUnder24` — interaktive Elemente < 24 px (WCAG 2.5.8). **Gate: keine neuen Einträge gegenüber dem letzten Lauf** (bestehende Einträge sind dokumentierte „equivalent target"-Ausnahmen).
  - `targets24to43` — Elemente unter dem 44-px-Komfortziel: beobachten, kein hartes Gate.
  - `beyondRightEdge` — Elemente jenseits der rechten Viewport-Kante: wie Overflow behandeln.
  - `contrastViolations` — axe-Befunde der Regel `color-contrast`, gefiltert auf `serious`/`critical`. **Gate: muss leer sein.** Was axe nicht sieht, steht in §10.
- Screenshots zusätzlich sichten (de **und** en — längere deutsche Strings sind der häufigste Umbruch-Bruch; und in der Auslieferungswelle eines Modus beide Themes).
- **Referenz:** `web/playwright.audit.config.ts`, `web/e2e/audit/ui-audit.audit.ts`.
