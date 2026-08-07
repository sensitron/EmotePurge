# Account-Menü im Header — Design

**Stand:** 2026-08-08 · **Status:** entworfen, nicht umgesetzt

## Ziel

Der Header rechts trägt heute sechs Dauer-Elemente: Theme-Icon, `DE EN`, Admin-Link, „Meine
Abstimmungen", Username, Logout-Button (`app-shell.ts:78-104`). Sie werden zu einem einzigen
Trigger zusammengefasst — einem runden Twitch-Profilbild mit Dropdown, wie es 7TV und Twitch
selbst verwenden. Der Header wird dadurch schlanker, der Nutzer sieht sich selbst, und die
Bereichsnavigation bleibt dort, wo sie ist (Tab-Leisten der Layouts, Back-Link nach §8.6).

**Die tragende Begründung ist die Rahmen-schweigt-Regel** (`DESIGN.md`, Components): „Für die
App-Kopfzeile gilt das eine Stufe strenger als für eine Seite: Was dort steht, steht auf jedem
Bildschirm in jeder Sitzung." Sechs Dauer-Elemente sind daran gemessen fünf zu viel — und das
Argument trägt am Desktop genauso wie auf dem Handy, während „der Header wird schlanker" dort
schwächer wiegt, wo Platz ohnehin da ist. Progressive Offenlegung statt neuer Dauer-Controls ist
der zweite Leitsatz, der dafür spricht; keiner spricht dagegen.

## Entschiedene Punkte

| Frage | Entscheidung |
|---|---|
| Menü-Inhalt | Alles: Konto, Navigation, Theme, Sprache, Logout |
| Mobil | Ein Menü für alle Breiten; Burger und Disclosure entfallen |
| Panel-Innenbau | Kompakte Zeilen mit `SegmentedControl`, Beschriftung darüber |
| Bild-Quelle | Claim im Session-Cookie, keine DB-Spalte, keine Migration |

## Komponentenschnitt

Drei neue Bausteine in `web/src/app/shared/`, zwei bestehende entfallen.

### `shared/ui/avatar.ts`

Runder Träger mit fester Größe, dessen Platz **vor** dem ersten Bild-Frame reserviert ist — sonst
springt der Header beim Laden, und Layout-Sprünge in der Shell sind ausgeschlossen.

- Inputs: `displayName` (required), `imageUrl` (optional, nullable), `size` (Default 32).
- Ohne URL oder nach einem `(error)` zeigt er den Anfangsbuchstaben von `displayName` auf
  `bg-accent-selected` mit `text-on-accent`.
- Das Bild folgt dem Muster aus `shared/emotes/emote-sprite.ts`: `loadedUrl`-Signal, das die
  geladene URL mit der aktuellen **vergleicht**, statt ein „hat mal geladen"-Boolean zu führen.
  Das ist hier weniger kritisch als beim Sprite-Hover, aber es kostet nichts und hält das Muster
  im Repo einheitlich.
- `NgOptimizedImage` wie überall sonst, ohne `disableOptimizedSrcset` (das ist die SVG-Regel).

### `shared/ui/display-preferences.ts`

Der Block „Darstellung" + „Sprache". Zwei beschriftete `<app-segmented-control>`-Gruppen, jede
über die volle Panelbreite, Beschriftung **über** der Gruppe.

Grund für die Beschriftung oben statt links: `SegmentedControl` nimmt nur Text-Labels
(`SegmentedControlOption.labelKey`), keine Icons. Drei Theme-Labels bei `text-sm px-3` passen
nicht neben eine Zeilenbeschriftung in ein 256 px breites Panel, nebeneinander über die volle
Breite dagegen schon.

Bindet an `ThemeService.preference` und `LanguageService.lang`. Nach diesem Umbau der **einzige**
Ort im Repo, an dem diese beiden Controls existieren.

### `shared/ui/account-menu.ts`

Ein Trigger, ein Panel, beide Auth-Zustände in einer Komponente:

| | eingeloggt | ausgeloggt |
|---|---|---|
| Trigger | `<app-avatar>` 32 px in einer 44-px-Fläche | Zahnrad-Icon, gleiche Fläche |
| Panel | Name · Meine Abstimmungen · [Admin] · Prefs · Abmelden | nur Prefs |

