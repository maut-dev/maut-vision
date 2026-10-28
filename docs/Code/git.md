# Git: commits, branches and releases

## Branches

| Branch | Purpose |
|---|---|
| `master` | Released versions only. Every commit on `master` is a release and carries a tag `vX.Y.Z`. |
| `dev` | Linear development history. Work branches start from `dev` and return to it. |
| `feature/<topic>` | A new capability. |
| `fix/<topic>` | A bug fix for the next regular release. |
| `docs/<topic>`, `refactor/<topic>`, `perf/<topic>`, `test/<topic>`, `build/<topic>` | Other work by commit type. |
| `release/x.y.z` | Release preparation: version bump, changelog, final checks. Starts from `dev`. |
| `hotfix/x.y.z` | An urgent fix of a released version. Starts from `master`. |

Topic names are short lowercase-kebab-case: `feature/job-cancellation`, `fix/startup-paths`. Keep `dev` linear: rebase a work
branch on `dev` before merging and merge with fast-forward or squash. Never force-push `master` or `dev`.

## Commits

One commit contains one coherent change; the subject explains the result. Commits follow
[Conventional Commits](https://www.conventionalcommits.org/):

```text
<type>(<scope>): <description>

feat(Core/Jobs): add job cancellation
fix(Core/Projects): reject invalid project names
refactor(Core/BaseModels): simplify catalog loading
fix(Desktop/Transport): handle cancelled requests
feat(UI/Projects): add project selection
build(Build): include initial data in publish output
docs(Docs): clarify working directory rules
```

Types: `feat` — a capability, `fix` — a bug fix, `refactor` — structure without behaviour change, `perf` — performance,
`test` — checks, `docs` — documentation, `build` — build and dependencies, `ci` — automation, `chore` — repository maintenance,
`style` — code formatting only.

The scope names the layer and block: `Core/<Block>`, `Desktop/<Block>`, `UI/<Block>`, matching the code (`Jobs`, `Projects`,
`BaseModels`, `Transport`, `Events`). Shared changes use `Core`, `Desktop`, `UI`, `Python`, `Build`, `Docs`, `Repo`. The case is
fixed. When several files change for one function, the scope follows its main responsibility.

The description is in English, starts with an imperative verb (`add`, `fix`, `remove`, `simplify`) and has no trailing period;
keep the whole subject within 72 characters. The body of a complex change explains the reason, the result and the checks. An
incompatible change is marked with `!` before the colon and a `BREAKING CHANGE:` footer:

```text
refactor(Core)!: use one working directory

BREAKING CHANGE: CoreOptions accepts WorkDirectory instead of separate roots.
```

Before committing, review the staged diff and run the [checks](review-and-testing.md) that match the change. Secrets, local
settings, working data, downloaded models, dependencies and build output never go into commits. The `catalog` folder and the
initial `appdata` set are versioned.

## Versions and releases

Versions follow [Semantic Versioning](https://semver.org/) `MAJOR.MINOR.PATCH`: MAJOR for incompatible changes of project data or
behaviour, MINOR for new capabilities, PATCH for fixes. Every published build has its own version. The single source of the
version is `Version` in `Directory.Build.props`; `ui/package.json` carries the same number, and `CHANGELOG.md` records each release.
The release procedure is described in [CONTRIBUTING.md](../../CONTRIBUTING.md#releasing).
