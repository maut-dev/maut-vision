# Contributing to Maut Vision

Thank you for your interest in Maut Vision! Bug reports, ideas, documentation fixes and code are welcome. By participating you agree to
follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- **Bugs and ideas** — open an issue with the [bug report](.github/ISSUE_TEMPLATE/bug_report.yml) or
  [feature request](.github/ISSUE_TEMPLATE/feature_request.yml) form. Search existing issues first.
- **Security problems** — never in a public issue; see [SECURITY.md](SECURITY.md).
- **Larger changes** — open an issue first and agree on the approach, so that work is not wasted.
- Read the [architecture overview](docs/overview.md) and the rules of the area you change. Contributor rules in short are in
  [AGENTS.md](AGENTS.md); a Russian overview is in [docs/ru/README.md](docs/ru/README.md).

## Development environment

| Tool | Version |
|---|---|
| Windows | 10 or 11, x64 |
| .NET SDK | 10 |
| Node.js + npm | current LTS |
| WebView2 Runtime | Evergreen (preinstalled on Windows 11) |
| Python | 3.14 (for training and checks) |
| NVIDIA GPU + driver with CUDA 13 support | optional, for GPU training |
| Inno Setup | 6.5+ (only to build the installer) |

```powershell
git clone https://github.com/maut-dev/maut-vision.git
cd maut-vision
git switch dev
.\tools\setup-python.ps1          # creates .dev/python-venv from python/requirements.txt (several GB)
.\start.ps1                       # builds UI and solution, starts the app with .dev/DevWorkDirectory
```

`start.ps1` uses `-Python <python.exe>`, then `MAUT_PYTHON`, then `.dev/python-venv`, then a worker built into `.dev/python-worker`.
Without any of them the application starts, but training and checks are unavailable.

## Checks

Run the checks that match your change ([details](docs/Code/review-and-testing.md)):

```powershell
.\start.ps1 -NoLaunch                 # build
npm --prefix ui run typecheck         # TypeScript
.\smoke.ps1 -SkipBuild                # Core, page and host checks with a fake Python worker
cd python; python -I -m unittest discover -s tests -t .   # worker unit tests
.\perf.ps1                            # storage, startup, lists and galleries on a large library
.\tools\run-e2e.ps1                   # end-to-end scenario on an installed test build (GPU)
```

## Branches

| Branch | Purpose |
|---|---|
| `master` | releases only; it receives nothing but merges of `dev`, each tagged `vX.Y.Z` |
| `dev` | linear development; the default target of pull requests |
| `feature/<topic>`, `fix/<topic>`, `docs/<topic>`, `refactor/<topic>`, `perf/<topic>`, `test/<topic>`, `build/<topic>` | work branches from `dev` |
| `release/x.y.z` | release preparation from `dev` |
| `hotfix/x.y.z` | urgent PATCH release; starts from `dev`, like any other change |

Recommended GitHub settings: protect `master` and `dev` (pull requests required, no force-push, linear history on `dev`), allow squash and
rebase merges for work branches, delete head branches after merge.

## Commits

[Conventional Commits](https://www.conventionalcommits.org/) with a scope that names the layer and block:

```text
feat(Core/Jobs): add job cancellation
fix(UI/Images): keep the selection after deletion
docs(Docs): describe the resource channel
build(Build): print the installer hash
```

Types: `feat`, `fix`, `refactor`, `perf`, `test`, `docs`, `build`, `ci`, `chore`, `style`. Scopes: `Core/<Block>`, `Desktop/<Block>`,
`UI/<Block>`, `Python`, `Docs`, `Build`, `Repo`. Imperative English description without a trailing period, at most 72 characters in the
subject. Incompatible changes use `!` and a `BREAKING CHANGE:` footer. See [docs/Code/git.md](docs/Code/git.md).

## Pull requests

1. Branch from `dev`: `git switch -c feature/my-topic dev`.
2. Keep the change focused; update the documentation when a rule changes and `CHANGELOG.md` under `[Unreleased]` for user-visible changes.
3. Rebase on the latest `dev` before opening the pull request; fill in the [template](.github/PULL_REQUEST_TEMPLATE.md).
4. A maintainer reviews and merges with squash or rebase so that `dev` stays linear.

Code style: [docs/Code/style.md](docs/Code/style.md). C# and TypeScript/TSX use 4 spaces; C# and TypeScript DTOs change together. The
new user-facing texts go to the UI catalogs (Russian and English), Core and Desktop send codes and values: see
[docs/UI/localization.md](docs/UI/localization.md).

## Releasing

Versions follow [Semantic Versioning](https://semver.org/) `MAJOR.MINOR.PATCH`; every published build has its own version.

1. Create a release branch from `dev`: `git switch -c release/1.1.0 dev`.
2. Bump the version in `Directory.Build.props` (`Version`) and `ui/package.json` (`version`, then `npm --prefix ui install
   --package-lock-only` to update the lock file).
3. Move the `[Unreleased]` entries of `CHANGELOG.md` to `[1.1.0] - YYYY-MM-DD` and update the comparison links.
4. Run the checks, rebuild the Python worker if its sources changed (`python/scripts/build-worker.ps1`) and build the installer:
   `.\build-installer.ps1`. The script prints the installer SHA-256.
5. Merge the release branch into `dev`, then `dev` into `master` without fast-forward, and tag the merge:

   ```powershell
   git switch dev
   git merge --ff-only release/1.1.0
   git switch master
   git merge --no-ff dev -m "chore(Repo): release 1.1.0"
   git tag -a v1.1.0 -m "Maut Vision 1.1.0"
   git switch dev
   git merge --ff-only master     # dev takes the release merge, so `git log dev..master` stays empty
   git push origin dev master v1.1.0
   ```

6. Publish the installer, its SHA-256 and the changelog section as a GitHub release (or at the download location), then delete the release
   branch.

**Changes always flow `dev` → `master`.** Nothing is merged or committed into `master` directly — not features, not fixes, not
documentation. A change reaches `master` only with the next release merge of `dev`, so `dev` always contains everything in `master`
(`git log dev..master` is empty).

**Hotfix.** Branch `hotfix/1.0.1` from `dev`, fix, bump the PATCH version and the changelog, merge into `dev`, then release `dev` into
`master` as above and tag `v1.0.1`. Delete the branch afterwards.

## License

By contributing you agree that your contributions are licensed under the [GNU AGPL-3.0](LICENSE), the license of the project.
