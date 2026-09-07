# Design: Aktionszeile — eine Absicht je Kommando (#80)

Erstellt am 2026-09-06 (Office Hours), überarbeitet nach drei adversarialen Prüfrunden (zwei
intern, eine Codex Sol) und einem Schiedsspruch vom 2026-09-07.
Branch: `feat/aktionszeile-80` · Repo: sensitron/EmotePurge · Status: BEREIT ZUR UMSETZUNG

## Problem

#80 fragt, ob „In Kanal kopieren…" ins Dock gehört statt in den Kopfbereich. Die Frage benennt
ein Symptom. Drei Befunde stehen dahinter, alle belegt.

**1. Die Prämisse des Issues stimmt nicht.** „In Kanal kopieren" wirkt nicht auf die Auswahl.
`openImportTarget()` friert **beides** ein — `selection` und `visible`
(`usage-stats-page.ts:1154-1159`) — und übergibt beide Zahlen an einen Dialog, der daraus eine
Bereichswahl baut. Kopieren und Exportieren sind Zwillinge, nicht Kopieren und Löschen. So war
es auch entschieden: `docs/designs/Emote-Import-38-2026-09-05.md:128-130` — „im Header neben dem
Export … Scope wie im Export-Dialog".

**2. Zwei Kommandos bedienen dieselbe Absicht.** Vom Betreiber während der Sitzung benannt:
*„die json datei die raus kommt kann man aber auch wo anders importieren. Es gibt also
mehrdeutige Befehle."*

- „In Kanal kopieren… → Ziel **Datei**" erzeugt eine JSON-Datei
  (`import-target-dialog.ts:26`: `target: {kind:'channel'} | {kind:'file'}`).
- „Exportieren → Format **JSON**" erzeugt ebenfalls eine JSON-Datei
  (`export-dialog.ts:11`; Standard ist `'csv'`, `:143`).
- Derselbe Importer frisst beide (`restore-panel.ts:20-38`: Purge-Protokoll, Emote-Liste,
  Nutzungs-Export).

Zwei Knöpfe im selben Kopfbereich führen also auf zwei Wegen zu je einer einspielbaren Datei —
und nichts sagt dem Nutzer, welcher gemeint ist.

**3. Der Weg herein ist unsichtbar.** Der Datei-Einstieg sitzt unter dem Raster
(`usage-stats-page.html:709-716`, `<app-restore-panel>` bei `:714`). Bei einem Set in
HandOfBlood-Größe steht er hinter hunderten Emote-Zeilen. Wörtlich: *„der Import Button ganz
unten unter den Emotes ist vielleicht nicht optimal, vielleicht wäre oben bei Export etc.
besser."*

## Die Geometrie, die alles entscheidet

Drei Flächen, drei unterschiedliche Sichtbarkeiten — hier lag der Denkfehler der ersten Fassung.

**Wortgebrauch, weil „Kopfbereich" doppelt belegt ist:** der **Shell-Header** (`app-shell.ts:35`,
`app-sticky-bar top-0 z-30`) ist immer sichtbar und gehört dem App-Rahmen; der **Seitenkopf**
(`usage-stats-page.html:6`) gehört der Seite und scrollt weg. Dieses Dokument und die Regel meinen
durchgängig den **Seitenkopf**.

| Fläche | sichtbar | Beleg |
|---|---|---|
| **Seitenkopf** | nur ganz oben, scrollt weg | `usage-stats-page.html:6` — schlichtes `<header>`, kein `sticky` |
| **Sticky-Leiste** | immer, aber voll | `:64` `app-sticky-bar top-24`; „around 200 px of chrome", auf dem Handy bewusst nicht gepinnt (`:60-63`) |
| **Dock** | immer, solange es etwas zu tun gibt | §2.5; `dockVisible()` in `usage-stats-page.ts:669-683` |

Daraus folgt die Rollenverteilung, und zwar zwingend:

