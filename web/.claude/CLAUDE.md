
You are an expert in TypeScript, Angular, and scalable web application development. You write functional, maintainable, performant, and accessible code following Angular and TypeScript best practices.

## UI design language (binding)

**Every UI change under `web/` MUST follow [`docs/UI-Designsprache.md`](../../docs/UI-Designsprache.md)** — colour tokens and theming, surfaces and rows (stretched-link contract), the sprite sheet (bands, sidecar, dock), typography scale, button/badge/banner primitives, destructive-action tiers, form and field-error ARIA patterns, loading/empty states, dialogs, navigation, i18n duties, and the accessibility checklist. Use its "Neue UI bauen" checklist before finishing any UI work, and verify layout-affecting changes with the UI audit harness described there. Do not rebuild what `shared/ui/` already provides.

## TypeScript Best Practices

- Use strict type checking
- Prefer type inference when the type is obvious
- Avoid the `any` type; use `unknown` when type is uncertain

## Angular Best Practices

- Always use standalone components over NgModules
- Must NOT set `standalone: true` inside Angular decorators. It's the default in Angular v20+.
- Do NOT set `changeDetection: ChangeDetectionStrategy.OnPush` explicitly. `OnPush` is the default in Angular v22+.
- Use signals for state management
- Implement lazy loading for feature routes
- Do NOT use the `@HostBinding` and `@HostListener` decorators. Put host bindings inside the `host` object of the `@Component` or `@Directive` decorator instead
- Use `NgOptimizedImage` for all static images.
  - `NgOptimizedImage` does not work for inline base64 images.
  - **SVG: use it, but add `disableOptimizedSrcset`.** The directive is built for raster images —
    it generates a density `srcset`, warns about intrinsic-versus-rendered size, and preconnects to
    the image loader. A vector has no resolution, so the `srcset` is meaningless and the size checks
    have nothing to measure. The directive stays anyway, because `@angular-eslint/template/prefer-ngsrc`
    has no per-extension exemption and swapping it for three `eslint-disable` comments trades the
    same noise for a lint rule with holes. Revisit if Angular ever ships an SVG-aware opt-out.

## Accessibility Requirements

- It MUST pass all AXE checks.
- It MUST follow all WCAG AA minimums, including focus management, color contrast, and ARIA attributes.

### Components

- Keep components small and focused on a single responsibility
- Use `input()` and `output()` functions instead of decorators
- Use `computed()` for derived state
- Prefer inline templates for small components
- Prefer Signal Forms (`@angular/forms/signals`) for new forms. They are stable in Angular v22+ and provide signal-based state, type-safe field access, and schema-based validation
- When not using Signal Forms, prefer Reactive forms instead of Template-driven ones
- Do NOT use `ngClass`, use `class` bindings instead
- Do NOT use `ngStyle`, use `style` bindings instead
- When using external templates/styles, use paths relative to the component TS file.

## State Management

- Use signals for local component state
- Use `computed()` for derived state
- Keep state transformations pure and predictable
- Do NOT use `mutate` on signals, use `update` or `set` instead

## Templates

- Keep templates simple and avoid complex logic
- Use native control flow (`@if`, `@for`, `@switch`) instead of `*ngIf`, `*ngFor`, `*ngSwitch`
- Use the async pipe to handle observables
- Do not assume globals like (`new Date()`) are available.

## Services

- Design services around a single responsibility
- Use the `providedIn: 'root'` option for singleton services
- Prefer the `@Service` decorator over `@Injectable({providedIn: 'root'})` for new singleton services (Angular v22+)
- Use the `inject()` function instead of constructor injection

## Member order (binding)

Classes are laid out in this order. `npm run lint` enforces the parts a linter can see; the rest is review discipline.

1. Module-level `const`s and types (outside the class)
2. `input()` / `output()` / `model()`
3. `inject()`
4. `rxResource()` / `signal()` / `computed()` / plain fields
5. `constructor()` with its `effect()`s
6. `public` / `protected` methods — the ones the template calls
7. `private` helpers, grouped by topic

**What ESLint actually enforces:** only `field → constructor → public → protected → private`. It cannot see steps 2–4 apart, because `input()`, `inject()`, `signal()` and `computed()` are all just property initializers to the parser — keeping inputs before `inject()` before state is on you.

