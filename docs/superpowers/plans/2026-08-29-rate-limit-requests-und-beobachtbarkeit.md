# Rate-Limit: Requests reduzieren und Ablehnungen beobachtbar machen — Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Die Live-Event-getriebenen Refetches eines Channel-Workspaces laufen in *einem* gedrosselten Zyklus über *eine* SSE-Verbindung, und eine Ablehnung durch den eigenen Rate-Limiter hinterlässt ab sofort ein Log, einen `Retry-After`-Header und einen übersetzbaren Fehlercode.

**Architecture:** Zwei getrennte Stoßrichtungen. (A) Frontend: `LiveUpdateService.stream()` wird pro URL multicast + ref-counted, damit Workspace-Layout und geroutete Kindseite sich eine `EventSource` teilen; danach wechselt das Layout von `liveEvents` auf `liveReload` mit derselben Debounce-Konstante wie die Usage-Seite — aus drei ungetakteten Abonnements auf demselben Stream wird eine gemeinsame Welle. (B) Backend: `RateLimiterOptions.OnRejected` bekommt eine Implementierung in einer eigenen Klasse `src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs`, die Policy-Name und Partition-Key aus der Partitionsfabrik über `HttpContext.Items` übernimmt, sie als Warnung loggt, `Retry-After` setzt und einen Body mit dem neuen `ApiErrorCodes.RateLimitExceeded` schreibt.

**Tech Stack:** ASP.NET Core 10 Minimal API, `System.Threading.RateLimiting` (Fixed Window), xUnit + NSubstitute + `WebApplicationFactory` · Angular 22 (Standalone, Signals, zoneless), RxJS `share`, Transloco · Vitest · Playwright

**Spec:** Kein separates Spec-Dokument. Die Beweisgrundlage ist die abgeschlossene Ursachenanalyse zu GitHub-Issue **#33** („A User with many Channels gets a rate limit error on first login") und **#35** („User got a rate limit error when deleting emotes"), gemessen im nginx-Access-Log der Produktion vom 2026-08-28. Sie ist in Task 3 wortgetreu als `docs/DECISIONS.md`-Eintrag zu hinterlegen und wird hier unter „Befundlage" vollständig wiedergegeben, damit Plan und Begründung zusammen reisen.

## Befundlage (die Messung, aus der dieser Plan folgt)

Abgelehnte Requests (HTTP 429) im Produktions-nginx-Log vom 2026-08-28:

```
22 /api/channels/basti_trimborn/emotes/duplicate-names   429
 7 /api/channels/basti_trimborn/usage-stats/totals        429
 7 /api/channels/basti_trimborn/emotes/active-set         429
 1 /api/channels/mine                                     429   (22:06:56)
 1 /api/channels/basti_trimborn/permissions               429   (22:26:08)
```

Alle 38 stammen vom eigenen ASP.NET-Rate-Limiter (Policy `ExternalApi`, 40 Requests/Minute, Fixed Window, partitioniert nach Twitch-User-Id, `QueueLimit 0`) — nicht von Cloudflare, nicht von nginx (das liefert 503), nicht von 7TV.

**Auslöser #35 (Löschen).** Der 7TV-Mass-Delete läuft direkt vom Browser gegen 7TV, rund 275 ms pro Emote. Jede Änderung meldet die 7TV-EventAPI an unseren Worker, der ein `channel.synced`-SSE-Event pusht. Daran hängen drei Refetches aus zwei Komponenten:

- `web/src/app/features/channel-workspace/channel-workspace-layout.ts:224-233` — `liveEvents(...)` **ohne Debounce**, ruft pro Event `loadDuplicateNames()` → `GET .../emotes/duplicate-names`. 22 von 38 Ablehnungen.
- `web/src/app/features/usage-stats/usage-stats-page.ts:642-659` — `liveReload(...)` mit 1000 ms Debounce, feuert pro Zyklus **zwei** Requests: `loadTotals()` und, bei `channel.synced`, `refreshSetStatus()`.

Hinzu kommt, was im Log nicht sichtbar ist: `LiveUpdateService.stream()` ist **cold und pro Subscriber**, die beiden Komponenten öffnen also je eine eigene `EventSource` auf dieselbe URL — zwei SSE-Verbindungen pro geöffnetem Workspace, zwei Auth-Handshakes, zwei Plätze in den Verbindungsgrenzen von `ILiveEventStream`. Auf der Admin-Monitoring-Seite sind es aus demselben Grund bis zu vier auf `/api/admin/live`.

**Auslöser #33 (Navigation).** Kein Login-Problem. Eine Workspace-Öffnung kostet mehrere Permits: `/permissions` (seit einem früheren Fix 30 s gecacht, `web/src/app/core/channels/channel.service.ts:51-76`), `duplicate-names`, `active-set`, `usage-stats/totals`, `usage-stats/series` — die vier letzten ungecacht. Jede Rückkehr zur Übersicht lädt `/api/channels/mine` neu (`overview-page.ts:47-49`). Der Kommentar in `Program.cs:104-106` hält fest, dass der Schritt von 20 auf 40 Permits das Problem nur verschoben hat.

**Warum nicht das Limit anheben.** Ausdrückliche Vorgabe des Betreibers. `ExternalApi` schützt nicht unsere Datenbank, sondern die **app-weiten** Twitch-Helix- und 7TV-Kontingente; ein einzelner schleifender Account kann sie leeren, und dann verliert jeder Moderator jedes Channels stillschweigend seine Rechte. Die Zahl 40 ist die Obergrenze, unter der `/mine` mit bis zu zehn Helix-Calls pro Request unter dem app-weiten Bucket (~800/min) bleibt.

## Global Constraints

- **Regel 1: Vor jedem `git commit` erst den Nutzer fragen.** Die Commit-Schritte in diesem Plan sind vorbereitet, **nicht freigegeben**. Der ausführende Agent legt den fertigen Diff vor und fragt.
- **Regel 2: Conventional Commits**, englisch, mehrere logisch getrennte Commits statt eines Sammel-Commits.
- **Regel 3:** Der Commit, der einen Vertrag ändert, enthält seinen `docs/DECISIONS.md`-Eintrag **im selben Commit** — hier Task 3. **Achtung: Eine parallele Session arbeitet gerade an `docs/DECISIONS.md`.** Der ausführende Agent liest die Datei unmittelbar vor dem Schreiben neu ein und fügt den Eintrag oben in die absteigend datierte Liste ein, statt aus einer alten Kopie zu arbeiten.
- **Regel 4/5/6:** Minimal API, keine Controller; kein `AppDbContext`/`IConnectionMultiplexer` aus Handlern. Dieser Plan legt keinen neuen Endpoint und keinen neuen Service an.
- **Regel 7:** Die API liefert nur sprachneutrale Codes. Ein neuer Code braucht denselben Eintrag in `web/src/app/core/i18n/api-error.ts` **und** in `web/public/i18n/de.json` **und** `en.json`. `api-error-locales.spec.ts` erzwingt die beiden hinteren Schritte, der Schritt von `ApiErrorCodes.cs` nach `api-error.ts` bleibt Disziplin — Task 4 ist genau dieser Schritt und darf nicht entfallen.
- **Regel 11:** Ein neuer `IEndpointFilter` oder eine Änderung an der Filter-Reihenfolge bekommt seinen Fall in `tests/EmotePurge.Api.Tests`. `OnRejected` ist streng genommen keines von beidem — es ist Middleware-Verhalten —, ist dort aber testbar und bekommt seinen Fall (Task 3), weil die Suite per `WebApplicationFactory` die echte `Program.cs`-Pipeline fährt.
- **Regel 12:** Neue Services/Guards/reine Utilities unter `web/src/app/core/` + `shared/` bekommen einen co-located `*.spec.ts` (Vitest). **Isolierte Komponententests sind ausdrücklich nicht Teil der Konvention** — die Änderung am `ChannelWorkspaceLayout` (Task 2) bekommt deshalb keine Komponentenspec, sondern einen Playwright-E2E-Fall.
- **Regel 16:** Backend-Änderungen vor dem Commit live gegen echte Postgres/Redis verifizieren, nicht nur `dotnet build` (Task 5).
- **Regel 18:** `npm --prefix web run format` und `dotnet format EmotePurge.slnx` vor jedem Commit; die CI prüft beides plus `npm --prefix web run lint`.
- **Sprache:** Bezeichner, Typen und **Kommentare in neuem Code englisch**; **Log- und `throw`-Messages deutsch**; Projektdoku deutsch; Commit-Messages englisch.
- **Die E2E-Suite läuft nur, wenn auf `:5151` keine Api lauscht.** Wer vorher `dotnet run --project src/EmotePurge.Api` gestartet hat, beendet es, sonst fällt rund die halbe Suite mit irreführenden „element not found"-Fehlern durch.
- **Ausgangslage im Repo:** aktueller Branch `feat/emote-static-images`, `docs/VPS-Reverse-Proxy.md` ist uncommitted verändert und gehört **nicht** zu dieser Arbeit. Vor Task 1 einen eigenen Branch anlegen (`git switch -c fix/rate-limit-requests-and-observability`) oder per `superpowers:using-git-worktrees` einen isolierten Worktree — und die fremde Änderung nicht mitnehmen.

