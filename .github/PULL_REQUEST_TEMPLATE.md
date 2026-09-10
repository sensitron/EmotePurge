## What changes, and why

<!-- Short description. Focus on why, not just what — the diff already shows what. -->

## Linked issue

Closes #

## Checklist

- [ ] `dotnet test EmotePurge.slnx` passes (needs Docker running — Testcontainers)
- [ ] `npm --prefix web test -- --watch=false` passes
- [ ] UI changes: `npm --prefix web run e2e` passes (only runs with no API listening on `:5151`)
- [ ] `node scripts/coverage-local.mjs` run, and the result looked at (it's an approximation, not a verdict — see CLAUDE.md)
- [ ] Formatters run: `dotnet format EmotePurge.slnx` and `npm --prefix web run format` (`npm --prefix web run lint` clean too)
- [ ] Changes a convention, contract or topology? Then this PR contains its `docs/DECISIONS.md` entry (Rule 3).
- [ ] Backend changes: verified live against real Postgres/Redis/Twitch/7TV, not just a green build (Rule 16).
