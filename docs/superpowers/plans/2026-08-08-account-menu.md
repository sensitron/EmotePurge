# Account-Menü im Header — Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Die sechs Dauer-Elemente rechts im App-Header werden zu einem einzigen Trigger — einem runden Twitch-Profilbild mit Dropdown, das Konto, Navigation, Darstellung, Sprache und Logout trägt.

**Architecture:** Drei neue Angular-Bausteine unter `web/src/app/shared/ui/` (`avatar.ts`, `display-preferences.ts`, `account-menu.ts`) ersetzen `theme-menu.ts`, `language-switcher.ts` und die handgebaute Mobile-Disclosure der Shell. Das Profilbild reist als Claim im Session-Cookie: Twitch liefert `profile_image_url` in der Helix-Antwort, die beim Login ohnehin geholt wird — kein DB-Feld, keine Migration. Das Panel benutzt das bestehende `Popover`-Primitive (Dismiss + Escape), der Host besitzt Sichtbarkeit und Fokusrückgabe.

**Tech Stack:** Angular 22 (Standalone, Signals, zoneless), Tailwind v4, Transloco, `@angular/common` `NgOptimizedImage` · ASP.NET Core Minimal API, Cookie-Auth · xUnit + NSubstitute · Vitest · Playwright

Spec: [`docs/superpowers/specs/2026-08-08-account-menu-design.md`](../specs/2026-08-08-account-menu-design.md)

## Global Constraints

