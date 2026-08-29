# Untersuchung: Warum Emote-Bilder im Atlas langsam erscheinen (2026-08-29)

**Anlass:** Beobachtung im Browser — beim Betrachten der Nutzungsseite eines großen Sets „laden viele Bilder nicht, bzw. nur sehr langsam", einzelne Zellen bleiben über zehn Sekunden leer. Eine externe Analyse (Codex) schlug als Ursache vor, dass `NgOptimizedImage` die Bilder auf `loading="lazy"` setzt und der Browser die Requests für Zeilen zurückhält, die CDK bereits gerendert hat.

**Ergebnis in einem Satz:** Die vorgeschlagene Ursache ist widerlegt; der Engpass ist die Warteschlange des Browsers, gespeist aus zu vielen und zu großen Bildern — behoben wurde er über die Bildgröße, nicht über die Ladestrategie.

Dieses Dokument hält vor allem fest, **was ausgeschlossen ist**. Der Vorschlag klingt plausibel und wird wiederkommen.

---

## 1. Messaufbau

Gemessen wurde gegen die echte `usage-stats-page` im Browser, nicht gegen eine Nachbau-Seite:

- 649 echte Emotes aus der Dev-Datenbank (`brudivoeller_tv`), mit echten `cdn.7tv.app`-URLs — 431 × `4x_static.webp` (animierte Emotes), 218 × `4x.webp` (nicht animierte). Das entspricht dem Zustand nach `1001d0b`.
- API per Playwright gemockt, antwortet also mit **null Latenz**. Alles, was vor dem ersten Bildrequest liegt, ist damit reine Client-Zeit.
- Desktop-Chromium, 1280 × 720, frischer Browser-Kontext (kalter Cache) je Lauf.
- Zeitmessung über CDP, **nicht** über `PerformanceResourceTiming`: `cdn.7tv.app` sendet kein `Timing-Allow-Origin`, die Seite selbst sieht DNS, Verbindungsaufbau und Bytegrößen deshalb als `0`.

Harness: [`web/e2e/atlas-image-loading.measure.ts`](../web/e2e/atlas-image-loading.measure.ts) mit [`web/playwright.measure.config.ts`](../web/playwright.measure.config.ts). Läuft nicht in `npm run e2e` mit.

---

## 2. Widerlegt: Lazy Loading verzögert hier nichts

Erstaufbau, ohne einen einzigen Scroll, vier Läufe je Variante:

| | `loading="lazy"` (Ist) | `loading="eager"` |
|---|---|---|
| `<img>` im DOM | 185 | 185 |
| davon angefordert | **184** | 184 |
| entferntestes angefordertes Bild | **1071 px** unter dem Viewport | 1071 px |
| Daten → erster Bildrequest | 341/365/373/376 ms | 307/320/326/476 ms |

In **jedem** Lauf identisch. Die Entfernungs-Buckets sind lückenlos: von 0–199 px bis 1000–1199 px ist jeder zu 100 % angefordert. Chromium lädt alles, was CDK rendert, sofort.

Der Grund ist arithmetisch: Chrome startet `loading="lazy"`-Requests bereits bei rund 1250 px Abstand zum Viewport (4G; 2500 px bei langsamerer Verbindung). Der CDK-Puffer der Seite ist mit `rowHeight * 8` = 544 px deutlich kleiner. Alles, was gerendert wird, liegt also ohnehin innerhalb der Schwelle — und jenseits davon existiert kein `<img>`, das man „eager" machen könnte.

`loading="eager"` ist damit **messbar wirkungslos**: es kann 100 % nicht überbieten. Der Zeitunterschied oben ist Rauschen (überlappende Bereiche, der schlechteste `eager`-Lauf ist langsamer als jeder `lazy`-Lauf).

**Nicht gemessen:** Safari/WebKit. Dessen Schwelle lag historisch bei rund 100 px, Firefox bei rund 600 px — belastbare aktuelle Zahlen waren nicht auffindbar. Dort *könnte* `eager` etwas bringen; auf Chromium nicht. Playwrights WebKit-Build braucht Systembibliotheken (`sudo npx playwright install-deps webkit`), die auf der Devbox nicht installiert sind.

---

## 3. Wo die Zeit beim Erstaufbau hingeht

HTML 15 ms → Hauptbundle 42 ms → DOMContentLoaded 310 ms → Übersetzungen 329 ms → **Totals-API beantwortet 895 ms** → **erster Bildrequest 1236 ms**.

