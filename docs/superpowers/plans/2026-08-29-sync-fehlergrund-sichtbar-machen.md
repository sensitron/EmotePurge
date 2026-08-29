# 7TV-Sync sagt, warum nichts da ist — Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ein Channel, der auf 7TV kein aktives Emote-Set hat, sagt das — im Empty-State der
Nutzungsseite, im Admin-Drilldown und in der Datenbank —, statt wie jeder andere Fehlerfall still
auf denselben Leerzustand zu fallen.

**Architecture:** Vier fachlich verschiedene Ausgänge des 7TV-Clients laufen heute in ein
gemeinsames `return null`. Sie bekommen einen Ergebnistyp (`SevenTvChannelStateResult` mit
`SevenTvLookupStatus`), `SevenTvSyncService` schreibt den Grund als sprachneutralen Code an zwei
neue `Channel`-Spalten (`LastSyncFailureReason`, `LastSyncAttemptAtUtc`) und **löscht ihn wieder,
sobald ein Sync gelingt**. Zwei bestehende Lesemodelle (`EmoteSetStatusDto` für die Nutzerseite,
`AdminChannelDto` für den Admin) tragen ihn nach vorn; das Frontend übersetzt den Code und bricht
sein Sync-Polling ab, sobald ein Grund vorliegt.

**Tech Stack:** .NET 10 (Minimal API, EF Core/Npgsql, xUnit + NSubstitute + Testcontainers),
Angular 22 (Standalone, Signals, zoneless), Transloco, Vitest über `@angular/build:unit-test`,
Playwright.

