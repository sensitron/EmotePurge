# `live.changed`-Event Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** LIVE-Badges auf Übersichts- und Admin-Channel-Seite aktualisieren sich ohne Browser-Refresh in beide Richtungen (live↔offline), getrieben von einem neuen Thin-Event `live.changed`.

**Architecture:** Der `TwitchLivePollWorker` diffed jeden erfolgreichen Helix-Poll gegen den zuletzt publizierten Zustand (pure Klasse `LiveStatusDiff`) und publiziert pro gewechseltem Channel ein `live.changed` auf den bestehenden Redis-Kanal `live:events`. Die Api bekommt einen neuen SSE-Endpoint `GET /api/channels/live-events` (nur eingeloggt, Typfilter `live.changed`); die Übersichtsseite wird auf `rxResource` + SSE-Reload umgebaut, die Admin-Liste abonniert den Typ zusätzlich.

**Tech Stack:** .NET 10 (Minimal API, Worker Service), StackExchange.Redis Pub/Sub, SSE (`TypedResults.ServerSentEvents`), Angular 22 (Signals, `rxResource`, bestehende `liveEvents`/`liveReload`-Helfer), xUnit, Vitest.

**Spec:** [docs/superpowers/specs/2026-08-05-live-changed-event-design.md](../specs/2026-08-05-live-changed-event-design.md)

## Global Constraints