Die API antwortet hier in Nullzeit, und trotzdem vergehen ~900 ms, bevor die App überhaupt fragt. Das ist Bootzeit, keine Bildplanung — in Produktion kommt die echte API-Laufzeit noch obendrauf. Kein Eingriff an der Ladestrategie berührt das.

Der Erstaufbau ist ohnehin nicht das Problem: die 184 Requests gehen in einem Fenster von rund 400 ms raus.

---

## 4. Falsche Fährte: die vermeintliche CDN-Drosselung

Ein Zwischenergebnis dieser Untersuchung war falsch und wird hier festgehalten, damit es niemand wiederholt.

Ein Abruf aller 649 URLs mit 60 parallelen `curl`-Prozessen ergab TTFB-Werte von 5,15 / 10,24 / 15,18 / 20,17 s — exakte Vielfache von 5 Sekunden. Das sah nach serverseitigem Queueing aus. Es war der **lokale DNS-Resolver**: 60 gleichzeitige Prozesse lösen 60 mal `cdn.7tv.app` auf, der Resolver verwirft Anfragen, jede läuft in den 5-Sekunden-Standardtimeout und wiederholt.

Mit vorab gepinntem DNS (`curl --resolve`), sonst identisch:

| 60 parallele Requests | DNS (Median) | TTFB (Median) | TTFB (max) |
|---|---|---|---|
| eigene DNS-Auflösung je Prozess | 2,51 s | 2,75 s | 20,17 s |
| DNS gepinnt | 0,00 s | **0,38 s** | 1,25 s |

**Das 7TV-CDN drosselt nicht.** Ein Browser macht eine DNS-Abfrage und eine Verbindung und ist von diesem Effekt gar nicht betroffen. Lehre: Lastmessungen gegen einen fremden Host niemals mit parallelen Einzelprozessen — entweder DNS pinnen oder im Browser messen.

Nebenbefund: `cdn.7tv.app` löst auf `37.27.171.109` auf, also **Hetzner, nicht Cloudflare**; der `Server`-Header sagt `SevenTV`, ein `cf-cache-status` existiert nicht. Vermutungen über Telekom↔Cloudflare-Peering gehen daher ins Leere. Alle 649 URLs antworten mit `200`, es gibt keine toten Bilder.

---

## 5. Die tatsächliche Ursache: die Warteschlange des Browsers

Beim Durchscrollen des gesamten Sets, Ausgangszustand, vier Läufe:

| | Einzelwerte | Median |
|---|---|---|
| Bytes vom CDN | 5,33 / 5,79 / 5,94 / 8,92 MB | 5,87 MB |
| TTFB CDN p50 | 162 / 251 / 265 / 177 ms | 177 ms |
| **Wartezeit vor dem Absenden, p90** | 2976 / 4781 / 3759 / 44 ms | **3368 ms** |
| **Zelle leer, p90** | 4077 / 6357 / 5528 / 1367 ms | **4803 ms** |
| Zellen über 5 s leer | 33 / 91 / 61 / 0 | 47 |
| max. gleichzeitig leere Zellen | 120 / 120 / 120 / 31 | 120 |

Das CDN antwortet durchgehend in rund 0,2 s. Die Sekunden entstehen **davor**: 650 Bilder werden über eine einzige Verbindung (h2/h3, 549 von 551 Requests wiederverwendet) angefordert, der Browser kann nur einen Teil gleichzeitig laufen lassen und stellt den Rest hinten an. Dabei behalten auch Zeilen ihren Platz in der Schlange, an denen längst vorbeigescrollt wurde — die Zelle, die man gerade ansieht, wartet hinter Bildern, die niemand mehr sieht.

Das erklärt das Beobachtungsbild „mal sofort da, mal ewig" vollständig.

---

## 6. Die Änderung: passende Größenvariante statt immer 4x

Ausgeliefert wurde für jede Zelle die 4x-Variante (~128 px) in eine 64-px-Zelle, in der das Bild auf ~56 px gerendert wird. 7TV bietet vier Größen; die Zuordnung übernimmt jetzt ein auf `EmoteSprite` begrenzter `IMAGE_LOADER`, aus dem Angular ein Density-`srcset` baut (Begründung im [Entscheidungslog](DECISIONS.md), Eintrag vom 2026-08-29).

Vorab geprüft: **alle 649 Emotes** haben eine funktionierende 2x-Variante (431 × `2x_static.webp`, 218 × `2x.webp`, null Fehlschläge). Das war notwendige Vorbedingung — eine fehlende Variante wäre wegen des `(error)`-Zweigs in `EmoteSprite` eine dauerhaft leere Zelle, kein sichtbarer Fehler.

