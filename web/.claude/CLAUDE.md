
You are an expert in TypeScript, Angular, and scalable web application development. You write functional, maintainable, performant, and accessible code following Angular and TypeScript best practices.

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

## EmotePurge-specific conventions

This is the Angular frontend (Modul D) for EmotePurge, a .NET 10 backend at the repo root (`src/EmotePurge.Api`, `.Core`, `.Infrastructure`, `.Worker`). See the root `CLAUDE.md` for the overall architecture; this file only covers `web/`-specific conventions.

- **Auth is HttpOnly-cookie-based, not JWT.** The API never returns a bearer token to the frontend — `POST /api/auth/twitch/login` is a full browser redirect (not an `HttpClient` call), and after the callback the session lives entirely in the cookie. Never store the auth session itself in `localStorage`/`sessionStorage`. (The one deliberate exception, in a later slice: 7TV write-tokens for the mass-delete engine live in `sessionStorage`, per the Zero-Knowledge principle in the root `Architectur.md` — that's a different, unrelated token.)
- **Dev workflow: start the API with `dotnet run --project src/EmotePurge.Api` (port 5151), not the VS Code `Api` F5 launch config.** That launch config hardcodes `ASPNETCORE_URLS=http://0.0.0.0:8080`, which doesn't match the Twitch OAuth redirect URI registered for local dev (`http://localhost:5151/api/auth/twitch/callback`) — using it while doing frontend work silently breaks login.
- **`ng serve` uses `web/proxy.conf.json` to forward `/api` to `http://localhost:5151`.** This keeps every API call same-origin from the browser's perspective even in dev, so cookies flow automatically without any CORS setup — don't add `withCredentials: true` or a CORS policy on the backend, it's not needed in dev or prod.
- **Prod topology: no separate frontend container.** The Angular production build is copied into `src/EmotePurge.Api/wwwroot/` at Docker build time (see the `web-build` stage in `src/EmotePurge.Api/Dockerfile`) and served by the Api itself as static files — same-origin there too, same reasoning as above.
- **New backend capabilities go through the existing service-layer pattern, never `AppDbContext` directly.** If a frontend feature needs new backend data, that means a new method on an `IXxxService`/`XxxService` pair (interface in `EmotePurge.Core/Services/`, implementation in `EmotePurge.Infrastructure/Services/`) plus a Minimal API endpoint in `src/EmotePurge.Api/Program.cs` — this is a hard rule in the root `CLAUDE.md`, applies here too.
- **Commit convention: Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, …), same as the rest of the repo.

