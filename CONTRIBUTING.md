# Contributing

Emote Purge is a web app for analyzing, community-rating, and cleaning up 7TV emote sets on
Twitch — live chat tracking, community voting sessions, and a mass-delete engine against the 7TV
API. This document is the short, outward-facing version of the project's rules. The full rulebook
lives in [`CLAUDE.md`](CLAUDE.md); if the two ever disagree, `CLAUDE.md` wins.

## Getting set up

Follow the "One-time setup" section in [`README.md`](README.md) — Twitch app registration, `.env`,
database migration, and the two ways to start the stack (full Docker, or local with hot reload).
Don't duplicate those steps here; they change independently of this file.

## The test gates

"Done" in this repo means these commands pass, not just `dotnet build`:

```bash
dotnet test EmotePurge.slnx                # backend — needs Docker running (Testcontainers)
npm --prefix web test -- --watch=false     # frontend unit (Vitest)
npm --prefix web run e2e                   # frontend E2E (Playwright, /api/** mocked)
```

Two known traps:

- `dotnet test` spins up real, ephemeral Postgres/Redis containers via Testcontainers. Without a
  running Docker daemon, it fails outright.
- The Playwright E2E suite only works when nothing is listening on port `5151`. Each test mocks
  its own `/api/**` calls; if a real Api answers instead, a `401` sends the app to the login page
  and most of the suite fails with misleading "element not found" errors, unrelated to what you
  changed.

Before opening a PR, also run:

```bash
node scripts/coverage-local.mjs
```

The SonarCloud quality gate requires 80% coverage on new code and blocks merges — none of the
three suites above measure coverage on their own, so this is how you find out before the PR does.

## Commit style

Conventional Commits (`feat:`, `fix:`, `chore:`, `docs:`, …). Prefer several logically separate
commits over one grab-bag commit per feature.

## The decision log

If your commit changes a convention, a contract, or a topology, it must add its entry to
[`docs/DECISIONS.md`](docs/DECISIONS.md) in the same commit — not as a follow-up. This is the rule
outside contributors are most likely to miss: a renamed endpoint, a new required config value, a
changed Docker topology, or a new architectural pattern all count, even if the code change itself
looks small.

## Formatting

```bash
npm --prefix web run format   # Prettier
dotnet format EmotePurge.slnx
```

CI checks both of these plus `npm --prefix web run lint`. A repo-wide reformat goes into its own
`style:` commit that touches nothing else.

## Language

Outward-facing artefacts — issues, PRs, code comments, commit messages, README, log and `throw`
messages — are English (see issue #152). The decision log's existing entries, plans, and concept
documents in `docs/` remain German; that split is intentional and documented in `CLAUDE.md` under
"Sprache".

## Where to read more

| Document | What's in it | Language |
|---|---|---|
| [`CLAUDE.md`](CLAUDE.md) | The full rulebook: 22 numbered rules, commands, architecture, layering | German |
| [`docs/Architectur.md`](docs/Architectur.md) | Full specification — modules, DB schema, Docker topology, communication flow | German |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | Chronological log of every architecture/infrastructure decision and why | German |
| [`docs/UI-Designsprache.md`](docs/UI-Designsprache.md) | Binding visual design language for anything under `web/` | German |