Admin-Zeile nur bei `isGlobalAdmin`. Die Komponente injiziert `AuthService` und liest
`currentUser()`; sie bekommt keine Inputs.

Zwei Details am Panel-Kopf, beide aus `DESIGN.md` abgeleitet:

- **Der Name trägt `font-medium`, nicht `font-semibold`.** Die Vier-Ebenen-Regel reserviert
  `semibold` für Überschriften; `font-medium` ist die Stufe, die Popover-Zeilen und
  Listenzeilen-Titel im Bestand benutzen. Ein fünftes Gewicht einzuführen wäre eine fünfte Ebene.
- **Der Kopf bleibt beim Überfahren stumm.** Er ist nicht klickbar, die Zeilen darunter sind es —
  und ein Hover darf nie einen Klick versprechen, den es nicht gibt. Das ist im Panel besonders
  leicht zu verletzen, weil der Kopf denselben Zeilenrhythmus hat wie die Einträge.

Damit setzen Shell, Landing und Login dasselbe `<app-account-menu/>` an dieselbe Stelle. Der
Login-Button bleibt daneben ein eigenständiges Element, kein Menü-Eintrag — ein Aufruf zur
Anmeldung gehört nicht hinter eine Klappe.

### Was ersatzlos entfällt

- `shared/ui/theme-menu.ts` → die Komponente `ThemeMenu`. `ThemeIcon` aus derselben Datei bleibt
  und zieht nach `shared/ui/theme-icon.ts`.
- `shared/i18n/language-switcher.ts` vollständig.
- Aus `features/shell/app-shell.ts`: der Burger-Button (`:107-140`), das Disclosure-Panel
  (`:142-197`), beide Host-Listener (`:26-29`) und die Handler `menuOpen` / `toggleMenu` /
  `closeMenu` / `onEscape` / `onDocumentClick` (`:253-266`).

Das sind rund 100 Zeilen handgebautes Dismiss-Verhalten. Der Doc-Kommentar von
`shared/ui/popover.ts:12-15` benennt diese Disclosure bereits selbst als eines der Duplikate, die
zusammengeführt gehören — der Umbau erledigt das nebenbei. Der Header hat danach **keinen
`md:`-Zweig mehr**.

## Verhalten

### Semantik: Disclosure, nicht `role="menu"`

Das Panel hält gemischte Kinder — Router-Links, zwei Radiogroups, einen Button. `role="menu"`
verlangt `menuitem`-Kinder; eine Radiogroup darin ist nicht valide. Also:

- Trigger: `aria-expanded`, `aria-controls`, `aria-label`.
- Panel: schlichter Container mit zwei `role="radiogroup"`-Inseln (die bringt
  `SegmentedControl` selbst mit).

**Das ist ein bewusster Rückbau gegenüber heute.** `theme-menu.ts` benutzt derzeit `role="menu"`
+ `menuitemradio`; beides fällt weg. Die Shell hat dieselbe Entscheidung für ihre Mobile-Disclosure
bereits getroffen und im Code begründet — wir folgen dem, statt einen zweiten Umgang zu etablieren.

### Barrierefreier Name

Der Avatar trägt `alt=""` (dekorativ — der Name steht im Panel), der Trigger braucht deshalb ein
`aria-label`: eingeloggt `account.trigger` mit `{{ name }}`-Parameter, ausgeloggt
`account.preferencesTrigger`.

### Schließen und Fokus

`shared/ui/popover.ts` erledigt Klick-außerhalb und Escape und meldet `closed`. Der Host besitzt
das Sichtbarkeits-Signal und die Fokusrückgabe an den Trigger — so steht es im Vertrag des
Primitives, und so machen es `theme-menu.ts:143-161` und `date-range-menu.ts:253-272` heute schon.

Bei Navigation schließt jeder Link selbst (`(click)="close()"`). Das sind jetzt zwei bis drei
Stellen **innerhalb einer** Komponente statt verstreut über das Shell-Template.

Kein Fokus-Trap, kein Backdrop, kein Scroll-Lock — wie bei allen bestehenden Popovers im Repo.

### Tastatur

Tabreihenfolge im offenen Panel: Meine Abstimmungen → [Admin] → Darstellung → Sprache → Abmelden.
Fünf Stationen, weil jede Segmentgruppe dank Roving-Tabindex **eine** Station ist und die
Pfeiltasten innerhalb wählen (`segmented-control.ts:57-84`).

