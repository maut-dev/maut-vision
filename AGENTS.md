# Contributor and agent rules

Read the [architecture overview](docs/overview.md) and the rules of the area you change before editing. User requirements take priority.

## Architecture

- Follow [Core](docs/Core/architecture.md), [Desktop](docs/Desktop/architecture.md) and the [code style](docs/Code/style.md). Public
  contracts live in `Interfaces`; Core implementations are `internal` in their aspect folders.
- `CoreFactory` keeps a single instance. Desktop shows the loading window, awaits `Core.InitializeAsync`, then opens the main UI.
- Managers are independent; scenarios that span several managers are coordinated by Jobs. Do not add a separate use-case layer.
- Long operations are jobs; requests are for short operations only. Large data goes through the resource channel, never through bridge
  messages.
- Change C# and TypeScript DTOs together. The UI and the host ship together without a `protocolVersion`.
- The project layout `projects/<projectId>/project.json` is fixed; schema changes are new steps appended to `ProjectSchema`.
- Hot paths use hardware acceleration (SIMD/AVX, GPU) with a scalar fallback and a measurement.

## Code and documentation

- C# and TypeScript/TSX use 4 spaces. PowerShell scripts with non-ASCII text are UTF-8 with BOM.
- The application UI and its messages are in Russian; identifiers, new comments, commits and the main documentation are in English.
- Documentation describes stable rules: topic, a short explanation, an example, details when needed. No progress reports, readiness lists
  or history in `docs/`; such notes belong in pull requests and the changelog.
- Do not present stubs as implemented features or old runs as checks of new changes.

## Checks

Run the [checks](docs/Code/review-and-testing.md) that match the change: at least `npm --prefix ui run typecheck` and
`.\smoke.ps1 -SkipBuild` after `.\start.ps1 -NoLaunch`; `python -I -m unittest discover -s tests -t .` in `python/` for worker changes;
`perf.ps1` for storage, startup, lists and galleries. Do not run parallel builds into the same output.

## Git

Follow the [Git rules](docs/Code/git.md):

- `master` holds releases, `dev` holds linear development; work branches are `<type>/<topic>` (`feature/…`, `fix/…`, `docs/…`,
  `refactor/…`), release and hotfix branches are `release/x.y.z` and `hotfix/x.y.z`.
- Commits are Conventional Commits `type(scope): description` with scopes `Core/<Block>`, `Desktop/<Block>`, `UI/<Block>`, `Python`,
  `Docs`, `Build`, `Repo`.
- Never commit secrets, working data, downloaded models, dependencies or build output. Publishing, force-pushing and rewriting shared
  history require an explicit request.