**Spec:** Kein eigenes Spec-Dokument. Grundlage ist GitHub-Issue **#32** („Channel with 7tv but
without an active emote set should display an error") plus die in diesem Plan wiedergegebene
Ursachenanalyse (Abschnitt „Befund"). Die Architektur-Begründung wandert in Task 8 nach
`docs/DECISIONS.md`.

## Befund (Ursachenanalyse, bereits abgeschlossen)

Vier Zustände enden heute im selben stillen `null`:

| # | Zustand | Ort | Log heute |
|---|---|---|---|
| 1 | Kein 7TV-Account (REST 404) | `SevenTvApiClient.cs:77-82` | `LogInformation` |
| 2 | **Account da, aber kein aktives Emote-Set** (`emote_set` fehlt im JSON) | `SevenTvApiClient.cs:89-92` | **gar keins** — das Issue-Szenario |
| 3 | API-/Netzwerkfehler | `SevenTvApiClient.cs:120-124` | `LogWarning` |
| 4 | Noch nie gesynct | — | nicht unterscheidbar von 1–3 |

`SevenTvSyncService.SyncChannelAsync` (`src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs:41-45`)
reicht alle vier als `null` weiter. `SevenTvPeriodicResyncWorker.ResyncOnceAsync`
(`src/EmotePurge.Worker/SevenTvPeriodicResyncWorker.cs:66-81`) prüft nur `if (result is not null)`
und hat keinen `else`-Zweig — **es wird nichts in die DB geschrieben**, auch `LastSyncedAtUtc` nicht.

## Global Constraints

Jede Task-Anforderung schließt diesen Abschnitt implizit ein.

- **Regel 1: vor jedem `git commit` erst den Nutzer fragen.** Der Plan nennt die Commit-Befehle
  wörtlich, aber **keiner** davon wird ohne Rückfrage ausgeführt.
- **Regel 2: Conventional Commits**, ein Commit je Task statt eines Sammel-Commits.
- **Regel 3:** Der Commit, der einen Vertrag ändert, enthält seinen `docs/DECISIONS.md`-Eintrag im
  selben Commit. Hier: **Task 8**. Neue API-Felder auf zwei Endpunkten *sind* eine Vertragsänderung.
  ⚠️ **Eine parallele Session arbeitet an `docs/DECISIONS.md`.** Die Datei bis Task 8 **nicht**
  anfassen, und dort vor dem Schreiben neu einlesen.
- **Regel 4:** Kein `AppDbContext` aus Minimal-API-Handlern. Es entsteht hier kein neuer Service —
  die zwei bestehenden (`IEmoteSetStatusService`, `IAdminChannelQueryService`) bekommen Felder.
  Kein generisches Repository.
- **Regel 7: sprachneutrale Codes statt Text.** Die Gründe gehen als `snake_case`-Strings über die
  API und werden **ausschließlich** im Frontend übersetzt, in **beiden** Locale-Dateien
  (`web/public/i18n/de.json` **und** `web/public/i18n/en.json`).
- **Regel 11:** Neue Logik in `EmotePurge.Infrastructure` bekommt einen Test in
  `tests/EmotePurge.Infrastructure.Tests` — `Unit/` wenn keine echte Infrastruktur berührt wird,
  `Integration/` (Testcontainers) wenn doch.
- **Regel 12:** Neue *reine* Utilities in `web/src/app/core/` bekommen einen co-located `*.spec.ts`.
  **Isolierte Komponententests sind bewusst nicht Teil der Konvention** — es entsteht **kein**
  `usage-stats-page.spec.ts` und **kein** `admin-channel-detail-page.spec.ts`; die Oberflächen
  werden per Playwright abgedeckt.
- **Regel 16:** Backend live gegen echte 7TV-Zugänge verifizieren, nicht nur `dotnet build`.
  Task 8 enthält den konkreten Verifikationsschritt mit einem realen Twitch-Channel **ohne**
  aktives 7TV-Emote-Set.
- **Regel 18:** vor dem Commit `dotnet format EmotePurge.slnx` und `npm --prefix web run format`;
  `npm --prefix web run lint` muss grün sein.
- **Sprache:** neue Bezeichner und Kommentare **englisch**, Log- und `throw`-Messages **deutsch**.
- **Regel 19:** Member-Reihenfolge in C#-Klassen `const` → `readonly` → Felder → Properties →
  öffentliche Methoden → private Methoden.
- **Migrationen:**
  `dotnet ef migrations add <Name> --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`.
  In Produktion laufen sie **manuell vor dem Deploy** (Task 8), nicht beim App-Start.
- **Die E2E-Suite läuft nur, wenn auf `:5151` keine Api lauscht.** Antwortet dort eine echte Api mit
  401, schickt der `apiAuthInterceptor` die App auf die Login-Seite und rund die halbe Suite fällt
  mit „element not found" durch — quer über Dateien, die mit der Änderung nichts zu tun haben. Vor
  jedem Playwright-Lauf ein laufendes `dotnet run` beenden.
- **Backend-Tests brauchen laufendes Docker** (Testcontainers, `postgres:16-alpine`).
- **Befehle:**
  - ein xUnit-Test: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SevenTvSyncServiceTests.SyncChannel_NoActiveEmoteSet_PersistsTheReason"`
  - eine Vitest-Datei: `npm --prefix web test -- --watch=false --include="src/app/core/emotes/seven-tv-sync-failure.spec.ts"`
    (auf Testnamen einengen: zusätzlich `--filter="..."` — **nicht** `-t`/`--grep`, die kennt dieser Builder nicht)
  - ein Playwright-Test: `npm --prefix web run e2e -- usage-atlas.e2e.spec.ts -g "names the missing emote set"`

## Nicht-Ziele (bewusst so entschieden)

1. **Ein Fehlschlag löscht nichts.** Weder `ActiveEmoteSetId` noch Emote-Zeilen werden bei einem
   Fehlversuch angefasst. Ein 7TV-Ausfall darf weder das Mass-Delete-Panel wegnehmen noch ein ganzes
   Set archivieren — dieselbe Asymmetrie, die schon die Empty-Set-Schutzlogik begründet.
2. **Folge daraus, als bekannte Grenze:** Ein Channel, der sein aktives Set *nachträglich* entfernt,
   behält seine alte `ActiveEmoteSetId` und seine aktiven Emotes. Er bekommt den Grund
   `no_active_emote_set`, zeigt aber weiterhin sein Raster — der Empty-State erscheint nur, wenn
   ohnehin nichts anzuzeigen ist. Das ist gewollt: die Alternative wäre ein Wipe auf Verdacht.
3. **Die Schutzlogik gegen leere Sets (`SevenTvSyncService.cs:56-67`) bleibt unangetastet** und
   schreibt **weder** einen Grund **noch** einen Versuchszeitstempel. Sie trifft bewusst *keine*
   Aussage über den Channel; der nächste Tick (60 s) entscheidet.
4. **Kein Warnbanner über einem gefüllten Raster.** Der Grund erscheint im Empty-State, nicht als
   dauerhafter Störer über Daten, die stimmen.

## File Structure

| Datei | Verantwortung |
|---|---|
| `src/EmotePurge.Core/SevenTv/SevenTvModels.cs` | **geändert** — `SevenTvLookupStatus`, `SevenTvChannelStateResult`, `SevenTvTwitchUserIdResult` |
| `src/EmotePurge.Core/Services/SevenTvSyncFailureReasons.cs` | **neu** — die drei Wire-Codes + `FromStatus`. Eigene Datei wie `ApiErrorCodes`: das ist der Vertrag, den das Frontend spiegelt |
| `src/EmotePurge.Core/SevenTv/ISevenTvApiClient.cs` | **geändert** — zwei Signaturen |
| `src/EmotePurge.Core/Entities/Channel.cs` | **geändert** — zwei Spalten |
| `src/EmotePurge.Core/Services/IEmoteSetStatusService.cs` | **geändert** — zwei DTO-Felder |
| `src/EmotePurge.Core/Services/IAdminChannelQueryService.cs` | **geändert** — zwei DTO-Felder |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs` | **geändert** — Gründe statt `null`, das stille `return null` bekommt sein Log |
| `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs` | **geändert** — schreibt und **löscht** den Grund |
| `src/EmotePurge.Infrastructure/Services/EmoteSetStatusService.cs` | **geändert** — reicht zwei Spalten durch |
| `src/EmotePurge.Infrastructure/Services/AdminChannelQueryService.cs` | **geändert** — Projektion + Aufbau |
| `src/EmotePurge.Infrastructure/Migrations/*_AddChannelSyncFailureReason.cs` | **neu, generiert** |
| `tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvSyncFailureReasonsTests.cs` | **neu** — reine Abbildung, keine Infrastruktur (wie `ChannelLiveStatesTests`) |
| `tests/EmotePurge.Infrastructure.Tests/Integration/{EmoteSetStatusService,SevenTvSyncService,AdminChannelQueryService}Tests.cs` | **geändert** |
| `web/src/app/core/emotes/seven-tv-sync-failure.ts` + `.spec.ts` | **neu** — Codeliste, Schlüsselbildung, Locale-Paritätsprüfung |
| `web/src/app/core/emotes/emote-set-status.model.ts` | **geändert** |
| `web/src/app/core/admin/admin.model.ts` | **geändert** |
| `web/public/i18n/de.json`, `en.json` | **geändert** — neuer Top-Level-Block `sevenTvSync.failure`, zwei Admin-Labels, ein korrigierter Hinweistext |
| `web/src/app/features/usage-stats/usage-stats-page.{ts,html}` | **geändert** — Polling-Abkürzung + Empty-State |
| `web/src/app/features/admin/admin-channel-detail-page.ts` | **geändert** — Banner + zwei `<dl>`-Zeilen |
| `web/src/app/features/admin/admin-channels-page.ts` | **geändert** — Kurzgrund in der Statuszeile |
| `web/e2e/support/mocks.ts`, `web/e2e/{usage-atlas,admin-channels}.e2e.spec.ts` | **geändert** |
| `docs/DECISIONS.md` | **geändert** — Task 8 |

**Warum die Codes in einer eigenen Core-Datei und nicht als Enum über die Leitung:**
`ChannelLiveStates` (in `ITwitchLiveStatusReader.cs`) begründet es schon wörtlich — „a string
contract rather than an enum so the JSON wire value is the value named here, independent of
serializer enum settings". Dazu kommt hier ein zweites Argument: das Enum trägt ein `Ok`, das nie
auf die Leitung darf. Also **Enum für den Kontrollfluss im Backend** (compile-time-vollständig im
`switch`) und **String-Konstanten für den Vertrag**, mit genau einer Abbildungsfunktion dazwischen.

---

### Task 1: Die vier Ausgänge bekommen einen Namen

**Files:**
- Modify: `src/EmotePurge.Core/SevenTv/SevenTvModels.cs` (ans Ende, nach `SevenTvEditorGrant`)
- Create: `src/EmotePurge.Core/Services/SevenTvSyncFailureReasons.cs`
- Test: `tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvSyncFailureReasonsTests.cs`

**Interfaces:**
- Consumes: nichts.
- Produces: `EmotePurge.Core.SevenTv.SevenTvLookupStatus` (`Ok`, `NoSevenTvAccount`,
  `NoActiveEmoteSet`, `Unavailable`); `SevenTvChannelStateResult(SevenTvLookupStatus Status,
  SevenTvChannelState? State)` mit `Ok(SevenTvChannelState)`/`Failed(SevenTvLookupStatus)`;
  `SevenTvTwitchUserIdResult(SevenTvLookupStatus Status, string? TwitchUserId)` mit
  `Ok(string)`/`Failed(SevenTvLookupStatus)`;
  `EmotePurge.Core.Services.SevenTvSyncFailureReasons` mit den Konstanten `NoSevenTvAccount =
  "no_seventv_account"`, `NoActiveEmoteSet = "no_active_emote_set"`, `Unavailable =
  "seventv_unavailable"` und `static string? FromStatus(SevenTvLookupStatus)`.

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

Neue Datei `tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvSyncFailureReasonsTests.cs`.
`Unit/`, nicht `Integration/`: die Klasse unter Test berührt keine echte Infrastruktur — dasselbe
Kriterium, nach dem `ChannelLiveStatesTests` dort liegt.

```csharp
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The wire contract the Angular app mirrors (Regel 7). Pinned here rather than left to review
// discipline: the codes travel through two DTOs, two locale files and a key builder, and a typo in
// any of them degrades silently to "no reason at all" — which is exactly the state issue #32 was.
public class SevenTvSyncFailureReasonsTests
{
    [Fact]
    public void FromStatus_Ok_HasNoReason()
    {
        // A success must never carry a code: null is what makes "the last attempt worked" and
        // "nothing has been attempted yet" readable as the same absence downstream.
        Assert.Null(SevenTvSyncFailureReasons.FromStatus(SevenTvLookupStatus.Ok));
    }

    [Theory]
    [InlineData(SevenTvLookupStatus.NoSevenTvAccount, "no_seventv_account")]
    [InlineData(SevenTvLookupStatus.NoActiveEmoteSet, "no_active_emote_set")]
    [InlineData(SevenTvLookupStatus.Unavailable, "seventv_unavailable")]
    public void FromStatus_MapsEveryFailureToItsWireCode(SevenTvLookupStatus status, string expected)
    {
        Assert.Equal(expected, SevenTvSyncFailureReasons.FromStatus(status));
    }

    [Fact]
    public void FromStatus_UnknownStatus_Throws()
    {
        // A future enum member must not silently become "no failure" — that would put a channel
        // back into the mute state this whole change removes.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SevenTvSyncFailureReasons.FromStatus((SevenTvLookupStatus)99));
    }
}
```

- [ ] **Step 2: Test laufen lassen und den Fehlschlag sehen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SevenTvSyncFailureReasonsTests"`
Expected: FAIL — Compilerfehler `CS0246: The type or namespace name 'SevenTvSyncFailureReasons' could not be found`.

- [ ] **Step 3: Die Core-Typen anlegen**

Ans **Ende** von `src/EmotePurge.Core/SevenTv/SevenTvModels.cs` anfügen:

```csharp
// Why a 7TV lookup produced no usable answer. Four outcomes used to collapse onto one `null`
// (issue #32): "no 7TV account", "account but no active emote set", "7TV unreachable" and "never
// synced" were indistinguishable, and the second one did not even log. Ok is not a failure and
// never reaches the wire — SevenTvSyncFailureReasons maps the other three onto the API contract.
public enum SevenTvLookupStatus
{
    Ok,
    NoSevenTvAccount,
    NoActiveEmoteSet,
    Unavailable
}

// The channel state plus why it is absent. State is non-null if and only if Status is Ok; the two
// factories are the only supported way to build one, so that invariant cannot be broken at a call
// site.
public record SevenTvChannelStateResult(SevenTvLookupStatus Status, SevenTvChannelState? State)
{
    public static SevenTvChannelStateResult Ok(SevenTvChannelState state) =>
        new(SevenTvLookupStatus.Ok, state);

    public static SevenTvChannelStateResult Failed(SevenTvLookupStatus status) =>
        new(status, null);
}

// Same shape for the Twitch-id resolution, which can only ever end in Ok, NoSevenTvAccount (no 7TV
// user carries that Twitch connection) or Unavailable. A separate record rather than a generic
// envelope: the property name says what it holds, which a `Value` never would.
public record SevenTvTwitchUserIdResult(SevenTvLookupStatus Status, string? TwitchUserId)
{
    public static SevenTvTwitchUserIdResult Ok(string twitchUserId) =>
        new(SevenTvLookupStatus.Ok, twitchUserId);

    public static SevenTvTwitchUserIdResult Failed(SevenTvLookupStatus status) =>
        new(status, null);
}
```

Neue Datei `src/EmotePurge.Core/Services/SevenTvSyncFailureReasons.cs`:

```csharp
using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>
/// Why the last full 7TV sync attempt for a channel produced nothing, as a stable, language-neutral
/// code (Regel 7). A string contract rather than <see cref="SevenTvLookupStatus"/> itself, for the
/// same two reasons as <c>ChannelLiveStates</c> and <c>ApiErrorCodes</c>: the JSON wire value is the
/// value named here, independent of serializer enum settings — and the enum carries an
/// <see cref="SevenTvLookupStatus.Ok"/> member that must never appear on the wire.
/// <para>
/// Mirrored in <c>web/src/app/core/emotes/seven-tv-sync-failure.ts</c>; every code needs a
/// <c>sevenTvSync.failure.<code></c> block in <b>both</b> locale files.
/// </para>
/// </summary>
public static class SevenTvSyncFailureReasons
{
    /// <summary>No 7TV account carries this Twitch channel's connection at all.</summary>
    public const string NoSevenTvAccount = "no_seventv_account";

    /// <summary>
    /// The 7TV account exists, but no emote set is active on it — the case behind issue #32, and
    /// the only one the channel owner can fix themselves in a minute.
    /// </summary>
    public const string NoActiveEmoteSet = "no_active_emote_set";

    /// <summary>7TV could not be reached or answered with an error. Transient by nature.</summary>
    public const string Unavailable = "seventv_unavailable";

    /// <summary>
    /// The single mapping point between the internal control flow and the wire contract.
    /// Returns <c>null</c> for <see cref="SevenTvLookupStatus.Ok"/>: a success has no reason, and
    /// that absence is what the persisted column uses to mean "the last attempt worked".
    /// </summary>
    public static string? FromStatus(SevenTvLookupStatus status) => status switch
    {
        SevenTvLookupStatus.Ok => null,
        SevenTvLookupStatus.NoSevenTvAccount => NoSevenTvAccount,
        SevenTvLookupStatus.NoActiveEmoteSet => NoActiveEmoteSet,
        SevenTvLookupStatus.Unavailable => Unavailable,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unbekannter 7TV-Lookup-Status — kein Fehlergrund zuzuordnen.")
    };
}
```

- [ ] **Step 4: Test laufen lassen und grün sehen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SevenTvSyncFailureReasonsTests"`
Expected: PASS, 5 Tests (1 Fact + 3 Theory-Fälle + 1 Fact).

- [ ] **Step 5: Prüfen, dass Core weiterhin nur BCL sieht**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CoreAssemblyReferenceTests"`
Expected: PASS — Enum und Records in `Core` sind erlaubt, 0 `PackageReference` bleibt unberührt.

- [ ] **Step 6: Formatieren und committen**

```bash
dotnet format EmotePurge.slnx
git add src/EmotePurge.Core/SevenTv/SevenTvModels.cs \
        src/EmotePurge.Core/Services/SevenTvSyncFailureReasons.cs \
        tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvSyncFailureReasonsTests.cs
git commit -m "feat(7tv): name the four outcomes of a channel state lookup"
```

**Regel 1: vorher den Nutzer fragen.**

---

### Task 2: Der Channel merkt sich den Grund, die Nutzer-API liest ihn

**Files:**
- Modify: `src/EmotePurge.Core/Entities/Channel.cs` (nach `LastSyncedAtUtc`, vor `Emotes`)
- Modify: `src/EmotePurge.Core/Services/IEmoteSetStatusService.cs`
- Modify: `src/EmotePurge.Infrastructure/Services/EmoteSetStatusService.cs:10-30`
- Create: `src/EmotePurge.Infrastructure/Migrations/<Zeitstempel>_AddChannelSyncFailureReason.cs` (generiert)
- Test: `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteSetStatusServiceTests.cs`

**Interfaces:**
- Consumes: `SevenTvSyncFailureReasons` aus Task 1.
- Produces: `Channel.LastSyncFailureReason` (`string?`), `Channel.LastSyncAttemptAtUtc`
  (`DateTime?`); `EmoteSetStatusDto(string ActiveEmoteSetId, int? Capacity, int OccupiedSlots,
  DateTime TrackedSince, string? SyncFailureReason, DateTime? LastSyncAttemptAtUtc)` — die zwei
  neuen Parameter **positionell am Ende, ohne Default** (es gibt genau eine Konstruktionsstelle).

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

In `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteSetStatusServiceTests.cs` **nach**
`GetAsync_BeforeTheFirstSync_ReportsNoOccupiedSlots` einfügen:

```csharp
    [Fact]
    public async Task GetAsync_ReportsThePersistedSyncFailureReason()
    {
        // The whole point of issue #32: "empty set id" alone cannot tell a channel whose first sync
        // is still running apart from one that has no active emote set on 7TV at all. The reason
        // column is what separates them, so it has to survive the round trip through Postgres.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest7", capacity: null, activeEmoteSetId: "");
        channel.LastSyncFailureReason = SevenTvSyncFailureReasons.NoActiveEmoteSet;
        channel.LastSyncAttemptAtUtc = new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal("no_active_emote_set", status.SyncFailureReason);
        Assert.Equal(new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc), status.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task GetAsync_NeverAttempted_ReportsNeitherReasonNorAttempt()
    {
        // The fourth state from the analysis: a freshly joined channel. Both fields null is what
        // lets the usage page keep polling instead of claiming a cause it does not have.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest8", capacity: null, activeEmoteSetId: "");

        var status = await new EmoteSetStatusService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Null(status.SyncFailureReason);
        Assert.Null(status.LastSyncAttemptAtUtc);
    }
```

Und oben in der Datei `using EmotePurge.Core.Services;` ergänzen.

- [ ] **Step 2: Test laufen lassen und den Fehlschlag sehen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~EmoteSetStatusServiceTests.GetAsync_ReportsThePersistedSyncFailureReason"`
Expected: FAIL — `CS1061: 'Channel' does not contain a definition for 'LastSyncFailureReason'`.

- [ ] **Step 3: Die zwei Spalten anlegen**

In `src/EmotePurge.Core/Entities/Channel.cs`, direkt **nach** `LastSyncedAtUtc` und **vor**
`public ICollection<Emote> Emotes`:

```csharp
    // When the last full 7TV sync attempt finished, successful or not. Deliberately a second column
    // next to LastSyncedAtUtc rather than a reinterpretation of it: the pair is the diagnosis. Equal
    // values mean the last attempt succeeded; a fresh attempt next to an old success means the
    // channel has been failing since then. Without it, a reason written three days ago would read as
    // a statement about right now.
    public DateTime? LastSyncAttemptAtUtc { get; set; }

    // Why that last attempt produced nothing, as one of SevenTvSyncFailureReasons — null when it
    // succeeded. Null plus an empty ActiveEmoteSetId is therefore the honest "no sync has been
    // attempted yet", which is precisely the state the usage page polls on. Cleared by
    // SevenTvSyncService on every successful sync, in the same block that stamps LastSyncedAtUtc, so
    // a channel that fixed its 7TV side stops being told it is broken.
    public string? LastSyncFailureReason { get; set; }
```

- [ ] **Step 4: Die Migration erzeugen und prüfen**

```bash
dotnet ef migrations add AddChannelSyncFailureReason \
  --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
```

Die erzeugte Datei muss genau zwei additive, nullable Spalten enthalten (und sonst nichts):

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncAttemptAtUtc",
                table: "Channels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncFailureReason",
                table: "Channels",
                type: "text",
                nullable: true);
        }
```

Enthält sie mehr, ist ein fremder Modellstand mitgewandert — dann verwerfen
(`dotnet ef migrations remove --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`)
und die Ursache klären, **nicht** die Datei von Hand kürzen.

- [ ] **Step 5: Die zwei DTO-Felder ergänzen**

In `src/EmotePurge.Core/Services/IEmoteSetStatusService.cs` den Record ersetzen (die bestehenden
`<param>`-Kommentare bleiben, zwei kommen dazu):

```csharp
/// <param name="SyncFailureReason">
/// One of <see cref="SevenTvSyncFailureReasons"/>, or <c>null</c> when the last sync attempt
/// succeeded — or when none has been made yet. Together with an empty
/// <paramref name="ActiveEmoteSetId"/> that absence is what tells "the first sync is still running"
/// apart from "this channel has no active emote set on 7TV", which used to look identical.
/// </param>
/// <param name="LastSyncAttemptAtUtc">
/// When the last attempt finished, successful or not. <c>null</c> means none has been made. Read
/// with <paramref name="SyncFailureReason"/>: it says how current the reason is.
/// </param>
public record EmoteSetStatusDto(
    string ActiveEmoteSetId,
    int? Capacity,
    int OccupiedSlots,
    DateTime TrackedSince,
    string? SyncFailureReason,
    DateTime? LastSyncAttemptAtUtc);
```

In `src/EmotePurge.Infrastructure/Services/EmoteSetStatusService.cs` den `return` erweitern:

```csharp
        return new EmoteSetStatusDto(
            channel.ActiveEmoteSetId,
            channel.ActiveEmoteSetCapacity,
            occupiedSlots,
            channel.TrackingResumedAt ?? channel.CreatedAt,
            channel.LastSyncFailureReason,
            channel.LastSyncAttemptAtUtc);
```

- [ ] **Step 6: Tests laufen lassen und grün sehen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~EmoteSetStatusServiceTests"`
Expected: PASS, 9 Tests. Die `PostgresFixture` migriert den Container mit den echten Migrationen —
grün heißt, die neue Migration ist im Build und läuft durch.

- [ ] **Step 7: Die lokale Entwicklungsdatenbank nachziehen**

```bash
docker compose up -d postgres
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
```

- [ ] **Step 8: Formatieren und committen**

```bash
dotnet format EmotePurge.slnx
git add src/EmotePurge.Core/Entities/Channel.cs \
        src/EmotePurge.Core/Services/IEmoteSetStatusService.cs \
        src/EmotePurge.Infrastructure/Services/EmoteSetStatusService.cs \
        src/EmotePurge.Infrastructure/Migrations \
        tests/EmotePurge.Infrastructure.Tests/Integration/EmoteSetStatusServiceTests.cs
git commit -m "feat(channels): persist why the last 7TV sync produced nothing"
```

**Regel 1: vorher den Nutzer fragen.**

---

### Task 3: Der Client reicht den Grund durch, der Sync schreibt und löscht ihn

**Files:**
- Modify: `src/EmotePurge.Core/SevenTv/ISevenTvApiClient.cs:5-12`
- Modify: `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:41-70` und `:73-125`
- Modify: `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs:20-95`
- Test: `tests/EmotePurge.Infrastructure.Tests/Integration/SevenTvSyncServiceTests.cs`

**Interfaces:**
- Consumes: `SevenTvChannelStateResult`, `SevenTvTwitchUserIdResult`, `SevenTvLookupStatus`,
  `SevenTvSyncFailureReasons.FromStatus` (Task 1); `Channel.LastSyncFailureReason`,
  `Channel.LastSyncAttemptAtUtc` (Task 2).
- Produces: `ISevenTvApiClient.ResolveTwitchUserIdAsync` → `Task<SevenTvTwitchUserIdResult>`
  (nicht mehr nullable), `ISevenTvApiClient.GetChannelStateForTwitchUserAsync` →
  `Task<SevenTvChannelStateResult>` (nicht mehr nullable). `SyncChannelAsync` behält
  `Task<SevenTvSyncResult?>` — **kein Worker muss angefasst werden**, weil die Persistenz dort
  passiert, wo der `AppDbContext` ohnehin liegt.

**Warum `SevenTvApiClient` selbst keinen eigenen Test bekommt:** die typisierten HTTP-Transporte
dieses Projekts werden bewusst live verifiziert statt gegen Fakes (Regel 16, s. die Begründung zu
`TwitchChatManager`/`SevenTvEventClient` in CLAUDE.md) — es existiert weder ein
`HttpMessageHandler`-Fixture noch ein Präzedenzfall dafür. Die Abbildung Status → Spalte deckt
`SevenTvSyncServiceTests` über einen substituierten Client ab; dass der *echte* Client die richtigen
Status liefert, prüft Task 8 gegen echtes 7TV.

- [ ] **Step 1: Die fehlschlagenden Tests schreiben**

In `tests/EmotePurge.Infrastructure.Tests/Integration/SevenTvSyncServiceTests.cs` ans **Ende der
Klasse**, vor die privaten Helfer:

```csharp
    // ---- Warum ein Sync nichts geliefert hat (Issue #32) ----

    // Builds a sync service whose 7TV client fails with a given status, so the four outcomes of the
    // analysis can be driven one by one. Separate from CreateRestService, which only knows success.
    private static SevenTvSyncService CreateFailingService(
        Persistence.AppDbContext db,
        EmoteMatchCache cache,
        Channel channel,
        SevenTvLookupStatus status)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Failed(status));
        return new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);
    }

    [Theory]
    [InlineData(SevenTvLookupStatus.NoActiveEmoteSet, "no_active_emote_set")]
    [InlineData(SevenTvLookupStatus.NoSevenTvAccount, "no_seventv_account")]
    [InlineData(SevenTvLookupStatus.Unavailable, "seventv_unavailable")]
    public async Task SyncChannel_NoActiveEmoteSet_PersistsTheReason(SevenTvLookupStatus status, string expectedReason)
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, $"wstest_reason_{expectedReason}");
        var service = CreateFailingService(db, cache, channel, status);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(result);
        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc, c.LastSyncedAtUtc })
            .SingleAsync();
        Assert.Equal(expectedReason, row.LastSyncFailureReason);
        Assert.NotNull(row.LastSyncAttemptAtUtc);
        // LastSyncedAtUtc keeps meaning "last *successful* sync" — a failed attempt must not
        // advance it, or the admin drilldown would report a healthy sync for a broken channel.
        Assert.Null(row.LastSyncedAtUtc);
    }

    [Fact]
    public async Task SyncChannel_FailedAttempt_LeavesTheKnownSetAndItsEmotesAlone()
    {
        // The asymmetry that governs this whole area: a 7TV outage must not take the mass-delete
        // panel away or archive a channel's entire set. A failure records *why*, and nothing else.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_reason_keeps", ("e1", "stable", false));
        var service = CreateFailingService(db, cache, channel, SevenTvLookupStatus.Unavailable);

        await service.SyncChannelAsync(channel.ChannelName);

        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.ActiveEmoteSetId, c.ActiveEmoteSetCapacity })
            .SingleAsync();
        Assert.Equal(SetId, row.ActiveEmoteSetId);
        Assert.False(await db.Emotes.Where(e => e.ChannelId == channel.Id)
            .Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_Success_ClearsAPreviousReason()
    {
        // The half that gets forgotten. A channel that activated an emote set on 7TV must stop being
        // told it has none — otherwise the empty state outlives the problem it describes.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_reason_cleared");
        channel.LastSyncFailureReason = SevenTvSyncFailureReasons.NoActiveEmoteSet;
        channel.LastSyncAttemptAtUtc = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "fresh"));

        await service.SyncChannelAsync(channel.ChannelName);

        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc, c.LastSyncedAtUtc })
            .SingleAsync();
        Assert.Null(row.LastSyncFailureReason);
        Assert.NotNull(row.LastSyncedAtUtc);
        // Attempt and success are stamped from the same instant on a successful run, so the pair
        // reads as "current" rather than leaving a stale attempt behind an up-to-date success.
        Assert.Equal(row.LastSyncedAtUtc, row.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_TouchesNeitherReasonNorAttempt()
    {
        // The empty-set guard (S3-12) deliberately makes no statement about the channel: it neither
        // succeeded nor failed, it declined to act. Writing an attempt timestamp there would claim a
        // reconciliation that never happened, and clearing a reason would hide a real one.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_reason_guard", ("e1", "stable", false));
        channel.LastSyncFailureReason = SevenTvSyncFailureReasons.Unavailable;
        await db.SaveChangesAsync();
        var service = CreateRestService(db, cache, channel, SetId);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc })
            .SingleAsync();
        Assert.Equal("seventv_unavailable", row.LastSyncFailureReason);
        Assert.Null(row.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task SyncChannel_UnresolvableTwitchId_RecordsTheMissingAccount()
    {
        // The pre-step: a channel whose TwitchChannelId was never backfilled resolves it through
        // 7TV's own user search. Its "no match" answer is a missing 7TV account, not a network
        // problem, and used to vanish into the same null as everything else.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        db.Channels.Add(new Channel { ChannelName = "wstest_reason_noid", TwitchChannelId = null, ActiveEmoteSetId = "" });
        await db.SaveChangesAsync();

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.ResolveTwitchUserIdAsync("wstest_reason_noid", Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.NoSevenTvAccount));
        var service = new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        var result = await service.SyncChannelAsync("wstest_reason_noid");

        Assert.Null(result);
        Assert.Equal("no_seventv_account", await db.Channels
            .Where(c => c.ChannelName == "wstest_reason_noid")
            .Select(c => c.LastSyncFailureReason).SingleAsync());
    }
```

Oben in der Datei `using EmotePurge.Core.Services;` ergänzen.

Außerdem die **drei bestehenden Erfolgs-Stubs** anpassen — `.Returns(new SevenTvChannelState(...))`
wird zu `.Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState(...)))`:

- `CreateRestService` (Zeile ~40)
- `CreateRestServiceWithCapacity` (Zeile ~57)
- `SyncChannel_WithDuplicateActiveNames_...` (Zeile ~96)
- `SyncChannel_PassesSevenTvUserIdThrough` (Zeile ~293)

- [ ] **Step 2: Tests laufen lassen und den Fehlschlag sehen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SevenTvSyncServiceTests"`
Expected: FAIL — `CS1929`/`CS0029`: `SevenTvChannelStateResult` lässt sich nicht als
`SevenTvChannelState?` zurückgeben.

- [ ] **Step 3: Die Client-Schnittstelle umstellen**

In `src/EmotePurge.Core/SevenTv/ISevenTvApiClient.cs` die zwei Signaturen ersetzen:

```csharp
    // Never null: the outcome is the answer. Ok carries the resolved Twitch user id, the three
    // failure statuses say why there is none — a distinction that used to be lost in a bare null.
    Task<SevenTvTwitchUserIdResult> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default);

    // The channel's active emote set plus the 7TV account id behind the Twitch connection — both
    // come from the same users/twitch/{id} response, so resolving them together costs no extra call.
    // Never null; State is populated if and only if Status is Ok. The three failure statuses are the
    // three ways this call can legitimately produce nothing, and they must stay apart: only one of
    // them ("no active emote set") is something the channel owner can fix.
    Task<SevenTvChannelStateResult> GetChannelStateForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Den Client die Gründe liefern lassen**

`src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs`, `ResolveTwitchUserIdAsync` komplett:

```csharp
    public async Task<SevenTvTwitchUserIdResult> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        try
        {
            var payload = new { query = GqlUsersQuery, variables = new { q = normalized } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlUsersResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            var match = dto?.Data?.Users
                .SelectMany(u => u.Connections)
                .FirstOrDefault(c => c.Platform == "TWITCH" &&
                    string.Equals(c.Username, normalized, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                logger.LogInformation("Kein 7TV-Twitch-Match für {Channel}.", normalized);
                return SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.NoSevenTvAccount);
            }

            return SevenTvTwitchUserIdResult.Ok(match.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Nutzersuche für {Channel} fehlgeschlagen, wird übersprungen.", normalized);
            return SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.Unavailable);
        }
    }
```

Und `GetChannelStateForTwitchUserAsync` — nur Kopf, die vier Rückgabestellen und der neue Log:

```csharp
    public async Task<SevenTvChannelStateResult> GetChannelStateForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"users/twitch/{twitchUserId}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Kein 7TV-Account für Twitch-ID {Id}.", twitchUserId);
                return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.NoSevenTvAccount);
            }

            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvUserRestDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            // A 200 with no body at all is a broken answer, not a statement about the account —
            // reporting it as "no emote set" would tell the owner to fix something that is fine.
            if (dto is null)
            {
                logger.LogWarning("7TV-Antwort für Twitch-ID {Id} war leer.", twitchUserId);
                return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable);
            }

            // The state behind issue #32, and the only one of the four that used to return silently:
            // the account exists, but no emote set is active on the Twitch connection. Logged at
            // Information because it is a legitimate configuration, not a fault of ours.
            if (dto.EmoteSet is null)
            {
                logger.LogInformation(
                    "7TV-Account für Twitch-ID {Id} hat kein aktives Emote-Set.", twitchUserId);
                return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.NoActiveEmoteSet);
            }

            // ... unverändert: emotes, addedAtByEmoteId-Overlay, sevenTvUserId, capacity ...

            return SevenTvChannelStateResult.Ok(
                new SevenTvChannelState(sevenTvUserId, new SevenTvEmoteSet(dto.EmoteSet.Id, emotes, capacity)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Emote-Set-Abruf für Twitch-ID {Id} fehlgeschlagen, wird übersprungen.", twitchUserId);
            return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable);
        }
    }
```

Der Block dazwischen (Emote-Mapping, v4-`addedAt`-Overlay, `sevenTvUserId`, `capacity`) bleibt
**wortgleich**; nur `dto?.EmoteSet` wird zu `dto.EmoteSet`, weil `dto` oben schon geprüft ist.

- [ ] **Step 5: Den Sync schreiben und löschen lassen**

`src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs`, Block ab Zeile 34 ersetzen:

```csharp
        var twitchUserId = channel.TwitchChannelId;
        if (twitchUserId is null)
        {
            var resolved = await sevenTvApiClient.ResolveTwitchUserIdAsync(normalized, cancellationToken);
            if (resolved.Status != SevenTvLookupStatus.Ok || resolved.TwitchUserId is null)
            {
                await RecordFailedAttemptAsync(channel, resolved.Status, cancellationToken);
                return null;
            }

            twitchUserId = resolved.TwitchUserId;
        }

        var channelState = await sevenTvApiClient.GetChannelStateForTwitchUserAsync(twitchUserId, cancellationToken);
        if (channelState.Status != SevenTvLookupStatus.Ok || channelState.State is null)
        {
            await RecordFailedAttemptAsync(channel, channelState.Status, cancellationToken);
            return null;
        }

        var emoteSet = channelState.State.EmoteSet;
```

Der Empty-Set-Guard darunter (Zeilen 56-67) bleibt **unverändert** — inklusive seines frühen
`return`, das damit weder Grund noch Versuchszeit anfasst.

Danach den Erfolgsblock ergänzen (der bestehende Kommentar zu `LastSyncedAtUtc` bleibt stehen):

```csharp
        // Unconditional, and deliberately not part of the change bookkeeping below: ... (bestehender Kommentar)
        var syncedAt = DateTime.UtcNow;
        channel.LastSyncedAtUtc = syncedAt;
        // The reset half of the contract, and the one that gets forgotten: a channel that activated
        // an emote set on 7TV must stop being told it has none. Written in the same block as the
        // success stamp so the two cannot drift apart — a reason cleared anywhere else would need a
        // second place to remember it.
        channel.LastSyncAttemptAtUtc = syncedAt;
        channel.LastSyncFailureReason = null;
```

Und ganz unten, bei den privaten Methoden (Regel 19: vor `RefreshMatchCacheAsync`):

```csharp
    /// <summary>
    /// Records why an attempt produced nothing. Writes the reason and the attempt timestamp and
    /// nothing else — deliberately not <c>ActiveEmoteSetId</c>, the capacity or any emote row: a
    /// 7TV outage must not take the mass-delete panel away or archive a whole set, and
    /// <c>LastSyncedAtUtc</c> keeps meaning "last *successful* sync".
    /// </summary>
    private async Task RecordFailedAttemptAsync(Channel channel, SevenTvLookupStatus status, CancellationToken cancellationToken)
    {
        var reason = SevenTvSyncFailureReasons.FromStatus(status);
        // Logged only when the reason changes: the periodic resync runs this for every broken
        // channel every 60 seconds, and an unconditional line would bury everything else in the log.
        // The stored value is what the UI reads, so nothing is lost by staying quiet.
        var changed = channel.LastSyncFailureReason != reason;

        channel.LastSyncAttemptAtUtc = DateTime.UtcNow;
        channel.LastSyncFailureReason = reason;
        await db.SaveChangesAsync(cancellationToken);

        if (changed)
        {
            logger.LogInformation(
                "7TV-Sync für {Channel} ohne Ergebnis: {Reason}.", channel.ChannelName, reason);
        }
    }
```

`using EmotePurge.Core.Services;` steht bereits oben in der Datei.

- [ ] **Step 6: Tests laufen lassen und grün sehen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SevenTvSyncServiceTests"`
Expected: PASS — die bestehenden Fälle unverändert grün, dazu 7 neue (3 Theory-Fälle + 4 Facts).

- [ ] **Step 7: Die ganze Solution bauen und testen**

Run: `dotnet build EmotePurge.slnx` und `dotnet test EmotePurge.slnx`
Expected: PASS. Besonders zu prüfen: `EmoteSetOwnershipService` und `SevenTvEditorService` nutzen
`ISevenTvApiClient`, aber **nicht** die zwei geänderten Methoden — sie müssen unverändert
kompilieren. Auch `SevenTvPeriodicResyncWorker`, `Worker.cs` und `SevenTvEventClient` bleiben
unangetastet, weil `SyncChannelAsync` seine Signatur behält.

- [ ] **Step 8: Formatieren und committen**

```bash
dotnet format EmotePurge.slnx
git add src/EmotePurge.Core/SevenTv/ISevenTvApiClient.cs \
        src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs \
        src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs \
        tests/EmotePurge.Infrastructure.Tests/Integration/SevenTvSyncServiceTests.cs
git commit -m "fix(7tv): stop losing why a channel sync produced nothing"
```

**Regel 1: vorher den Nutzer fragen.**

---

### Task 4: Der Admin-Drilldown bekommt den Grund als Datum

**Files:**
- Modify: `src/EmotePurge.Core/Services/IAdminChannelQueryService.cs`
- Modify: `src/EmotePurge.Infrastructure/Services/AdminChannelQueryService.cs`
- Test: `tests/EmotePurge.Infrastructure.Tests/Integration/AdminChannelQueryServiceTests.cs`

**Interfaces:**
- Consumes: `Channel.LastSyncFailureReason`, `Channel.LastSyncAttemptAtUtc` (Task 2).
- Produces: `AdminChannelDto` mit zwei zusätzlichen Parametern **am Ende, nach `LiveState`**:
  `string? LastSyncFailureReason = null, DateTime? LastSyncAttemptAtUtc = null`. Ans Ende, weil
  `AdminEndpoints.cs:214` und `:265` den Record per `with { LiveState = ... }` fortschreiben —
  ein Einschub in der Mitte würde jede positionelle Konstruktion still verschieben.

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

In `tests/EmotePurge.Infrastructure.Tests/Integration/AdminChannelQueryServiceTests.cs` einfügen:

```csharp
    [Fact]
    public async Task GetAsync_ReportsWhyTheLastSyncProducedNothing()
    {
        // The support drilldown's whole job is "why isn't my channel syncing?". Before this, a
        // channel without an active 7TV emote set showed an empty set id and a null last sync — the
        // same picture as a channel that was joined a second ago.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "adminsyncreason", isBotActive: true);
        channel.LastSyncFailureReason = SevenTvSyncFailureReasons.NoActiveEmoteSet;
        channel.LastSyncAttemptAtUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var row = await new AdminChannelQueryService(db).GetAsync("adminsyncreason");

        Assert.NotNull(row);
        Assert.Equal("no_active_emote_set", row.LastSyncFailureReason);
        Assert.Equal(new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc), row.LastSyncAttemptAtUtc);
        // The list and the drilldown share one aggregation path, so they can never disagree.
        var listRow = Assert.Single(await new AdminChannelQueryService(db).ListAsync(),
            c => c.ChannelName == "adminsyncreason");
        Assert.Equal("no_active_emote_set", listRow.LastSyncFailureReason);
    }
```

`using EmotePurge.Core.Services;` steht dort bereits.

- [ ] **Step 2: Test laufen lassen und den Fehlschlag sehen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AdminChannelQueryServiceTests.GetAsync_ReportsWhyTheLastSyncProducedNothing"`
Expected: FAIL — `CS1061: 'AdminChannelDto' does not contain a definition for 'LastSyncFailureReason'`.

- [ ] **Step 3: DTO und Projektion erweitern**

In `src/EmotePurge.Core/Services/IAdminChannelQueryService.cs`, zwei `<param>`-Blöcke vor den Record
und zwei Parameter ans Ende:

```csharp
/// <param name="LastSyncFailureReason">
/// One of <see cref="SevenTvSyncFailureReasons"/> when the last attempt produced nothing, else
/// <c>null</c>. This is what separates the four states that used to look identical here: no 7TV
/// account, an account without an active emote set, 7TV unreachable, and never synced at all.
/// </param>
/// <param name="LastSyncAttemptAtUtc">
/// When the last attempt finished, successful or not. Read next to
/// <paramref name="LastSyncedAtUtc"/>: equal values mean the last attempt succeeded, a newer attempt
/// next to an older success means the channel has been failing since then.
/// </param>
public record AdminChannelDto(
    string ChannelName,
    string? TwitchChannelId,
    bool IsBotActive,
    DateTime CreatedAt,
    int EmoteCount,
    int ArchivedEmoteCount,
    int ActiveVoteSessionCount,
    int VoteSessionCount,
    DateTime? LastSyncedAtUtc,
    DateTime? LastInventoryChangeUtc,
    string? ActiveEmoteSetId = null,
    int? ActiveEmoteSetCapacity = null,
    DateTime? TrackingResumedAt = null,
    string LiveState = ChannelLiveStates.Unknown,
    string? LastSyncFailureReason = null,
    DateTime? LastSyncAttemptAtUtc = null);
```

In `src/EmotePurge.Infrastructure/Services/AdminChannelQueryService.cs` das private `ChannelRow`
um zwei Felder erweitern (Reihenfolge in Record **und** `Projection` gleich halten):

```csharp
    private sealed record ChannelRow(
        string Id,
        string ChannelName,
        string? TwitchChannelId,
        bool IsBotActive,
        DateTime CreatedAt,
        DateTime? LastSyncedAtUtc,
        string ActiveEmoteSetId,
        int? ActiveEmoteSetCapacity,
        DateTime? TrackingResumedAt,
        string? LastSyncFailureReason,
        DateTime? LastSyncAttemptAtUtc)
    {
        public static Expression<Func<Channel, ChannelRow>> Projection { get; } =
            c => new ChannelRow(
                c.Id,
                c.ChannelName,
                c.TwitchChannelId,
                c.IsBotActive,
                c.CreatedAt,
                c.LastSyncedAtUtc,
                c.ActiveEmoteSetId,
                c.ActiveEmoteSetCapacity,
                c.TrackingResumedAt,
                c.LastSyncFailureReason,
                c.LastSyncAttemptAtUtc);
    }
```

Und in `BuildAsync` den `return new AdminChannelDto(...)` um die zwei letzten Argumente ergänzen —
`LiveState` muss dabei **benannt** übergeben werden, weil es übersprungen wird:

```csharp
                    c.ActiveEmoteSetCapacity,
                    c.TrackingResumedAt,
                    LastSyncFailureReason: c.LastSyncFailureReason,
                    LastSyncAttemptAtUtc: c.LastSyncAttemptAtUtc);
```

(Das vorangehende `c.TrackingResumedAt` bleibt positionell; `LiveState` behält seinen Default und
wird wie bisher erst im Endpoint per `with` gesetzt.)

- [ ] **Step 4: Tests laufen lassen und grün sehen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AdminChannelQueryServiceTests"`
Expected: PASS.

- [ ] **Step 5: Die ganze Backend-Suite laufen lassen**

Run: `dotnet test EmotePurge.slnx`
Expected: PASS — `EmotePurge.Api.Tests` fährt die echte Pipeline und beweist, dass die zwei neuen
Felder die Admin-Routen nicht brechen.

- [ ] **Step 6: Formatieren und committen**

```bash
dotnet format EmotePurge.slnx
git add src/EmotePurge.Core/Services/IAdminChannelQueryService.cs \
        src/EmotePurge.Infrastructure/Services/AdminChannelQueryService.cs \
        tests/EmotePurge.Infrastructure.Tests/Integration/AdminChannelQueryServiceTests.cs
git commit -m "feat(admin): serve the last sync failure reason per channel"
```

**Regel 1: vorher den Nutzer fragen.**

---

### Task 5: Das Frontend kennt die drei Gründe und hat für jeden Worte

**Files:**
- Create: `web/src/app/core/emotes/seven-tv-sync-failure.ts`
- Create: `web/src/app/core/emotes/seven-tv-sync-failure.spec.ts`
- Modify: `web/src/app/core/emotes/emote-set-status.model.ts`
- Modify: `web/src/app/core/admin/admin.model.ts` (nach `activeEmoteSetCapacity`)
- Modify: `web/public/i18n/de.json`, `web/public/i18n/en.json`

**Interfaces:**
- Consumes: die Wire-Codes aus Task 1 (`no_seventv_account`, `no_active_emote_set`,
  `seventv_unavailable`).
- Produces: `SevenTvSyncFailureReason` (Union-Typ), `SEVEN_TV_SYNC_FAILURE_REASONS`
  (`readonly SevenTvSyncFailureReason[]`), `sevenTvSyncFailureKey(reason, part)` mit
  `part: 'title' | 'hint' | 'short'` → `sevenTvSync.failure.<reason>.<part>`;
  `EmoteSetStatus.syncFailureReason`/`.lastSyncAttemptAtUtc`;
  `AdminChannel.lastSyncFailureReason`/`.lastSyncAttemptAtUtc`.

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

Neue Datei `web/src/app/core/emotes/seven-tv-sync-failure.spec.ts`:

```ts
import { describe, expect, it } from 'vitest';

import {
  SEVEN_TV_SYNC_FAILURE_REASONS,
  SevenTvSyncFailureReason,
  sevenTvSyncFailureKey,
} from './seven-tv-sync-failure';
import de from '../../../../public/i18n/de.json';
import en from '../../../../public/i18n/en.json';

/**
 * The same guard `api-error-locales.spec.ts` exists for, applied to the second language-neutral
 * code list the API now serves (Regel 7). Nothing under `web/src` reads the locale files at build
 * time, so without this a reason shipped by the API and forgotten in a locale would render as the
 * raw key `sevenTvSync.failure.no_active_emote_set.title` on the page — the exact silent state
 * issue #32 was about, one layer up.
 */
describe('7TV sync failure reasons', () => {
  const locales = { de, en } as Record<
    string,
    { sevenTvSync: { failure: Record<string, Record<string, string>> } }
  >;

  it('builds the translation key from the wire code', () => {
    expect(sevenTvSyncFailureKey('no_active_emote_set', 'title')).toBe(
      'sevenTvSync.failure.no_active_emote_set.title',
    );
    expect(sevenTvSyncFailureKey('seventv_unavailable', 'short')).toBe(
      'sevenTvSync.failure.seventv_unavailable.short',
    );
  });

  it.each(Object.keys(locales))('%s translates every reason in all three lengths', (name) => {
    const failures = locales[name].sevenTvSync.failure;
    const missing: string[] = [];
    for (const reason of SEVEN_TV_SYNC_FAILURE_REASONS) {
      for (const part of ['title', 'hint', 'short'] as const) {
        if (!failures[reason]?.[part]) {
          missing.push(`${reason}.${part}`);
        }
      }
    }

    expect(missing).toEqual([]);
  });

  it.each(Object.keys(locales))('%s carries no translations for unknown reasons', (name) => {
    // The other direction: a reason removed from the API but left behind reads like a supported
    // case to the next person editing the file.
    const known = new Set<string>(SEVEN_TV_SYNC_FAILURE_REASONS);
    const stray = Object.keys(locales[name].sevenTvSync.failure).filter((r) => !known.has(r));

    expect(stray).toEqual([]);
  });

  it('has identical key sets in both locales', () => {
    expect(Object.keys(de.sevenTvSync.failure).sort()).toEqual(
      Object.keys(en.sevenTvSync.failure).sort(),
    );
  });

  it('accepts exactly the three codes the API can send', () => {
    // Compile-time proof that the union and the runtime list cannot drift: the array is typed as
    // the union, and this assignment fails to compile if the union grows without the array.
    const all: readonly SevenTvSyncFailureReason[] = SEVEN_TV_SYNC_FAILURE_REASONS;
    expect(all).toEqual(['no_seventv_account', 'no_active_emote_set', 'seventv_unavailable']);
  });
});
```

- [ ] **Step 2: Test laufen lassen und den Fehlschlag sehen**

Run: `npm --prefix web test -- --watch=false --include="src/app/core/emotes/seven-tv-sync-failure.spec.ts"`
Expected: FAIL — `Cannot find module './seven-tv-sync-failure'`.

- [ ] **Step 3: Die Codeliste und den Schlüsselbau anlegen**

Neue Datei `web/src/app/core/emotes/seven-tv-sync-failure.ts`:

```ts
/**
 * Mirrors `src/EmotePurge.Core/Services/SevenTvSyncFailureReasons.cs` — the API sends only these
 * stable, language-neutral codes and never prose (Regel 7), so translation happens exactly once,
 * through {@link sevenTvSyncFailureKey}.
 *
 * Exported as a runtime list as well as a type so `seven-tv-sync-failure.spec.ts` can assert that
 * both locale files carry every reason. The step from the C# file to here stays discipline, the
 * same gap `api-error.ts` documents.
 */
export type SevenTvSyncFailureReason =
  | 'no_seventv_account'
  | 'no_active_emote_set'
  | 'seventv_unavailable';

export const SEVEN_TV_SYNC_FAILURE_REASONS: readonly SevenTvSyncFailureReason[] = [
  'no_seventv_account',
  'no_active_emote_set',
  'seventv_unavailable',
];

/**
 * Three lengths per reason, because the same fact is needed at three sizes and splitting them per
 * surface would let the wording drift: `title` and `hint` build the user's empty state, `short` is
 * the one-liner the admin list row and the drilldown banner carry.
 */
export function sevenTvSyncFailureKey(
  reason: SevenTvSyncFailureReason,
  part: 'title' | 'hint' | 'short',
): string {
  return `sevenTvSync.failure.${reason}.${part}`;
}
```

- [ ] **Step 4: Die zwei Modelle erweitern**

In `web/src/app/core/emotes/emote-set-status.model.ts` den Import und zwei Felder ergänzen:

```ts
import { SevenTvSyncFailureReason } from './seven-tv-sync-failure';
```

und im Interface, nach `trackedSince`:

```ts
  /**
   * Why the last 7TV sync attempt produced nothing, `null` when it succeeded — or when none has
   * been made yet. Read together with `activeEmoteSetId`: an empty id and a `null` reason is the
   * only combination that genuinely means "the first sync is still running", and it is the only one
   * worth polling on.
   */
  syncFailureReason: SevenTvSyncFailureReason | null;

  /** ISO timestamp of the last attempt, successful or not; `null` when none has been made. */
  lastSyncAttemptAtUtc: string | null;
```

In `web/src/app/core/admin/admin.model.ts`, im Interface `AdminChannel` nach
`activeEmoteSetCapacity`:

```ts
  /** Why the last 7TV sync attempt produced nothing (`SevenTvSyncFailureReason`), else null. The
   *  field that tells a channel with no active 7TV emote set apart from one joined a second ago. */
  lastSyncFailureReason: SevenTvSyncFailureReason | null;
  /** When the last attempt finished, successful or not. Says how current `lastSyncFailureReason`
   *  is; null means none has been made. */
  lastSyncAttemptAtUtc: string | null;
```

plus den Import `import { SevenTvSyncFailureReason } from '../emotes/seven-tv-sync-failure';`
(erlaubt: `core/` → `core/`).

- [ ] **Step 5: Beide Locale-Dateien ergänzen**

**Neuer Top-Level-Block** in `web/public/i18n/de.json` — direkt vor `"voting"` einfügen, damit die
beiden Dateien Zeile für Zeile parallel bleiben:

```json
  "sevenTvSync": {
    "failure": {
      "no_seventv_account": {
        "title": "Für diesen Twitch-Channel gibt es kein 7TV-Konto.",
        "hint": "Emote Purge liest die Emotes über 7TV. Ohne ein 7TV-Konto mit verknüpftem Twitch-Login gibt es hier nichts zu zählen. Sobald das Konto existiert, füllt sich diese Seite von selbst.",
        "short": "7TV: kein Konto für diesen Twitch-Channel."
      },
      "no_active_emote_set": {
        "title": "Dieser Channel hat auf 7TV kein aktives Emote-Set.",
        "hint": "Auf 7tv.app lässt sich ein Emote-Set anlegen und für den Channel aktiv schalten. Danach füllt sich diese Seite von selbst — der Bot fragt 7TV jede Minute erneut.",
        "short": "7TV: kein aktives Emote-Set."
      },
      "seventv_unavailable": {
        "title": "7TV war beim letzten Versuch nicht erreichbar.",
        "hint": "Das ist meist vorübergehend und liegt nicht an diesem Channel. Der Bot versucht es jede Minute erneut; die Seite aktualisiert sich von selbst, sobald es geklappt hat.",
        "short": "7TV war beim letzten Versuch nicht erreichbar."
      }
    }
  },
```

Dasselbe an derselben Stelle in `web/public/i18n/en.json`:

```json
  "sevenTvSync": {
    "failure": {
      "no_seventv_account": {
        "title": "There is no 7TV account for this Twitch channel.",
        "hint": "Emote Purge reads emotes through 7TV. Without a 7TV account linked to this Twitch login there is nothing to count here. This page fills itself in once the account exists.",
        "short": "7TV: no account for this Twitch channel."
      },
      "no_active_emote_set": {
        "title": "This channel has no active emote set on 7TV.",
        "hint": "Create an emote set on 7tv.app and set it active for the channel. This page fills itself in afterwards — the bot asks 7TV again every minute.",
        "short": "7TV: no active emote set."
      },
      "seventv_unavailable": {
        "title": "7TV could not be reached on the last attempt.",
        "hint": "This is usually temporary and not caused by this channel. The bot retries every minute; this page updates itself as soon as it works.",
        "short": "7TV was unreachable on the last attempt."
      }
    }
  },
```

**Ein bestehender Hinweistext wird jetzt falsch** und muss mit: `usageStats.noActiveEmotesHint`
(de.json Zeile 578, en.json Zeile 578) behauptet „oder der erste Sync läuft noch" — genau die
Vermutung, die dieser Umbau durch eine Aussage ersetzt. Neu:

```json
    "noActiveEmotesHint": "Das aktive 7TV-Emote-Set dieses Channels enthält derzeit keine Emotes.",
```
```json
    "noActiveEmotesHint": "This channel's active 7TV emote set currently holds no emotes.",
```

**Zwei Admin-Labels**, in `admin.channelDetail.database` (de) nach `"lastSync"`:

```json
      "lastAttempt": "Letzter Sync-Versuch",
      "syncFailure": "Letzter Fehlergrund",
```

und (en):

```json
      "lastAttempt": "Last sync attempt",
      "syncFailure": "Last failure reason",
```

- [ ] **Step 6: Test laufen lassen und grün sehen**

Run: `npm --prefix web test -- --watch=false --include="src/app/core/emotes/seven-tv-sync-failure.spec.ts"`
Expected: PASS, 6 Tests.

- [ ] **Step 7: Formatieren, linten, committen**

```bash
npm --prefix web run format
npm --prefix web run lint
git add web/src/app/core/emotes/seven-tv-sync-failure.ts \
        web/src/app/core/emotes/seven-tv-sync-failure.spec.ts \
        web/src/app/core/emotes/emote-set-status.model.ts \
        web/src/app/core/admin/admin.model.ts \
        web/public/i18n/de.json web/public/i18n/en.json
git commit -m "feat(web): translate the three 7TV sync failure reasons"
```

**Regel 1: vorher den Nutzer fragen.**

---

### Task 6: Die Nutzungsseite hört auf zu raten

**Files:**
- Modify: `web/src/app/core/emotes/emote-admin.service.spec.ts:56-77`
- Modify: `web/src/app/features/usage-stats/usage-stats-page.ts:246-248`, `:988-1005`, `:1037-1058`
- Modify: `web/src/app/features/usage-stats/usage-stats-page.html:303-312`
- Modify: `web/e2e/support/mocks.ts` (`mockActiveEmoteSet`, ~Zeile 700)
- Modify: `web/e2e/usage-atlas.e2e.spec.ts`

**Interfaces:**
- Consumes: `EmoteSetStatus.syncFailureReason` (Task 5), `sevenTvSyncFailureKey` (Task 5).
- Produces: `usage-stats-page.ts` bekommt `protected readonly syncFailureReason:
  Signal<SevenTvSyncFailureReason | null>` und `protected readonly syncFailureKey =
  sevenTvSyncFailureKey`.

**Warum das Polling abgekürzt werden darf:** `awaitSync` fragt heute bis zu 15-mal alle 2 s, obwohl
schon die **erste** Antwort mit einem Grund beweist, dass nie eine Set-Id kommt. 30 Sekunden
Ladebanner, gefolgt von einem falschen „das Set ist wohl leer". Auch `seventv_unavailable` bricht
ab: eine konkrete, wahre Aussage sofort schlägt eine halbe Minute Warten auf eine falsche. Der
Rückweg ist gedeckt — die Seite abonniert `channel.synced` und ruft dann `refreshSetStatus()`
(`usage-stats-page.ts:656-658`), und ein geglückter Erstsync setzt `HasChanges` (leere → gefüllte
Set-Id), veröffentlicht also genau dieses Ereignis.

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

In `web/src/app/core/emotes/emote-admin.service.spec.ts` den Test `getSetStatus GETs the active-set
endpoint` ersetzen und einen zweiten anfügen:

```ts
  it('getSetStatus GETs the active-set endpoint', () => {
    let status: EmoteSetStatus | undefined;
    service.getSetStatus('sensitron').subscribe((value) => (status = value));

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/active-set');
    expect(req.request.method).toBe('GET');
    req.flush({
      activeEmoteSetId: 'set-1',
      capacity: 1000,
      occupiedSlots: 847,
      trackedSince: '2026-06-12T09:14:00Z',
      syncFailureReason: null,
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
    });

    expect(status).toEqual({
      activeEmoteSetId: 'set-1',
      capacity: 1000,
      occupiedSlots: 847,
      trackedSince: '2026-06-12T09:14:00Z',
      syncFailureReason: null,
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
    });
  });

  it('getSetStatus passes a sync failure reason through untranslated', () => {
    // The code must reach the page verbatim: translation happens exactly once, in the template
    // (Regel 7), and a service that mapped it to prose here would put German into the model.
    let status: EmoteSetStatus | undefined;
    service.getSetStatus('sensitron').subscribe((value) => (status = value));

    httpMock.expectOne('/api/channels/sensitron/emotes/active-set').flush({
      activeEmoteSetId: '',
      capacity: null,
      occupiedSlots: 0,
      trackedSince: '2026-06-12T09:14:00Z',
      syncFailureReason: 'no_active_emote_set',
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
    });

    expect(status?.syncFailureReason).toBe('no_active_emote_set');
  });
```

- [ ] **Step 2: Test laufen lassen und den Fehlschlag sehen**

Run: `npm --prefix web test -- --watch=false --include="src/app/core/emotes/emote-admin.service.spec.ts"`
Expected: FAIL — `toEqual` meldet die zwei fehlenden Schlüssel im ersten Test. (Der zweite ist schon
grün: `HttpClient` reicht unbekannte Felder durch. Er hält die Absicht fest, nicht den Fehlschlag.)

- [ ] **Step 3: Die Seite den Grund lesen lassen**

`web/src/app/features/usage-stats/usage-stats-page.ts` — Import ergänzen:

```ts
import { sevenTvSyncFailureKey } from '../../core/emotes/seven-tv-sync-failure';
```

Nach Zeile 248 (`trackedSince`) einfügen:

```ts
  /** Why the last 7TV sync produced nothing, or null when it worked (or was never attempted). */
  protected readonly syncFailureReason = computed(() => this.setStatus()?.syncFailureReason ?? null);
  protected readonly syncFailureKey = sevenTvSyncFailureKey;
```

In `load()` (Zeile ~988) die Polling-Bedingung schärfen:

```ts
        // An empty id means SevenTvSyncService has not written a set for this channel. Only worth
        // waiting on while there is no reason: with one, the answer is already final — no sync will
        // ever produce an id until the cause is fixed on 7TV, and channel.synced brings us back
        // (see the live subscription in the constructor) the moment it is.
        if (!status.activeEmoteSetId && !status.syncFailureReason) {
          this.awaitSync(channelName, from, to);
        }
```

In `awaitSync` (Zeile ~1037) Abbruchbedingung und Subscriber:

```ts
        // Completes on the first status that settles the question — a set id (the sync landed) or a
        // reason (it cannot land). Running to SYNC_POLL_MAX_ATTEMPTS against a known reason spent
        // 30 seconds to arrive at an answer the first tick already had.
        first((status) => !!status?.activeEmoteSetId || !!status?.syncFailureReason, null),
      )
      .subscribe((status) => {
        this.isAwaitingSync.set(false);
        if (!status) {
          return;
        }

        // Adopted even without a set id: the reason is the whole payload in that case, and the
        // empty state below renders from it.
        this.setStatus.set(status);
        if (status.activeEmoteSetId) {
          this.loadTotals(channelName, from, to);
        }
      });
