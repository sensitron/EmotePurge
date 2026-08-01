# EmotePurge — Frontend

Angular 22 (Standalone Components + Signals, kein NgModule), Tailwind CSS, Transloco für i18n. Wird im Produktions-Image in `src/EmotePurge.Api/wwwroot/` gebaut und von der Api selbst ausgeliefert — es gibt **keinen** eigenen Frontend-Container und keinen eigenen Port.

**Setup, Twitch-App-Registrierung und wie man den Stack hochzieht, steht in der [Root-README](../README.md).** Hier stehen nur die `web/`-eigenen Kommandos.

```bash
npm install
npm start                    # ng serve auf :4200, proxied /api -> :5151
npm run build
npm test -- --watch=false    # Vitest
npm run e2e                  # Playwright, /api/** gemockt
npm run format               # Prettier
npm run lint                 # ESLint
```

`npm start` erwartet die Api parallel auf Port **5151** (`dotnet run --project src/EmotePurge.Api`) — nicht die VS-Code-Launch-Config `Api`, die hart auf `:8080` bindet und damit den lokal registrierten Twitch-Redirect bricht.

## Verbindlich vor jeder Änderung

- [`.claude/CLAUDE.md`](.claude/CLAUDE.md) — Frontend-Konventionen: Member-Reihenfolge, Signals, Auth-Modell, SSE über `EVENT_SOURCE_FACTORY`
- [`../docs/UI-Designsprache.md`](../docs/UI-Designsprache.md) — Primitives, Typo-Skala, A11y-Checkliste. Nicht neu bauen, was `shared/ui/` schon hat.

## Aufbau

| Ordner | Inhalt | Darf importieren |
|---|---|---|
| `core/` | Services, Guards, Models, Interceptors | nichts aus `shared/` oder `features/` |
| `shared/` | wiederverwendbare Bausteine | nur `core/` |
| `features/` | geroutete Seiten | `core/` + `shared/` |