Der Fokus wandert beim Öffnen **nicht** automatisch ins Panel: es steht im DOM direkt hinter dem
Trigger, ein Tab genügt.

### Unbestimmter Auth-Zustand

Bevor `/me` beantwortet ist, weiß die Komponente nicht, ob sie Avatar oder Zahnrad zeigt. Heute
blitzt an dieser Stelle der Login-Button auf und wird ersetzt; unbehandelt würde das Zahnrad zum
Avatar springen — ein sichtbarer Umschlag mitten im Header.

Lösung: Solange `currentUser()` unbestimmt ist, rendert der Trigger die **reservierte Fläche mit
dem Monogramm-Träger ohne Buchstaben** (gleiche 44 × 44 px, gleiche runde 32-px-Platte, keine
Schrift, kein Zahnrad) und ist `disabled`. Damit ändert sich beim Auflösen nur der Inhalt der
Platte, nie ihre Größe oder Position. Kein Spinner — die Auflösung dauert einen Roundtrip, und ein
Spinner im Header wäre lauter als die Sache ist.

### Touch

| Element | Maß |
|---|---|
| Trigger | 44 × 44 px, Avatar 32 px mittig |
| Panel-Zeilen | `min-h-11` |
| Segmente | `min-h-11` über neues `size`-Input |
| Panel | `w-64`, `align="end"` |

Der Trigger passt mit 44 px und 12 px Luft in den `h-14`-Header; der Höhenvertrag aus §8.5
(`top-14`, `top-24`, alle `scroll-mt-*`) bleibt **unangetastet**.

`popover.ts` deckelt bereits auf `max-w-[calc(100vw-2rem)]`, das Panel steht auf 360 px also
nicht über.

`SegmentedControl` bekommt ein **additives** `size`-Input: `'sm'` ist der Default und behält das
heutige `py-1.5`, `'lg'` setzt `min-h-11`. Bestehende Aufrufstellen ändern sich dadurch nicht.
Die 44 px sind hier eine Ergonomie-Entscheidung, keine Zertifikatspflicht — `PRODUCT.md` hat die
formale WCAG-Zusage 2026-08-06 zurückgenommen; Tastaturbedienbarkeit und Fokus-Sichtbarkeit
bleiben davon ausdrücklich unberührt. Ohne das `size`-Input blieben die Segmente bei ~34 px —
besser als die ~20 px der heutigen `DE EN`-Buttons, aber kein Daumenziel.

### Bekannte Falle: das Panel darf nicht beschnitten werden

Das Panel ist rund 320 px hoch und hängt absolut positioniert aus einem 56-px-Header. Es liegt
damit **innerhalb** des Header-Stacking-Kontexts — das ist Absicht (`popover.ts:16-19`: Panels
öffnen aus Sticky-Leisten und müssen deren Kontext erben, ein an `<body>` gehängter
CDK-Overlay-Container kann das nicht).

Der Preis dieser Entscheidung: Bekommt irgendein Vorfahr des Headers `overflow: hidden` oder
`overflow: auto`, wird das Panel abgeschnitten. Heute trägt keiner davon eines. Wer am
Shell-Layout arbeitet, muss das wissen — deshalb steht es hier und gehört in denselben
Doku-Absatz wie die z-Leiter.

## Backend: die Claim-Kette

Twitch liefert `profile_image_url` in der Helix-Antwort, die beim Login ohnehin abgerufen wird
(`AuthEndpoints.cs:74-89`). Sie wird derzeit nur weggeworfen. Sechs Dateien, **keine Migration**:

| Datei | Änderung |
|---|---|
| `Infrastructure/Twitch/TwitchApiDtos.cs:18-23` | `ProfileImageUrl` — die SnakeCase-Policy trifft `profile_image_url` von selbst |
| `Core/Twitch/TwitchModels.cs:28` | viertes, nullable Feld am `TwitchUserInfo`-Record |
| `Infrastructure/Twitch/TwitchHelixClient.cs:30` | durchreichen |
| `Api/Auth/TwitchClaimTypes.cs` | `twitch:profile_image` |
| `Api/Endpoints/AuthEndpoints.cs:91-103` | Claim setzen — **nur wenn nicht leer**, `Claim` verträgt kein `null` |
| `Api/Endpoints/AuthEndpoints.cs:117-132` | `profileImageUrl` in `/me` projizieren |