- **Regel 1: Vor jedem `git commit` erst den Nutzer fragen.** Die Commit-Schritte in diesem Plan sind vorbereitet, nicht freigegeben.
- **Regel 2: Conventional Commits**, englisch, mehrere logisch getrennte Commits.
- **Regel 3:** Der Commit, der eine Konvention/einen Vertrag ändert, enthält seinen `docs/DECISIONS.md`-Eintrag im selben Commit (Task 9).
- **Regel 6:** Minimal API, keine Controller. Endpoints in `Endpoints/*.cs`.
- **Regel 11:** Neue *Logik* in `EmotePurge.Infrastructure`/`Core` bekommt einen Test in `tests/EmotePurge.Infrastructure.Tests`. Reine Feld-Durchreicher nicht.
- **Regel 12:** Neue Services/Guards/**reine Utilities** unter `web/src/app/core/` + `shared/` bekommen einen co-located `*.spec.ts`. **Isolierte Komponententests sind ausdrücklich nicht Teil der Konvention** — `Avatar`, `DisplayPreferences` und `AccountMenu` bekommen deshalb *keine* Vitest-Specs; verifiziert wird live im Browser. Neue Vitest-Arbeit fällt in diesem Plan nur bei `AuthService` an.
- **Regel 13:** Nie ein required Signal-Input im Konstruktor lesen.
- **Regel 16:** Backend-Änderungen vor dem Commit live gegen echte Postgres/Redis/Twitch verifizieren, nicht nur `dotnet build`.
- **Regel 18:** `npm --prefix web run format` und `dotnet format EmotePurge.slnx` vor dem Commit.
- **Sprache:** Bezeichner, Typen, Kommentare in neuem Code **englisch**; Log-/`throw`-Messages **deutsch**; Projektdoku **deutsch**; Commit-Messages **englisch**.
- **Farbe nur aus Tokens** (`web/src/styles.css`): keine Paletten-Utility unter `web/src/app/` — `npm run lint` erzwingt das.
- **Höhenvertrag §8.5 bleibt unangetastet:** Header `h-14`, Tab-Leisten `top-14`, Filter-Toolbars `top-24`.
- **E2E-Suite läuft nur, wenn auf `:5151` keine Api lauscht.** Vorher `dotnet run` beenden.

## Abweichungen von der Spec (bewusst, mit Begründung)

Vier Punkte, an denen dieser Plan von der Spec abweicht. Wer die Spec neben den Plan legt, soll die Unterschiede benannt vorfinden statt sie zu suchen.

1. **`ThemeIcon` wird gelöscht, nicht umgezogen.** Die Spec sagt „bleibt und zieht nach `shared/ui/theme-icon.ts`". Nach dem Umbau rendert sie aber niemand mehr: `SegmentedControl` nimmt nur Text-Labels, und das war die einzige Verwendung außerhalb von `theme-menu.ts`. Eine Komponente, die nichts importiert, ist toter Code, den kein Linter meldet. Die Datei ist über die Historie von `theme-menu.ts` jederzeit zurückholbar, falls die Theme-Zeile je Icons bekommt. **Wer das anders will, sagt es im Review — es ist eine Löschung, kein Detail.**
2. **Kein `aria-controls` am Trigger, stattdessen `aria-haspopup="dialog"`.** Die Spec listet `aria-controls`. Das Panel existiert im geschlossenen Zustand aber gar nicht im DOM (`@if`), und ein `aria-controls` auf eine nicht auflösbare IDREF ist ungültiges ARIA. `theme-menu.ts` und `date-range-menu.ts` machen es heute schon ohne. `aria-expanded` + `aria-haspopup` + `aria-label` tragen die Semantik vollständig.
3. **Der URL-Umschreiber landet in `EmotePurge.Core/Twitch/`, nicht als Inline-Ausdruck in `AuthEndpoints.cs`.** Die Spec verlangt den Test in `tests/EmotePurge.Infrastructure.Tests/Unit/` — das Testprojekt sieht `Core` (transitiv über `Infrastructure`), aber nicht `Api`. Als reine String-Funktion verletzt der Helfer Cores BCL-only-Regel nicht. Aufgerufen wird er weiterhin genau dort, wo die Spec ihn haben will: beim Setzen des Claims.
4. **Die NG0913-Begründung stimmt so nicht — der Rewrite bleibt trotzdem.** `NgOptimizedImage` warnt erst, wenn die intrinsische Breite die gerenderte (mal DPR) um mehr als 1000 px übersteigt; 300 gegen 64 löst das nicht aus. Der Rewrite auf `-70x70` bleibt, aber aus dem tragfähigen Grund: ein 300×300-PNG statt eines 70×70 auf jedem Seitenaufruf ist Payload, den niemand sieht. Steht so in Task 1.

## Offen gelassene Entscheidungen

Zwei Punkte hat der Betreiber ausdrücklich vertagt. Der Plan baut die Vorgabe, beide sind danach in einer Zeile änderbar:

- **„Meine Abstimmungen" liegt im Menü** (Spec-Vorgabe). Draußen sichtbar wäre möglich, verlangt aber das kürzere Label „Abstimmungen", weil auf 360 px sonst Wordmark + Link + 44-px-Trigger nicht in eine Zeile passen.
- **Der ausgeloggte Trigger ist ein unbeschriftetes Zahnrad** (Variante 1 der Spec). Entschieden wird am gebauten Zustand, nicht am Entwurf.

## Dateistruktur

**Neu:**

| Datei | Verantwortung |
|---|---|
| `src/EmotePurge.Core/Twitch/TwitchProfileImage.cs` | Eine reine String-Funktion: Twitch-Avatar-URL auf Anzeigegröße bringen |
| `tests/EmotePurge.Infrastructure.Tests/Unit/TwitchProfileImageTests.cs` | deren Test, inkl. „Muster passt nicht" |
| `web/src/app/shared/ui/avatar.ts` | Runder Bildträger mit Monogramm-Rückfall und reserviertem Platz |
| `web/src/app/shared/ui/display-preferences.ts` | Der Block „Darstellung" + „Sprache" — nach diesem Umbau der einzige Ort im Repo, an dem diese beiden Controls existieren |
| `web/src/app/shared/ui/account-menu.ts` | Trigger + Panel, beide Auth-Zustände |

**Geändert:**

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Infrastructure/Twitch/TwitchApiDtos.cs` | `ProfileImageUrl` am `TwitchUserDto` |
| `src/EmotePurge.Core/Twitch/TwitchModels.cs` | viertes, nullable Feld am `TwitchUserInfo` |
| `src/EmotePurge.Infrastructure/Twitch/TwitchHelixClient.cs` | durchreichen |
| `src/EmotePurge.Api/Auth/TwitchClaimTypes.cs` | `twitch:profile_image` |
| `src/EmotePurge.Api/Endpoints/AuthEndpoints.cs` | Claim setzen (nur wenn nicht leer), in `/me` projizieren |
| `src/EmotePurge.Api/Program.cs` | `https://static-cdn.jtvnw.net` in `img-src` |
| `web/src/app/core/auth/auth.model.ts` | `profileImageUrl: string \| null` |
| `web/src/app/core/auth/auth.service.ts` | `isResolved` öffentlich machen |
| `web/src/app/core/auth/auth.service.spec.ts` | Fixture + ein Test für `isResolved` |
| `web/src/app/shared/ui/segmented-control.ts` | additives `size`-Input |
| `web/src/app/features/shell/app-shell.ts` | Burger, Disclosure, Host-Listener, vier Handler raus; ein `<app-account-menu/>` rein |
| `web/src/app/features/landing/landing-page.html` + `.ts` | dito |
| `web/src/app/features/login/login-page.ts` | dito |
| `web/public/i18n/de.json`, `en.json` | neue Keys, `shell.menu` raus |
| `web/e2e/support/mocks.ts` | `AUTH_USER` bekommt `profileImageUrl` |
| `web/e2e/theme.spec.ts` | drei Fälle über das neue Menü |
| `web/e2e/channel-workspace.e2e.spec.ts` | Logout liegt jetzt im Panel |
| `web/e2e/audit/ui-audit.audit.ts` | Kommentar am `overview-worker-stale`-Shot |
| `docs/UI-Designsprache.md` | drei Stellen (§2.0, z-Leiter, Primitives-Liste) |
| `docs/DECISIONS.md` | ein Eintrag |

**Gelöscht:** `web/src/app/shared/ui/theme-menu.ts` (samt `ThemeIcon`, s. Abweichung 1) · `web/src/app/shared/i18n/language-switcher.ts` (danach ist `web/src/app/shared/i18n/` leer und verschwindet mit)

---

### Task 1: Twitch-Avatar-URL auf Anzeigegröße bringen

Reine String-Logik, deshalb der einzige Backend-Teil mit eigenem Test (Regel 11). Twitch liefert `…-profile_image-300x300.png`; ein 300er-Bild in einem 32-px-Kasten ist Payload, den niemand sieht. 70 px deckt 32 px bei DPR 2.

**Files:**
- Create: `src/EmotePurge.Core/Twitch/TwitchProfileImage.cs`
- Test: `tests/EmotePurge.Infrastructure.Tests/Unit/TwitchProfileImageTests.cs`

**Interfaces:**
- Consumes: nichts
- Produces: `EmotePurge.Core.Twitch.TwitchProfileImage.ToAvatarSize(string url) -> string` (statisch, nicht-null Ein- und Ausgabe)

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

Datei `tests/EmotePurge.Infrastructure.Tests/Unit/TwitchProfileImageTests.cs`:

```csharp
using EmotePurge.Core.Twitch;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free: pure string logic. This is the one place where anything in this codebase makes an
// assumption about the shape of a Twitch CDN URL, which is exactly why it has a test that pins the
// fallback: an unrecognised shape must pass through untouched rather than break the avatar.
public class TwitchProfileImageTests
{
    [Fact]
    public void ToAvatarSize_ReplacesTheDefaultSizeMarker()
    {
        const string url = "https://static-cdn.jtvnw.net/jtv_user_pictures/abc-profile_image-300x300.png";

        Assert.Equal(
            "https://static-cdn.jtvnw.net/jtv_user_pictures/abc-profile_image-70x70.png",
            TwitchProfileImage.ToAvatarSize(url));
    }

    [Fact]
    public void ToAvatarSize_LeavesAnUnknownShapeUntouched()
    {
        // If Twitch ever changes the URL form, the avatar must still load — just larger than needed.
        const string url = "https://static-cdn.jtvnw.net/user-default-pictures-uv/some-guid.png";

        Assert.Equal(url, TwitchProfileImage.ToAvatarSize(url));
    }

    [Fact]
    public void ToAvatarSize_IsIdempotent()
    {
        const string url = "https://static-cdn.jtvnw.net/jtv_user_pictures/abc-profile_image-70x70.png";

        Assert.Equal(url, TwitchProfileImage.ToAvatarSize(url));
    }
}
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag prüfen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests --filter FullyQualifiedName~TwitchProfileImageTests`
Expected: Compile-Fehler `CS0103` / `The name 'TwitchProfileImage' does not exist`

- [ ] **Step 3: Die minimale Implementierung schreiben**

Datei `src/EmotePurge.Core/Twitch/TwitchProfileImage.cs`:

```csharp
namespace EmotePurge.Core.Twitch;

/// <summary>
/// Twitch serves avatars at 300x300 and encodes the size in the file name. The header renders them
/// at 32 CSS px, so the default costs roughly an order of magnitude more bytes than it can show, on
/// every page load. 70 px covers 32 px at a device pixel ratio of 2.
/// </summary>
public static class TwitchProfileImage
{
    private const string DefaultSizeMarker = "-300x300";
    private const string AvatarSizeMarker = "-70x70";

    /// <summary>
    /// Guarded on purpose: <see cref="string.Replace(string, string, StringComparison)"/> returns
    /// the input unchanged when the marker is absent, so an unrecognised URL form falls through
    /// softly instead of breaking. This is the only assumption this codebase makes about the shape
    /// of a Twitch CDN URL.
    /// </summary>
    public static string ToAvatarSize(string url) =>
        url.Replace(DefaultSizeMarker, AvatarSizeMarker, StringComparison.Ordinal);
}
```

- [ ] **Step 4: Test laufen lassen, Erfolg prüfen**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests --filter FullyQualifiedName~TwitchProfileImageTests`
Expected: PASS (3 Tests)

- [ ] **Step 5: Prüfen, dass Core sauber geblieben ist**

Run: `dotnet test tests/EmotePurge.Infrastructure.Tests --filter FullyQualifiedName~CoreAssemblyReferenceTests`
Expected: PASS — `EmotePurge.Core` hat weiterhin 0 `PackageReference` und 0 `ProjectReference`.

- [ ] **Step 6: Formatieren und committen**

```bash
dotnet format EmotePurge.slnx
git add src/EmotePurge.Core/Twitch/TwitchProfileImage.cs tests/EmotePurge.Infrastructure.Tests/Unit/TwitchProfileImageTests.cs
git commit -m "feat(core): serve Twitch avatars at the size we actually render"
```

---

### Task 2: Die Claim-Kette — vom Helix-DTO bis ins Frontend-Modell

Ein Feld reist durch sechs Backend-Dateien und landet als siebtes im TypeScript-Modell. Keine Entscheidungslogik, keine Migration, kein neuer Test außer dem aus Task 1 — deshalb ein Task statt sieben.

`/me` bleibt DB- und HTTP-frei, wie es der Kommentar auf `AuthEndpoints.cs:119-121` zusichert. Die `User`-Entität wird **nicht** angefasst.

**Files:**
- Modify: `src/EmotePurge.Infrastructure/Twitch/TwitchApiDtos.cs:18-23`
- Modify: `src/EmotePurge.Core/Twitch/TwitchModels.cs:28`
- Modify: `src/EmotePurge.Infrastructure/Twitch/TwitchHelixClient.cs:30`
- Modify: `src/EmotePurge.Api/Auth/TwitchClaimTypes.cs`
- Modify: `src/EmotePurge.Api/Endpoints/AuthEndpoints.cs:91-103` und `:117-132`
- Modify: `src/EmotePurge.Api/Program.cs:206`
- Modify: `web/src/app/core/auth/auth.model.ts`
- Modify: `web/src/app/core/auth/auth.service.spec.ts:10-16`
- Modify: `web/e2e/support/mocks.ts:3-10`

**Interfaces:**
- Consumes: `TwitchProfileImage.ToAvatarSize(string) -> string` aus Task 1
- Produces:
  - `TwitchUserInfo(string Id, string Login, string DisplayName, string? ProfileImageUrl = null)`
  - `TwitchClaimTypes.ProfileImageUrl = "twitch:profile_image"`
  - `GET /api/auth/me` liefert zusätzlich `profileImageUrl: string | null`
  - TypeScript: `AuthUser.profileImageUrl: string | null`

- [ ] **Step 1: Das DTO-Feld ergänzen**

In `src/EmotePurge.Infrastructure/Twitch/TwitchApiDtos.cs`, `TwitchUserDto`:

```csharp
internal sealed class TwitchUserDto
{
    public string Id { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    // Twitch sends "profile_image_url"; the SnakeCase naming policy in TwitchJsonOptions maps it.
    public string ProfileImageUrl { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Das Core-Modell erweitern**

In `src/EmotePurge.Core/Twitch/TwitchModels.cs`, Zeile 28 ersetzen:

```csharp
// ProfileImageUrl is nullable because it is optional to us, not to Twitch: an account without a
// custom picture still gets a default URL, but a session created before this field existed carries
// no claim for it. The avatar falls back to a monogram in that case.
public record TwitchUserInfo(string Id, string Login, string DisplayName, string? ProfileImageUrl = null);
```

- [ ] **Step 3: Im Helix-Client durchreichen**

In `src/EmotePurge.Infrastructure/Twitch/TwitchHelixClient.cs`, Zeile 30 ersetzen:

```csharp
            return user is null ? null : new TwitchUserInfo(user.Id, user.Login, user.DisplayName, user.ProfileImageUrl);
```

- [ ] **Step 4: Den Claim-Typ ergänzen**

In `src/EmotePurge.Api/Auth/TwitchClaimTypes.cs`, nach `TokenExpiresAtUtc`:

```csharp
    // Carried in the session cookie rather than a User column: the avatar follows the login, and a
    // DB field would need a migration plus a refresh story for a picture nobody has to have fresh.
    public const string ProfileImageUrl = "twitch:profile_image";
```

- [ ] **Step 5: Den Claim beim Login setzen**

In `src/EmotePurge.Api/Endpoints/AuthEndpoints.cs`, direkt **nach** dem `var claims = new List<Claim> { … };`-Block (also nach der schließenden `};` auf Zeile 103) einfügen:

```csharp
            // Conditionally, because Claim's constructor rejects a null value — and an account
            // Twitch answered for without a picture URL is a legitimate outcome, not an error.
            if (!string.IsNullOrEmpty(userInfo.ProfileImageUrl))
            {
                claims.Add(new Claim(TwitchClaimTypes.ProfileImageUrl, TwitchProfileImage.ToAvatarSize(userInfo.ProfileImageUrl)));
            }
```

`EmotePurge.Core.Twitch` ist auf Zeile 7 bereits eingebunden — kein neues `using`.

- [ ] **Step 6: In `/me` projizieren**

In derselben Datei, im `/me`-Handler, nach der `displayName`-Zeile:

```csharp
                profileImageUrl = user.FindFirstValue(TwitchClaimTypes.ProfileImageUrl),
```

`FindFirstValue` liefert `null`, wenn der Claim fehlt — genau das, was eine vor dem Deploy erzeugte Session braucht.

- [ ] **Step 7: Die CSP öffnen**

In `src/EmotePurge.Api/Program.cs`, Zeile 206 ersetzen:

```csharp
        "img-src 'self' data: https://*.7tv.app https://7tv.io https://static-cdn.jtvnw.net; " +
```

Und den Kommentar auf `:178-179` nachziehen, damit die Begründung die Liste weiter erklärt:

```csharp
// purpose, s. CLAUDE.md "Zero-Knowledge für Schreib-Tokens"), img-src covers the 7TV CDN that
// serves emote preview images embedded via Emote.ImageUrl, plus Twitch's own CDN for the account
// menu's avatar. Without that second host no picture loads at all, whatever the claim says.
```

**Ohne diesen Schritt lädt kein einziges Bild**, unabhängig davon, ob die URL im DTO ankommt.

- [ ] **Step 8: Backend bauen und die Api-Filter-Matrix prüfen**

```bash
dotnet build EmotePurge.slnx
dotnet test tests/EmotePurge.Api.Tests
```

Expected: Build grün, alle Api-Tests grün. `AuthFilterMatrixTests.cs:68` deckt `/me` nur auf 401 ab und prüft das Response-Shape nicht — hier ist nichts nachzuziehen.

- [ ] **Step 9: Das Frontend-Modell erweitern**

`web/src/app/core/auth/auth.model.ts`:

```typescript
export interface AuthUser {
  twitchUserId: string;
  login: string;
  displayName: string;
  tokenExpiresAtUtc: string;
  isGlobalAdmin: boolean;
  /** Null for a session created before this claim existed — the avatar falls back to a monogram. */
  profileImageUrl: string | null;
}
```

- [ ] **Step 10: Beide Fixtures nachziehen**

`web/src/app/core/auth/auth.service.spec.ts`, im `USER`-Objekt:

```typescript
const USER: AuthUser = {
  twitchUserId: '123',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2026-07-28T00:00:00Z',
  isGlobalAdmin: false,
  profileImageUrl: 'https://static-cdn.jtvnw.net/jtv_user_pictures/abc-profile_image-70x70.png',
};
```

`web/e2e/support/mocks.ts`, im `AUTH_USER`-Objekt (die Playwright-Läufe haben keinen Netzzugang zum Twitch-CDN, deshalb hier bewusst `null` — der Monogramm-Pfad ist der, den die Suite sehen soll):

```typescript
export const AUTH_USER = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2099-01-01T00:00:00Z',
  // Non-admin by default — admin flows pass { ...AUTH_USER, isGlobalAdmin: true }.
  isGlobalAdmin: false,
  // Null on purpose: the e2e run has no route to Twitch's CDN, so the monogram fallback is both
  // the honest and the only stable thing to assert against.
  profileImageUrl: null as string | null,
};
```

- [ ] **Step 11: Frontend-Typen prüfen**

Run: `npm --prefix web run build`
Expected: kompiliert. (Noch rendert nichts das neue Feld — das ist ab Task 4 der Fall.)

- [ ] **Step 12: Live verifizieren (Regel 16)**

```bash
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api
```

Dann im Browser `http://localhost:5151/api/auth/twitch/login` durchlaufen und danach `http://localhost:5151/api/auth/me` aufrufen. Erwartet: `profileImageUrl` steht in der Antwort und endet auf `-70x70.png`. Die URL im Browser öffnen — das Bild muss laden. **Kein `dotnet build` als Ersatz:** ob die SnakeCase-Policy `profile_image_url` wirklich trifft, sagt nur eine echte Helix-Antwort.

