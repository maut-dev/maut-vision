## Summary

<!-- What does this change do and why? Link the issue: "Closes #123". -->

## Type of change

- [ ] Bug fix (`fix`)
- [ ] New feature (`feat`)
- [ ] Refactoring without behaviour change (`refactor`)
- [ ] Performance (`perf`)
- [ ] Documentation (`docs`)
- [ ] Build or packaging (`build`)
- [ ] Breaking change (`!`, describe the migration below)

## Checks

<!-- Mark what you ran; see docs/Code/review-and-testing.md. -->

- [ ] `.\start.ps1 -NoLaunch`
- [ ] `npm --prefix ui run typecheck`
- [ ] `.\smoke.ps1 -SkipBuild`
- [ ] Python unit tests (`python -I -m unittest discover -s tests -t .` in `python/`)
- [ ] `.\perf.ps1` (storage, startup, lists, galleries)
- [ ] Manual check in the application (describe below)

## Checklist

- [ ] The branch is based on the latest `dev` and targets `dev`.
- [ ] Commits follow Conventional Commits `type(scope): description`.
- [ ] C# and TypeScript DTOs are changed together.
- [ ] Documentation is updated if a rule changed.
- [ ] `CHANGELOG.md` has an entry under `[Unreleased]` for user-visible changes.

## Screenshots

<!-- For UI changes. -->
