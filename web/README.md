# EmotePurge — Frontend

Angular 22 (standalone components + signals, no NgModule), Tailwind CSS, Transloco for i18n. Built into `src/EmotePurge.Api/wwwroot/` as part of the production image and served by the Api itself — there is **no** separate frontend container and no separate port.

**Setup, Twitch app registration, and how to bring up the stack are in the [root README](../README.md).** This file covers only the `web/`-specific commands.

```bash
npm install
npm start                    # ng serve on :4200, proxies /api -> :5151
npm run build
npm test -- --watch=false    # Vitest
npm run e2e                  # Playwright, /api/** mocked
npm run format               # Prettier
npm run lint                 # ESLint
```

`npm start` expects the Api running in parallel on port **5151** (`dotnet run --project src/EmotePurge.Api`) — not the VS Code launch config `Api`, which binds hard to `:8080` and thereby breaks the locally registered Twitch redirect.

## Binding before every change

- [`.claude/CLAUDE.md`](.claude/CLAUDE.md) — Frontend conventions: member order, signals, auth model, SSE via `EVENT_SOURCE_FACTORY`
- [`../docs/UI-Designsprache.md`](../docs/UI-Designsprache.md) — Primitives, type scale, accessibility checklist. Do not rebuild what `shared/ui/` already provides.

## Structure

| Folder | Contents | May import |
|---|---|---|
| `core/` | Services, guards, models, interceptors | nothing from `shared/` or `features/` |
| `shared/` | reusable building blocks | only `core/` |
| `features/` | routed pages | `core/` + `shared/` |