Danach `dotnet run` beenden — sonst fällt später die E2E-Suite reihenweise durch (s. Global Constraints).

- [ ] **Step 13: Formatieren und committen**

```bash
dotnet format EmotePurge.slnx
npm --prefix web run format
git add src/EmotePurge.Infrastructure/Twitch/TwitchApiDtos.cs src/EmotePurge.Core/Twitch/TwitchModels.cs src/EmotePurge.Infrastructure/Twitch/TwitchHelixClient.cs src/EmotePurge.Api/Auth/TwitchClaimTypes.cs src/EmotePurge.Api/Endpoints/AuthEndpoints.cs src/EmotePurge.Api/Program.cs web/src/app/core/auth/auth.model.ts web/src/app/core/auth/auth.service.spec.ts web/e2e/support/mocks.ts
git commit -m "feat(api): carry the Twitch avatar as a session claim"
```

---

### Task 3: `SegmentedControl` bekommt ein `size`-Input

Additiv: `'sm'` ist der Default und lässt jede bestehende Aufrufstelle unverändert. `'lg'` macht die Gruppe blockbreit und die Segmente daumentauglich — ohne das blieben sie bei rund 34 px.

Die 44 px sind eine Ergonomie-Entscheidung, keine Zertifikatspflicht: `PRODUCT.md` hat die formale WCAG-Zusage am 2026-08-06 zurückgenommen. Tastaturbedienbarkeit und Fokus-Sichtbarkeit bleiben davon unberührt.

**Files:**
- Modify: `web/src/app/shared/ui/segmented-control.ts`

**Interfaces:**
- Consumes: nichts
- Produces: `SegmentedControl` mit zusätzlichem `readonly size = input<'sm' | 'lg'>('sm')`. Bestehende Signatur (`options`, `ariaLabel`, `value` als `model.required<string>()`) unverändert.

- [ ] **Step 1: Das Input und die beiden abgeleiteten Klassen ergänzen**

In `web/src/app/shared/ui/segmented-control.ts`. Der `@Component`-Decorator bekommt eine `host`-Bindung, die Klassenketten im Template werden durch zwei `computed()` ersetzt:

```typescript
@Component({
  selector: 'app-segmented-control',
  imports: [TranslocoPipe],
  // 'lg' stretches to its container, which needs a block-level host to stretch inside. Bound
  // conditionally rather than set unconditionally so that every existing 'sm' call site — all of
  // them inline in a flex row — keeps the inline host it was laid out against.
  host: { '[class.block]': "size() === 'lg'" },
  template: `
    <div role="radiogroup" [attr.aria-label]="ariaLabel()" [class]="groupClass()">
      <!-- Separators come from the container background showing through the 1px gaps, not from
           per-button borders: with flex-wrap (long option sets on narrow screens) that draws the
           dividers between rows too, which a border-l on each button cannot.

           The trick depends on the carrier contrasting with the segments, and the direction of that
           contrast flips between modes: inset-hover is LIGHTER than inset in dark and DARKER than it
           in light. Naming the two roles instead of two greys is what makes the flip automatic —
           hardcoded slate-700-over-slate-800 would have inverted into an invisible divider. -->
      @for (option of options(); track option.value; let index = $index) {
        <button
          type="button"
          role="radio"
          [attr.aria-checked]="value() === option.value"
          [tabindex]="tabIndexFor(option)"
          [class]="
            segmentClass() +
            (value() === option.value
              ? 'bg-accent-selected font-medium text-on-accent'
              : 'bg-surface-inset text-fg-secondary hover:bg-surface-inset-hover')
          "
          (click)="value.set(option.value)"
          (keydown)="onKeydown($event, index)"
        >
          {{ option.labelKey | transloco }}
        </button>
      }
    </div>
  `,
})
export class SegmentedControl {
  readonly options = input.required<SegmentedControlOption[]>();
  readonly ariaLabel = input('');
  /**
   * 'lg' is for touch surfaces where the control is the row rather than a chip in one — currently
   * only the account menu's panel. It raises the segments to a 44 px thumb target and lets the
   * group fill its container; 'sm' is every other call site and is unchanged by this input existing.
   */
  readonly size = input<'sm' | 'lg'>('sm');
  readonly value = model.required<string>();

  protected readonly groupClass = computed(
    () =>
      (this.size() === 'lg' ? 'flex w-full ' : 'inline-flex ') +
      'flex-wrap gap-px overflow-hidden rounded-md border border-surface-inset-hover bg-surface-inset-hover',
  );

  protected readonly segmentClass = computed(
    () =>
      'grow px-3 py-1.5 text-sm whitespace-nowrap transition ' +
      (this.size() === 'lg' ? 'min-h-11 ' : ''),
  );

  protected tabIndexFor(option: SegmentedControlOption): number {
    const options = this.options();
    const selectedExists = options.some((candidate) => candidate.value === this.value());
    const isTabStop = selectedExists ? option.value === this.value() : option === options[0];
    return isTabStop ? 0 : -1;
  }

  protected onKeydown(event: KeyboardEvent, index: number): void {
    let delta: number;
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        delta = 1;
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        delta = -1;
        break;
      default:
        return;
    }
    event.preventDefault();
    const options = this.options();
    const next = (index + delta + options.length) % options.length;
    this.value.set(options[next].value);
    const group = (event.currentTarget as HTMLElement).closest('[role="radiogroup"]');
    group?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[next]?.focus();
  }
}
```