Wirkung, gemächliches Scrollen (Pause 1200 ms je Schritt):

| | vorher (4 Läufe) | nachher (3 Läufe) |
|---|---|---|
| Bytes | 5,33 / 5,79 / 5,94 / 8,92 MB | 3,51 / 3,50 / 3,50 MB |
| Wartezeit p90 | 2976 / 4781 / 3759 / 44 ms | 47 / 685 / 95 ms |
| Zelle leer p90 | 4077 / 6357 / 5528 / 1367 ms | 831 / 1954 / 959 ms |
| Zellen über 5 s leer | 33 / 91 / 61 / 0 | **0 / 0 / 0** |
| max. gleichzeitig leere Zellen | 120 / 120 / 120 / 31 | 0 / 32 / 0 |

Kontrolle, dass die Umschreibung greift: in allen Nachher-Läufen wurden **ausschließlich 2x-URLs** angefordert, kein einziges 4x.

Der mehrsekündige Ausläufer verschwindet in jedem Lauf. Die Byte-Halbierung ist deterministisch und wirkt am stärksten dort, wo die Leitung schmal ist.

---

## 7. Nicht umgesetzt, mit Begründung

**Kleinerer CDK-Puffer** (`rowHeight * 2` / `* 4` statt `* 4` / `* 8`) — gebaut, gemessen, verworfen. Isoliert gegen den Loader gemessen (Pause 1200 ms, Median): Loader allein 959 ms Zellenlatenz p90, Loader + kleinerer Puffer 699 ms, bei stark überlappenden Bereichen. Kein belegbarer Zusatznutzen, dafür weniger Vorlaufstrecke. Eine Änderung ohne Beleg wird nicht ausgeliefert.

**`loading="eager"`, größerer Puffer, Idle-Cache-Warmer** — die drei Kernvorschläge der externen Analyse. Alle drei erhöhen genau die Zahl, die den Stau verursacht: gleichzeitig angeforderte Bilder. Der Warmer würde zusätzlich das ganze Set (~900 Emotes) an jeden Besucher schieben, Handy inklusive.

**`preconnect` auf `cdn.7tv.app`** — nicht umgesetzt, aber unschädlich. Gemessene Verbindungskosten beim ersten Bild: DNS 29 ms + TCP 111 ms + TLS 77 ms ≈ 217 ms, danach wird die Verbindung für alle Requests wiederverwendet. Gebraucht wird sie erst bei ~1240 ms, ein Hinweis im HTML nähme sie aus dem kritischen Pfad. Einmalig ~200 ms, gegenüber den Sekunden aus Abschnitt 5 nebensächlich. Falls doch: **ohne** `crossorigin`, sonst öffnet der Browser eine zweite, ungenutzte Verbindung — die `<img>` laufen ohne CORS.

---

## 8. Offene Punkte

- **Schnelles Durchscrollen** (Pause 350 ms) ist nicht belastbar gemessen. Median besser (Zellenlatenz p90 3512 → 1427 ms, Zellen über 2 s 138 → 2), aber einer von drei Läufen war schlechter als die Baseline. Dort wird keine Verbesserung behauptet.
- **Safari/WebKit** ungemessen (s. Abschnitt 2).
- **Request-Churn beim Erstaufbau:** 239 Requests für 185 Bilder — 58 (~24 %) gelten Zeilen, die nie angezeigt werden, weil CDK den Zeilensatz neu baut, sobald die Sheet-Breite feststeht. In allen acht Läufen identisch reproduziert, nicht weiter verfolgt.
- **Messungen liefen gegen den Dev-Server** mit gemockter API, 649 statt ~900 Emotes, Scrollen in 600-px-Sprüngen simuliert. Die Relationen tragen, die Absolutwerte sind gegenüber einem Prod-Build pessimistisch.

## 9. Methodische Lehre

Einzelläufe streuen hier **stärker als der gemessene Effekt**. Beim schnellen Scrollen lag die Wartezeit p90 derselben Variante zwischen 77 und 2228 ms. Ein erster Vorher/Nachher-Vergleich aus je einem Lauf zeigte −89 % Wartezeit und −66 % Latenz; nach Wiederholung hielt davon nur die Byte-Zahl. Wer hier nachmisst: mindestens drei Läufe je Variante, im Wechsel statt blockweise (sonst fällt Netzdrift auf eine Variante), und Mediane samt Einzelwerten berichten.