- **Alles nach oben** verschlechtert die Auffindbarkeit, weil der Seitenkopf die einzige Fläche
  ist, die verschwindet. Die Sticky-Leiste als Ausweichort ist versperrt: eine dritte Zeile bei
  Auswahl kostet vertikalen Platz genau dann, wenn man Emotes ansehen will. (Die 844-px-Rechnung
  aus `usage-stats-page.html:60-63` stützt das nur qualitativ: unterhalb `sm` pinnt die Leiste
  gar nicht, und Auswahl wie Dock sind ohnehin hinter `isCoarse` gegated — den gemessenen Fall
  gäbe es dort nicht.) Den Seitenkopf sticky zu machen ist ebenfalls versperrt, aber nicht durch
  §8.5: die Seite selbst verzichtet begründet darauf (`usage-stats-page.html:59-60`, „this page
  does not get to add a rung to it"), und §8.5 macht jede neue Sprosse zur Änderung **aller**
  `top`-Werte.
- **Alles nach unten** verlangt ein dauerhaft geparktes Dock. §2.5 hat das mit Begründung
  abgelehnt: eine dauerhaft geparkte Aktionsleiste ist ein Bedienelement, an dem der Erstbesuch
  vorbeilesen muss.

**Der Zweck der Dock-Kurzform ist damit Auffindbarkeit, nicht Kürze.** Sie spart *keinen* Klick:
die Bereichs-Radiogruppe erscheint ohnehin nur bei vorhandener Auswahl
(`import-target-dialog.ts:49`) und steht dann schon auf „Auswahl" (`:177-178`). Sie steht im
Dock, weil im Moment des Markierens der Blick unten ist und der Seitenkopf weggescrollt sein
kann.

## Prämissen

1. Zwei Kommandos bedienen dieselbe Absicht „diese Emotes woanders hinbringen". Belegt durch den
   Code und durch die Beobachtung des Betreibers — **nicht** durch einen zweiten Nutzer.
   Stichprobengröße eins.
2. Der Export hat daneben eine zweite, unabhängige Absicht: ein Auszug zum Ansehen und Rechnen
   (CSV, der Standard). Die bleibt und ist unstrittig.
3. Die Ortsfrage aus #80 ist eine Folge, keine Ursache.
4. Der Weg herein hat keine sichtbare Entsprechung zum Weg hinaus. Belege getrennt halten: die
   Fehlbedienung vom 2026-08-04 belegt eine falsche **Erwartung** („Export und Import sind ein
   Paar") — der Nutzer hat das Panel ja gefunden, nur falsch benutzt. Sie stützt Befund 2, nicht
   Befund 3. Für Befund 3 steht allein die Aussage des Betreibers: „der Import Button ganz unten
   sieht man nicht". Das ist ein direkter Nutzerbefund, aber n = 1.
5. Eine Aktion darf an zwei Orten stehen, wenn die Orte verschiedene Fragen beantworten: der
   Kopfbereich „was mache ich mit diesem Kanal?", das Dock „was passiert mit diesen n?".

## Randbedingungen

- **Die Aktionszeile des Docks gehört dem geteilten Panel.** „Zur Abstimmung stellen" steht nicht
  neben `app-mass-delete-panel`, sondern wird per `ngProjectAs="[selection-actions]"` **hinein**
  projiziert (`usage-stats-page.html:748-778`). Reihenfolge und Trennung lassen sich deshalb nur
  **im** Panel umsetzen (`mass-delete-panel.ts`, Vertrag „constructive before destructive"). Die
  Voting-Detail-Seite montiert dasselbe Panel ohne Dock (`vote-session-detail-page.html:150`) und
  lässt den Projektionsschlitz leer — ein dort eingebauter Trenner hinge frei. Der *Auslöser*
  wird in `usage-stats-page.html` deklariert, der *Zeilenaufbau* braucht im Panel eine Bedingung.
- **`importScopeCurrent()` ist Pflicht für jeden Kopier-Auslöser.** Der Kopfknopf ist darauf
  gegated (`usage-stats-page.html:39`), weil ein Kanalwechsel innerhalb der Route Name, Set-Id
  und Zeilen kurzzeitig auseinanderlaufen lässt — ein Fang in diesem Fenster kopierte Kanal A's
  Emotes unter B's Namen in ein fremdes 7TV-Set. Die Kurzform erbt das nicht automatisch.
- **Die Capture-Disziplin von `openImportTarget()` muss erhalten bleiben.** Zeilen werden **vor**
  Dialogöffnung eingefroren, weil `usageFlushed`/`channel.synced` das Raster nachladen und die
  Auswahl das überlebt. Ein zweiter Auslöser erbt das nur, wenn er denselben Pfad ruft.
- **Ein fester Bereich ist eine Vertragsänderung.** `openImportTarget()` nimmt heute keinen
  Parameter; das Ausblenden der Radiogruppe hängt allein an
  `ImportTargetDialogData.selectionCount`. Ein erzwungener Bereich braucht ein neues Feld und
  eine Anpassung von `import-target-dialog.spec.ts`.
- **`pointer: coarse`** gated alle 7TV-Schreibwege (§2.5). Der Datei-Weg trägt sein
  `@if (!isCoarse())` heute bei `usage-stats-page.html:713`; im Kopfbereich muss es **neu
  gesetzt** werden, es erbt sich nicht.
- **Aktives Set:** `RestorePanel` nimmt `setId` als required input, und `parsePurgeRunProtocol`
  validiert Kanal *und* Set-Id. Im Kopfbereich bleibt das Set-Gate bestehen — ohne aktives Set
  gibt es keinen Datei-Weg. Das ist eine Entscheidung, keine Selbstverständlichkeit: §2.5 hat für
  die Import-Sektion ausdrücklich das Gegenteil entschieden (sie sitzt **außerhalb** des
  Set-Gates, weil sie einen Lauf in ein *fremdes* Set zeigt). Hier geht es um das *eigene* Set,
  deshalb bleibt das Gate.
- **`SevenTvRunArbiter.activeRun()`** sperrt während jedes Laufs alle schreibenden Auslöser, ohne
  Hinweistext (§4.2). Neue Auslöser folgen dem.
- **Regel 3** (Konventionsänderung ⇒ `DECISIONS.md`-Eintrag im selben Commit), **Regel 12**
  (Verhalten bekommt Specs, Vorlage nicht), **Regel 7** (neue Keys in beiden Locales).

## Die Regel, die daraus wird

**Nicht** in §2.5 — dessen erster Satz lautet „Was für sie gilt, gilt für nichts anderes in der
App"; eine app-weite Regel dort einzutragen hebelt ihn aus. Und **nicht** in §7.2, das die
Zeilenreihenfolge zweier konkreter Dialoge beschreibt. Auch **nicht** in Kapitel 4 — das heißt
„Buttons, Badges, Banner" und enthält Primitive, keine Platzierungsregeln. Die Regel ist eine
Aussage darüber, *welche Fläche welches Kommando trägt*, und gehört damit zu §8, neben §8.4a
(Inhaltsbreite) und §8.5 (Sticky-Ebenen): **neuer Abschnitt §8.7**. §2.5, §4.2 und §7.2 verweisen
darauf.

> **§8.6 ist bereits vergeben** („Rücknavigation", `UI-Designsprache.md:452`) und wird aus
> mindestens sechs Stellen namentlich referenziert: `back-link.ts:10`, `app.config.ts:65`,
> `vote-session-detail-page.html:4`, `admin-layout.ts:19`, `my-votings-page.ts:42`,
> `DECISIONS.md:2151`. Eine zweite §8.6 machte diese Verweise mehrdeutig, ein stilles Umnummerieren
> machte sie falsch. Deshalb **§8.7**. Von Codex Sol gefunden; zwei interne Prüfrunden hatten es
> übersehen.

Der Text ist nach der Schiedsentscheidung vom 2026-09-07 in zwei Teile getrennt: was als **Regel**
gilt (beschreibt gelebte Praxis, braucht keinen weiteren Beleg) und was als **Erlaubnis** steht
(die Kurzform, deren Nutzen noch unbelegt ist). Der Unterschied ist der Grund, dass §8.7 heute
überhaupt geschrieben werden darf.

> **Geltungsbereich:** Seiten mit Mehrfachauswahl auf einem Bogen — heute die Usage-Stats-Seite
> und die Voting-Detail-Seite.
>
> **Seitenkopf** (der Kopf *der Seite*, nicht der Shell-Header) trägt Kommandos, die ohne Auswahl
> vollständig sind. Jedes hat genau eine Absicht; überlappen sich zwei, wird eine umbenannt oder
> fallengelassen — nicht verschoben. **Überlappung heißt: zwei Kommandos mit verschiedenem Namen
> für dieselbe Absicht.** Dieselbe Aktion unter demselben Verb an zwei Orten ist ein zweiter
> Einstieg, keine Überlappung — die Gleichheit des Verbs ist dabei Bedingung, nicht Beiwerk.
>
> **Auswahlgebundene Kommandos** — solche, die ohne Auswahl gar nicht ausführbar sind (Löschen,
> Zur Abstimmung stellen) — stehen **nicht** im Seitenkopf. Wo es ein Dock gibt, stehen sie dort;
> wo keins ist, im Fluss unter dem Bogen, wie auf der Voting-Detail-Seite
> (`vote-session-detail-page.html:149-157`). Das Dock ist eine Zugabe, kein Erfordernis.
>
> **Das Dock trägt außerdem den Laufzustand** — Fortschritt, Protokoll, Wiederherstellen nach
> einem Lauf. Das kann keine andere Fläche, weil es einen *abgeschlossenen* Lauf überdauern muss.
>
> **Erlaubnis, keine Pflicht:** ein Kommando, dessen Ergebnis von der Auswahl abhängt, **darf**
> zusätzlich als Kurzform im Dock stehen — gleiches Verb, ohne Bereichsfrage, mit der Anzahl im
> Text. Begründung ist Auffindbarkeit im Moment des Markierens, nicht Klick-Ersparnis: der
> Seitenkopf scrollt weg, das Dock nicht. **Diese Begründung ist bislang unbelegt** (n = 1); sie
> ist deshalb als „darf" formuliert und zwingt keine künftige Aktion in zwei Einstiege.
>
> *Beispiel, warum „darf" und nicht „muss":* der Export trägt bewusst **keine** Kurzform. Seine
> Bereichsvorgabe ist `visible` (`export-dialog.ts:139-141`), gegenläufig zum Ziel-Dialog, der auf
> `selection` steht — §7.2 hält die Asymmetrie als Absicht fest. Eine Export-Kurzform mit
> erzwungenem `selection` machte aus dieser Vorgabe stillschweigend ihr Gegenteil.
>
> **Reihenfolge:** zerstörende Aktionen stehen ans Ende der **konstruktiven** Gruppe, mit einem
> Abstand davor (NN/G zu Mengenaktionen); neutrale Ausstiege wie „Auswahl aufheben" dürfen
> folgen. Das ist eine Anweisung zur *Position*, nicht zur Optik: die Buttonvarianten bleiben,
> wie §4.2 sie vorschreibt.

## Verworfene Ansätze

- **A — nur die zwei Handgriffe:** verworfen, weil die Überlappung bliebe, die der Betreiber
  selbst als das eigentliche Problem benannt hat.
- **C — die obere Leiste verwandelt sich** (Material/Gmail-Muster): Das Muster *ersetzt* die
  Leistenzeile im Auswahlzustand, es addiert keine — „kein Platz" wäre also ein Scheinargument
  und ist hier nicht der Grund. Der echte Grund: die Leiste trägt Zeitraum, Sortierung, Filter
  und die Inspektorzeile. Genau diese Angaben braucht man **während** man eine Auswahl prüft —
  „habe ich den richtigen Zeitraum? ist der Filter noch an?". Sie im Auswahlzustand zu ersetzen
  nimmt dem Nutzer die Antwort auf die Frage, die der Auswahl vorausgeht. Das Gegenargument der
  ersten Fassung („die Kopfzeile ist sticky") war schlicht falsch und ist ersetzt.

## Gewählter Ansatz: B — eine Absicht je Kommando

Skizze der Anordnung: `Aktionszeile-80-2026-09-06.png` (daneben; sie zeigt zwei Verben für
dieselbe Aktion — ein Fehler der Skizze, s. offene Frage 1).

**Kopfbereich**, drei Gruppen:

| Kommando | Absicht | Änderung |
|---|---|---|
| **Exportieren** | Auszug zum Ansehen und Rechnen | bleibt; die JSON-Option wird im Dialog als Datenauszug benannt, nicht als Transportweg |
| **Übertragen…** | der Weg hinaus, Ziel Kanal *oder* Datei | heute „In Kanal kopieren…"; Verhalten unverändert, Benennung offen (Frage 1) |
| **Datei einspielen…** | der Weg herein | zieht aus dem Fluss nach oben (Supersede, s. u.) |
| **Aktualisieren** | Seitenwartung | bleibt |

**Dock**, sobald etwas markiert ist — Zeilenaufbau im geteilten Panel, hinter einer Bedingung,
damit die Voting-Seite unverändert bleibt:

`[n markiert · Plätze] [In Kanal kopieren… (n)] [Zur Abstimmung stellen (n)] ——— [Löschen (n)] [Auswahl aufheben]`

Die Kurzform ruft denselben `openImportTarget()`-Pfad mit erzwungenem Bereich `selection`; der
Ziel-Dialog öffnet ohne Bereichs-Radiogruppe. Alle Sperren des Kopfknopfs gelten mit,
`importScopeCurrent()` eingeschlossen.

**Der Datei-Weg wird ein Dialog, kein nackter Dateidialog.** Der Kopfknopf öffnet einen kleinen
Dialog, der (a) die drei zulässigen Sorten aufzählt — der heutige Hinweistext, sichtbar
**bevor** man sucht —, (b) das `<input type="file">` selbst trägt, und (c) alle Lesefehler
aufnimmt, die heute im Panel darunter erscheinen: aus `readEnvelope` (`notJson`,
`csvInsteadOfJson`, `wrongKind`) und aus den beiden Parsern — `wrongChannel`
(`purge-run-export.ts:141`), `wrongSet` (`:148`) und `votingExport` (`:103` sowie
`import-source-parser.ts:32`).

Das Dateiauswahlfenster öffnet aus einem Klick **innerhalb** dieses Dialogs. Heute ist der
programmatische `click()` auf das File-Input unkritisch, weil er direkt aus einem Nutzerklick
läuft (`restore-panel.ts:92-94`, gerufen aus `:51`); **nach** dem Schließen eines CDK-Dialogs
wäre dieselbe Zeile eine verbrauchte Geste und das Fenster bliebe **stumm** aus.

**Verschachtelte Dialoge sind der eigentliche Aufwandstreiber.** Der Einspiel-Dialog muss aus
sich heraus zwei verschiedene Ketten starten: beim Purge-Protokoll kommt der Token-Prompt **vor**
der Bestätigung, bei Emote-Liste und Nutzungs-Export **danach** (`startImportFlow` macht das
selbst, R2). §7.2 hält diese Asymmetrie als Absicht fest. Zwei Prompt-Ordnungen aus einem bereits
offenen CDK-Dialog — das ist der Grund für Aufwand M, nicht die Verschiebung des Knopfes.

**Präzisierung von `docs/DECISIONS.md:2469` — kein Supersede.** Dort steht: „`RestorePanel`
bleibt im Fluss, weil es eine Wiederherstellungs-Hilfe ist und nicht Teil des Markierens." Der
Gegensatz, den der Satz zieht, ist *Fluss ↔ Dock*, und in diesem Gegensatz bleibt er gültig: das
Panel gehört weiterhin nicht ins Dock. Der Seitenkopf **ist** Fluss. Was der Satz nicht
beantwortet, ist die Frage, an welcher Stelle des Flusses — und unter dem Raster ist es bei einem
großen Set unauffindbar. Der neue Eintrag beantwortet diese Frage und lässt den alten stehen.

**Warum die Überlappung beim Einspielen bleiben darf:** dass der Importer den Nutzungs-Export
frisst, war die *Reaktion* auf die Fehlbedienung vom 2026-08-04 (A16). Bewusste Nachsicht beim
Einspielen — sie bleibt, sie wird nur auf der Ausgabe-Seite nicht als eigener Weg beworben.

## Offene Fragen

1. ~~**Ein Verb oder zwei?**~~ **Entschieden am 2026-09-07 (Schiedsspruch): ein Verb, zwingend.**
   Die Gleichheit des Verbs ist genau das, was den zweiten Einstieg von einer Doppelung trennt —
   sie ist Bedingung, nicht Geschmack. Solange die Umbenennung in #92 liegt, heißt die
   Kurzform deshalb **wortgleich wie heute**: „In Kanal kopieren… (n)". Umbenannt werden später
   beide zusammen oder keiner.
2. **Bleibt die JSON-Option im Export-Dialog?** Empfehlung ja, nur umbenannt. Entfernen wäre der
   sauberste Schnitt, aber ein Bruch ohne Beleg, dass sie niemand nutzt.
3. ~~**Umfang des Export-Teils.**~~ **Entschieden am 2026-09-06: aufgeteilt.** Dieser Branch
   trägt **Regel + Dock-Kurzform** — beides klein und direkt aus #80 ableitbar. Der **Umzug des
   Datei-Wegs** bekommt ein eigenes Issue: er ist der einzige Teil mit echtem Umbauaufwand
   (Komponentenschnitt, zwei verschachtelte Dialogketten) und verdient ein eigenes Diff und einen
   eigenen Review. Die **Export-Umbenennung** bekommt ebenfalls ein eigenes Issue, weil ihr Beleg
   die schwächste Stichprobe hat. Die Regel wird hier trotzdem vollständig geschrieben — sie
   begründet die Plätze aller drei Bausteine, auch der noch nicht umgesetzten.
4. **Kopfzeile auf schmalen Fenstern.** Vier Knöpfe brechen um (`flex-wrap`), die Gruppierung
   wird dann undeutlich. Nicht gemessen.
5. **Abhängigkeit: die Inhaltsbreite der App.** Der Betreiber arbeitet auf 1080p mit 125 %
   Skalierung, also ~1536 CSS-Pixel; `max-w-5xl` (1024 px) lässt dort je ~256 px Rand, bei 100 %
   je ~448 px. Eine breitere Shell entschärft beide Seiten dieses Entwurfs: die Kopfzeile bricht
   nicht mehr um, und mehr Emote-Spalten pro Zeile heißt weniger Scrollen, also bleibt der
   nicht-sticky Kopfbereich länger erreichbar. §8.4a **erlaubt** das — verboten ist allein eine
   *routengesteuerte* Breite („ausgeschlossen, nicht offen"), nicht eine andere einzige. Die
   Breite steht an **acht** Stellen: `app-shell.ts:36` (Shell-Kopfzeile), `:187` (`<main>`) und
   sechsmal in `landing-page.html` (5, 46, 78, 108, 151, 191). Der Betreiber nennt sie als
   Ursache dafür, dass die Filterleiste und der Seitenkopf eng wirken. **Gehört als eigenes,
   app-weites Issue geführt, nicht in diesen Branch** — und ändert an der Rangfolge der Flächen
   nichts, weil der Seitenkopf auch breit noch wegscrollt.
6. **Tastatur und Fokus.** Der Dock-Auslöser erweitert die Aktionszeile, für die §2.5 „Tastatur
   ist gleichwertig" verlangt; der Einspiel-Dialog braucht eine Fokusführung auf das File-Input.
   Regel 12 erlaubt Accessibility-Semantik ausdrücklich als Prüfgegenstand — gehört in Schritt 6.
7. **Neue i18n-Keys** (Regel 7, beide Locales): Beschriftung des Übertragen-Kommandos, Titel und
   Sortenliste des Einspiel-Dialogs, Beschriftung der Dock-Kurzform, benannte JSON-Option im
   Export-Dialog. Keine `ApiErrorCodes` betroffen — alles Transloco.

## Erfolgskriterien

- **Betreiber-Urteil** (kein Test: n = 1, und die Person war am Entwurf beteiligt): der Datei-Weg
  wird beim nächsten Live-Test ohne Suchen gefunden.
- **Erste unabhängige Probe:** bei der Mod-Discord-Vorstellung (#68) wird nicht erklärt, wo der
  Datei-Weg sitzt — es wird beobachtet, ob jemand danach fragt.
- Genau **ein** Kommando bewirbt den Transportweg. Der JSON-Export bleibt einspielbar (offene
  Frage 2 empfiehlt, ihn zu behalten) — die Beschriftung darf das nur nicht mehr anbieten.
  „Genau eine richtige Antwort" wäre falsch formuliert, solange die Option existiert.
- Die Regel beantwortet die Platzfrage für Stufe C (fremder Kanal als Quelle) ohne neue
  Diskussion.
- Alle Suiten grün, Coverage-Näherung über der 80-%-Schwelle.

## Auslieferung

Bestehende Pipeline: GHCR-Images + Portainer-Stack auf `emotepurge.app`. Keine Migration, kein
Backend-Endpunkt, kein neuer Fehlercode.

## Nächste Schritte

**Dieser Branch:** Schritte 1, 3, 4 sowie die zugehörigen Teile von 6, 7 und 8.
**Eigenes Issue #91 (Datei-Weg nach oben):** Schritt 2 samt seinen Test- und Doku-Nachzügen.
**Eigenes Issue #92 (Export entschärfen):** Schritt 5.
**Eigenes Issue #93 (Inhaltsbreite der App):** offene Frage 5.

1. **Regel schreiben** — neuer Abschnitt §8.7 in `docs/UI-Designsprache.md`, Verweise aus §2.5,
   §4.2 und §7.2, `DECISIONS.md`-Eintrag im selben Commit (Regel 3) inklusive der Präzisierung zu
   `:2469`. **§7.2 ist mitbetroffen:** dort ist die Zeilenreihenfolge des Ziel-Dialogs als Vertrag
   festgehalten („Bereichs-Radiogruppe … nur, wenn eine Grid-Auswahl existiert"); dass eine
   Aufrufstelle den Bereich erzwingen kann, ändert diesen Vertrag und gehört in denselben Eintrag.
   Ebenfalls nachzuziehen, sobald das Übertragen-Kommando umbenannt wird: §7.2, §4.2,
   `DECISIONS.md:626` und die Template-Kommentare `usage-stats-page.html:21-32` und `:710-712`
   (Letzterer wird durch den Umzug faktisch falsch).
   **Die Dock-Kurzform steht im DECISIONS-Eintrag ausdrücklich „auf Probe bis #68"** — das Muster
   kennt das Repo von der Sidecar-Beschriftung. Wird sie zurückgenommen, bricht keine Regel, weil
   §8.7 sie nur erlaubt und nicht verlangt.
2. **Datei-Weg** — `RestorePanel` in einen `RestoreTrigger` (Kopfbereich) und einen
   Einspiel-Dialog zerlegen: Sortenliste, eigenes `<input type="file">`, Fehlerbanner, Token-
   Prompt- und Restore-Confirm-Kette. `!isCoarse()` und das Set-Gate im Kopfbereich neu setzen.
   Aufwand M, nicht S — es ist ein Komponentenschnitt, kein Umzug.
3. **Dock-Kurzform** — Auslöser in `usage-stats-page.html`, projiziert in den
   `selection-actions`-Schlitz; `ImportTargetDialogData` um das Bereichs-Feld erweitern;
   `importScopeCurrent()` und die Arbiter-Sperre mitnehmen.
4. **Zeilenaufbau** — Trennung von „Löschen" in `mass-delete-panel.ts`, hinter einer Bedingung,
   damit die Voting-Seite mit leerem Schlitz unverändert bleibt.
5. **Export entschärfen** — JSON-Option benennen; Beschriftung des Übertragen-Kommandos nach
   Antwort auf Frage 1. Fällt weg, wenn Frage 3 zugunsten eines eigenen Issues entschieden wird.
6. **Tests** — neue Specs für Sperrentscheidungen und Zeilenreihenfolge (Regel 12); **bestehende**
   Fälle nachziehen, die den Datei-Weg an seiner alten Stelle greifen (`restore-panel.spec.ts`,
   `web/e2e/emote-import.e2e.spec.ts`, `web/e2e/audit/ui-audit.audit.ts`).
7. **Gates** — Vitest, E2E (nur mit freiem `:5151`), `node scripts/coverage-local.mjs` **nach**
   dem Commit. Achtung: ein Komponentenschnitt erzeugt „neuen Code" mit vollem Nenner, während
   die alten Specs an der alten Struktur hängen — der typische Gate-Reißer bei Umbauten.
8. **Zweitmeinungen** — `/codex:adversarial-review --model gpt-5.6-sol` auf dieses Dokument,
   `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` vor dem Merge.

## Was aus der Prüfrunde übrig bleibt

**Runde 1: 4/10.** Vier Einwände wurden am Code bestätigt und eingearbeitet: die invertierte
Kernthese, die nicht-sticky Kopfzeile, die Projektion der Aktionszeile ins geteilte Panel und der
bestehende `DECISIONS.md`-Eintrag zum Ort des Restore-Panels.

**Runde 2: 7/10**, Belege 21 von 25 zeilengenau. Eingearbeitet: der Regeltext widersprach seiner
eigenen Skizze („ans Ende der Zeile", während „Auswahl aufheben" folgt) und schloss über
„schreibend" aus, obwohl der eigene Kandidat zur Hälfte nur eine Datei herunterlädt — das
Kriterium heißt jetzt „Ergebnis hängt von der Auswahl ab", und die Export-Ausnahme steht auf
einem tragfähigen Grund (der Vorgabe `visible`, die eine Kurzform aushebelte). Dazu: Geltungs-
bereich verb-basiert statt an der Existenz eines Docks, Regel aus Kapitel 4 heraus nach §8 (damals §8.6, von Runde 3 auf §8.7 korrigiert),
„Supersede" zu „Präzisierung" korrigiert, §7.2-Vertrag nachgetragen, das Wort „Kopfbereich"
entzweit, die verschachtelten Dialoge als eigentlicher Aufwandstreiber benannt, vier
Belegstellen berichtigt.

**Nicht übernommen:** der Einwand, die Mehrdeutigkeit auf der Ausgabe-Seite sei unbelegt — sie
ist vom Betreiber in der Sitzung selbst benannt worden. Dass die Stichprobe eins ist, steht in
Prämisse 1 und in offener Frage 3.

**Umfangs-Einwand:** vom Betreiber entschieden — aufteilen, s. offene Frage 3.

**Runde 3: Codex Sol, adversarial, Verdict `needs-attention` (No-ship für den Regelteil).**
Zwei Befunde übernommen, weil am Code bestätigt:

- **§8.6 war bereits vergeben** („Rücknavigation", `UI-Designsprache.md:452`), referenziert aus
  sechs Stellen. Beide internen Runden hatten das durchgewinkt — der Abschnitt heißt jetzt §8.7.
- **Der Geltungsbereich war bei einer von zwei Referenzseiten falsch.** Die Voting-Detail-Seite
  trägt ihre Auswahlaktionen nicht im Seitenkopf, sondern im Fluss
  (`vote-session-detail-page.html:149-157`). Die Regel benennt das jetzt als zweiten zulässigen
  Ort statt als Ausnahme.
- Dazu die Belegkorrektur zu `wrongSet` (`:148`, nicht `:141`).

**Schiedsspruch vom 2026-09-07** (Fable, weil Codex und der interne Prüfer dieselbe Stelle
gegensätzlich bewerteten — Projektregel: der Orchestrator löst das nicht selbst auf):

- **Frage 1 an den internen Prüfer.** Die Kurzform ist eine legitime zweite Tür, keine verkleidete
  Doppelung — aber die Begründung im Entwurf war falsch gefasst. Nicht der *Ort* macht den
  Unterschied, sondern der **Name**: Überlappung heißt zwei Kommandos mit verschiedenem Namen für
  dieselbe Absicht. Damit ist offene Frage 1 geschlossen: ein Verb, zwingend.
- **Codex' Gegenvorschlag abgelehnt** (Dock fest `selection`, Seitenkopf fest `visible`,
  Bereichswahl weg). Zwei gleich benannte Knöpfe, die ohne Rückfrage verschieden große Mengen
  übertragen, sind die härtere Mehrdeutigkeit: wer oben mit Auswahl klickt, bekommt 900 Emotes
  vorgesetzt. Er hebelt zudem die erst gestern mit Begründung eingebaute R12-Vorbelegung aus
  (`import-target-dialog.ts:40`, „the asymmetry is the decision").
- **Die Sachfrage hinter Codex' zweitem Befund geht an Codex.** Die Auffindbarkeitsbehauptung ist
  unbelegt. Konsequenz ist aber nicht „keine Regel", sondern eine **geteilte** Regel: Seitenkopf/
  Dock/Reihenfolge beschreiben gelebte Praxis und werden Regel; die Kurzform wird **Erlaubnis**
  („darf"), nicht Pflicht, und geht als Einzelfall auf Widerruf in den DECISIONS-Eintrag, mit #68
  als Probe. Wird sie zurückgenommen, bricht keine Regel.

## Was mir an deinem Denken aufgefallen ist

- Du hast meine Entweder-oder-Frage zurückgewiesen — *„Ich finde Option 1 und 2 beide valide"* —
  und dann geliefert, was an der Frage falsch war: *„beides betrifft ja die auswahl, und dort
  fehlt dann eben der kopiervorgang."* Das war die Achse, nicht die Position.
- Der Befund, der die Sitzung gedreht hat, kam von dir: *„die json datei die raus kommt kann man
  aber auch wo anders importieren."* Ich hatte beide Dialoge gelesen und die Überlappung nicht
  gesehen.
- Nach dem vernichtenden Review hast du nicht die Entscheidung verteidigt, sondern die Tür weiter
  aufgemacht: *„noch gibt es kaum user, aktuell würde ein redesign nicht viel kosten."* Das ist
  die richtige Rechnung zum richtigen Zeitpunkt — sie hat hier nur zufällig gegen den größeren
  Umbau ausgeschlagen.