Der Import auf Zeile 1 wird zu:

```typescript
import { Component, computed, input, model } from '@angular/core';
```

- [ ] **Step 2: Prüfen, dass keine bestehende Aufrufstelle sich ändert**

```bash
npm --prefix web run build
npm --prefix web run lint
grep -rn "app-segmented-control" web/src/app --include=*.ts --include=*.html
```

Expected: Build und Lint grün. Jede gefundene Aufrufstelle steht ohne `size` da und rendert damit exakt wie vorher (`inline-flex`, `py-1.5`, kein `block` am Host).

- [ ] **Step 3: Im Browser gegenprüfen**

`npm --prefix web start`, dann die Usage-Stats-Seite eines Channels öffnen. Der Zeitraum-Umschalter dort ist eine bestehende `sm`-Aufrufstelle: Breite, Höhe und Segmentabstände müssen unverändert aussehen. Pfeiltasten innerhalb der Gruppe weiterhin bedienbar, ein Tab-Stop für die ganze Gruppe.

- [ ] **Step 4: Formatieren und committen**

```bash
npm --prefix web run format
git add web/src/app/shared/ui/segmented-control.ts
git commit -m "feat(web): give the segmented control a touch-sized variant"
```

---

### Task 4: `Avatar`

Runder Träger mit fester Größe, dessen Platz **vor** dem ersten Bild-Frame reserviert ist — sonst springt der Header beim Laden, und Layout-Sprünge in der Shell sind ausgeschlossen.

**Files:**
- Create: `web/src/app/shared/ui/avatar.ts`

**Interfaces:**
- Consumes: `AuthUser.profileImageUrl` (Task 2), Muster aus `web/src/app/shared/emotes/emote-sprite.ts`
- Produces: `Avatar`, Selektor `app-avatar`, Inputs `displayName: string` (required), `imageUrl: string | null` (Default `null`), `size: number` (Default `32`)

- [ ] **Step 1: Die Komponente schreiben**

Datei `web/src/app/shared/ui/avatar.ts`:

```typescript
import { NgOptimizedImage } from '@angular/common';
import { Component, computed, input, signal } from '@angular/core';

/**
 * Round picture carrier with a monogram fallback, sized in px rather than by class because the one
 * thing it must never do is change size: it sits in a 56 px header, and a plate that grows when the
 * picture arrives is a layout jump in the app frame — the one place the design language rules out
 * entirely. So the plate is painted first at its final size and the picture appears inside it.
 *
 * `settled` is keyed on url identity, not on a "has loaded once" boolean — the same pattern
 * `emote-sprite.ts` uses and for the same reason: the node is reused across url changes, so the
 * signal has to name *which* url it belongs to. Here that matters less than on a hovered atlas, but
 * it costs nothing and keeps one pattern in the repo instead of two.
 *
 * An empty `displayName` renders the plate with no letter at all. That is not a degenerate case but
 * a state the account menu needs: before /api/auth/me answers, the trigger must hold its exact
 * final shape without claiming to know whose account it is.
 *
 * Decorative throughout: `aria-hidden` on the plate, `alt=""` on the picture. The accessible name
 * belongs to whatever interactive element wraps this.
 */
@Component({
  selector: 'app-avatar',
  imports: [NgOptimizedImage],
  template: `
    <span
      aria-hidden="true"
      class="relative flex shrink-0 items-center justify-center overflow-hidden rounded-full bg-accent-selected font-medium text-on-accent"
      [style.width.px]="size()"
      [style.height.px]="size()"
      [style.font-size.px]="monogramSize()"
    >
      {{ settled() ? '' : monogram() }}
      @if (imageUrl(); as url) {
        <img
          [ngSrc]="url"
          [width]="size()"
          [height]="size()"
          alt=""
          class="absolute inset-0 h-full w-full object-cover"
          [style.visibility]="settled() ? null : 'hidden'"
          (load)="loadedUrl.set(url)"
          (error)="loadedUrl.set(null)"
        />
      }
    </span>
  `,
})
export class Avatar {
  readonly displayName = input.required<string>();
  readonly imageUrl = input<string | null>(null);
  /**
   * Edge length in px. Must be constant per call site — NgOptimizedImage objects to width/height
   * changing after init.
   */
  readonly size = input(32);

  protected readonly loadedUrl = signal<string | null>(null);

  protected readonly settled = computed(
    () => this.loadedUrl() !== null && this.loadedUrl() === this.imageUrl(),
  );
  protected readonly monogram = computed(() =>
    this.displayName().trim().slice(0, 1).toUpperCase(),
  );
  protected readonly monogramSize = computed(() => Math.round(this.size() * 0.45));
}
```

- [ ] **Step 2: Bauen und linten**

```bash
npm --prefix web run build
npm --prefix web run lint
```

Expected: grün. Insbesondere kein `@angular-eslint/template/prefer-ngsrc`-Verstoß — das `<img>` benutzt `ngSrc`.

- [ ] **Step 3: Committen**

Noch rendert die Komponente nirgends; das ist beabsichtigt, sie wird in Task 6 verdrahtet und dort auch zum ersten Mal im Browser gesehen.

```bash
npm --prefix web run format
git add web/src/app/shared/ui/avatar.ts
git commit -m "feat(web): add a round avatar with a monogram fallback"
```

---

### Task 5: `DisplayPreferences`

Der Block „Darstellung" + „Sprache". Zwei beschriftete Gruppen, jede über die volle Panelbreite, Beschriftung **über** der Gruppe — weil `SegmentedControl` nur Text-Labels nimmt und drei Theme-Labels bei `text-sm px-3` nicht neben eine Zeilenbeschriftung in ein 256 px breites Panel passen.

**Files:**
- Create: `web/src/app/shared/ui/display-preferences.ts`
- Modify: `web/public/i18n/de.json`, `web/public/i18n/en.json`

**Interfaces:**
- Consumes: `SegmentedControl` mit `size="lg"` (Task 3), `ThemeService.preference` / `.setPreference()`, `LanguageService.lang` / `.setLang()`
- Produces: `DisplayPreferences`, Selektor `app-display-preferences`, keine Inputs

- [ ] **Step 1: Die i18n-Keys ergänzen**

`web/public/i18n/de.json` — den `languageSwitcher`-Block ersetzen:

```json
  "languageSwitcher": {
    "label": "Sprache",
    "ariaLabel": "Sprache wählen",
    "de": "Deutsch",
    "en": "English"
  },
```

`web/public/i18n/en.json` — dito:

```json
  "languageSwitcher": {
    "label": "Language",
    "ariaLabel": "Choose language",
    "de": "Deutsch",
    "en": "English"
  },
```

`ariaLabel` verliert den `{{ lang }}`-Parameter: es beschriftet künftig die Gruppe, nicht einzelne Buttons. Die Segment-Labels sind in beiden Locales identisch, weil Sprachnamen als Endonyme stehen — wer die Oberfläche auf Englisch liest, sucht trotzdem „Deutsch".

Der `theme`-Block bleibt in beiden Dateien **unverändert**: `theme.label` wird die Gruppenbeschriftung, `theme.ariaLabel` das Gruppen-Label, `theme.system` / `.light` / `.dark` werden die Segment-Labels.

- [ ] **Step 2: Die Komponente schreiben**

Datei `web/src/app/shared/ui/display-preferences.ts`:

```typescript
import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { AppLang, LanguageService, SUPPORTED_LANGS } from '../../core/i18n/language.service';
import { THEME_PREFERENCES, ThemePreference, ThemeService } from '../../core/theme/theme.service';
import { SegmentedControl, SegmentedControlOption } from './segmented-control';

const THEME_OPTIONS: SegmentedControlOption[] = THEME_PREFERENCES.map((value) => ({
  value,
  labelKey: `theme.${value}`,
}));

const LANGUAGE_OPTIONS: SegmentedControlOption[] = SUPPORTED_LANGS.map((value) => ({
  value,
  labelKey: `languageSwitcher.${value}`,
}));

/**
 * Theme and language, the two personal display preferences, as one block. After this rebuild it is
 * the only place in the repo where either control exists — they used to be two components that the
 * shell, the landing page and the login page each carried a copy of, in two different layouts.
 *
 * Caption above the group rather than beside it: SegmentedControl takes text labels only, and three
 * theme labels at text-sm/px-3 do not fit next to a row caption in a 256 px panel. Across the full
 * width they do.
 *
 * Both options tables are derived from the services' own constants, so adding a fourth theme or a
 * third language needs a translation and nothing else here.
 */
@Component({
  selector: 'app-display-preferences',
  imports: [SegmentedControl, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-3 px-3 py-3">
      <div class="flex flex-col gap-1.5">
        <span class="text-xs font-medium text-fg-muted">{{ 'theme.label' | transloco }}</span>
        <app-segmented-control
          size="lg"
          [options]="themeOptions"
          [ariaLabel]="'theme.ariaLabel' | transloco"
          [value]="themeService.preference()"
          (valueChange)="setTheme($event)"
        />
      </div>

      <div class="flex flex-col gap-1.5">
        <span class="text-xs font-medium text-fg-muted">{{
          'languageSwitcher.label' | transloco
        }}</span>
        <app-segmented-control
          size="lg"
          [options]="languageOptions"
          [ariaLabel]="'languageSwitcher.ariaLabel' | transloco"
          [value]="languageService.lang()"
          (valueChange)="setLanguage($event)"
        />
      </div>
    </div>
  `,
})
export class DisplayPreferences {
  protected readonly themeService = inject(ThemeService);
  protected readonly languageService = inject(LanguageService);

  protected readonly themeOptions = THEME_OPTIONS;
  protected readonly languageOptions = LANGUAGE_OPTIONS;

  // One-way in, explicit out rather than a two-way binding: both services persist the choice in
  // their setter, so writing the signal directly would change the UI and forget the preference.
  // The casts are safe because both option tables are built from the very constants they narrow to.
  protected setTheme(value: string): void {
    this.themeService.setPreference(value as ThemePreference);
  }

  protected setLanguage(value: string): void {
    this.languageService.setLang(value as AppLang);
  }
}
```

- [ ] **Step 3: Bauen und linten**

```bash
npm --prefix web run build
npm --prefix web run lint
```

Expected: grün.

- [ ] **Step 4: Committen**

```bash
npm --prefix web run format
git add web/src/app/shared/ui/display-preferences.ts web/public/i18n/de.json web/public/i18n/en.json
git commit -m "feat(web): put theme and language into one preferences block"
```

---

### Task 6: `AccountMenu`

Ein Trigger, ein Panel, beide Auth-Zustände in einer Komponente. Sie injiziert `AuthService` und bekommt keine Inputs.

Diese Aufgabe enthält eine Ergänzung, die in der Spec fehlt und ohne die der ausgeloggte Fall bricht: **die Komponente muss `ensureLoaded()` selbst anstoßen.** Auf `/welcome` und `/login` rendert sie außerhalb der Shell, und die Shell ist heute der einzige Ort, der `/api/auth/me` abruft. Ohne den eigenen Aufruf bliebe der Trigger dort für immer im unbestimmten Zustand — also dauerhaft deaktiviert. `ensureLoaded()` ist idempotent (interner `isLoaded`-Cache), der Aufruf in `AppShell` bleibt deshalb unangetastet und verursacht keinen zweiten Request.

**Files:**
- Create: `web/src/app/shared/ui/account-menu.ts`
- Modify: `web/src/app/core/auth/auth.service.ts`
- Modify: `web/src/app/core/auth/auth.service.spec.ts`
- Modify: `web/public/i18n/de.json`, `web/public/i18n/en.json`

**Interfaces:**
- Consumes: `Avatar` (Task 4), `DisplayPreferences` (Task 5), `Popover` + `POPOVER_ANCHOR_ATTRIBUTE` aus `shared/ui/popover.ts`, `AuthUser.profileImageUrl` (Task 2)
- Produces:
  - `AuthService.isResolved: Signal<boolean>` — `false`, bis `/api/auth/me` einmal geantwortet hat
  - `AccountMenu`, Selektor `app-account-menu`, keine Inputs

- [ ] **Step 1: Den fehlschlagenden Service-Test schreiben**

In `web/src/app/core/auth/auth.service.spec.ts`, im `describe('ensureLoaded', …)`-Block ergänzen:

```typescript
    it('reports isResolved only once /api/auth/me has answered', () => {
      expect(service.isResolved()).toBe(false);

      service.ensureLoaded().subscribe();
      expect(service.isResolved()).toBe(false);

      httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

      // A logged-out visitor is a resolved state, not a pending one — the account menu draws a
      // different trigger for each and must not be able to confuse them.
      expect(service.isResolved()).toBe(true);
      expect(service.currentUser()).toBeNull();
    });
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag prüfen**

Run: `npm --prefix web test -- --watch=false -t "isResolved"`
Expected: FAIL — `service.isResolved is not a function`

- [ ] **Step 3: `isResolved` veröffentlichen**

In `web/src/app/core/auth/auth.service.ts`, direkt **nach** `private readonly isLoaded = signal(false);`:

```typescript
  /**
   * False until /api/auth/me has answered once, whichever way it answered. `currentUser()` alone
   * cannot express this: it is null both before the request and for a logged-out visitor, and the
   * account menu has to draw a different trigger for each — a gear appearing and then flipping to
   * an avatar is a visible swap in the middle of the header.
   */
  readonly isResolved = this.isLoaded.asReadonly();
```

- [ ] **Step 4: Test laufen lassen, Erfolg prüfen**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS, alle Vitest-Specs grün.

- [ ] **Step 5: Die i18n-Keys ergänzen**

`web/public/i18n/de.json`, ein neuer Block direkt **vor** `"shell"`:

```json
  "account": {
    "trigger": "Konto-Menü von {{ name }}",
    "preferencesTrigger": "Einstellungen"
  },
```

`web/public/i18n/en.json`, an derselben Stelle:

```json
  "account": {
    "trigger": "Account menu for {{ name }}",
    "preferencesTrigger": "Settings"
  },
```

- [ ] **Step 6: Die Komponente schreiben**

Datei `web/src/app/shared/ui/account-menu.ts`:

```typescript
import { DOCUMENT } from '@angular/common';
import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { Avatar } from './avatar';
import { DisplayPreferences } from './display-preferences';
import { Popover } from './popover';

/**
 * Everything personal in the app frame behind one trigger: who you are, where your own pages are,
 * how the app looks, which language it speaks, and the way out.
 *
 * The reason is the rule that what stands in the app header stands on every screen in every
 * session. Six permanent controls measured against that are five too many, and the argument holds
 * on a desktop just as it does on a phone — which is why this replaces both the desktop cluster and
 * the mobile disclosure with one thing rather than two.
 *
 * Disclosure semantics, deliberately not role="menu": the panel holds mixed children — router
 * links, two radiogroups, a button — and role="menu" requires menuitem children, which a radiogroup
 * inside it is not. This is a step back from what theme-menu.ts did (menuitemradio) and the same
 * decision the shell's own disclosure already took.
 *
 * It calls ensureLoaded() itself because it renders on the landing and login pages too, outside the
 * shell that is otherwise the only caller. The call is idempotent, so the shell's own stays.
 */
@Component({
  selector: 'app-account-menu',
  imports: [Avatar, DisplayPreferences, Popover, RouterLink, TranslocoPipe],
  template: `
    <div class="relative" data-popover-anchor>
      <!-- 44 px in a 56 px header leaves 6 px of air top and bottom. The plate inside is 32 px and
           is painted before the picture arrives, so nothing in this box ever changes size. -->
      <button
        #trigger
        type="button"
        class="inline-flex h-11 w-11 items-center justify-center rounded-md text-fg-muted transition hover:text-fg disabled:cursor-default"
        aria-haspopup="dialog"
        [attr.aria-expanded]="isOpen()"
        [attr.aria-label]="triggerLabel()"
        [disabled]="!authResolved()"
        (click)="toggle()"
      >
        @if (!authResolved()) {
          <!-- Reserved, silent, letterless: the shape is final, only its content resolves. No
               spinner — it costs one roundtrip, and a spinner in the header would be louder than
               the thing it reports. -->
          <app-avatar displayName="" />
        } @else if (currentUser(); as user) {
          <app-avatar [displayName]="user.displayName" [imageUrl]="user.profileImageUrl" />
        } @else {
          <svg
            class="h-5 w-5"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.75"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <circle cx="12" cy="12" r="3.25" />
            <path
              d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"
            />
          </svg>
        }
      </button>

      @if (isOpen()) {
        <app-popover align="end" width="w-64" [ariaLabel]="triggerLabel()" (closed)="close()">
          <div class="flex flex-col">
            @if (currentUser(); as user) {
              <!-- No hover on this row. It carries the same rhythm as the entries below it but is
                   not clickable, and a hover must never promise a click that is not there. -->
              <div class="flex items-center gap-3 border-b border-border px-3 py-3">
                <app-avatar
                  [displayName]="user.displayName"
                  [imageUrl]="user.profileImageUrl"
                  [size]="36"
                />
                <!-- font-medium, not semibold: semibold is reserved for headings, and a fifth
                     weight would be a fifth level in a four-level scale. -->
                <span class="truncate text-sm font-medium text-fg">{{ user.displayName }}</span>
              </div>

              <a
                routerLink="/my-votings"
                class="flex min-h-11 items-center px-3 text-sm text-fg-body transition hover:bg-surface-inset"
                (click)="close()"
              >
                {{ 'shell.myVotings' | transloco }}
              </a>

              @if (user.isGlobalAdmin) {
                <!-- Visibility only — /admin is behind adminGuard and every admin endpoint behind
                     GlobalAdminAuthorizationFilter. The flag rides along on the cached /me. -->
                <a
                  routerLink="/admin"
                  class="flex min-h-11 items-center px-3 text-sm text-fg-body transition hover:bg-surface-inset"
                  (click)="close()"
                >
                  {{ 'shell.admin' | transloco }}
                </a>
              }

              <div class="border-t border-border">
                <app-display-preferences />
              </div>

              <button
                type="button"
                class="flex min-h-11 items-center border-t border-border px-3 text-left text-sm text-fg-body transition hover:bg-surface-inset"
                (click)="logout()"
              >
                {{ 'shell.logout' | transloco }}
              </button>
            } @else {
              <app-display-preferences />
            }
          </div>
        </app-popover>
      }
    </div>
  `,
})
export class AccountMenu {
  private readonly authService = inject(AuthService);
  private readonly transloco = inject(TranslocoService);
  private readonly document = inject(DOCUMENT);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  protected readonly currentUser = this.authService.currentUser;
  protected readonly authResolved = this.authService.isResolved;
  protected readonly isOpen = signal(false);

  /**
   * Translated imperatively rather than through the pipe, because it carries an interpolated name
   * into an attribute. Reading activeLang is what makes it follow a language switch made in this
   * very panel — translate() is a plain call and would otherwise never re-run.
   */
  private readonly activeLang = toSignal(this.transloco.langChanges$, {
    initialValue: this.transloco.getActiveLang(),
  });

  protected readonly triggerLabel = computed(() => {
    this.activeLang();
    const user = this.currentUser();
    return user
      ? this.transloco.translate('account.trigger', { name: user.displayName })
      : this.transloco.translate('account.preferencesTrigger');
  });

  constructor() {
    // The landing and login pages render outside AppShell, which is otherwise the only caller.
    // Idempotent, so the shell's own call is untouched and no second request is made.
    this.authService.ensureLoaded().subscribe();
  }

  protected toggle(): void {
    if (this.isOpen()) {
      this.close();
      return;
    }
    this.isOpen.set(true);
  }

  protected close(): void {
    if (!this.isOpen()) {
      return;
    }
    // Focus would otherwise fall to <body> together with the panel that held it.
    const hadFocus = this.elementRef.nativeElement.contains(this.document.activeElement);
    this.isOpen.set(false);
    if (hadFocus) {
      this.trigger()?.nativeElement.focus();
    }
  }

  protected logout(): void {
    this.close();
    this.authService.logout();
  }
}
```