**Deliberately not enabled: `@angular-eslint/inject-at-top`.** It wants `inject()` before every other member, which contradicts the order above and would turn the majority of existing components into violations. Config follows the codebase here, not the other way round.

Two habits this exists to stop, both found by measurement in the 2026-08-01 review: declaring new state *after* the constructor because that is where the cursor happens to be, and dropping a new private helper next to its caller instead of at the end.

## EmotePurge-specific conventions

This is the Angular frontend (Modul D) for EmotePurge, a .NET 10 backend at the repo root (`src/EmotePurge.Api`, `.Core`, `.Infrastructure`, `.Worker`). See the root `CLAUDE.md` for the overall architecture; this file only covers `web/`-specific conventions.

- **Auth is HttpOnly-cookie-based, not JWT.** The API never returns a bearer token to the frontend — `POST /api/auth/twitch/login` is a full browser redirect (not an `HttpClient` call), and after the callback the session lives entirely in the cookie. Never store the auth session itself in `localStorage`/`sessionStorage`. (The one deliberate exception, in a later slice: 7TV write-tokens for the mass-delete engine live in `sessionStorage`, per the Zero-Knowledge principle in the root `Architectur.md` — that's a different, unrelated token.)
- **Dev workflow: start the API with `dotnet run --project src/EmotePurge.Api` (port 5151), not the VS Code `Api` F5 launch config.** That launch config hardcodes `ASPNETCORE_URLS=http://0.0.0.0:8080`, which doesn't match the Twitch OAuth redirect URI registered for local dev (`http://localhost:5151/api/auth/twitch/callback`) — using it while doing frontend work silently breaks login.
- **`ng serve` uses `web/proxy.conf.json` to forward `/api` to `http://localhost:5151`.** This keeps every API call same-origin from the browser's perspective even in dev, so cookies flow automatically without any CORS setup — don't add `withCredentials: true` or a CORS policy on the backend, it's not needed in dev or prod.
- **Prod topology: no separate frontend container.** The Angular production build is copied into `src/EmotePurge.Api/wwwroot/` at Docker build time (see the `web-build` stage in `src/EmotePurge.Api/Dockerfile`) and served by the Api itself as static files — same-origin there too, same reasoning as above.
- **New backend capabilities go through the existing service-layer pattern, never `AppDbContext` directly.** If a frontend feature needs new backend data, that means a new method on an `IXxxService`/`XxxService` pair (interface in `EmotePurge.Core/Services/`, implementation in `EmotePurge.Infrastructure/Services/`) plus a Minimal API endpoint in `src/EmotePurge.Api/Endpoints/*.cs` (grouped by domain, e.g. `ChannelEndpoints.cs`, `VoteSessionEndpoints.cs` — endpoints no longer live in `Program.cs` itself, see the root `CLAUDE.md` architecture log entry from 2026-07-28) — this is a hard rule in the root `CLAUDE.md`, applies here too.
- **Pages do not build the SSE pipeline themselves — use `liveEvents()` / `liveReload()` from `core/live/live-reload.ts`.** `liveEvents(url, accept)` when the handler needs the individual event (its `channel`, its `sessionId`); `liveReload(url, {accept, debounceMs})` when it only means "something changed, refetch" — it collapses a burst into one emission and hands over the set of types seen in it, which is what removes the need for a `syncSeenSinceReload`-style field. Both accept a `Signal<string>` for a URL that follows a route parameter and apply `takeUntilDestroyed()` themselves, so call them from a field initializer or the constructor.
- **`EventSource` is never constructed directly — always go through the `EVENT_SOURCE_FACTORY` InjectionToken** (`core/live/event-source.factory.ts`). SSE streams bypass `HttpClient` entirely, so neither the `apiAuthInterceptor` (401 handling) nor `HttpTestingController` apply to them; the factory token is what makes `LiveUpdateService` stubbable in Vitest, and Playwright specs stub `window.EventSource` via `installLiveStub` in `e2e/support/mocks.ts`. Live events are thin notifications (`{type, channel, sessionId?}`) — react by re-fetching through the existing REST services, never by trusting event payloads as data.
- **Commit convention: Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, …), same as the rest of the repo.