`/me` bleibt damit DB- und HTTP-frei, wie es der Kommentar auf `:119-121` zusichert. Die
`User`-Entität wird **nicht** angefasst.

### Bestehende Sessions

Wer beim Deploy angemeldet ist, hat den Claim nicht. `/me` liefert dort `null`, der Avatar zeigt
das Monogramm, beim nächsten Login heilt es. Kein Bruch — aber unmittelbar nach dem Deploy sieht
man selbst zunächst kein Bild, und das ist kein Fehler.

### Bildgröße

Twitch liefert `…-profile_image-300x300.png`. Ein 300er-Bild in einem 32-px-Kasten löst in Dev
[NG0913](https://angular.dev/errors/NG0913) aus, den Oversize-Hinweis von `NgOptimizedImage`.

Beim Setzen des Claims wird `-300x300` durch `-70x70` ersetzt — 70 px deckt 32 px bei DPR 2 —
und zwar **bewacht**: greift das Muster nicht, geht die URL unverändert durch. Das ist die einzige
Stelle, an der wir etwas über Twitchs URL-Form annehmen, und sie fällt bei einer Änderung weich
zurück statt zu brechen.

### CSP

`Api/Program.cs:206` — `https://static-cdn.jtvnw.net` in die `img-src`-Liste. **Ohne diesen
Schritt lädt kein einziges Bild**, unabhängig davon, ob die URL im DTO ankommt. Die Allow-Listen
sind laut Kommentar auf `:174-176` bewusst schmal; dies ist ein begründeter Zusatz, kein Aufweichen.

## Auswirkungen

### Tests

Es gibt weder `theme-menu.spec.ts` noch `language-switcher.spec.ts` noch `app-shell.spec.ts` —
keine Unit-Kollateralschäden. Der Aufwand liegt in E2E:

| Datei | Was bricht |
|---|---|
| `web/e2e/theme.spec.ts:119-150` | klickt `button "Darstellung wählen"` und `menuitemradio "Hell"`. Beide Selektoren verschwinden: der Trigger heißt anders, `menuitemradio` wird `radio`. Betrifft drei Fälle — Shell, `/welcome`, `/login` |
| `web/e2e/channel-workspace.e2e.spec.ts:38-39` | erwartet den Logout-Button sichtbar; er liegt danach im geschlossenen Panel, der Test muss erst öffnen |
| `web/src/app/core/auth/auth.service.spec.ts:10-16` | `USER`-Fixture braucht `profileImageUrl` |
| `web/e2e/support/mocks.ts:3-10` | `AUTH_USER`-Fixture ebenso |

`tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs:68` deckt `/me` nur auf 401 ab und bleibt
gültig. `ApiFactory.cs:119-145` (`TestAuthHandler`) braucht kein neues Claim, solange kein Test
das Response-Shape prüft.

**Neue Tests verlangen die Regeln nur an einer Stelle:** Die Claim-Kette ist ein
Feld-Durchreicher ohne Entscheidungslogik. Der `-70x70`-Rewrite **ist** Logik und bekommt nach
Regel 11 seinen Test in `tests/EmotePurge.Infrastructure.Tests/Unit/`, inklusive des Falls
„Muster passt nicht → URL unverändert".

Der UI-Audit-Shot `overview-worker-stale` (`web/e2e/audit/ui-audit.audit.ts:339-343`) begründet
sich im Kommentar mit dem Zusammenspiel von Worker-Warnung und Menü-Button auf 360 px. Der Shot
bleibt sinnvoll, der Kommentar wird faktisch falsch und ist nachzuziehen.

### i18n

- **Bleiben:** `theme.label` („Darstellung"/„Appearance"), `theme.system`, `theme.light`,
  `theme.dark` — sie werden zu Gruppenbeschriftung und Segment-Labels.
- **Ändert sich:** `languageSwitcher.ariaLabel` verliert den `{{ lang }}`-Parameter; es beschriftet
  künftig die Gruppe, nicht einzelne Buttons.
- **Neu:** `languageSwitcher.label`, Segment-Labels „Deutsch"/„English", `account.trigger`
  (mit `{{ name }}`), `account.preferencesTrigger`.
- **Entfällt:** `shell.menu` (Aria-Label des Burgers).

Der Locale-Paritätstest greift nur für `ApiErrorCodes` — hier bleibt es Disziplin.

### Dokumentation (Regel 3, im selben Commit)

| Stelle | Warum falsch |
|---|---|
| `docs/UI-Designsprache.md:49-50` | „`<app-theme-menu>` … in der Kopfzeile neben dem Sprachumschalter, mobil in derselben Disclosure" — in jedem Teilsatz überholt; die Referenzzeile nennt die gelöschte Datei |
| `docs/UI-Designsprache.md:364` | nennt die Mobile-Disclosure in der verbindlichen z-Leiter |
| `docs/UI-Designsprache.md:409` | führt `ThemeMenu` in der Primitives-Liste |

Dazu ein `DECISIONS.md`-Eintrag mit drei Begründungen: ein Menü statt sechs Dauer-Controls,
Disclosure statt `role="menu"`, Claim statt DB-Spalte.

## Offener Punkt: „Meine Abstimmungen"

Die Spec setzt den Link **ins Menü**. Der Nutzer erwägt, ihn draußen zu lassen; das ist beim Bauen
noch änderbar und kostet eine Zeile. Die Konsequenzen, damit die Entscheidung informiert fällt:

- **Im Menü** (Vorgabe): Header rechts trägt genau ein Element, kein Breakpoint-Zweig. Preis: ein
  Klick mehr auf dem Weg zu einer häufig besuchten Seite.
- **Draußen, immer sichtbar:** Auf 360 px stehen Wordmark (~110 px), Link und 44-px-Trigger in
  einer 328-px-Zeile. Mit „Meine Abstimmungen" wird das zu eng; mit dem kürzeren Label
  „Abstimmungen" geht es knapp auf. Das Label wäre also mitzuändern.
- **Draußen, erst ab `md`:** holt genau den `md:`-Zweig zurück, den dieser Umbau beseitigt, und
  verlangt den Eintrag zusätzlich im Menü. Nicht empfohlen.

## Offener Punkt: das Zahnrad für Ausgeloggte

Der Trigger im ausgeloggten Zustand ist ein unbeschriftetes Zahnrad. Das ist der eine Punkt, an
dem der Entwurf gegen eine benannte Heuristik läuft: „Recognition Rather Than Recall" verlangt
Labels an Icons statt Icon-only-Navigation, und ausgeloggt auf Landing und Login ist der Besucher
per Definition der Erstbesucher. Der Avatar hat dieses Problem nicht — ein rundes Profilbild oben
rechts ist eine etablierte Konvention und trägt zusätzlich einen Namen.

**Stand 2026-08-08:** Der Betreiber lässt es vorerst bei Variante 1 und entscheidet später — der
ausgeloggte Zustand unterscheidet sich ohnehin in mehr als diesem Detail, und die Entscheidung
fällt leichter am gebauten Zustand als am Entwurf.

Drei Wege:

1. **Zahnrad wie beschrieben.** Einfachste Variante, eine Komponente, ein Ort. Preis: ein
   Erstbesucher muss klicken, um die Sprache zu finden.
2. **Zahnrad mit Textlabel** („Einstellungen"/„Settings") auf Landing und Login, nur dort, wo
   Platz ist. Löst das Problem, kostet aber wieder eine Variante.
3. **Theme und Sprache bleiben auf Landing und Login inline** und wandern nur in der
   angemeldeten Shell ins Menü. Am freundlichsten für den Erstbesucher, aber genau der
   Auth-abhängige Ortswechsel derselben Controls, den der Umbau vermeiden wollte.

## Nach der Umsetzung

`context.mjs` hat gemeldet, dass in dieser Session kein automatischer Impeccable-Hook läuft. Wenn
die UI steht, einmal — nicht früher — den mechanischen Detektor über die geänderten Dateien
laufen lassen:

```
node C:\Users\admin\.claude\skills\impeccable\scripts\detect.mjs --json <geänderte Dateien>
```

## Nicht Teil dieser Arbeit

- Keine DB-Spalte für das Profilbild und keine Migration — bewusst, siehe oben.
- Kein Avatar in der Admin-Nutzerliste. Wenn der später gewünscht ist, ist *das* der Moment für
  die DB-Spalte, nicht dieser.
- Keine stündliche Aktualisierung des Bildes. Es folgt dem Login, das genügt.
- Kein Fokus-Trap und kein Scroll-Lock im Panel — das Repo hält es bei allen Popovers so.