- [ ] **Step 7: Bauen, linten, Unit-Tests**

```bash
npm --prefix web run build
npm --prefix web run lint
npm --prefix web test -- --watch=false
```

Expected: alles grün.

- [ ] **Step 8: Committen**

Noch rendert die Komponente nirgends — verdrahtet und live gesehen wird sie in Task 7.

```bash
npm --prefix web run format
git add web/src/app/shared/ui/account-menu.ts web/src/app/core/auth/auth.service.ts web/src/app/core/auth/auth.service.spec.ts web/public/i18n/de.json web/public/i18n/en.json
git commit -m "feat(web): add the account menu behind one header trigger"
```

---

### Task 7: Shell, Landing und Login umstellen — und die beiden alten Komponenten löschen

Der große Schnitt. Danach hat der Header **keinen `md:`-Zweig mehr**, und rund 100 Zeilen handgebautes Dismiss-Verhalten sind weg — der Doc-Kommentar von `popover.ts:12-15` benennt diese Disclosure selbst als eines der Duplikate, die zusammengehören.

**Files:**
- Modify: `web/src/app/features/shell/app-shell.ts`
- Modify: `web/src/app/features/landing/landing-page.html`, `landing-page.ts`
- Modify: `web/src/app/features/login/login-page.ts`
- Modify: `web/public/i18n/de.json`, `en.json` (`shell.menu` entfällt)
- Delete: `web/src/app/shared/ui/theme-menu.ts`
- Delete: `web/src/app/shared/i18n/language-switcher.ts` (und das dann leere Verzeichnis `web/src/app/shared/i18n/`)

**Interfaces:**
- Consumes: `AccountMenu` (Task 6), `AuthService.isResolved` (Task 6)
- Produces: nichts Neues

- [ ] **Step 1: Den Shell-Header ersetzen**

In `web/src/app/features/shell/app-shell.ts` den gesamten Block von `<!-- Desktop: everything inline, as before. -->` (Zeile 77) bis einschließlich der schließenden `}` der Disclosure (Zeile 197) durch dies ersetzen:

```html
          <div class="flex items-center gap-3">
            <!-- Gated on authResolved so the button does not flash and get replaced: the header
                 must not visibly change its mind about who you are. -->
            @if (authResolved() && !currentUser()) {
              <a routerLink="/login" appButton="primary">
                {{ 'shell.login' | transloco }}
              </a>
            }
            <app-account-menu />
          </div>
```

Außerdem in derselben Datei:

- Zeile 39: `class="relative mx-auto flex h-full max-w-5xl items-center justify-between gap-3"` → `relative` streichen. Es trug die absolute Disclosure; das Panel hängt jetzt am eigenen `data-popover-anchor` innerhalb von `AccountMenu`.
- Der `host`-Block (Zeilen 26-29) entfällt vollständig.

- [ ] **Step 2: Die Shell-Klasse aufräumen**

Imports (Zeilen 1-13) werden zu:

```typescript
import { NgOptimizedImage } from '@angular/common';
import { Component, computed, effect, inject } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { WorkerHealthService } from '../../core/health/worker-health.service';
import { LOGO_SRC } from '../../shared/branding/logo';
import { AccountMenu } from '../../shared/ui/account-menu';
import { Button } from '../../shared/ui/button';
import { HealthMarker } from '../../shared/ui/health-marker';
```

Der `imports`-Block des Decorators:

```typescript
  imports: [
    AccountMenu,
    Button,
    HealthMarker,
    NgOptimizedImage,
    RouterLink,
    RouterOutlet,
    TranslocoPipe,
  ],
```

Der Klassenrumpf — der Konstruktor bleibt Wort für Wort, wie er ist:

```typescript
export class AppShell {
  private readonly authService = inject(AuthService);
  private readonly healthService = inject(WorkerHealthService);
  private readonly router = inject(Router);

  protected readonly currentUser = this.authService.currentUser;
  protected readonly authResolved = this.authService.isResolved;
  protected readonly workerStale = computed(() => this.healthService.status() === 'stale');
  protected readonly logoSrc = LOGO_SRC;

  constructor() {
    // …unverändert…
  }
}
```

Ersatzlos gestrichen: das Feld `menuButton`, das Signal `menuOpen` und die vier Methoden `logout`, `toggleMenu`, `closeMenu`, `onEscape`, `onDocumentClick`. `logout()` lebt jetzt in `AccountMenu`.

- [ ] **Step 3: Login-Seite umstellen**

In `web/src/app/features/login/login-page.ts` den Block auf Zeile 46-49 ersetzen:

```html
        <app-account-menu />
```

Import auf Zeile 8 und 10 raus, dafür `import { AccountMenu } from '../../shared/ui/account-menu';`. Der `imports`-Array auf Zeile 95 wird zu:

```typescript
  imports: [AccountMenu, Button, NgOptimizedImage, RouterLink, TranslocoPipe],
```

- [ ] **Step 4: Landing-Seite umstellen**

In `web/src/app/features/landing/landing-page.html` die Zeilen 33-36 ersetzen:

```html
        <app-account-menu />
```