- **KEIN `git commit` durch Ausführende/Subagents.** Commits macht ausschließlich die Hauptsession **nach expliziter Nutzer-Freigabe** (CLAUDE.md Regel 1). Die Commit-Schritte in den Tasks beschreiben nur die vorgesehene Aufteilung; sie werden gesammelt am Ende ausgeführt (nach Live-Verifikation, Regel 16).
- Bezeichner/Kommentare in neuem Code englisch; Log-Messages deutsch (CLAUDE.md „Sprache").
- C#-Member-Reihenfolge nach Regel 19 (`const` → `readonly` → veränderliche Felder → …).
- Kein `AppDbContext`/`IConnectionMultiplexer` in Api-Handlern (Regel 4) — der neue Endpoint nutzt nur `ILiveEventStream`.
- Formatierung vor dem Commit: `dotnet format EmotePurge.slnx` und `npm --prefix web run format` (Regel 18).
- `docker compose up -d --build` (nie `up` ohne `--build`) für den Live-Test (Regel 15).

---

### Task 1: Core — Event-Typ `live.changed`

**Files:**
- Modify: `src/EmotePurge.Core/Messaging/LiveEvents.cs`

**Interfaces:**
- Produces: `LiveEvents.LiveChanged == "live.changed"`; der Typ ist Mitglied von `LiveEvents.AdminTypes` **und** `LiveEvents.ChannelTypes`.

- [ ] **Step 1: Konstante ergänzen** — nach dem `ChannelSynced`-Member (Zeile 29):

```csharp
    /// <summary>
    /// The worker's Helix live poll saw this channel flip between live and offline (either
    /// direction — the payload carries no state, clients refetch).
    /// </summary>
    public const string LiveChanged = "live.changed";
```

- [ ] **Step 2: Beide Filterlisten erweitern** — die zwei `HashSet`-Initialisierer (Zeilen 48–53) werden zu:

```csharp
    /// <summary>Types the admin stream (<c>GET /api/admin/live</c>) forwards.</summary>
    public static readonly IReadOnlySet<string> AdminTypes =
        new HashSet<string>(StringComparer.Ordinal) { WorkerHealth, WorkerRoster, ChannelSynced, LiveChanged };

    /// <summary>Types the channel stream (<c>GET /api/channels/{channelName}/live</c>) forwards.</summary>
    public static readonly IReadOnlySet<string> ChannelTypes =
        new HashSet<string>(StringComparer.Ordinal) { UsageFlushed, VoteChanged, ChannelSynced, LiveChanged };
```

- [ ] **Step 3: Build prüfen**

Run: `dotnet build src/EmotePurge.Core/EmotePurge.Core.csproj`
Expected: Build succeeded.

*(Commit-Zuordnung: Commit A, s. Task 8.)*

---

### Task 2: Worker — pure Diff-Klasse `LiveStatusDiff` (TDD)

**Files:**
- Create: `src/EmotePurge.Worker/LiveStatusDiff.cs`
- Test: `tests/EmotePurge.Worker.Tests/LiveStatusDiffTests.cs`

**Interfaces:**
- Produces: `LiveStatusDiff.Compute(IReadOnlySet<string>? previousLiveLogins, IReadOnlyCollection<string> currentLiveLogins)` → `LiveStatusChanges(IReadOnlyList<string> WentLive, IReadOnlyList<string> WentOffline)` mit `IsEmpty`-Property und `LiveStatusChanges.None`. Task 3 verlässt sich auf exakt diese Namen.

- [ ] **Step 1: Failing Tests schreiben** — `tests/EmotePurge.Worker.Tests/LiveStatusDiffTests.cs` (Namespace-Konvention `EmotePurge.Worker.Tests`, nur xunit, kein NSubstitute):

```csharp
using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure like the other worker policies. The null-baseline case is the load-bearing one: without it,
// the first poll after a cold start (no Redis snapshot survived) would fire one event per live
// channel and make every open tab refetch for transitions that never happened.
public class LiveStatusDiffTests
{
    [Fact]
    public void Compute_WithoutBaseline_ReportsNoChanges()
    {
        var changes = LiveStatusDiff.Compute(null, ["alpha", "beta"]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void Compute_ChannelWentLive_ReportsIt()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string>(), ["alpha"]);

        Assert.Equal(["alpha"], changes.WentLive);
        Assert.Empty(changes.WentOffline);
    }

    [Fact]
    public void Compute_ChannelWentOffline_ReportsIt()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string> { "alpha" }, []);

        Assert.Empty(changes.WentLive);
        Assert.Equal(["alpha"], changes.WentOffline);
    }

    [Fact]
    public void Compute_UnchangedState_ReportsNothing()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string> { "alpha" }, ["alpha"]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void Compute_BothDirectionsAtOnce_ReportsBoth()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string> { "alpha", "beta" }, ["beta", "gamma"]);

        Assert.Equal(["gamma"], changes.WentLive);
        Assert.Equal(["alpha"], changes.WentOffline);
    }

    [Fact]
    public void Compute_EmptyToEmpty_ReportsNothing()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string>(), []);

        Assert.True(changes.IsEmpty);
    }
}
```

- [ ] **Step 2: Tests laufen lassen — müssen ROT sein**

Run: `dotnet test tests/EmotePurge.Worker.Tests --filter LiveStatusDiffTests`
Expected: Compile-Fehler „LiveStatusDiff does not exist".

- [ ] **Step 3: Implementierung** — `src/EmotePurge.Worker/LiveStatusDiff.cs`:

```csharp
namespace EmotePurge.Worker;

/// <summary>
/// Pure diff between two consecutive live polls — the entire decision behind the
/// <c>live.changed</c> event, kept TwitchLib- and Redis-free next to the other worker policies.
/// A null baseline means "no previous statement" (first poll ever, or the Redis snapshot had
/// expired before boot) and yields no changes: better to miss one transition than to storm every
/// open tab with events for channels that merely kept their state.
/// </summary>
public static class LiveStatusDiff
{
    public static LiveStatusChanges Compute(
        IReadOnlySet<string>? previousLiveLogins,
        IReadOnlyCollection<string> currentLiveLogins)
    {
        if (previousLiveLogins is null)
        {
            return LiveStatusChanges.None;
        }

        var current = currentLiveLogins as IReadOnlySet<string>
            ?? currentLiveLogins.ToHashSet(StringComparer.Ordinal);
        var wentLive = current.Where(login => !previousLiveLogins.Contains(login)).ToList();
        var wentOffline = previousLiveLogins.Where(login => !current.Contains(login)).ToList();
        return new LiveStatusChanges(wentLive, wentOffline);
    }
}

public sealed record LiveStatusChanges(
    IReadOnlyList<string> WentLive,
    IReadOnlyList<string> WentOffline)
{
    public static readonly LiveStatusChanges None = new([], []);

    public bool IsEmpty => WentLive.Count == 0 && WentOffline.Count == 0;
}
```

- [ ] **Step 4: Tests laufen lassen — müssen GRÜN sein**

Run: `dotnet test tests/EmotePurge.Worker.Tests --filter LiveStatusDiffTests`
Expected: 6/6 PASS.

*(Commit-Zuordnung: Commit A.)*

---

### Task 3: Worker — Publisher-Extension + `TwitchLivePollWorker`-Verdrahtung

**Files:**
- Modify: `src/EmotePurge.Worker/LiveEventPublisher.cs`
- Modify: `src/EmotePurge.Worker/TwitchLivePollWorker.cs`

**Interfaces:**
- Consumes: `LiveEvents.LiveChanged` (Task 1), `LiveStatusDiff.Compute` / `LiveStatusChanges` (Task 2), bestehend: `IRedisPublisher`, `ITwitchLiveStatusReader` (beides Singletons aus `AddEmotePurgeInfrastructure`, keine DI-Änderung nötig).
- Produces: `redisPublisher.PublishLiveChangedAsync(logger, channelName, ct)` — gleiche Swallow-and-log-Semantik wie `PublishChannelSyncedAsync`.

- [ ] **Step 1: Extension-Methode ergänzen** — in `LiveEventPublisher.cs` nach `PublishChannelSyncedAsync`; außerdem im Klassen-XML-Kommentar „the thin <see cref="LiveEvents.ChannelSynced"/> event" zu „the thin live events" verallgemeinern (eine Wortänderung genügt):

```csharp
    /// <summary>
    /// Same swallow-and-log contract as above, for <see cref="LiveEvents.LiveChanged"/>: the poll
    /// result is already persisted when this runs, and a Redis pub/sub hiccup must never fail it.
    /// </summary>
    public static async Task PublishLiveChangedAsync(
        this IRedisPublisher redisPublisher,
        ILogger logger,
        string channelName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redisPublisher.PublishAsync(
                LiveEvents.Channel,
                new LiveEvent(LiveEvents.LiveChanged, ChannelName.Normalize(channelName)).Serialize(),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Live-Event '{Type}' für {Channel} konnte nicht veröffentlicht werden.",
                LiveEvents.LiveChanged, channelName);
        }
    }
```

- [ ] **Step 2: `TwitchLivePollWorker` erweitern.** Drei Änderungen:

(a) Primary-Constructor um zwei Parameter ergänzen (nach `liveStatusWriter`) + neues Using `EmotePurge.Core.Messaging`:

```csharp
public class TwitchLivePollWorker(
    ILogger<TwitchLivePollWorker> logger,
    ITwitchAppTokenProvider appTokenProvider,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ITwitchLiveStatusWriter liveStatusWriter,
    ITwitchLiveStatusReader liveStatusReader,
    IRedisPublisher redisPublisher) : BackgroundService
```

(b) Veränderliches Feld nach `_pollInterval` (Regel 19: readonly vor veränderlich):

```csharp
    // Baseline for the transition diff — null until the first successful publish. Not the Redis
    // snapshot itself: reading it back every tick would race the write, and in-memory is exact.
    private IReadOnlySet<string>? _lastPublishedLiveLogins;
```

(c) `PublishLiveStatusAsync` bekommt den `CancellationToken` durchgereicht (Aufruf Zeile 77 wird `await PublishLiveStatusAsync(streams, ct);`) und wird zu:

```csharp
    // Best-effort with its own catch: a Redis hiccup must not cost the coverage rows that follow.
    private async Task PublishLiveStatusAsync(IReadOnlyList<TwitchStreamInfo> streams, CancellationToken ct)
    {
        try
        {
            // UserLogin is documented as already-lowercase, but the key is a cross-process
            // contract keyed by normalized names — normalize anyway (rule 9).
            var liveLogins = streams
                .Select(s => ChannelName.Normalize(s.UserLogin))
                .Distinct()
                .ToList();

            // Baseline for the diff. In-memory after the first poll; across a worker restart the
            // previous Redis snapshot (TTL = twice the poll interval) fills in, so a flip during a
            // short restart still produces its event instead of being swallowed. Read before the
            // write below overwrites it.
            var baseline = _lastPublishedLiveLogins;
            if (baseline is null)
            {
                var previousSnapshot = await liveStatusReader.ReadAsync(ct);
                baseline = previousSnapshot?.LiveChannelLogins.ToHashSet(StringComparer.Ordinal);
            }

            await liveStatusWriter.PublishAsync(
                new TwitchLiveStatusSnapshot(DateTime.UtcNow, liveLogins),
                TwitchLiveStatusKeys.TimeToLiveFor(_pollInterval));

            _lastPublishedLiveLogins = liveLogins.ToHashSet(StringComparer.Ordinal);

            var changes = LiveStatusDiff.Compute(baseline, liveLogins);
            if (changes.IsEmpty)
            {
                return;
            }

            foreach (var channelName in changes.WentLive)
            {
                await redisPublisher.PublishLiveChangedAsync(logger, channelName, ct);
            }

            foreach (var channelName in changes.WentOffline)
            {
                await redisPublisher.PublishLiveChangedAsync(logger, channelName, ct);
            }

            logger.LogInformation(
                "Live-Status-Wechsel publiziert: live gegangen [{WentLive}], offline gegangen [{WentOffline}].",
                string.Join(", ", changes.WentLive),
                string.Join(", ", changes.WentOffline));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live-Status-Publish nach Redis fehlgeschlagen — Key läuft aus, UI zeigt „unbekannt“.");
        }
    }
```

Wichtig: Der Fehlgeschlagener-Poll-Pfad (Helix `null`, Zeile 65–72) bleibt unverändert — er erreicht `PublishLiveStatusAsync` nie, Baseline bleibt stehen, keine Events.

- [ ] **Step 3: Gesamte Worker-Suite laufen lassen**

Run: `dotnet test tests/EmotePurge.Worker.Tests`
Expected: alle PASS (inkl. der 6 neuen).

- [ ] **Step 4: Solution bauen**

Run: `dotnet build EmotePurge.slnx`
Expected: Build succeeded.

*(Commit-Zuordnung: Commit A.)*

---

### Task 4: Api — SSE-Endpoint `GET /api/channels/live-events` + Testmatrix

**Files:**
- Modify: `src/EmotePurge.Api/Endpoints/LiveEndpoints.cs`
- Test: `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`

**Interfaces:**
- Consumes: `LiveEvents.LiveChanged` (Task 1), bestehend: `LiveEndpoints.OpenAsync`, `ILiveEventStream`.
- Produces: Route `GET /api/channels/live-events` (auth-pflichtig, kein Rollencheck, Typfilter nur `live.changed`). Task 5 verwendet exakt diesen Pfad.

- [ ] **Step 1: Testmatrix erweitern** — in der `[Theory]` `EveryProtectedEndpoint_Answers401_ForAnAnonymousCaller` (Zeile 50–68) zwei Zeilen ergänzen:

```csharp
    [InlineData("GET", "/api/channels/live-events")]
    [InlineData("GET", "/api/admin/live")]
```

Ehrliche Einordnung: Die erste Zeile ist **kein scharfer Red/Green-Fall** — vor dem Endpoint-Add matcht `/api/channels/live-events` die Route `/api/channels/{channelName}` und antwortet ebenfalls 401 anonym. Die Zeilen pinnen den Auth-Zwang der beiden SSE-Routen (Regel 11) und schließen nebenbei die bestehende Lücke, dass **kein** SSE-Endpoint in der Matrix stand (`/api/admin/live` fehlte auch). Die eigentliche Funktionsverifikation ist der Live-Test in Task 8.

- [ ] **Step 2: Tests laufen lassen**

Run: `dotnet test tests/EmotePurge.Api.Tests`
Expected: PASS (s. Einordnung oben — beide Routen antworten schon jetzt 401 anonym).

- [ ] **Step 3: Endpoint registrieren** — in `LiveEndpoints.MapLiveEndpoints`, nach dem bestehenden `app.MapGet("/api/channels/{channelName}/live", ...)`-Block:

```csharp
        // The overview's stream: any logged-in user may listen. Cross-channel on purpose — the
        // events are rare (a real transition of a tracked channel) and carry only the channel
        // name; the refetch through GET /api/channels/mine stays the authorization boundary.
        // No route conflict with {channelName}/live: this template has one segment fewer, and
        // the literal segment outranks {channelName} for a request to /api/channels/live-events.
        app.MapGet("/api/channels/live-events", (
            HttpContext httpContext,
            ILiveEventStream liveEventStream,
            CancellationToken ct) =>
            OpenAsync(
                httpContext,
                liveEventStream,
                liveEvent => string.Equals(liveEvent.Type, LiveEvents.LiveChanged, StringComparison.Ordinal),
                ct))
        .RequireAuthorization();
```

Kein `ChannelNameValidationFilter` (es gibt keinen Namensparameter), kein Rate-Limiting (bewusst, wie der Kommentar am Methodenkopf für beide Bestandsstreams begründet).

- [ ] **Step 4: Build + Api-Tests**

Run: `dotnet build EmotePurge.slnx` und `dotnet test tests/EmotePurge.Api.Tests`
Expected: Build succeeded, alle PASS.

*(Commit-Zuordnung: Commit B.)*

---

### Task 5: Frontend — Event-Typ + URL-Konstante

**Files:**
- Modify: `web/src/app/core/live/live-event.model.ts`

**Interfaces:**
- Produces: `LIVE_EVENT_TYPES.liveChanged === 'live.changed'` und `LIVE_STATUS_URL === '/api/channels/live-events'`. Tasks 6+7 importieren beide.

- [ ] **Step 1: Konstanten ergänzen** — in `LIVE_EVENT_TYPES` (nach `channelSynced`):

```typescript
  liveChanged: 'live.changed',
```

und nach `ADMIN_LIVE_URL`:

```typescript
/** Cross-channel stream for the overview: carries only `live.changed`. Login required. */
export const LIVE_STATUS_URL = '/api/channels/live-events';
```

- [ ] **Step 2: Frontend-Tests + Lint**

Run: `npm --prefix web test -- --watch=false` und `npm --prefix web run lint`
Expected: PASS (reine Konstanten-Ergänzung, kein Spec nötig — `api-error-locales.spec.ts` betrifft nur Error-Codes).

*(Commit-Zuordnung: Commit C.)*

---

### Task 6: Frontend — Übersichtsseite: `rxResource` + SSE-Reload + Tooltip-Fix

**Files:**
- Modify: `web/src/app/features/overview/overview-page.ts`
- (Template `overview-page.html` bleibt unverändert — alle Bindings behalten ihre Namen.)

**Interfaces:**
- Consumes: `LIVE_STATUS_URL`, `LIVE_EVENT_TYPES.liveChanged` (Task 5); bestehend: `liveReload` aus `core/live/live-reload`, `rxResource` aus `@angular/core/rxjs-interop`.
- Produces: nichts für andere Tasks; Template-Vertrag (`myChannels()`, `errorMessage()`, `liveAgeMinutes()`, `helixUnavailable()`, `reauthRequired()`, `sevenTvUnavailable()`) bleibt identisch.

- [ ] **Step 1: Debounce-Konstante lokalisieren**

Run: `grep -rn "LIVE_RELOAD_DEBOUNCE_MS" web/src --include="*.ts" | head -5`
Expected: Export-Fundstelle in `core/live/` (die JSDoc von `liveReload` referenziert sie; genutzt von usage-stats/voting-Seiten). Import-Pfad im nächsten Schritt entsprechend setzen. Falls sie wider Erwarten nicht existiert: lokale Konstante `const LIVE_RELOAD_DEBOUNCE_MS = 250;` in `overview-page.ts` anlegen.

- [ ] **Step 2: Komponente umbauen.** Vollständiger neuer Klassenrumpf (Imports entsprechend: `DestroyRef` zu `@angular/core`-Import ergänzen, `rxResource` aus `@angular/core/rxjs-interop`, `liveReload` + Konstanten aus `core/live/`; der `MyChannelDto`-Import weicht `MyChannelsResult`, falls `MyChannelDto` sonst ungenutzt):

```typescript
const LIVE_AGE_TICK_MS = 30_000;

@Component({
  selector: 'app-overview-page',
  imports: [Button, EmptyState, NoticeBanner, RouterLink, SkeletonRows, StatusBadge, TranslocoPipe],
  templateUrl: './overview-page.html',
})
export class OverviewPage {
  private readonly authService = inject(AuthService);
  private readonly channelService = inject(ChannelService);
  private readonly router = inject(Router);
  private readonly workerHealthService = inject(WorkerHealthService);

  // The header dot alone is a 10px signal nobody notices — while the worker is down, nothing is
  // being counted, which deserves a real page-level notice on the entry page.
  protected readonly workerDisconnected = computed(
    () => this.workerHealthService.status() === 'stale',
  );

  // rxResource instead of a one-shot constructor subscribe: live.changed pushes reload the list,
  // and a resource keeps its previous value during the reload, so rows never blank out.
  private readonly myChannelsResource = rxResource({
    stream: () => this.channelService.listMine(),
  });

  protected readonly myChannels = computed(() =>
    this.myChannelsResource.hasValue() ? this.myChannelsResource.value().channels : null,
  );
  protected readonly helixUnavailable = computed(
    () => this.myChannelsResource.hasValue() && this.myChannelsResource.value().helixUnavailable,
  );
  protected readonly reauthRequired = computed(
    () => this.myChannelsResource.hasValue() && this.myChannelsResource.value().reauthRequired,
  );
  protected readonly sevenTvUnavailable = computed(
    () => this.myChannelsResource.hasValue() && this.myChannelsResource.value().sevenTvUnavailable,
  );
  private readonly livePolledAtUtc = computed(() =>
    this.myChannelsResource.hasValue() ? this.myChannelsResource.value().livePolledAtUtc : null,
  );

  // Ticking clock signal so the tooltip below ages while the page is open. Date.now() read
  // directly inside a computed() freezes at first render (rule 14) — that was a real bug.
  private readonly nowMs = signal(Date.now());

  /** Age of the live-poll data in whole minutes, for the badge tooltip. */
  protected readonly liveAgeMinutes = computed(() => {
    const polledAt = this.livePolledAtUtc();
    if (!polledAt) {
      return 0;
    }
    return Math.max(0, Math.round((this.nowMs() - new Date(polledAt).getTime()) / 60_000));
  });

  // Kept separate from the resource's own error so a failed action is not wiped out by the
  // reload that follows it, and vice versa — same reasoning as admin-channels-page.ts.
  private readonly actionError = signal<string | null>(null);

  protected readonly errorMessage = computed(() => {
    const actionError = this.actionError();
    if (actionError) {
      return actionError;
    }
    const loadError = this.myChannelsResource.error();
    return loadError instanceof HttpErrorResponse ? apiErrorTranslationKey(loadError) : null;
  });

  constructor() {
    // liveReload, not liveEvents: one poll tick can flip several channels at once, and the
    // debounce collapses that burst into a single refetch.
    liveReload(LIVE_STATUS_URL, {
      accept: [LIVE_EVENT_TYPES.liveChanged],
      debounceMs: LIVE_RELOAD_DEBOUNCE_MS,
    }).subscribe(() => this.myChannelsResource.reload());

    const tick = setInterval(() => this.nowMs.set(Date.now()), LIVE_AGE_TICK_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(tick));
  }

  protected join(channelName: string): void {
    this.channelService.join(channelName).subscribe({
      next: () => this.openChannel(channelName),
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  // Same call as join(), but stays on the overview and flips the row in place — someone is likely
  // reactivating one of several channels, and being navigated away after each one is in the way.
  protected reactivate(channelName: string): void {
    this.channelService.join(channelName).subscribe({
      next: () => {
        this.myChannelsResource.update((result) =>
          result
            ? {
                ...result,
                channels: result.channels.map((c) =>
                  c.channelName === channelName ? { ...c, isTracked: true, isBotActive: true } : c,
                ),
              }
            : result,
        );
      },
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected openChannel(channelName: string): void {
    this.router.navigate(['/channels', channelName]);
  }

  // Fresh Twitch OAuth round-trip (full browser redirect). Returning to the overview afterwards
  // is exactly the backend's default post-login redirect, so no returnUrl stash is needed.
  protected relogin(): void {
    this.authService.login();
  }

  // 401 is not handled here — apiAuthInterceptor resets the session and redirects for every
  // /api/ call in the app.
  private handleError(error: HttpErrorResponse): void {
    this.actionError.set(apiErrorTranslationKey(error));
  }
}
```

Hinweis für den Umsetzenden: Vorher `overview-page.html` einmal ganz lesen und prüfen, dass **nur** die oben genannten Bindings verwendet werden — der Umbau von `signal` auf `computed` ist für das Template transparent, aber ein übersehenes Binding (z. B. ein direkter `myChannels.set`-Aufruf an anderer Stelle) würde erst zur Laufzeit auffallen. Falls `this.myChannelsResource.update(...)` in der installierten Angular-Version nicht existiert (WritableResource-API), stattdessen `this.myChannelsResource.value.update(...)` mit gleichem Callback verwenden.

- [ ] **Step 3: Tests, Lint, Build**

Run: `npm --prefix web test -- --watch=false && npm --prefix web run lint`
Expected: PASS (die Seite hat bewusst keinen eigenen Spec, Regel 12).

*(Commit-Zuordnung: Commit C — enthält Task 5+6+7 vollständig; der mitbehobene Tooltip-Bug wird im Message-Body genannt statt als eigener Commit, weil er dieselben Dateien berührt.)*

---

### Task 7: Frontend — Admin-Channel-Liste: `live.changed` abonnieren + Tooltip-Fix

**Files:**
- Modify: `web/src/app/features/admin/admin-channels-page.ts`

**Interfaces:**
- Consumes: `LIVE_EVENT_TYPES.liveChanged` (Task 5); bestehend: `liveEvents`, `ADMIN_LIVE_URL`.

- [ ] **Step 1: Subscription erweitern — mit Typ-Guard.** Der Konstruktor-Block (Zeile 324–337) wird zu:

```typescript
  constructor() {
    // A sync finished somewhere, or a channel's live state flipped: the aggregates on every row
    // can have moved, so reload unconditionally. The resync hint below must only react to
    // channel.synced — a live.changed for the same channel says nothing about the resync.
    // liveEvents, not liveReload: the hint needs the individual event's `channel` and `type`,
    // which a merged burst would flatten away.
    liveEvents(ADMIN_LIVE_URL, [LIVE_EVENT_TYPES.channelSynced, LIVE_EVENT_TYPES.liveChanged]).subscribe(
      (event) => {
        this.channelsResource.reload();
        if (
          event.type === LIVE_EVENT_TYPES.channelSynced &&
          event.channel &&
          event.channel === this.resyncFeedback()
        ) {
          this.showResyncFeedback(event.channel, RESYNC_COMPLETED_KEY);
        }
      },
    );

    const tick = setInterval(() => this.nowMs.set(Date.now()), LIVE_AGE_TICK_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(tick));
  }
```

**Der Typ-Guard `event.type === LIVE_EVENT_TYPES.channelSynced` ist Pflicht** — ohne ihn würde ein `live.changed` für einen frisch resyncten Channel den „Resync läuft"-Hinweis fälschlich auf „abgeschlossen" hochstufen.

- [ ] **Step 2: Tooltip-Fix (derselbe Regel-14-Bug wie auf der Übersicht).** Datei-Kopf: `const LIVE_AGE_TICK_MS = 30_000;` neben die anderen Konstanten (Zeile 26 ff.); `DestroyRef` in den `@angular/core`-Import aufnehmen. In der Klasse ein `nowMs`-Signal vor `liveAgeMinutes` (Feld-Reihenfolge analog Übersicht) und `Date.now()` im `computed` ersetzen:

```typescript
  // Ticking clock signal so the tooltip ages while the page is open — Date.now() read directly
  // inside a computed() freezes at first render (rule 14).
  private readonly nowMs = signal(Date.now());

  /** Age of the live-poll data in whole minutes, for the badge tooltip. */
  protected readonly liveAgeMinutes = computed(() => {
    const polledAt = this.channelsResource.hasValue()
      ? this.channelsResource.value().livePolledAtUtc
      : null;
    if (!polledAt) {
      return 0;
    }
    return Math.max(0, Math.round((this.nowMs() - new Date(polledAt).getTime()) / 60_000));
  });
```

- [ ] **Step 3: Tests, Lint**

Run: `npm --prefix web test -- --watch=false && npm --prefix web run lint`
Expected: PASS.

*(Commit-Zuordnung: Commit C.)*

---

### Task 8: DECISIONS.md, Formatierung, Live-Verifikation, Commit-Serie

**Files:**
- Modify: `docs/DECISIONS.md` (neuer Eintrag oben, absteigende Datumsordnung)
- Modify: `docs/Feature-Ideen-2026-08-01.md` (Statuszeile B10 um einen Halbsatz ergänzen)

- [ ] **Step 1: DECISIONS.md-Eintrag verfassen** (Format an die bestehenden Einträge oben in der Datei angleichen; `**Betrifft:**`-Zeile ist Pflicht). Inhaltliche Kernpunkte, die drinstehen müssen:
  - **2026-08-05 — `live.changed`: Live-Badges bekommen Push-Updates.** Revidiert die B10-Entscheidung „Kein SSE-Anschluss: die Listen laden den Zustand beim Fetch mit, 5-Minuten-Granularität rechtfertigt keine Push-Updates" — der beobachtete Fall (Badge 30+ min falsch, weil die Seite nie neu lädt) hat gezeigt, dass nicht die Granularität das Problem ist, sondern das Fehlen jedes Rückwegs in den offenen Tab.
  - Ein Thin-Event pro gewechseltem Channel (konsistent zu `channel.synced`), publiziert vom `TwitchLivePollWorker` nach Diff gegen die letzte Publikation (`LiveStatusDiff`, pur, getestet); Baseline nach Worker-Neustart aus dem Redis-Snapshot, ohne Baseline keine Events (kein Event-Sturm beim Kaltstart).
  - Neuer SSE-Endpoint `GET /api/channels/live-events` (nur eingeloggt, kein Rollencheck, bewusst ohne Per-User-Filterung — Begründung: seltene Events, Payload nur Channel-Name, Autorisierungsgrenze bleibt der Refetch). `live.changed` fließt zusätzlich über Admin- und per-Channel-Stream (`AdminTypes`/`ChannelTypes`).
  - **Betrifft:** `LiveEvents.cs`, `TwitchLivePollWorker.cs`, `LiveEventPublisher.cs`, `LiveStatusDiff.cs`, `LiveEndpoints.cs`, `live-event.model.ts`, `overview-page.ts`, `admin-channels-page.ts`
- [ ] **Step 2: B10-Statuszeile ergänzen** — in `docs/Feature-Ideen-2026-08-01.md` bei B10 den Zusatz „seit 2026-08-05 mit `live.changed`-Push (SSE) statt Refresh-only" anfügen (bestehendes Statuszeilen-Format beibehalten).
- [ ] **Step 3: Formatierung**

Run: `dotnet format EmotePurge.slnx` und `npm --prefix web run format`
Expected: keine oder nur eigene Dateien geändert; danach `git status` prüfen.

- [ ] **Step 4: Volle Test-Suiten**

Run: `dotnet test EmotePurge.slnx` (Docker muss laufen — Testcontainers) und `npm --prefix web test -- --watch=false && npm --prefix web run e2e`
Expected: alles PASS.

- [ ] **Step 5: Live-Verifikation (Regel 16).** Stack neu bauen: `docker compose up -d --build`. Dann drei Prüfungen:
  1. **Frontend-Kette:** Browser auf `http://localhost:8080/` (eingeloggt), DevTools-Network offen. In zweiter Shell: `docker compose exec redis redis-cli PUBLISH live:events '{"type":"live.changed","channel":"irgendeinchannel"}'` → die Übersicht muss `/api/channels/mine` refetchen (Network-Tab), ohne Seiten-Reload; ebenso die Admin-Channel-Liste (`/api/admin/channels`).
  2. **Worker-Diff-Kette (Boot-Baseline):** `docker compose exec redis redis-cli SET worker:live-status '{"generatedAtUtc":"2026-08-05T12:00:00Z","liveChannelLogins":["fakechannel123"]}'` (camelCase — `JsonSerializerOptions.Web`), dann `docker compose restart worker`. Der erste Poll liest die Baseline mit `fakechannel123`, der echte Helix-Poll enthält ihn nicht → Worker-Log muss „Live-Status-Wechsel publiziert: … offline gegangen [fakechannel123]" zeigen (`docker compose logs -f worker`), und `docker compose exec redis redis-cli SUBSCRIBE live:events` (vor dem Restart starten) muss das Event zeigen.
  3. **SSE-Endpoint direkt:** eingeloggt im Browser `fetch('/api/channels/live-events')` in der Konsole anstoßen oder den EventSource der Übersicht im Network-Tab prüfen: Status 200, `text/event-stream`, Heartbeat-Pings kommen an. Ohne Session (Inkognito): 401.
- [ ] **Step 6: Nutzer-Freigabe für die Commit-Serie einholen (Regel 1).** Vorgesehene Serie (Regel 2, logisch getrennt; Spec+Plan-Dateien in den passenden Commit aufnehmen):
  - **Commit A** — `feat(worker): publish live.changed events on live-state transitions` — `LiveEvents.cs`, `LiveStatusDiff.cs`, `LiveEventPublisher.cs`, `TwitchLivePollWorker.cs`, `LiveStatusDiffTests.cs`, `docs/DECISIONS.md`, `docs/Feature-Ideen-2026-08-01.md` (Vertrags-Commit ⇒ DECISIONS im selben Commit, Regel 3), plus `docs/superpowers/specs/…` und `docs/superpowers/plans/…`.
  - **Commit B** — `feat(api): add cross-channel SSE stream for live.changed` — `LiveEndpoints.cs`, `AuthFilterMatrixTests.cs`.
  - **Commit C** — `feat(web): auto-update live badges on overview and admin channel list` — `live-event.model.ts`, `overview-page.ts`, `admin-channels-page.ts`; Message-Body nennt den mitbehobenen Regel-14-Tooltip-Bug.
- [ ] **Step 7: Nach Freigabe committen** (Hauptsession, gezieltes `git add` pro Commit — Parallel-Session-Regel: nur exakt die eigenen Dateien stagen).