## Entscheidungen, die dieser Plan trifft

Fünf Punkte, an denen es Alternativen gab. Wer den Plan prüft, soll sie benannt vorfinden statt sie zu suchen.

1. **`liveReload` *und* eine geteilte Verbindung, nicht nur ein Debounce.** Der Debounce allein löst #35 (22 → ~7 Requests). Er löst aber nicht, dass zwei Komponenten desselben Workspaces zwei SSE-Verbindungen halten und ihre Debounce-Fenster unabhängig voneinander takten. Multicast auf `LiveUpdateService.stream()` ist die kleinere Änderung von beiden (eine `share()`-Zeile plus Map), sie ist in `core/` isoliert testbar, sie ändert an keiner Aufrufstelle etwas, und sie macht die Fenster deckungsgleich: gleiche Quelle, gleiche Events, gleiche Dauer → eine Welle. Zusammen fallen die ~38 abgelehnten Requests des gemessenen Vorfalls auf 3 pro Burst.
2. **Kein gemeinsamer Koordinator-Service.** Naheliegend wäre ein `ChannelReloadCoordinator` in `core/live/`, bei dem sich Layout und Seite mit Callbacks registrieren. Dagegen: er zieht die Entscheidung, *was* nachgeladen wird, aus den Komponenten in die Core-Schicht, wo sie nicht hingehört; er braucht eine eigene Lebenszyklus-Verwaltung, die `takeUntilDestroyed` heute geschenkt bekommt; und er kauft gegenüber „eine Quelle + eine Konstante" exakt nichts — die Requestzahl pro Burst bleibt in beiden Entwürfen drei. Verworfen.
3. **Kein Aussetzen der Reloads während des eigenen Mass-Delete-Laufs.** Technisch reizvoll (`SevenTvDeleteService.isRunning` existiert), weil die `channel.synced`-Flut während eines Laufs selbstverschuldet ist und die UI ihren Fortschritt ohnehin optimistisch zeigt. Verworfen, weil der Debounce den Fall bereits erledigt: bei einem Event alle ~275 ms feuert ein 1000-ms-`debounceTime` erst in einer Lücke ≥ 1 s, ein dichter Lauf kollabiert also von sich aus auf ein bis zwei Zyklen. Ein Signal-Gate in einer generischen Core-Utility für einen Fall, den die vorhandene Mechanik schon abdeckt, ist YAGNI.
4. **`duplicate-names` wird nicht in `active-set` gefaltet.** Beide Endpoints haben dieselbe Zielgruppe (`UsageStatsAccessAuthorizationFilter`), denselben Auslöser (`channel.synced`) und würden zusammengelegt sowohl die Workspace-Öffnung (5 → 4 Requests) als auch jeden Burst (3 → 2) verbilligen. Verworfen für diesen Plan: die beiden Werte werden von **verschiedenen** Komponenten gerendert (Banner im Layout, Slot-Budget in der Usage-Seite), eine Zusammenlegung bräuchte also einen geteilten Store und einen Umbau der 1109 Zeilen langen `usage-stats-page.ts` — bei einer Konvention, die isolierte Komponententests ausschließt, ist das ein Risiko ohne Netz. Bleibt als benannte Option für #33, falls die Navigationsseite nach dieser Runde noch drückt.
5. **Der neue Fehlercode *ergänzt* `errors.status.rateLimited`, er ersetzt ihn nicht — mit wortgleichem Text.** Sobald der Limiter einen Body schickt, greift für ihn `errors.api.rate_limit_exceeded` statt des Status-Fallbacks. Der Fallback bleibt trotzdem stehen und bleibt getestet: er deckt ab sofort genau die 429er ab, die **nicht** von uns kommen (nginx, Cloudflare, ein zwischengeschalteter Proxy) — und für die ist er weiterhin die einzig richtige Antwort. Der Text ist bewusst identisch: der Nutzer soll denselben Satz lesen, egal wer gedrosselt hat; die Unterscheidung ist eine Diagnose-Eigenschaft, keine UX-Eigenschaft. Ein zweiter, abweichender Text („Warte noch 37 Sekunden") wäre besser, verlangt aber, dass `apiErrorTranslationKey` Parameter durchreicht — das ist ein Umbau jeder Aufrufstelle und gehört nicht in diesen Plan. `retryAfterSeconds` steht im Body und wartet dort darauf.

## Dateistruktur

**Neu:**

| Datei | Verantwortung |
|---|---|
| `src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs` | Die Partitionsfabrik **und** die Ablehnungs-Antwort: Log, `Retry-After`, Body. Beide zusammen, weil nur die Fabrik weiß, wie der Partition-Key zustande kam, und nur die Antwort ihn braucht |
| `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs` | Der 429-Vertrag an der echten Pipeline: Status, Header, Body, Logzeile — und die Gegenprobe, dass ein Request innerhalb des Budgets unangetastet bleibt |

**Geändert:**

| Datei | Änderung |
|---|---|
| `web/src/app/core/live/live-update.service.ts` | `stream()` wird pro URL multicast + ref-counted; der bisherige Rumpf wandert in ein privates `connect()` |
| `web/src/app/core/live/live-update.service.spec.ts` | Vier neue Fälle: geteilte Verbindung, Verbindung bleibt beim vorletzten Abmelden offen, getrennte URLs, Neuaufbau nach dem letzten Abmelden |
| `web/src/app/core/live/live-reload.ts` | Neue exportierte Konstante `CHANNEL_RELOAD_DEBOUNCE_MS`; zwei Doc-Kommentare, die Task 1 falsch macht, werden richtiggestellt |
| `web/src/app/features/channel-workspace/channel-workspace-layout.ts:224-233` | `liveEvents` → `liveReload` mit der geteilten Konstante |
| `web/src/app/features/usage-stats/usage-stats-page.ts:127,647-649` | Lokales `USAGE_RELOAD_DEBOUNCE_MS` weicht der geteilten Konstante |
| `web/e2e/channel-workspace.e2e.spec.ts` | Neuer Fall: ein Burst von fünf `channel.synced` kostet einen `duplicate-names`-Refetch, nicht fünf |
| `src/EmotePurge.Api/Program.cs:95-141` | `OnRejected` verdrahtet; die lokale `PartitionPerUser`-Funktion zieht nach `RateLimitRejection` um; die vier `AddPolicy`-Aufrufe laufen über einen lokalen Helfer, der den Policy-Namen genau einmal nennt |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | Neu: `RateLimitExceeded = "rate_limit_exceeded"` |
| `web/src/app/core/i18n/api-error.ts` | Spiegel des neuen Codes; zwei Doc-Sätze, die „the rate limiter with a bare 429" behaupten, werden richtiggestellt |
| `web/src/app/core/i18n/api-error.spec.ts` | Zwei Fälle: codierte 429 → `errors.api.rate_limit_exceeded`, body-lose 429 → weiterhin `errors.status.rateLimited` |
| `web/public/i18n/de.json`, `web/public/i18n/en.json` | `errors.api.rate_limit_exceeded` |
| `docs/DECISIONS.md` | Der Vertragseintrag, im selben Commit wie Task 3 |

---

## Task 1: Eine SSE-Verbindung pro URL statt eine pro Abonnent

**Files:**
- Modify: `web/src/app/core/live/live-update.service.ts`
- Modify: `web/src/app/core/live/live-reload.ts` (nur ein Doc-Kommentar)
- Test: `web/src/app/core/live/live-update.service.spec.ts`

**Interfaces:**
- Consumes: nichts aus früheren Tasks.
- Produces: `LiveUpdateService.stream(url: string): Observable<LiveEvent>` — Signatur unverändert, Semantik neu: multicast pro URL, ref-counted. Task 2 verlässt sich darauf, dass zwei Aufrufe mit derselben URL denselben Stream liefern.

- [ ] **Step 1: Die vier fehlschlagenden Tests schreiben**

In `web/src/app/core/live/live-update.service.spec.ts`, hinter den vorhandenen Fällen, vor dem schließenden `});`:

```ts
  it('shares one connection between two subscribers of the same url', () => {
    // The workspace layout and the page routed into it both listen to this URL. Before this they
    // opened two EventSources, ran two auth handshakes and took two slots in ILiveEventStream.
    const first: LiveEvent[] = [];
    const second: LiveEvent[] = [];
    const url = '/api/channels/sensitron/live';

    const firstSubscription = service.stream(url).subscribe((event) => first.push(event));
    const secondSubscription = service.stream(url).subscribe((event) => second.push(event));

    expect(FakeEventSource.instances).toHaveLength(1);

    FakeEventSource.instances[0].emit(
      JSON.stringify({ type: 'channel.synced', channel: 'sensitron' }),
    );

    expect(first).toEqual([{ type: 'channel.synced', channel: 'sensitron' }]);
    expect(second).toEqual([{ type: 'channel.synced', channel: 'sensitron' }]);

    firstSubscription.unsubscribe();
    secondSubscription.unsubscribe();
  });

  it('keeps the connection open while one subscriber remains', () => {
    const url = '/api/channels/sensitron/live';
    const firstSubscription = service.stream(url).subscribe();
    const secondSubscription = service.stream(url).subscribe();
    const source = FakeEventSource.instances[0];

    firstSubscription.unsubscribe();

    expect(source.closeCount).toBe(0);

    secondSubscription.unsubscribe();

    expect(source.closeCount).toBe(1);
  });

  it('opens a separate connection per url', () => {
    const one = service.stream('/api/channels/one/live').subscribe();
    const two = service.stream('/api/channels/two/live').subscribe();

    expect(FakeEventSource.instances.map((instance) => instance.url)).toEqual([
      '/api/channels/one/live',
      '/api/channels/two/live',
    ]);

    one.unsubscribe();
    two.unsubscribe();
  });

  it('reconnects a url whose last subscriber had left', () => {
    // The switchMap in liveEvents() drops a channel's stream on navigation and picks it up again on
    // the way back — a cached-but-dead observable would leave that user with no live updates at all.
    const url = '/api/channels/sensitron/live';
    service.stream(url).subscribe().unsubscribe();
    expect(FakeEventSource.instances).toHaveLength(1);

    const again = service.stream(url).subscribe();

    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[1].url).toBe(url);
    again.unsubscribe();
  });
```

- [ ] **Step 2: Die Tests laufen lassen und den Fehlschlag prüfen**

Run: `npm --prefix web test -- --watch=false live-update.service`
Expected: FAIL. Der erste Fall meldet `expected length 1, received 2` (zwei `EventSource`-Instanzen), der zweite `expected 0, received 1` (die erste Abmeldung schließt schon).

- [ ] **Step 3: `stream()` auf Multicast umbauen**

In `web/src/app/core/live/live-update.service.ts`:

Import erweitern:

```ts
import { Observable, share } from 'rxjs';
```

Feld zu den anderen Feldern (nach `statusSignal`/`status`, vor `stream`):

```ts
  /** One entry per URL this session has ever subscribed to. Holds no connection — only the (inert)
   *  multicast Observable, so a session that visited twenty channels carries twenty closures. */
  private readonly sharedStreams = new Map<string, Observable<LiveEvent>>();
```

`stream()` ersetzen (der bisherige Rumpf wandert unverändert nach `connect()`):

```ts
  /**
   * Multicast per URL, ref-counted: the first subscriber opens the connection, the last one to
   * unsubscribe closes it, and everyone in between shares the same `EventSource`.
   *
   * Still cold — nothing is opened until someone subscribes — and a URL whose subscribers have all
   * left is rebuilt from scratch on the next one, which is what keeps the `switchMap` in
   * {@link liveEvents} working across navigation.
   *
   * Sharing rather than one connection per subscriber, because the subscribers of one URL sit on top
   * of each other: the channel workspace layout and whichever page is routed into it both listen to
   * `/api/channels/{name}/live`, and the admin monitoring page has up to three listeners on
   * `/api/admin/live`. Each of those used to be its own connection, its own auth handshake, and its
   * own slot in ILiveEventStream's connection limits.
   *
   * One behavioural consequence: the single visibility-retry after a fatal close is now per
   * connection, not per component. That is the more honest budget — the connection is what failed.
   */
  stream(url: string): Observable<LiveEvent> {
    const shared = this.sharedStreams.get(url);
    if (shared) {
      return shared;
    }

    const created = this.connect(url).pipe(share({ resetOnRefCountZero: true }));
    this.sharedStreams.set(url, created);
    return created;
  }
```

Am Klassenende, als privates Member (Member-Reihenfolge: private Helfer ans Ende):

```ts
  /** The single-subscriber connection the multicast above wraps. */
  private connect(url: string): Observable<LiveEvent> {
    return new Observable<LiveEvent>((subscriber) => {
      // ... unveränderter bisheriger Rumpf von stream() ...
    });
  }
```

- [ ] **Step 4: Tests laufen lassen und den Erfolg prüfen**

Run: `npm --prefix web test -- --watch=false live-update.service`
Expected: PASS, alle Fälle — auch die vorhandenen (`is cold`, `closes the connection on unsubscribe`, `does not rebuild the connection after a fatal error`). Bleibt einer davon rot, ist die Ursache fast sicher, dass `share()` ohne `resetOnRefCountZero: true` konfiguriert wurde.

- [ ] **Step 5: Den Doc-Kommentar richtigstellen, den dieser Umbau falsch macht**

In `web/src/app/core/live/live-reload.ts` sagt der Block über `liveReload` heute: „the reason it existed is that `LiveUpdateService.stream()` is cold — subscribing a second time to inspect the events would open a second `EventSource`". Das stimmt nach Step 3 nicht mehr. Ersetzen durch:

```ts
 * Emits **once per debounced burst**, carrying the set of event types merged into that burst. That
 * set is what replaces the `syncSeenSinceReload` field two pages used to keep. Since 2026-08-29
 * `LiveUpdateService.stream()` is shared per URL, so a second subscription no longer costs a second
 * connection — but it would still cost a second debounce pipeline with its own, independently timed
 * burst boundary, which is exactly the thing this function exists to have only one of.
```

- [ ] **Step 6: Die ganze Frontend-Unit-Suite laufen lassen**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS. `live-reload.spec.ts` enthält den Fall „opens exactly one EventSource, so the merged-type set is not a second subscription" — der bleibt gültig und muss grün sein.

- [ ] **Step 7: Formatieren und committen** (Regel 1: vorher fragen)

```bash
npm --prefix web run format
npm --prefix web run lint
git add web/src/app/core/live/live-update.service.ts web/src/app/core/live/live-update.service.spec.ts web/src/app/core/live/live-reload.ts
git commit -m "perf(web): share one SSE connection per live url"
```

---

## Task 2: Der Workspace-Reload läuft im selben gedrosselten Zyklus wie die Usage-Seite

**Files:**
- Modify: `web/src/app/core/live/live-reload.ts` (neue Konstante)
- Modify: `web/src/app/features/channel-workspace/channel-workspace-layout.ts` (Import-Block und Zeilen 224-233)
- Modify: `web/src/app/features/usage-stats/usage-stats-page.ts` (Zeile 127 und 647-649)
- Test: `web/e2e/channel-workspace.e2e.spec.ts`

**Interfaces:**
- Consumes: die geteilte Verbindung aus Task 1 (`LiveUpdateService.stream()` multicast pro URL). Ohne sie funktioniert dieser Task auch — dann sind es zwei Verbindungen mit zwei gleich langen, aber unabhängig gestarteten Fenstern.
- Produces: `CHANNEL_RELOAD_DEBOUNCE_MS: number` aus `web/src/app/core/live/live-reload.ts`, exportiert.

- [ ] **Step 1: Den fehlschlagenden E2E-Fall schreiben**

In `web/e2e/channel-workspace.e2e.spec.ts`, innerhalb der `test.describe('authenticated broadcaster', …)`-Gruppe, direkt hinter dem Fall „shows no duplicate-name banner when every active emote name is unique":

```ts
  test('a burst of sync events costs one duplicate-names refetch, not one per event', async ({
    page,
  }) => {
    // The measured cause of issue #35: a 7TV mass delete pushes one channel.synced per removed
    // emote (~275 ms apart) and the workspace layout refetched the collision set on every one of
    // them — 22 of the 38 requests the API rejected with 429 on 2026-08-28.
    await mockChannelPermissions(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    // Counted instead of mocked through mockDuplicateEmoteNames: the number of calls *is* the
    // assertion here.
    let duplicateNameRequests = 0;
    await page.route('**/api/channels/sensitron/emotes/duplicate-names', (route) => {
      duplicateNameRequests++;
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    });

    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    expect(duplicateNameRequests).toBe(1);

    // Emitted inside one evaluate so the five frames really are one burst — five separate
    // round-trips from the test runner could straddle the debounce window.
    await page.evaluate(() => {
      const emit = (window as unknown as { __emitLive: (event: unknown) => void }).__emitLive;
      for (let index = 0; index < 5; index++) {
        emit({ type: 'channel.synced', channel: 'sensitron' });
      }
    });

    // One debounce window plus slack. waitForTimeout is the honest tool here: the thing under test
    // is that nothing happens for a second.
    await page.waitForTimeout(1500);

    expect(duplicateNameRequests).toBe(2);
  });
```

- [ ] **Step 2: Den E2E-Fall laufen lassen und den Fehlschlag prüfen**

Erst sicherstellen, dass auf `:5151` **keine** Api lauscht (`ss -ltnp | grep 5151` — falls doch, `dotnet run` beenden), dann:

Run: `npm --prefix web run e2e -- -g "a burst of sync events"`
Expected: FAIL mit `expected 2, received 6` — fünf ungedrosselte Refetches plus der eine vom Laden.

- [ ] **Step 3: Die geteilte Konstante anlegen**

In `web/src/app/core/live/live-reload.ts`, auf Modulebene über `liveEvents`:

```ts
/**
 * The debounce every channel-scoped reload uses, so that the refetches one `channel.synced` triggers
 * land in one wave instead of several staggered ones.
 *
 * Shared rather than declared per page because the pages sit on top of each other: the workspace
 * layout and the usage page are mounted together and listen to the same (shared) connection, so one
 * value here means one burst boundary for both. One second was the usage page's own figure and the
 * reasoning carries over unchanged — the worker flushes chat usage in 30-second batches, so pushes
 * arrive in bursts rather than continuously, and a second merges a burst without making the update
 * feel delayed. Against a 7TV mass delete (one event every ~275 ms) it collapses the whole run into
 * one or two refetches, because the window only elapses in a gap.
 *
 * The vote-session detail page keeps its own, shorter 500 ms window on purpose: a live tally is the
 * thing its user is watching, and it is never mounted while the mass-delete panel runs.
 */
export const CHANNEL_RELOAD_DEBOUNCE_MS = 1000;
```

Im `@example`-Block derselben Datei steht heute `debounceMs: LIVE_RELOAD_DEBOUNCE_MS` — eine Konstante, die es nirgends gibt. Im selben Zug auf `CHANNEL_RELOAD_DEBOUNCE_MS` korrigieren.

- [ ] **Step 4: Die Usage-Seite auf die geteilte Konstante umstellen**

In `web/src/app/features/usage-stats/usage-stats-page.ts` den lokalen Block bei Zeile 121-127 löschen:

```ts
// The worker flushes chat usage in 30-second batches, so pushes arrive in bursts rather than
// continuously. One second of debounce merges a burst (several channels' flushes land in the same
// tick) into a single refetch without making the update feel delayed.
const USAGE_RELOAD_DEBOUNCE_MS = 1000;
```

(Der Inhalt dieses Kommentars steckt jetzt in der Doku der geteilten Konstante — nicht duplizieren.)

Den vorhandenen Import aus `core/live/live-reload` erweitern:

```ts
import { CHANNEL_RELOAD_DEBOUNCE_MS, liveReload } from '../../core/live/live-reload';
```

Und die Verwendung bei Zeile 647-649:

```ts
    liveReload(this.liveUrl, {
      accept: [LIVE_EVENT_TYPES.usageFlushed, LIVE_EVENT_TYPES.channelSynced],
      debounceMs: CHANNEL_RELOAD_DEBOUNCE_MS,
    }).subscribe((seen) => {
```

- [ ] **Step 5: Das Workspace-Layout auf `liveReload` umstellen**

In `web/src/app/features/channel-workspace/channel-workspace-layout.ts` den Import bei Zeile 14 ersetzen:

```ts
import { CHANNEL_RELOAD_DEBOUNCE_MS, liveReload } from '../../core/live/live-reload';
```

Und die Subscription bei Zeile 218-233 ersetzen:

```ts
    // The 202 only means "the worker was told". This is what turns "angestoßen" into
    // "abgeschlossen": the RESYNC path publishes channel.synced unconditionally, unlike the
    // periodic one, precisely so this confirmation can exist. The stream is already scoped to this
    // channel, so no event needs inspecting — but the upgrade only fires while a resync of ours is
    // still on screen, otherwise the periodic sync of any channel would announce itself.
    //
    // liveReload rather than liveEvents, since 2026-08-29: a 7TV mass delete pushes one
    // channel.synced per removed emote, roughly every 275 ms, and this handler refetches
    // duplicate-names on every one of them. Undebounced that was the single largest source of the
    // 429s in issue #35 — 22 of 38 rejected requests. The window delays the resync confirmation by
    // at most one second, which is well inside RESYNC_FEEDBACK_MS.
    liveReload(
      computed(() => channelLiveUrl(this.channelName())),
      {
        accept: [LIVE_EVENT_TYPES.channelSynced],
        debounceMs: CHANNEL_RELOAD_DEBOUNCE_MS,
      },
    ).subscribe(() => {
      if (this.resyncFeedbackKey() !== null) {
        this.showResyncFeedback('channelWorkspace.resync.completed');
      }
      // The inventory changed, so the collision set may have too — including the good case where
      // the banner disappears right after the user fixed the duplicate on 7TV.
      this.loadDuplicateNames(this.channelName());
    });
```

`loadDuplicateNames` prüft bereits selbst `canViewUsageStats()` und schickt für einen Nutzer ohne diese Berechtigung nichts los — hier ist nichts zu ergänzen.

- [ ] **Step 6: Den neuen E2E-Fall laufen lassen**

Run: `npm --prefix web run e2e -- -g "a burst of sync events"`
Expected: PASS, `duplicateNameRequests === 2`.

- [ ] **Step 7: Den bestehenden Resync-Bestätigungsfall laufen lassen — die nutzersichtbare Regression**

Run: `npm --prefix web run e2e -- -g "resync reports queued and upgrades to finished"`
Expected: PASS. Dieser Fall ist der Grund, warum die Subscription im Layout überhaupt bestehen bleibt: „Resync angestoßen …" muss nach dem `channel.synced` zu „Resync abgeschlossen." werden. Neu ist nur, dass das jetzt rund eine Sekunde später passiert — Playwrights `expect`-Timeout von 5 s deckt das ab, `RESYNC_FEEDBACK_MS` (4000) auch. Wird der Fall rot, ist das kein Timing-Problem, sondern ein Zeichen, dass die Bedingung `resyncFeedbackKey() !== null` verlorengegangen ist.

- [ ] **Step 8: Volle Frontend-Suiten**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS

Run: `npm --prefix web run e2e`
Expected: PASS, 76+ Fälle. (Wieder: `:5151` muss frei sein.)

- [ ] **Step 9: Formatieren und committen** (Regel 1: vorher fragen)

```bash
npm --prefix web run format
npm --prefix web run lint
git add web/src/app/core/live/live-reload.ts web/src/app/features/channel-workspace/channel-workspace-layout.ts web/src/app/features/usage-stats/usage-stats-page.ts web/e2e/channel-workspace.e2e.spec.ts
git commit -m "fix(web): debounce the workspace duplicate-name refetch onto the shared reload cycle"
```

---

## Task 3: Der Rate-Limiter antwortet sichtbar — Log, `Retry-After`, Fehlercode

**Files:**
- Create: `src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs`
- Create: `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs`
- Modify: `src/EmotePurge.Api/Program.cs:13-17` (usings), `:93-141` (Limiter-Block)
- Modify: `src/EmotePurge.Api/Validation/ApiErrorCodes.cs`
- Modify: `docs/DECISIONS.md`

**Interfaces:**
- Consumes: `ApiErrorCodes` (internal, `EmotePurge.Api.Validation`), `TestAuthHandler.UserIdHeader` / `.LoginHeader` und `ApiFactory` aus `tests/EmotePurge.Api.Tests`.
- Produces:
  - `internal static class EmotePurge.Api.RateLimiting.RateLimitRejection` mit
    `internal const string LogCategory = "EmotePurge.Api.RateLimiting"`,
    `internal static readonly TimeSpan Window`,
    `public static RateLimitPartition<string> PartitionPerUser(HttpContext httpContext, string policyName, int permitLimit)`,
    `public static ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)`.
  - `ApiErrorCodes.RateLimitExceeded = "rate_limit_exceeded"` — Task 4 spiegelt genau diesen String.
  - Antwortvertrag: HTTP 429, Header `Retry-After: <ganze Sekunden>`, Body `{"errorCode":"rate_limit_exceeded","retryAfterSeconds":<int>}`.

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

Neu, `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The rejected half of the rate limiter, over the real Program.cs pipeline. Until 2026-08-29 there
/// was none: every policy answered a bare 429 — no body, no Retry-After, no log line — so a
/// throttled user got the frontend's generic status message and the server side of the story did not
/// exist at all. Finding out that issues #33/#35 were self-inflicted took two rounds and a read of
/// the production nginx access log; that is what these three assertions are meant to prevent.
/// </summary>
public class RateLimitRejectionTests : IClassFixture<ApiFactory>
{
    /// <summary>The ExternalApi policy's budget, mirrored from Program.cs.</summary>
    private const int ExternalApiPermitLimit = 40;

    /// <summary>
    /// Chosen because both services its handler resolves are substituted in ApiFactory, so a
    /// request costs no database and no Redis — the loop below sends more than eighty of them.
    /// </summary>
    private const string PermissionsPath = "/api/channels/testchannel/permissions";

    private readonly ApiFactory _factory;

    public RateLimitRejectionTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ExhaustedBudget_Answers429_WithRetryAfterAndAnErrorCode()
    {
        using var client = _factory.CreateClient();

        // A partition key of this test's own: the limiter partitions by the NameIdentifier claim, so
        // sharing one across tests would make the outcome depend on execution order.
        using var response = await ExhaustAsync(client, "rate-limit-contract");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
        var retryAfterSeconds = int.Parse(retryAfter);
        // A fixed window of one minute: never longer than the window, never zero (a client told to
        // retry after zero seconds retries straight into the next rejection).
        Assert.InRange(retryAfterSeconds, 1, 60);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ApiErrorCodes.RateLimitExceeded,
            body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(retryAfterSeconds, body.RootElement.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task RequestWithinTheBudget_IsUntouched()
    {
        // The other direction: OnRejected must not run for an accepted request, or every response in
        // the app would carry a Retry-After telling clients to back off from nothing.
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, "rate-limit-headroom");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Rejection_IsLoggedWithPolicyPathAndPartition()
    {
        var log = new CapturingLoggerProvider();
        // Its own host rather than the shared fixture's: a logging provider has to be registered at
        // startup, and a fresh host also means a fresh, unspent limiter.
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(log)));
        using var client = factory.CreateClient();

        using var response = await ExhaustAsync(client, "rate-limit-log");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var entry = Assert.Single(log.Entries.Where(e => e.Category == RateLimitRejection.LogCategory));
        Assert.Equal(LogLevel.Warning, entry.Level);
        // The three things the production investigation had to reconstruct from nginx: which policy,
        // which path, whose budget.
        Assert.Contains("ExternalApi", entry.Message);
        Assert.Contains(PermissionsPath, entry.Message);
        Assert.Contains("rate-limit-log", entry.Message);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, PermissionsPath);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return client.SendAsync(request);
    }

    /// <summary>Spends the budget and hands back the first rejected answer.</summary>
    private static async Task<HttpResponseMessage> ExhaustAsync(HttpClient client, string userId)
    {
        // Deliberately more than two windows' worth: a fixed window that happens to roll over
        // mid-loop hands out a second full budget, and a loop of exactly PermitLimit + 1 would then
        // never see a rejection at all. Eighty-one requests cannot fit into two budgets of forty.
        const int attempts = (2 * ExternalApiPermitLimit) + 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var response = await SendAsync(client, userId);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        throw new InvalidOperationException(
            $"Nach {attempts} Anfragen kam keine 429-Antwort — der Rate-Limiter greift nicht.");
    }

    /// <summary>Captures every log entry so a test can assert on one. ILogger has no test double in
    /// this project, and the one thing under test here is that a line is written at all.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        internal sealed record LogEntry(string Category, LogLevel Level, string Message);

        private sealed class CapturingLogger(string category, CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => provider.Add(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}
```

- [ ] **Step 2: Den Test laufen lassen und den Fehlschlag prüfen**

Run: `dotnet test tests/EmotePurge.Api.Tests/EmotePurge.Api.Tests.csproj --filter FullyQualifiedName~RateLimitRejectionTests`
Expected: FAIL — Kompilierfehler `CS0234`/`CS0117`: `RateLimitRejection` und `ApiErrorCodes.RateLimitExceeded` existieren nicht. Das ist der erwartete erste Fehlschlag.

- [ ] **Step 3: Den Fehlercode ergänzen**

In `src/EmotePurge.Api/Validation/ApiErrorCodes.cs`, unmittelbar **vor** `UnexpectedError` (die Reihenfolge ist der Spiegel von `KNOWN_API_ERROR_CODES` in `api-error.ts`, die in Task 4 an derselben Stelle ergänzt wird):

```csharp
    // The generic rate limiter's 429, distinct from ResyncCooldownActive above: that one names a
    // per-channel cooldown any moderator can trip, this one means the caller's own per-minute budget
    // is spent. Both carry retryAfterSeconds and a Retry-After header, so the frontend can say which
    // of the two happened instead of falling back to one message for every 429 in existence.
    public const string RateLimitExceeded = "rate_limit_exceeded";
```

- [ ] **Step 4: `RateLimitRejection` anlegen**

Neu, `src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs`:

```csharp
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EmotePurge.Api.Validation;
using Microsoft.AspNetCore.RateLimiting;

namespace EmotePurge.Api.RateLimiting;

/// <summary>
/// The partitioning and the rejected answer of every rate-limit policy, in one place because they
/// share a secret: only the partitioner knows how the partition key was derived, and only the
/// rejection needs to name it.
/// </summary>
/// <remarks>
/// Before 2026-08-29 there was no rejection handler at all. A throttled request got a bare 429 — no
/// body, no Retry-After, no log line — so the frontend fell back to its generic status message and
/// the server side of the story simply did not exist. Two issues (#33, #35) had to be traced through
/// the production nginx access log to establish that the 429s were ours and not Cloudflare's.
/// </remarks>
internal static class RateLimitRejection
{
    /// <summary>
    /// Category of the rejection log. A constant with a stable, explicit name rather than
    /// <c>ILogger<Program></c>, because log aggregation alerts on it (module E) and a category
    /// derived from a type would move the moment the type does.
    /// </summary>
    internal const string LogCategory = "EmotePurge.Api.RateLimiting";

    /// <summary>
    /// The window of every policy — they differ only in permit count. Also the Retry-After fallback:
    /// the full window is never too short, so a client that waits it out is always past the boundary.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string PolicyItemKey = "RateLimit:Policy";
    private const string PartitionItemKey = "RateLimit:PartitionKey";

    /// <summary>
    /// Partitions by the authenticated Twitch user, falling back to the remote IP and finally to a
    /// shared bucket. Runs after UseAuthentication, so the claim is there for every endpoint that
    /// requires auth.
    /// </summary>
    /// <remarks>
    /// Records the policy name and the partition key on the request. <see cref="OnRejectedAsync"/>
    /// has no other way to name either: the middleware hands it a lease, not a policy, and
    /// re-deriving the key there would duplicate the fallback chain above — two copies that can
    /// drift, in the one place whose whole job is to say accurately what happened.
    /// </remarks>
    public static RateLimitPartition<string> PartitionPerUser(
        HttpContext httpContext,
        string policyName,
        int permitLimit)
    {
        var partitionKey = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        httpContext.Items[PolicyItemKey] = policyName;
        httpContext.Items[PartitionItemKey] = partitionKey;

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = Window,
            QueueLimit = 0
        });
    }

    /// <summary>
    /// Answers a rejected request: one warning in the log, a Retry-After header, and a body carrying
    /// a translatable error code.
    /// </summary>
    /// <remarks>
    /// The status code is already 429 when this runs — <c>RateLimiterOptions.RejectionStatusCode</c>
    /// is applied *before* the callback, which is why writing a body here is safe and why nothing
    /// below sets a status.
    /// </remarks>
    public static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var policyName = httpContext.Items[PolicyItemKey] as string ?? "unknown";
        var partitionKey = httpContext.Items[PartitionItemKey] as string ?? "unknown";

        // FixedWindowRateLimiter reports the wait to the next window boundary on a failed lease. On
        // .NET 10 that value is the whole window rather than the remainder — an over-estimate, and
        // the safe direction to be wrong in; .NET 11 makes it exact with no change here.
        var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : (int)Window.TotalSeconds;

        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        // The partition key is a Twitch user id for every authenticated caller and the remote IP only
        // for the anonymous health endpoint — both are already in the database respectively in the
        // reverse proxy's own access log, so this adds no category of data the host did not hold.
        var logger = httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(LogCategory);
        logger.LogWarning(
            "Rate-Limit erreicht: Policy {RateLimitPolicy}, {RequestMethod} {RequestPath}, Partition {RateLimitPartition}, Retry-After {RetryAfterSeconds}s",
            policyName,
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            partitionKey,
            retryAfterSeconds);

        // Shaped like the resync cooldown's 429 (ChannelEndpoints.cs), so the frontend's existing
        // apiErrorTranslationKey handles both without a special case.
        await httpContext.Response.WriteAsJsonAsync(
            new { errorCode = ApiErrorCodes.RateLimitExceeded, retryAfterSeconds },
            cancellationToken);
    }
}
```

- [ ] **Step 5: `Program.cs` verdrahten**

Import-Block: `using EmotePurge.Api.RateLimiting;` ergänzen, `using System.Threading.RateLimiting;` (Zeile 16) entfernen — der Typ wird hier nicht mehr genannt. `using System.Security.Claims;` bleibt (`OnValidatePrincipal` braucht es).

Den `AddRateLimiter`-Block ersetzen. Die vier erklärenden Kommentarblöcke über den Policies bleiben **wortgleich stehen** — sie sind die Begründung der Zahlen und dürfen bei diesem Umbau nicht verlorengehen:

```csharp
// Two policies rather than one, because "expensive" meant two unrelated things. Both partition by
// authenticated user (every endpoint carrying them requires auth), falling back to the remote IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Until 2026-08-29 there was none, and a rejected request left no trace anywhere on this side —
    // see RateLimitRejection for what that cost.
    options.OnRejected = RateLimitRejection.OnRejectedAsync;

    // Names the policy exactly once. The partitioner needs the name for the rejection log, and
    // AddPolicy does not hand it over.
    void AddPerUserPolicy(string policyName, int permitLimit) =>
        options.AddPolicy(policyName, httpContext =>
            RateLimitRejection.PartitionPerUser(httpContext, policyName, permitLimit));

    // Strict: every endpoint under this policy makes uncached calls to Twitch Helix or 7TV, and
    // those quotas are per application, not per user. A single looping account could therefore
    // exhaust the app-wide bucket, at which point Helix returns nothing for *everyone* and
    // ModeratorCheckService can no longer distinguish "not a mod" from "quota exhausted" — every
    // moderator of every channel silently loses their permissions.
    // 40, not 20: ordinary navigation burns several permits per page switch (/channels/mine on
    // every overview visit, /permissions plus usage-stats on every workspace entry), so 20/min
    // ran out after ~7 page switches of plain clicking. The worst case per account stays below
    // the app-wide Helix bucket (~800/min) even at /mine's up-to-10 Helix calls per request.
    // Deliberately not raised again on 2026-08-29: the second round of 429s (issues #33/#35) was
    // request volume, not ceiling — the fix is in the frontend's reload cycle.
    AddPerUserPolicy("ExternalApi", permitLimit: 40);

    // Generous: bookkeeping against our own database with no downstream cost. Deliberately split
    // out of the strict policy — sync-deleted is the one call that must never be dropped (a 429
    // there leaves the database diverging from 7TV with no signal), and it used to share the
    // 20/min budget with join and the vote endpoints.
    AddPerUserPolicy("Bookkeeping", permitLimit: 120);

    // Stricter than ExternalApi by a factor of eight, for the one endpoint a user can trigger that
    // costs an unconditional 7TV call *and* fans a live event out to every open page of the channel.
    // It is only half the guard: this partitions per user, so fifteen moderators of one channel
    // would still get fifteen budgets — the per-channel half is IChannelResyncCooldown. Neither
    // mechanism covers the other's case.
    AddPerUserPolicy("ChannelResync", permitLimit: 5);

    // For the payload-free GET /api/health: public and anonymous, so this always partitions by IP
    // (PartitionPerUser falls back to it). One Redis read per hit — cheap, but unauthenticated,
    // and its legitimate callers are machines on fixed cadences: the container HEALTHCHECK
    // (every 30 s, from localhost) and the external uptime monitor (every 60 s).
    AddPerUserPolicy("PublicHealth", permitLimit: 30);
});
```

Die frühere lokale `static RateLimitPartition<string> PartitionPerUser(HttpContext, int)` unterhalb des Blocks ersatzlos löschen.

- [ ] **Step 6: Den Test laufen lassen und den Erfolg prüfen**

Run: `dotnet test tests/EmotePurge.Api.Tests/EmotePurge.Api.Tests.csproj --filter FullyQualifiedName~RateLimitRejectionTests`
Expected: PASS, drei Fälle.

Scheitert `Rejection_IsLoggedWithPolicyPathAndPartition` mit „keine Einträge", liegt es fast sicher am Mindest-Loglevel: `ApiFactory` setzt `SetMinimumLevel(LogLevel.Warning)`, und die Zeile ist `LogWarning` — passt. Scheitert `ExhaustedBudget…` mit einem leeren Body, wurde die Antwort schon gestartet, bevor `OnRejected` lief; das wäre ein Widerspruch zum dokumentierten Verhalten von `RejectionStatusCode` und gehört dann untersucht, nicht umgangen.

- [ ] **Step 7: Die ganze Api-Testsuite laufen lassen**

Run: `dotnet test tests/EmotePurge.Api.Tests/EmotePurge.Api.Tests.csproj`
Expected: PASS. `AuthFilterMatrixTests` schickt für jeden Fall eine frische `Guid` als User-Id und ist deshalb von der veränderten Partitionierung unberührt — bleibt sie trotzdem rot, ist der Grund im Umbau von `PartitionPerUser` zu suchen, nicht im Test.

- [ ] **Step 8: Live verifizieren (Regel 16)**

```bash
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api
```

Im Browser einloggen, dann in einer zweiten Shell 41-mal denselben `ExternalApi`-Endpoint mit der Session-Cookie aufrufen (`curl -b`), und prüfen: die letzte Antwort ist `429`, trägt `Retry-After`, trägt den JSON-Body — und in der Api-Konsole steht **eine** `warn`-Zeile mit `Rate-Limit erreicht: Policy ExternalApi, …`. Das ist die Eigenschaft, um derentwillen dieser Task existiert; sie darf nicht nur im Testhost gelten.
Danach `dotnet run` beenden, sonst fällt später die E2E-Suite durch.

- [ ] **Step 9: Den `docs/DECISIONS.md`-Eintrag schreiben** (Regel 3 — im selben Commit)

**Zuerst die Datei neu einlesen** (eine parallele Session arbeitet daran), dann oben in die absteigend datierte Liste einfügen, direkt unter der `---`-Linie hinter dem Vorwort:

```markdown
### 2026-08-29 — Der Rate-Limiter sagt, dass er abgelehnt hat: Log, Retry-After und ein Fehlercode

**Betrifft:** `src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs`, `src/EmotePurge.Api/Program.cs`, `src/EmotePurge.Api/Validation/ApiErrorCodes.cs`, `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs`, `web/src/app/core/i18n/api-error.ts`, `web/public/i18n/*.json`

Der Rate-Limiter hatte kein `OnRejected`. Eine Ablehnung war damit ein nackter 429: kein Body, kein `Retry-After`, keine Logzeile. Serverseitig war nicht erkennbar, dass überhaupt jemand abgelehnt worden war.

**Was das gekostet hat.** Die Issues #33 („A User with many Channels gets a rate limit error on first login") und #35 („User got a rate limit error when deleting emotes") brauchten zwei Untersuchungsrunden und einen Zugriff auf das Produktions-nginx-Log, nur um die Frage zu beantworten, *wer* die 429er schickt. Sie kamen aus unserer eigenen Policy `ExternalApi`; Cloudflare und nginx waren als Verdächtige nur deshalb im Rennen, weil unsere Antwort nichts über sich selbst aussagte. Die Auswertung des Logs vom 2026-08-28 ergab 38 Ablehnungen, 22 davon auf `emotes/duplicate-names`.

**Der neue Vertrag.** Jede vom Limiter abgelehnte Anfrage antwortet ab sofort mit `429`, einem `Retry-After`-Header in ganzen Sekunden und dem Body `{"errorCode":"rate_limit_exceeded","retryAfterSeconds":<int>}`. Das ist dieselbe Form, die `resync_cooldown_active` seit jeher hat, weshalb das Frontend keinen Sonderfall braucht: `apiErrorTranslationKey` erkennt den Code und rendert `errors.api.rate_limit_exceeded`.

**Der Status-Fallback bleibt und ändert seine Bedeutung.** `errors.status.rateLimited` deckt ab jetzt genau die 429er ab, die *nicht* von uns kommen — nginx, Cloudflare, ein zwischengeschalteter Proxy. Der Text der beiden Schlüssel ist bewusst wortgleich: welcher Dienst gedrosselt hat, ist eine Diagnose-Eigenschaft und keine, die dem Nutzer eine andere Handlungsanweisung gibt. `retryAfterSeconds` steht im Body und wird heute nicht angezeigt — dafür müsste `apiErrorTranslationKey` Parameter durchreichen, was jede Aufrufstelle anfasst.

**Die Logzeile nennt drei Dinge**, weil genau diese drei aus dem nginx-Log rekonstruiert werden mussten: welche Policy, welcher Pfad, welcher Partition-Key. Der Key ist für jeden angemeldeten Aufrufer die Twitch-User-Id und nur beim anonymen `/api/health` die Remote-IP — beides hält der Host ohnehin schon, in der Datenbank respektive im Access-Log des Reverse Proxy. Die Log-Kategorie ist der feste String `EmotePurge.Api.RateLimiting` statt `ILogger<Program>`: die Aggregation (S3-36) alarmiert darauf, und eine aus einem Typ abgeleitete Kategorie wandert mit dem Typ.

**Partitionierung und Ablehnung liegen in einer Klasse**, weil sie sich ein Geheimnis teilen: nur die Partitionsfabrik weiß, wie der Key zustande kam (Claim → IP → `unknown`), und nur die Ablehnung braucht ihn. Sie reicht ihn über `HttpContext.Items` weiter, statt die Kette ein zweites Mal nachzubauen — zwei Kopien einer Fallback-Kette, die auseinanderlaufen können, ausgerechnet an der Stelle, deren ganze Aufgabe es ist, genau zu sagen, was passiert ist.

**`Retry-After` auf .NET 10 ist eine Überschätzung, und das ist die richtige Richtung.** `FixedWindowRateLimiter` meldet als `MetadataName.RetryAfter` das ganze Fenster statt der Restzeit; wer es abwartet, ist immer über der Grenze. .NET 11 macht den Wert exakt, ohne dass hier etwas zu ändern wäre.

**Das Limit wurde nicht angehoben.** Naheliegend, und schon einmal getan — von 20 auf 40, was das Problem verschob statt es zu lösen. `ExternalApi` schützt nicht unsere Datenbank, sondern die app-weiten Helix- und 7TV-Kontingente: ein einzelner schleifender Account leert sie, und danach verliert jeder Moderator jedes Channels stillschweigend seine Rechte. Die Ursache lag in der Requestzahl, nicht in der Decke — s. den Eintrag zum Reload-Zyklus im selben Datum.
```

- [ ] **Step 10: Formatieren und committen** (Regel 1: vorher fragen)

```bash
dotnet format EmotePurge.slnx
git add src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs src/EmotePurge.Api/Program.cs src/EmotePurge.Api/Validation/ApiErrorCodes.cs tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs docs/DECISIONS.md
git commit -m "feat(api): answer a rate-limited request with a log line, Retry-After and an error code"
```

---

## Task 4: Der Frontend-Spiegel des neuen Fehlercodes

**Files:**
- Modify: `web/src/app/core/i18n/api-error.ts`
- Modify: `web/public/i18n/de.json`, `web/public/i18n/en.json`
- Test: `web/src/app/core/i18n/api-error.spec.ts` (und, ohne eigene Änderung, `api-error-locales.spec.ts`)

**Interfaces:**
- Consumes: den String `"rate_limit_exceeded"` aus `ApiErrorCodes.RateLimitExceeded` (Task 3).
- Produces: den Übersetzungsschlüssel `errors.api.rate_limit_exceeded` in beiden Locales.

- [ ] **Step 1: Die fehlschlagenden Tests schreiben**

In `web/src/app/core/i18n/api-error.spec.ts`, hinter dem Fall „falls back to the status for an unrecognized errorCode":

```ts
  it('renders the rate limiter’s own 429 through its error code', () => {
    // Since 2026-08-29 the limiter sends a body. The status fallback below still exists, and now
    // means specifically "a 429 that did not come from us" — nginx, Cloudflare, a proxy in between.
    expect(
      apiErrorTranslationKey(
        errorWith(429, { errorCode: 'rate_limit_exceeded', retryAfterSeconds: 42 }),
      ),
    ).toBe('errors.api.rate_limit_exceeded');
  });

  it('keeps the resync cooldown apart from the generic budget', () => {
    // Two different 429s with two different answers: "someone else's resync is running" versus
    // "your own minute is spent".
    expect(apiErrorTranslationKey(errorWith(429, { errorCode: 'resync_cooldown_active' }))).toBe(
      'errors.api.resync_cooldown_active',
    );
  });
```

- [ ] **Step 2: Die Tests laufen lassen und den Fehlschlag prüfen**

Run: `npm --prefix web test -- --watch=false api-error`
Expected: FAIL. Der erste neue Fall liefert `errors.status.rateLimited` statt `errors.api.rate_limit_exceeded` (der Code steht noch nicht in `KNOWN_API_ERROR_CODES`). `api-error-locales.spec.ts` ist an dieser Stelle noch grün — es sieht `ApiErrorCodes.cs` nicht.

- [ ] **Step 3: Den Code in `api-error.ts` spiegeln**

In `web/src/app/core/i18n/api-error.ts`, in `KNOWN_API_ERROR_CODES` unmittelbar **vor** `'unexpected_error'` (gleiche Stelle wie in `ApiErrorCodes.cs`):

```ts
  'rate_limit_exceeded',
```

Und den Doc-Block über `apiErrorTranslationKey` richtigstellen — der Satz „the rate limiter with a bare 429" gilt nicht mehr:

```ts
/**
 * Resolves an HTTP error from the EmotePurge API to a translation key: `errors.api.<code>` for a
 * recognized `errorCode` body, otherwise a message derived from the status code.
 *
 * The status fallback exists because a large share of real failures carry no body at all — the four
 * authorization endpoint filters answer with a bare `Forbid()`, a dropped connection has status 0.
 * All of those used to collapse into "Etwas ist schiefgelaufen. Bitte versuch es erneut.", which
 * told a freshly promoted moderator to retry an action that could not succeed until the mod-role
 * cache expired.
 *
 * Our own rate limiter left the same gap until 2026-08-29 and now sends `rate_limit_exceeded` with a
 * `Retry-After`. `errors.status.rateLimited` therefore stays, but from here on it covers the 429s
 * that did *not* come from this API — nginx, Cloudflare, a proxy in between. Both keys carry the
 * same sentence on purpose: which service throttled is a diagnostic fact, not a different
 * instruction to the user.
 */
```

- [ ] **Step 4: Beide Locale-Dateien ergänzen**

`web/public/i18n/de.json`, in `errors.api`, vor `"unexpected_error"`:

```json
    "rate_limit_exceeded": "Zu viele Anfragen in kurzer Zeit. Warte einen Moment und versuch es erneut.",
```

`web/public/i18n/en.json`, an derselben Stelle:

```json
    "rate_limit_exceeded": "Too many requests in a short time. Wait a moment and try again.",
```

Wortgleich mit `errors.status.rateLimited` in derselben Datei — das ist Absicht und in Entscheidung 5 oben begründet.

- [ ] **Step 5: Tests laufen lassen und den Erfolg prüfen**

Run: `npm --prefix web test -- --watch=false api-error`
Expected: PASS — `api-error.spec.ts` mit den zwei neuen Fällen und dem unveränderten `[429, 'errors.status.rateLimited']`-Fall für die body-lose Antwort, und `api-error-locales.spec.ts` mit allen drei Richtungen (Code → Übersetzung in beiden Sprachen, keine verwaisten Schlüssel, identische Schlüsselmengen).

- [ ] **Step 6: Formatieren und committen** (Regel 1: vorher fragen)

```bash
npm --prefix web run format
npm --prefix web run lint
git add web/src/app/core/i18n/api-error.ts web/src/app/core/i18n/api-error.spec.ts web/public/i18n/de.json web/public/i18n/en.json
git commit -m "feat(web): translate the rate limiter's rate_limit_exceeded code"
```

---

## Task 5: Gesamtverifikation

**Files:** keine Änderung — dieser Task prüft nur.

**Interfaces:**
- Consumes: alles aus Task 1-4.
- Produces: die Belege, ohne die keine Erfolgsmeldung gemacht werden darf (`superpowers:verification-before-completion`: Evidenz vor Behauptung).

- [ ] **Step 1: Formatprüfung**

```bash
dotnet format EmotePurge.slnx --verify-no-changes
npm --prefix web run format -- --check
npm --prefix web run lint
```
Expected: alle drei ohne Befund. Meldet `dotnet format` Änderungen, gehören sie in den Task-Commit, zu dem sie sachlich passen — **nicht** in einen eigenen `style:`-Commit (der ist laut Regel 18 für repoweite Reformatierungen reserviert).

- [ ] **Step 2: Backend-Suiten**

```bash
docker compose up -d postgres redis
dotnet test EmotePurge.slnx
```
Expected: PASS über alle drei Testprojekte. `EmotePurge.Infrastructure.Tests` braucht laufendes Docker (Testcontainers), die beiden anderen nicht.

- [ ] **Step 3: Frontend-Suiten**

```bash
npm --prefix web test -- --watch=false
```
Expected: PASS.

- [ ] **Step 4: E2E**

Vorher prüfen, dass auf `:5151` nichts lauscht:

```bash
ss -ltn | grep :5151 || echo "frei"
npm --prefix web run e2e
```
Expected: PASS, alle Fälle in beiden Projekten (`chromium`, `mobile-chrome`). Fällt hier rund die halbe Suite mit „element not found" quer über unbeteiligte Dateien durch, ist die Ursache mit hoher Wahrscheinlichkeit eine noch laufende Api auf `:5151` und nicht diese Änderung.

- [ ] **Step 5: Der Nachweis am laufenden System (das eigentliche Ziel)**

```bash
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api
npm --prefix web start
```

Im Browser einen Channel-Workspace auf `/channels/<name>/usage-stats` öffnen und in den DevTools nachsehen:

1. **Netzwerk-Reiter, Filter `live`:** genau **eine** offene `text/event-stream`-Verbindung auf `/api/channels/<name>/live`, nicht zwei. Das ist der Nachweis für Task 1.
2. **In den Reiter `vote-sessions` und zurück wechseln:** es kommt keine zweite `live`-Verbindung hinzu — der Layout-Abonnent hält sie über den Routenwechsel.
3. **Einen Resync auslösen:** „Resync angestoßen …" erscheint sofort, „Resync abgeschlossen." rund eine Sekunde nach dem `channel.synced`. Das ist der nutzersichtbare Vertrag aus Task 2, den der Debounce nicht brechen darf.
4. **Falls ein Test-Channel mit 7TV-Schreibrechten verfügbar ist:** einen kleinen Mass-Delete über mehrere Emotes laufen lassen und im Netzwerk-Reiter zählen — es dürfen nur ein bis zwei `duplicate-names`-Requests entstehen, nicht einer pro Emote. Ist kein solcher Channel greifbar, ersetzt der E2E-Fall aus Task 2 den Nachweis; das ist dann im Abschlussbericht so zu sagen und nicht als Live-Verifikation auszugeben.

Danach `dotnet run` und `npm start` beenden.

- [ ] **Step 6: Abschlussbericht**

Zusammenfassen, welche Befehle mit welchem Ergebnis liefen, und die beiden Issues #33/#35 mit dem Ergebnis kommentieren. Ausdrücklich benennen, was **nicht** adressiert wurde: die Navigationsseite von #33 (fünf Permits pro Workspace-Öffnung, davon vier ungecacht) ist unverändert — dieser Plan senkt die Last des Live-Zyklus, nicht die des Seitenwechsels. Wenn ein Nutzer mit vielen Channels weiterhin 429er sieht, ist die nächste Maßnahme Entscheidung 4 oben (`duplicate-names` in die `active-set`-Antwort falten), nicht ein höheres Limit.

---

## Selbstprüfung

**Abdeckung gegen den Auftrag.** (A) Requests reduzieren: Task 1 (eine Verbindung statt zwei/vier) und Task 2 (`duplicate-names` hängt nicht mehr ungedrosselt an jedem Event; ein Fenster für Layout und Usage-Seite). Die Wahl zwischen „Debounce analog `liveReload`" und „ein gemeinsamer Zyklus" ist getroffen und begründet — beides, weil der Debounce die Requestzahl senkt und die geteilte Quelle die Zyklen deckungsgleich macht; ein Koordinator-Service ist mit Begründung verworfen. Die Resync-Bestätigung bleibt erhalten und hat mit dem bestehenden E2E-Fall in Task 2/Step 7 einen benannten Wächter. (B) Beobachtbarkeit: Task 3 liefert strukturiertes Log (Policy, Pfad, Partition-Key), `Retry-After` und Body mit `ApiErrorCode`, Task 4 den Frontend-Spiegel. Regel 7 ist über Task 3 (C#) + Task 4 (TS + beide Locales + Spec) vollständig bedient. Regel 3 ist Task 3/Step 9. Regel 11 ist Task 3/Step 1 mit der Feststellung, dass `OnRejected` in `tests/EmotePurge.Api.Tests` testbar ist — die Suite fährt die echte Pipeline, und `TestAuthHandler` erlaubt eine eigene Partition pro Test. Regel 12 ist eingehalten: neue Core-Logik (Task 1) bekommt ihre Vitest-Spec, die Komponentenänderung (Task 2) bewusst keine, sondern einen E2E-Fall. Regel 1 steht in jedem Commit-Schritt.

**Platzhalter.** Kein „TBD", kein „analog zu Task N", kein „Fehlerbehandlung ergänzen". Jeder Code-Schritt trägt den Code, jeder Test-Schritt die Erwartung samt Fehlerbild.

**Typkonsistenz.** `CHANNEL_RELOAD_DEBOUNCE_MS` heißt in Task 2 überall gleich (Deklaration, beide Verwendungen, Doku-Beispiel). `ApiErrorCodes.RateLimitExceeded` (C#) und `'rate_limit_exceeded'` (TS/JSON) sind derselbe String an vier Stellen. `RateLimitRejection.LogCategory`, `.Window`, `.PartitionPerUser`, `.OnRejectedAsync` werden in Task 3 mit genau den Signaturen deklariert, mit denen `Program.cs` und der Test sie verwenden; `PartitionPerUser` hat drei Parameter, und alle vier Aufrufstellen gehen über den lokalen Helfer.

---