(also der Kommentar „Same pair as the app shell…" samt beider Komponenten — die Begründung ist mit den Komponenten hinfällig geworden.)

In `web/src/app/features/landing/landing-page.ts` die beiden Imports gegen `AccountMenu` tauschen und den `imports`-Array entsprechend anpassen.

- [ ] **Step 5: Die beiden alten Komponenten löschen**

```bash
git rm web/src/app/shared/ui/theme-menu.ts web/src/app/shared/i18n/language-switcher.ts
```

`ThemeIcon` fällt mit `theme-menu.ts` — nach dem Umbau rendert sie niemand mehr (s. Abweichung 1). `web/src/app/shared/i18n/` ist danach leer; Git entfernt es von selbst.

- [ ] **Step 6: `shell.menu` aus beiden Locales entfernen**

Der Key war das Aria-Label des Burgers. Ohne Burger ist er tot. In `web/public/i18n/de.json` und `en.json` jeweils die `"menu"`-Zeile aus dem `shell`-Block streichen.

- [ ] **Step 7: Prüfen, dass nichts zurückgeblieben ist**

```bash
grep -rn "theme-menu\|language-switcher\|ThemeMenu\|ThemeIcon\|LanguageSwitcher\|shell.menu\|data-shell-menu" web/src web/e2e docs
```

Expected: Treffer nur noch in `docs/UI-Designsprache.md` (Task 9) und ggf. in `web/e2e/` (Task 8). Kein Treffer mehr unter `web/src/`. Der Kommentar in `web/src/app/app.config.ts:53` nennt `LanguageSwitcher` beispielhaft — den Satz auf `AccountMenu` umschreiben, damit er auf etwas Existierendes zeigt.

- [ ] **Step 8: Bauen, linten, Unit-Tests**

```bash
npm --prefix web run build
npm --prefix web run lint
npm --prefix web test -- --watch=false
```

Expected: alles grün. (Die E2E-Suite bricht hier erwartungsgemäß — sie kommt in Task 8.)

- [ ] **Step 9: Live prüfen, Desktop**

```bash
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api
npm --prefix web start
```

Auf `http://localhost:4200`, eingeloggt, abhaken:

- [ ] Der Header rechts trägt genau **ein** Element. Kein Sprung beim Laden — die Trigger-Fläche steht sofort und ändert nur ihren Inhalt.
- [ ] Das Profilbild lädt (nicht das Monogramm). Lädt es nicht: DevTools-Konsole auf CSP-Verstoß prüfen, dann Task 2 Step 7.
- [ ] Panel öffnet, Klick außerhalb schließt, Escape schließt, in beiden Fällen steht der Fokus wieder auf dem Trigger.
- [ ] Tabreihenfolge im offenen Panel: Meine Abstimmungen → [Admin] → Darstellung → Sprache → Abmelden. **Fünf Stationen** — jede Segmentgruppe ist dank Roving-Tabindex *eine*, Pfeiltasten wählen innerhalb.
- [ ] Theme- und Sprachwechsel wirken sofort, das Panel bleibt dabei offen, und das Aria-Label des Triggers wechselt die Sprache mit.
- [ ] Der Kopf des Panels reagiert **nicht** auf Überfahren; die Zeilen darunter schon.
- [ ] Beide Modi angesehen, hell und dunkel.
- [ ] Ausgeloggt (`/welcome`, `/login`): Zahnrad, Panel zeigt nur die Einstellungen, der Login-Button steht daneben und blitzt nicht auf.
- [ ] Konsole ohne neue Warnungen — insbesondere kein NG0913 und keine `NgOptimizedImage`-Meldung zum Avatar.

- [ ] **Step 10: Live prüfen, Handy**

`dotnet run` beenden, dann nach `docs/Testumgebung-Mobile-2026-08-07.md`:

```bash
dotnet run --project src/EmotePurge.Api --launch-profile lan
npm --prefix web run start:lan
```

Am Gerät über `https://dev.home.sensitron.me`:

- [ ] Trigger als Daumenziel brauchbar, Panel geht auf.
- [ ] Das Panel wird **nicht beschnitten**. Es ist rund 320 px hoch und hängt aus einem 56-px-Header; bekäme irgendein Vorfahr `overflow: hidden` oder `auto`, wäre es hier abgeschnitten. Heute trägt keiner eines — das ist der Test dafür.
- [ ] Auf 360 px steht das Panel nicht über den Viewport hinaus (`popover.ts` deckelt auf `max-w-[calc(100vw-2rem)]`).
- [ ] Segmente sind als Daumenziele brauchbar (`min-h-11`).
- [ ] Header-Höhe unverändert: die Tab-Leisten kleben weiterhin genau unter ihm, kein Spalt und keine Überlappung.

Danach `dotnet run` beenden — sonst fällt Task 8 reihenweise durch.

- [ ] **Step 11: Formatieren und committen**

```bash
npm --prefix web run format
git add -A web/src web/public/i18n
git commit -m "feat(web): fold the header controls into the account menu"
```

---

### Task 8: Die E2E-Suiten nachziehen

Vier Selektoren verschwinden mit dem alten Header. Es gibt weder `theme-menu.spec.ts` noch `language-switcher.spec.ts` noch `app-shell.spec.ts` — Unit-Kollateralschäden gibt es also nicht, der Aufwand liegt vollständig in E2E.

**Files:**
- Modify: `web/e2e/theme.spec.ts:119-150`
- Modify: `web/e2e/channel-workspace.e2e.spec.ts:36-39`
- Modify: `web/e2e/audit/ui-audit.audit.ts:339-343`

**Interfaces:**
- Consumes: die neuen Aria-Labels aus Task 6 — eingeloggt `Konto-Menü von Sensitron`, ausgeloggt `Einstellungen`; Theme-Segmente jetzt `role="radio"` statt `menuitemradio`
- Produces: nichts

- [ ] **Step 1: Die Suite laufen lassen und den Ist-Schaden festhalten**

Erst sicherstellen, dass auf `:5151` **keine** Api lauscht — sonst schickt der `apiAuthInterceptor` die App bei jedem ungemockten Request auf die Login-Seite, und rund die halbe Suite fällt mit „element not found" durch, quer über Dateien, die mit der Änderung nichts zu tun haben.

```bash
npm --prefix web run e2e
```

Expected: FAIL in `theme.spec.ts` (drei Fälle) und `channel-workspace.e2e.spec.ts` (ein Fall). Die Liste notieren — bricht mehr, gehört das hier mit repariert.

- [ ] **Step 2: `theme.spec.ts` umstellen**

Zeilen 119-132 ersetzen:

```typescript
  test('the header menu switches the mode and persists the choice', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockMyChannels(page, []);
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

    await page.getByRole('button', { name: 'Konto-Menü von Sensitron' }).click();
    await page.getByRole('radio', { name: 'Hell' }).click();

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    const stored = await page.evaluate(() => localStorage.getItem('emotepurge.theme'));
    expect(stored).toBe('light');
  });
```

Zeilen 141-149 ersetzen (Kommentar darüber bleibt):

```typescript
    test(`the ${name} page carries its own theme switch`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: 'dark' });
      await page.goto(path);

      // Logged out, so the trigger is the gear rather than an avatar.
      await page.getByRole('button', { name: 'Einstellungen' }).click();
      await page.getByRole('radio', { name: 'Hell' }).click();

      await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    });
```

- [ ] **Step 3: `channel-workspace.e2e.spec.ts` umstellen**

Zeilen 36-39 ersetzen:

```typescript
    await expect(page).toHaveURL('/');
    await expect(page.getByRole('link', { name: 'Login' })).toHaveCount(0);

    // Name and logout now live behind the account menu — the header itself carries one control.
    await page.getByRole('button', { name: 'Konto-Menü von Sensitron' }).click();
    await expect(page.getByText('Sensitron', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Logout' })).toBeVisible();
```

- [ ] **Step 4: Den Audit-Kommentar korrigieren**

In `web/e2e/audit/ui-audit.audit.ts`, den Kommentar über `slug: 'overview-worker-stale'` (Zeilen 340-342) ersetzen:

```typescript
    // The one state in which the header says anything about the worker at all. Worth a shot of its
    // own precisely because it is rare: at 360px the warning shares the bar with the wordmark and
    // the account-menu trigger, and nothing else in the app ever puts a third thing in that row.
```

Der Shot bleibt sinnvoll — nur „menu button" ist seit dem Umbau kein zutreffender Name mehr.

- [ ] **Step 5: Die Suite grün fahren**

```bash
npm --prefix web run e2e
```

Expected: PASS, vollständig. Zum Kalibrieren: die Suite lief am 2026-08-07 mit 76 Fällen in 52 s durch. Deutlich mehr Zeit bei vielen roten Fällen heißt fast immer: es läuft doch eine Api auf `:5151`.

- [ ] **Step 6: Den Audit-Harness fahren**

Die Änderung ist layoutwirksam (Header, alle Breiten), deshalb nach `docs/UI-Designsprache.md` §12:

```bash
npm --prefix web run e2e -- --grep @audit
```

Expected: 0 Verstöße auf `serious`/`critical` im axe-Kontrastgate, keine horizontale Überlaufmeldung auf 360 px.

Wenn der Harness Referenzbilder führt, die sich durch den neuen Header ändern: die neuen Stände prüfen (nicht nur blind übernehmen) und im selben Commit aktualisieren.

- [ ] **Step 7: Formatieren und committen**

```bash
npm --prefix web run format
git add web/e2e
git commit -m "test(web): drive theme and logout through the account menu"
```

---

### Task 9: Dokumentation, Entscheidungslog und der mechanische Detektor

Regel 3: Ein Commit, der eine Konvention oder einen Vertrag ändert, enthält seinen `DECISIONS.md`-Eintrag im selben Commit. Drei Stellen in der Designsprache sind durch den Umbau faktisch falsch geworden.

**Files:**
- Modify: `docs/UI-Designsprache.md` (§2.0 Theme-Bullet + Referenzzeile, z-Leiter in §8.5, Primitives-Liste in §11)
- Modify: `docs/DECISIONS.md`
- Modify: `docs/superpowers/specs/2026-08-08-account-menu-design.md` (Statuszeile)

**Interfaces:**
- Consumes: alles Vorherige
- Produces: nichts

- [ ] **Step 1: §2.0 — das Bedienelement-Bullet und die Referenzzeile**

In `docs/UI-Designsprache.md` das Bullet „**Bedienelement ist `<app-theme-menu>`** …" ersetzen:

```markdown
- **Bedienelement ist `<app-display-preferences>`** — zwei beschriftete `SegmentedControl`-Gruppen (Darstellung, Sprache), die ausschließlich im Panel von `<app-account-menu>` (§7.1) leben. Es gibt keine zweite Stelle im Repo, an der Theme oder Sprache umgestellt werden; Shell, Landing und Login setzen dasselbe Menü an dieselbe Stelle. Bewusst kein durchklickender Icon-Button: dessen nächster Zustand ist nicht ansagbar. Und bewusst `role="radiogroup"` statt `role="menuitemradio"` — das Panel hält gemischte Kinder, für die `role="menu"` nicht gilt.
```

Die Referenzzeile darunter:

```markdown
- **Referenz:** `web/src/app/core/theme/theme.service.ts` (+ `theme.service.spec.ts`), `web/src/app/shared/ui/display-preferences.ts`, `web/src/app/shared/ui/account-menu.ts`, `web/public/theme-init.js`, `web/src/index.html`, Flow-Test `web/e2e/theme.spec.ts`.
```

- [ ] **Step 2: §8.5 — die z-Leiter und die Clipping-Falle**

In der z-Leiter-Zeile den Einschub „(die Mobile-Disclosure des Headers liegt als `z-20` **im** Header-Kontext und damit über allem)" ersetzen durch:

```markdown
(das Panel von `<app-account-menu>` liegt als `z-30` **im** Header-Kontext und damit über allem)
```

Und direkt darunter als eigenes Bullet ergänzen — das ist der Preis der Entscheidung gegen CDK Overlay und gehört in denselben Absatz wie die z-Leiter:

```markdown
- **Das Panel des Account-Menüs darf nicht beschnitten werden.** Es ist rund 320 px hoch und hängt absolut positioniert aus einem 56-px-Header, liegt also **innerhalb** des Header-Stacking-Kontexts. Das ist Absicht (`popover.ts:16-19`: Panels öffnen aus Sticky-Leisten und müssen deren Kontext erben, was ein an `<body>` gehängter CDK-Overlay-Container nicht kann). Der Preis: bekommt irgendein Vorfahr des Headers `overflow: hidden` oder `overflow: auto`, wird das Panel abgeschnitten. Heute trägt keiner davon eines. **Wer am Shell-Layout arbeitet, prüft das am Gerät nach.**
```

- [ ] **Step 3: §11 — die Primitives-Liste**

In Checklistenpunkt 1 `ThemeMenu` durch `AccountMenu` + `DisplayPreferences` + `Avatar` ersetzen:

```markdown
`DateRangeMenu`/`UsageRangeMenu` · `AccountMenu` + `DisplayPreferences` + `Avatar` · `.app-input*`.
```

- [ ] **Step 4: Den `DECISIONS.md`-Eintrag schreiben**

Als **neuen obersten** Eintrag in `docs/DECISIONS.md` (absteigend nach Datum):

```markdown
## 2026-08-08 — Der Header trägt ein Element statt sechs, und das Profilbild reist im Cookie

**Betrifft:** `web/src/app/shared/ui/account-menu.ts`, `avatar.ts`, `display-preferences.ts`, `segmented-control.ts`, `web/src/app/features/shell/app-shell.ts`, `landing-page.html`, `login-page.ts`, `web/src/app/core/auth/auth.service.ts`, `auth.model.ts`, `src/EmotePurge.Api/Endpoints/AuthEndpoints.cs`, `Auth/TwitchClaimTypes.cs`, `Program.cs`, `src/EmotePurge.Core/Twitch/TwitchProfileImage.cs`, `TwitchModels.cs`, `src/EmotePurge.Infrastructure/Twitch/TwitchApiDtos.cs`, `TwitchHelixClient.cs`, `docs/UI-Designsprache.md`

Drei Entscheidungen in einem Umbau.

**Ein Menü statt sechs Dauer-Controls.** Der Header rechts trug Theme-Icon, `DE EN`, Admin-Link, „Meine Abstimmungen", Username und Logout-Button — auf jedem Bildschirm, in jeder Sitzung. Gemessen an der Regel, dass die App-Kopfzeile nur trägt, was wirklich überall gebraucht wird, sind das fünf zu viel. Das Argument trägt am Desktop genauso wie auf dem Handy, während „der Header wird schlanker" dort schwächer wiegt, wo Platz ohnehin da ist. Mit dem Umbau entfallen der Burger, die Mobile-Disclosure und rund 100 Zeilen handgebautes Dismiss-Verhalten — das dritte der drei Duplikate, die der Doc-Kommentar von `popover.ts` selbst benennt. Der Header hat danach keinen `md:`-Zweig mehr. Der Login-Button bleibt bewusst **außerhalb** des Menüs: ein Aufruf zur Anmeldung gehört nicht hinter eine Klappe.

**Disclosure statt `role="menu"`.** Das Panel hält gemischte Kinder — zwei Router-Links, zwei Radiogroups, einen Button. `role="menu"` verlangt `menuitem`-Kinder; eine Radiogroup darin ist nicht valide. Trigger trägt `aria-expanded` + `aria-haspopup="dialog"` + `aria-label`, das Panel ist ein schlichter Container mit zwei `role="radiogroup"`-Inseln. Das ist ein **Rückbau** gegenüber `theme-menu.ts`, das `menuitemradio` benutzte — die Shell hatte dieselbe Entscheidung für ihre Disclosure aber bereits getroffen, und zwei Umgangsweisen für dieselbe Sache sind schlechter als eine korrekte. `aria-controls` steht bewusst **nicht** am Trigger: das Panel existiert im geschlossenen Zustand nicht im DOM, und eine nicht auflösbare IDREF ist ungültiges ARIA.

**Claim statt DB-Spalte.** Twitch liefert `profile_image_url` in derselben Helix-Antwort, die beim Login ohnehin geholt und bisher weggeworfen wurde. Sie wandert als Claim ins Session-Cookie, nicht als Spalte in `User`: keine Migration, kein Refresh-Konzept für ein Bild, das niemand aktuell haben muss, und `/api/auth/me` bleibt DB- und HTTP-frei. Der Preis ist bekannt und akzeptiert — wer beim Deploy angemeldet ist, hat den Claim nicht, sieht bis zum nächsten Login das Monogramm, und das ist kein Fehler. Ein Avatar in der Admin-Nutzerliste wäre der Moment für die Spalte; dieser Umbau ist es nicht.

Zwei Nebenentscheidungen: Die URL wird beim Setzen des Claims von `-300x300` auf `-70x70` umgeschrieben (32 CSS px bei DPR 2), **bewacht** — greift das Muster nicht, geht die URL unverändert durch, und das ist die einzige Stelle im Code, die etwas über Twitchs URL-Form annimmt. Und `https://static-cdn.jtvnw.net` kommt in die `img-src`-Allowlist der CSP; ohne diesen Zusatz lädt kein Bild, unabhängig vom Claim. Die Listen sind bewusst schmal — das ist ein begründeter Zusatz, kein Aufweichen.
```

- [ ] **Step 5: Die Spec-Statuszeile nachziehen**

In `docs/superpowers/specs/2026-08-08-account-menu-design.md`, Zeile 3:

```markdown
**Stand:** 2026-08-08 · **Status:** umgesetzt
```

- [ ] **Step 6: Den mechanischen Detektor laufen lassen**

Jetzt — und erst jetzt, wenn die UI steht:

```bash
node C:\Users\admin\.claude\skills\impeccable\scripts\detect.mjs --json web/src/app/shared/ui/account-menu.ts web/src/app/shared/ui/avatar.ts web/src/app/shared/ui/display-preferences.ts web/src/app/shared/ui/segmented-control.ts web/src/app/features/shell/app-shell.ts
```

Befunde durchgehen und beheben, was zutrifft. Was nicht zutrifft, wird im Bericht an den Nutzer benannt statt stillschweigend übergangen.

- [ ] **Step 7: Committen**

```bash
git add docs/UI-Designsprache.md docs/DECISIONS.md docs/superpowers/specs/2026-08-08-account-menu-design.md
git commit -m "docs: record the account menu as the one header control"
```

---

## Abschluss

- [ ] **Vollständiger Durchlauf, alle vier Suiten**

```bash
dotnet build EmotePurge.slnx
dotnet test EmotePurge.slnx                # braucht laufendes Docker (Testcontainers)
npm --prefix web test -- --watch=false
npm --prefix web run e2e                   # nur ohne laufende Api auf :5151
npm --prefix web run lint
```

- [ ] **Stack neu bauen, damit der Nutzer testen kann**

```bash
docker compose up -d --build
```

`--build` ist nicht optional: `up` allein reused ein vorhandenes, potenziell uraltes Image klaglos (Regel 15).

- [ ] **Prod-Migration:** keine. Der Umbau fasst kein DB-Schema an — das war der Sinn der Claim-Entscheidung.

- [ ] **Nach dem Deploy dem Nutzer sagen:** wer beim Deploy angemeldet ist, sieht zunächst das Monogramm statt seines Bildes. Beim nächsten Login heilt das von selbst. Das ist kein Fehler und braucht keine Aktion.