```

- [ ] **Step 4: Den Empty-State den Grund sagen lassen**

`web/src/app/features/usage-stats/usage-stats-page.html`, den Zweig ab Zeile 306
(`@else if (atlasOrder().length === 0)`) ersetzen:

```html
      } @else if (atlasOrder().length === 0) {
        @if (emotes().length === 0) {
          <!-- Nested inside "no emotes at all", never above the grid: a channel that still has its
               previous set must keep seeing its numbers while 7TV is briefly unreachable. The reason
               replaces the guess, it does not replace the data. -->
          @if (syncFailureReason(); as reason) {
            <app-empty-state
              [title]="syncFailureKey(reason, 'title') | transloco"
              [description]="syncFailureKey(reason, 'hint') | transloco"
            />
          } @else {
            <app-empty-state
              [title]="'usageStats.noActiveEmotes' | transloco"
              [description]="'usageStats.noActiveEmotesHint' | transloco"
            />
          }
        } @else {
          <!-- The set has emotes; the filters hide all of them — offer the way back. -->
          <app-empty-state [title]="'usageStats.noMatches' | transloco">
            <button type="button" appButton="neutral" (click)="usageFilter.reset()">
              {{ 'usageStats.resetFilters' | transloco }}
            </button>
          </app-empty-state>
        }
      } @else {
```

- [ ] **Step 5: Den E2E-Mock um die zwei Felder erweitern**

`web/e2e/support/mocks.ts`, `mockActiveEmoteSet`:

```ts
export async function mockActiveEmoteSet(
  page: Page,
  channelName: string,
  activeEmoteSetId = 'set-1',
  status: {
    capacity?: number | null;
    occupiedSlots?: number;
    trackedSince?: string;
    syncFailureReason?: string | null;
    lastSyncAttemptAtUtc?: string | null;
  } = {},
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/emotes/active-set`, (route) =>
    fulfillJson(route, 200, {
      activeEmoteSetId,
      capacity: status.capacity ?? 1000,
      occupiedSlots: status.occupiedSlots ?? 3,
      trackedSince: status.trackedSince ?? '2026-06-12T09:14:00Z',
      syncFailureReason: status.syncFailureReason ?? null,
      lastSyncAttemptAtUtc: status.lastSyncAttemptAtUtc ?? null,
    }),
  );
}
```

- [ ] **Step 6: Den E2E-Test schreiben**

In `web/e2e/usage-atlas.e2e.spec.ts` ans Ende der Datei anhängen (Struktur wie `openAtlas`, aber mit
eigenem Set-Status, deshalb ohne den Helfer):

```ts
test.describe('a channel without an active 7TV emote set', () => {
  test('names the missing emote set instead of guessing', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockDuplicateEmoteNames(page, 'sensitron');
    // Empty set id *and* a reason: exactly the state issue #32 describes.
    await mockActiveEmoteSet(page, 'sensitron', '', {
      capacity: null,
      occupiedSlots: 0,
      syncFailureReason: 'no_active_emote_set',
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
    });
    await mockUsageTotals(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(
      page.getByText('Dieser Channel hat auf 7TV kein aktives Emote-Set.'),
    ).toBeVisible();
    await expect(page.getByText('Auf 7tv.app lässt sich ein Emote-Set anlegen')).toBeVisible();
    // The poll banner must not appear at all: with a reason in hand there is nothing to wait for,
    // and it used to hold the page for 30 seconds before falling back to the wrong message.
    await expect(page.getByText('Emote-Set wird geladen')).toHaveCount(0);
    await expect(
      page.getByText('Entweder ist das 7TV-Emote-Set leer, oder der erste Sync läuft noch'),
    ).toHaveCount(0);
  });
});
```

- [ ] **Step 7: Vitest und Playwright laufen lassen**

```bash
npm --prefix web test -- --watch=false
```
Expected: PASS.

```bash
# Regel: vorher ein laufendes `dotnet run --project src/EmotePurge.Api` beenden.
npm --prefix web run e2e -- usage-atlas.e2e.spec.ts
```
Expected: PASS, inkl. des neuen Falls.

- [ ] **Step 8: Formatieren, linten, committen**

```bash
npm --prefix web run format
npm --prefix web run lint
git add web/src/app/core/emotes/emote-admin.service.spec.ts \
        web/src/app/features/usage-stats/usage-stats-page.ts \
        web/src/app/features/usage-stats/usage-stats-page.html \
        web/e2e/support/mocks.ts web/e2e/usage-atlas.e2e.spec.ts
git commit -m "feat(web): tell the viewer why a channel shows no emotes"
```

**Regel 1: vorher den Nutzer fragen.**

---

### Task 7: Der Admin sieht den Grund, ohne ihn abzuleiten

**Files:**
- Modify: `web/src/app/features/admin/admin-channel-detail-page.ts` (Template + ein `computed`)
- Modify: `web/src/app/features/admin/admin-channels-page.ts:270-275`
- Modify: `web/src/app/core/admin/admin.service.spec.ts` (Fixtures)
- Modify: `web/e2e/support/mocks.ts` (`MockAdminChannel`, beide Admin-Mocks)
- Modify: `web/e2e/admin-channels.e2e.spec.ts`

**Interfaces:**
- Consumes: `AdminChannel.lastSyncFailureReason`/`.lastSyncAttemptAtUtc` (Task 5),
  `sevenTvSyncFailureKey` (Task 5).
- Produces: nichts, was spätere Tasks brauchen.

**Warum ein eigenes Banner statt eines fünften `verdictKey`:** `verdictKey` rangiert
Worker-Zustände; ein 7TV-Grund ist eine Aussage der *Datenbankseite* und wird oft **zusammen** mit
`degraded` zutreffen (ein Channel ohne Set bekommt nie ein `sevenTvEmoteSetAcknowledged`). Ein
eigenes Banner **über** dem Verdikt erklärt das Verdikt, statt mit ihm um einen Platz zu
konkurrieren — und die Rangfolge bleibt unverändert.

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

In `web/e2e/admin-channels.e2e.spec.ts` innerhalb der bestehenden `test.describe`-Gruppe:

```ts
  test('the drilldown names why a channel never synced', async ({ page }) => {
    await mockAdminChannelList(page, [{ channelName: 'sensitron' }]);
    await mockAdminChannelDetail(page, {
      channelName: 'sensitron',
      lastSyncedAtUtc: null,
      activeEmoteSetId: null,
      lastSyncFailureReason: 'no_active_emote_set',
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
    });

    await page.goto('/admin/channels/sensitron');

    // The sentence that used to be missing entirely: the page showed "kein Sync" and left the
    // admin to guess between four causes.
    await expect(page.getByText('7TV: kein aktives Emote-Set.')).toBeVisible();
    await expect(page.getByText('Letzter Sync-Versuch')).toBeVisible();
  });
```

- [ ] **Step 2: Test laufen lassen und den Fehlschlag sehen**

Run: `npm --prefix web run e2e -- admin-channels.e2e.spec.ts -g "names why a channel never synced"`
Expected: FAIL — TypeScript kennt `lastSyncFailureReason` an `MockAdminChannel` nicht.

- [ ] **Step 3: Die Admin-Mocks erweitern**

`web/e2e/support/mocks.ts` — in `MockAdminChannel` nach `activeEmoteSetCapacity`:

```ts
  lastSyncFailureReason?: string | null;
  lastSyncAttemptAtUtc?: string | null;
```

und in **beiden** Mock-Bodies (`mockAdminChannelList`, `mockAdminChannelDetail`) nach
`trackingResumedAt: null,`:

```ts
        lastSyncFailureReason: c.lastSyncFailureReason ?? null,
        lastSyncAttemptAtUtc: c.lastSyncAttemptAtUtc ?? null,
```

(im Detail-Mock heißt die Variable `channel` statt `c`).

- [ ] **Step 4: Den Drilldown erweitern**

`web/src/app/features/admin/admin-channel-detail-page.ts` — Import:

```ts
import { sevenTvSyncFailureKey } from '../../core/emotes/seven-tv-sync-failure';
```

Im Template **direkt vor** dem `@if (verdictKey(); as verdict)`-Block:

```html
        @if (syncFailureReason(); as reason) {
          <!-- Above the verdict on purpose: a channel with no active 7TV emote set will also read
               as "degraded" (its emote-set subscription can never be acknowledged), and this is the
               sentence that explains that one rather than competing with it. -->
          <app-notice-banner variant="warning">
            {{ syncFailureKey(reason, 'short') | transloco }}
          </app-notice-banner>
        }
```

Und in der Datenbank-`<dl>`, direkt nach der `lastSync`-Zeile, zwei weitere:

```html
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.channelDetail.database.lastAttempt' | transloco }}
              </dt>
              <!-- The counterpart to "letzter erfolgreicher Sync": equal values mean the last
                   attempt worked, a newer attempt next to an older success dates the breakage. -->
              <dd class="text-fg-body">{{ formatDateTime(data.channel.lastSyncAttemptAtUtc) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.channelDetail.database.syncFailure' | transloco }}
              </dt>
              <dd class="text-fg-body">
                @if (syncFailureReason(); as reason) {
                  {{ syncFailureKey(reason, 'short') | transloco }}
                } @else {
                  {{ NO_VALUE }}
                }
              </dd>
            </div>
```

In der Klasse, neben `emoteSetMismatch`:

```ts
  protected readonly syncFailureKey = sevenTvSyncFailureKey;

  /** The database side's own finding, independent of anything the worker reports. */
  protected readonly syncFailureReason = computed(
    () => this.detail()?.channel.lastSyncFailureReason ?? null,
  );
```

- [ ] **Step 5: Die Admin-Liste den Kurzgrund zeigen lassen**

`web/src/app/features/admin/admin-channels-page.ts`, direkt nach dem `stats.lastSync`-Span:

```html
                @if (channel.lastSyncFailureReason; as reason) {
                  <span aria-hidden="true">·</span>
                  <!-- The list is where an admin scans for the odd one out; "letzter Sync: —" alone
                       looked identical for a channel joined a minute ago and one that can never
                       sync at all. -->
                  <span class="text-fg-secondary">
                    {{ syncFailureKey(reason, 'short') | transloco }}
                  </span>
                }
```

und in der Klasse:

```ts
  protected readonly syncFailureKey = sevenTvSyncFailureKey;
```

plus den Import.

- [ ] **Step 6: Die Service-Fixtures nachziehen**

`web/src/app/core/admin/admin.service.spec.ts` — in den drei Channel-Literalen (Zeilen ~180, ~215,
~254) je zwei Felder ergänzen, damit die Fixtures die echte Antwort abbilden:

```ts
        lastSyncFailureReason: null,
        lastSyncAttemptAtUtc: null,
```

Im Detail-Fixture (~Zeile 180) stattdessen den Fall mit Grund abbilden und eine Zusicherung
anhängen:

```ts
        lastSyncFailureReason: 'no_active_emote_set',
        lastSyncAttemptAtUtc: '2026-08-01T12:00:00Z',
```
```ts
    expect(result?.channel.lastSyncFailureReason).toBe('no_active_emote_set');
```

- [ ] **Step 7: Tests laufen lassen und grün sehen**

```bash
npm --prefix web test -- --watch=false
npm --prefix web run e2e -- admin-channels.e2e.spec.ts
```
Expected: beide PASS.

- [ ] **Step 8: Formatieren, linten, committen**

```bash
npm --prefix web run format
npm --prefix web run lint
git add web/src/app/features/admin/admin-channel-detail-page.ts \
        web/src/app/features/admin/admin-channels-page.ts \
        web/src/app/core/admin/admin.service.spec.ts \
        web/e2e/support/mocks.ts web/e2e/admin-channels.e2e.spec.ts
git commit -m "feat(admin): state the 7TV sync failure in the channel drilldown"
```

**Regel 1: vorher den Nutzer fragen.**

---

### Task 8: Live gegen echtes 7TV verifizieren, Entscheidung protokollieren

**Files:**
- Modify: `docs/DECISIONS.md` (**erst hier anfassen** — parallele Session, vorher neu einlesen)

**Interfaces:**
- Consumes: alles aus Task 1–7.
- Produces: nichts.

- [ ] **Step 1: Den Stack lokal hochziehen**

```bash
docker compose up -d postgres redis
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
dotnet run --project src/EmotePurge.Api
# zweite Shell:
dotnet run --project src/EmotePurge.Worker
```

- [ ] **Step 2: Einen realen Twitch-Channel ohne aktives 7TV-Emote-Set finden**

Der Fall muss **echt** sein, nicht simuliert (Regel 16). Kandidaten direkt gegen 7TV prüfen — die
Twitch-User-Id kommt aus 7TVs eigener Suche, dieselbe Abfrage, die `ResolveTwitchUserIdAsync` fährt:

```bash
# 1) Twitch-Id über 7TV auflösen (<login> durch den Kandidaten ersetzen)
curl -s https://7tv.io/v3/gql \
  -H 'content-type: application/json' \
  -d '{"query":"query($q: String!){ users(query:$q){ id username connections { platform username id } } }","variables":{"q":"<login>"}}' \
  | jq '.data.users[].connections[] | select(.platform=="TWITCH")'

# 2) Der entscheidende Test: emote_set ist null → genau Fall 2 der Analyse
curl -s https://7tv.io/v3/users/twitch/<twitch-id> | jq '{user: .user.id, emote_set: .emote_set}'
```

Gesucht ist eine Antwort mit `"emote_set": null` und einer nicht-leeren `user.id`. Wer keinen
fremden Channel findet: ein eigener 7TV-Zweitaccount, bei dem auf 7tv.app **kein** Set als aktiv
gesetzt ist, erzeugt denselben Zustand. Für den Gegentest zu Fall 1 reicht eine Twitch-Id ohne
7TV-Konto — dann antwortet derselbe Aufruf mit **404**.

- [ ] **Step 3: Den Channel joinen und den Worker-Log lesen**

Im Browser `http://localhost:5151/api/auth/twitch/login`, dann den Channel über die App joinen.
Im Worker-Log muss innerhalb einer Minute genau die Zeile stehen, die vorher fehlte:

```
7TV-Account für Twitch-ID <id> hat kein aktives Emote-Set.
7TV-Sync für <channel> ohne Ergebnis: no_active_emote_set.
```

Und — Beweis für die Log-Drosselung — die zweite Zeile darf sich **nicht** jede Minute wiederholen.

- [ ] **Step 4: Die Spalten in Postgres nachsehen**

```bash
docker compose exec postgres psql -U emotepurge -d emotepurge -c \
  'SELECT "ChannelName", "ActiveEmoteSetId", "LastSyncedAtUtc", "LastSyncAttemptAtUtc", "LastSyncFailureReason" FROM "Channels" ORDER BY "LastSyncAttemptAtUtc" DESC NULLS LAST LIMIT 5;'
```

Erwartet für den Testchannel: `LastSyncFailureReason = no_active_emote_set`,
`LastSyncAttemptAtUtc` frisch, `LastSyncedAtUtc` **null**, `ActiveEmoteSetId` leer.

- [ ] **Step 5: Die zwei Oberflächen ansehen**

```bash
npm --prefix web start
```

- `http://localhost:4200/channels/<channel>/usage-stats` → **„Dieser Channel hat auf 7TV kein
  aktives Emote-Set."** plus Hinweistext. Das Ladebanner „Emote-Set wird geladen" darf **nicht**
  erscheinen; die Seite steht sofort.
- `http://localhost:4200/admin/channels/<channel>` → Warnbanner „7TV: kein aktives Emote-Set.",
  „Letzter Sync-Versuch" gefüllt, „Letzter erfolgreicher Sync" auf `—`.

- [ ] **Step 6: Den Reset live beweisen — der Fall, der sonst vergessen wird**

Auf 7tv.app für denselben Channel ein Emote-Set aktiv schalten. Innerhalb einer Minute muss:

- der Worker den Sync melden (`7TV-Set <id> für <channel> synchronisiert.`),
- die **offene** Nutzungsseite von selbst umschlagen (`channel.synced` → `refreshSetStatus()`),
- und die Datenbank sauber sein:

```bash
docker compose exec postgres psql -U emotepurge -d emotepurge -c \
  'SELECT "LastSyncFailureReason", "LastSyncedAtUtc" = "LastSyncAttemptAtUtc" AS in_sync FROM "Channels" WHERE "ChannelName" = ''<channel>'';'
```

Erwartet: `LastSyncFailureReason` ist **NULL**, `in_sync` ist **t**.

- [ ] **Step 7: Den Entscheidungseintrag schreiben**

⚠️ **`docs/DECISIONS.md` vorher neu einlesen** — eine parallele Session hat daran gearbeitet. Der
Eintrag kommt **direkt unter** den bestehenden 2026-08-29-Eintrag („Emote-Bilder werden als
4x-Standbild gespeichert"), weil innerhalb eines Datums chronologisch sortiert wird.

```markdown
### 2026-08-29 — Ein Sync, der nichts liefert, sagt warum

**Betrifft:** `src/EmotePurge.Core/SevenTv/{SevenTvModels.cs,ISevenTvApiClient.cs}`, `src/EmotePurge.Core/Services/SevenTvSyncFailureReasons.cs`, `src/EmotePurge.Core/Entities/Channel.cs`, `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs`, `src/EmotePurge.Infrastructure/Services/{SevenTvSyncService,EmoteSetStatusService,AdminChannelQueryService}.cs`, `src/EmotePurge.Infrastructure/Migrations/*_AddChannelSyncFailureReason.cs`, `web/src/app/core/emotes/seven-tv-sync-failure.ts`, `web/src/app/features/usage-stats/usage-stats-page.{ts,html}`, `web/src/app/features/admin/admin-channel-detail-page.ts`, `web/public/i18n/*.json`

Issue #32: Ein Channel mit 7TV-Konto, aber ohne aktives Emote-Set sah im Frontend aus wie jeder andere leere Channel — „entweder ist das 7TV-Emote-Set leer, oder der erste Sync läuft noch". Auch der Admin-Drilldown sagte nur, dass kein Sync stattgefunden habe.

**Vier Zustände, ein `null`.** `SevenTvApiClient` konnte auf vier fachlich verschiedene Arten nichts liefern: kein 7TV-Account (404, `LogInformation`), Account ohne aktives Set (`emote_set` fehlt im JSON — **ohne jedes Log**), API-/Netzwerkfehler (`LogWarning`), und „noch nie gesynct", das von den drei anderen nicht zu unterscheiden war. `SevenTvSyncService` reichte alle vier als `null` weiter, `SevenTvPeriodicResyncWorker` hatte zu `if (result is not null)` keinen `else`-Zweig — es wurde **nichts** geschrieben, auch `LastSyncedAtUtc` nicht. Die Oberfläche konnte also gar nichts anderes sagen als eine Vermutung mit „entweder … oder".

**Ergebnistyp statt `null`.** `GetChannelStateForTwitchUserAsync` und `ResolveTwitchUserIdAsync` geben jetzt `SevenTvChannelStateResult` bzw. `SevenTvTwitchUserIdResult` zurück, beide nie `null`, beide mit einem `SevenTvLookupStatus` (`Ok`, `NoSevenTvAccount`, `NoActiveEmoteSet`, `Unavailable`). Der stille Zweig hat sein Log bekommen. Ein zusätzlicher Fall ist dabei aufgefallen und getrennt worden: eine 200er-Antwort mit leerem Body ist kaputt, nicht „kein Set" — sie zählt als `Unavailable`, damit niemand aufgefordert wird, etwas zu reparieren, das in Ordnung ist.

**Enum innen, String außen.** Der Kontrollfluss im Backend läuft über das Enum (vollständiges `switch`), die API liefert die drei Codes `no_seventv_account`, `no_active_emote_set`, `seventv_unavailable` als Strings — dieselbe Begründung wie bei `ChannelLiveStates` und `ApiErrorCodes`: der Wert auf der Leitung ist der Wert, der im Code steht, unabhängig von Serializer-Einstellungen. Dazu kommt hier, dass das Enum ein `Ok` trägt, das nie auf die Leitung darf. Genau eine Abbildungsfunktion (`SevenTvSyncFailureReasons.FromStatus`) verbindet beide; ein unbekannter Status wirft, statt still zu „kein Fehler" zu werden.

**Zwei Spalten, nicht eine.** `Channel.LastSyncFailureReason` **und** `Channel.LastSyncAttemptAtUtc`. Der Grund allein wäre wertlos: ohne Versuchszeitpunkt liest sich ein drei Tage alter Grund wie eine Aussage über jetzt. Das Paar mit dem bestehenden `LastSyncedAtUtc` ist die eigentliche Diagnose — gleiche Werte heißen „der letzte Versuch hat geklappt", ein neuer Versuch neben einem alten Erfolg datiert den Ausfall. `LastSyncedAtUtc` bleibt dabei streng „letzter **erfolgreicher** Sync"; ein Fehlversuch bewegt es nicht.

**Der Reset ist der Teil, der sonst vergessen wird.** `LastSyncFailureReason = null` steht im selben Block wie `LastSyncedAtUtc = syncedAt`, nicht an einer zweiten Stelle: ein Channel, der auf 7TV ein Set aktiviert, muss sofort aufhören, als kaputt beschrieben zu werden — sonst überlebt der Empty-State das Problem, das er beschreibt. Live verifiziert, in beide Richtungen.

**Ein Fehlschlag löscht nichts.** Weder `ActiveEmoteSetId` noch Emote-Zeilen werden angefasst — dieselbe Asymmetrie, die schon die Empty-Set-Schutzlogik (S3-12) begründet: ein 7TV-Ausfall darf nicht das Mass-Delete-Panel wegnehmen oder ein ganzes Set archivieren. Bekannte Folge, bewusst in Kauf genommen: ein Channel, der sein Set *nachträglich* deaktiviert, behält seine alten Emote-Zeilen und zeigt weiter sein Raster; der Grund steht dann im Admin-Drilldown, aber der Nutzer sieht keinen Empty-State, weil es nichts Leeres zu zeigen gibt. Die Alternative wäre ein Wipe auf Verdacht.

**Die Schutzlogik gegen leere Sets schreibt bewusst gar nichts** — weder Grund noch Versuchszeit. Sie trifft keine Aussage über den Channel, sie verweigert die Aktion; der nächste Tick entscheidet. Ein Versuchszeitstempel dort würde eine Reconciliation behaupten, die nie stattgefunden hat.

**Der Worker bleibt unverändert.** Die Persistenz sitzt in `SevenTvSyncService`, wo der `AppDbContext` ohnehin liegt — der fehlende `else`-Zweig in `ResyncOnceAsync` wird damit gegenstandslos statt nachgerüstet. `SyncChannelAsync` behält seine Signatur, also auch `Worker.cs` und `SevenTvEventClient`.

**Das Polling der Nutzungsseite bricht jetzt ab.** `awaitSync` lief bis zu 15 Ticks à 2 s, obwohl schon die erste Antwort mit einem Grund beweist, dass nie eine Set-Id kommt: 30 Sekunden Ladebanner, gefolgt von einer falschen Aussage. Auch `seventv_unavailable` bricht ab — eine wahre Aussage sofort schlägt eine halbe Minute Warten auf eine falsche. Der Rückweg ist gedeckt: die Seite hört auf `channel.synced` und lädt den Set-Status neu, und ein geglückter Erstsync setzt `HasChanges` (leere → gefüllte Set-Id), veröffentlicht also genau dieses Ereignis.

**Der alte Hinweistext ist mit weggefallen.** `usageStats.noActiveEmotesHint` sagte „entweder ist das 7TV-Emote-Set leer, oder der erste Sync läuft noch" — genau die Vermutung, die diese Änderung durch eine Aussage ersetzt. Er heißt jetzt nur noch, was er sicher weiß: das aktive Set enthält derzeit keine Emotes.

**Migration** `AddChannelSyncFailureReason`, zwei nullable Spalten, additiv — vom alten Image ignoriert und deshalb wie üblich **vor** dem Deploy von Hand nachzuziehen.
```

- [ ] **Step 8: Alles formatieren und die volle Suite fahren**

```bash
dotnet format EmotePurge.slnx
npm --prefix web run format
npm --prefix web run lint
dotnet build EmotePurge.slnx
dotnet test EmotePurge.slnx
npm --prefix web test -- --watch=false
# vorher `dotnet run --project src/EmotePurge.Api` beenden:
npm --prefix web run e2e
```
Expected: alles grün. `git status` darf danach nur `docs/DECISIONS.md` als geändert zeigen.

- [ ] **Step 9: Committen**

```bash
git add docs/DECISIONS.md
git commit -m "docs: record why a 7TV sync now says what it failed at"
```

**Regel 1: vorher den Nutzer fragen.**

- [ ] **Step 10: Die Prod-Migration einplanen (nicht ausführen)**

Dem Nutzer als Hinweis mitgeben, **nicht** selbst tun (Regel 17, Passwort liegt nur auf dem VPS):
Die Migration läuft in Produktion **manuell und vor** dem Deploy der neuen Images. Additiv, also
ignoriert das noch laufende alte Image die zwei neuen Spalten klaglos; umgekehrt liefe die neue Api
gegen fehlende Spalten.

```bash
ssh -N -L 15432:127.0.0.1:5433 <VPS-USER>@<VPS-HOST>
# zweite Shell:
dotnet ef migrations list  --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'
dotnet ef database update  --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'
dotnet ef migrations list  --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'
```

Erst `list` (mehr `(Pending)` als die eine erwartete heißt: Prod hängt Runden zurück — dann erst
durchsehen), dann `update`, dann `list` zur Gegenprobe.

---

## Selbstprüfung

**Abdeckung gegen den Auftrag.** Ursache im Client erhalten → Task 1+3. Am Channel persistiert
(Feld + Migration) → Task 2+3. Admin-Drilldown → Task 4+7. Empty-State der Nutzerseite → Task 5+6.
Rückgabewert als Ergebnistyp statt `null`, Enum in Core ohne Paketverweis → Task 1 (durch
`CoreAssemblyReferenceTests` gedeckt). Felder-Frage (Grund **und** Versuchszeitstempel) und Reset →
Task 2/3, mit eigenem Test `SyncChannel_Success_ClearsAPreviousReason`. Polling-Abkürzung → Task 6.
Regel 4 (kein neuer DB-Zugriff aus Handlern) → es entsteht kein neuer Service, nur Felder an zweien.
Regel 7 (Codes + beide Locales) → Task 5, per Spec erzwungen. Regel 11 → ein `Unit/`-Test (reine
Abbildung) und drei `Integration/`-Dateien (alle berühren `AppDbContext`). Regel 12 → ein
co-located Spec für die neue reine Utility, **keine** isolierten Komponententests. Regel 16 →
Task 8 mit realem Channel ohne aktives Set. Regel 3 → Task 8, ein Commit mit DECISIONS-Eintrag.
Migration manuell vor Deploy → Task 8 Step 10. Empty-Set-Schutzlogik unangetastet → Nicht-Ziel 3,
plus der Test `SyncChannel_ImplausibleEmptyLiveSet_TouchesNeitherReasonNorAttempt`.

**Typkonsistenz.** `SevenTvLookupStatus` / `SevenTvChannelStateResult` / `SevenTvTwitchUserIdResult`
werden in Task 1 definiert und in Task 3 unter genau diesen Namen verwendet.
`SevenTvSyncFailureReasons.FromStatus` heißt in Task 1, 3 und im Test gleich.
`Channel.LastSyncFailureReason`/`LastSyncAttemptAtUtc` (Task 2) heißen in Task 3, 4 und in der SQL-
Prüfung gleich. Die Wire-Codes (`no_seventv_account`, `no_active_emote_set`, `seventv_unavailable`)
stehen wortgleich in C#-Konstanten, Tests, TS-Union, Locale-Schlüsseln und E2E-Mocks. Das JSON-Feld
heißt auf der Nutzerseite `syncFailureReason` (aus `EmoteSetStatusDto.SyncFailureReason`) und auf
der Admin-Seite `lastSyncFailureReason` (aus `AdminChannelDto.LastSyncFailureReason`) — **bewusst
verschieden**, weil die Admin-Zeile das Feld neben `lastSyncedAtUtc` liest und die Nutzerseite kein
zweites „last" braucht; beide sind in Task 5 im jeweiligen Frontend-Modell so benannt.
`sevenTvSyncFailureKey(reason, part)` hat in Task 5, 6 und 7 dieselbe Signatur.
