# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`AGENTS.md` is the main orientation document for this repo (architecture, data model, storage rules, DI order, test caveats). It is imported here so there is one source of truth; update it, not this file, when those details change.

@AGENTS.md

## Commands not spelled out in AGENTS.md

```bash
# Build everything, including the test project
dotnet build Hymnal.slnx

# Standard full test run (skips the test that always hangs)
dotnet test Hymnal.slnx --filter "FullyQualifiedName!~GanttView_RowMoveShortcutHelper" --blame-hang-timeout 90s

# One test class or one test
dotnet test Hymnal.slnx --filter "FullyQualifiedName~BeatBoardServiceTests"
dotnet test Hymnal.slnx --filter "FullyQualifiedName~BeatBoardServiceTests.<TestMethodName>"
```

## Releases and updates

Every push to `main` that passes CI is released by the shared workflow in [MattEland.HopeLab.Shared](https://github.com/QuantumHope/MattEland.HopeLab.Shared) (`.github/workflows/app-release.yml`, called from `ci.yml`) as `v<major>.<minor>.<commits on main>`: self-contained win-x64/linux-x64 archives, a per-user `setup.exe`, a `.deb`, `latest.json` and the install scripts. `hopelab-app.json` describes Hymnal to it (executable, icons, setup AppId, data folder); major.minor come from `<Version>` in `Directory.Build.props`. Never change `windows.setupAppId`.

In the app, `src/Hymnal/Infrastructure/Updates/` wires the shared `MattEland.HopeLab.Updates.ReactiveUI` package (version `0.1.*`): `HymnalUpdates` (options, kept in step with `hopelab-app.json` by a test), `HymnalUpdateHost` (toasts, browser, `settings.json` preferences) and `UpdateRestart` (save everything, start the new copy, exit). The banner is `Views/UpdateBannerView`, the Settings section `Views/UpdatesSettingsView`. Update downloads and a GitHub token live in `~/.hymnal/`; settings stay in the OS app-data folder. Only a release build (it has `release.json`) updates itself. Don't run the installers or `setup.exe` against the real profile.

## Test project scope

`tests/Hymnal.Core.Tests` is the only test project, but it references both `Hymnal.Core` and the `Hymnal` app and uses `Avalonia.Headless`. ViewModel and View tests (`ViewModels/`, `Views/`) live there next to the Core tests, so the headless isolation and watcher-disposal rules in AGENTS.md apply to UI tests too.

## Other agent tooling in the repo

`.github/copilot-instructions.md`, `.github/skills/` and `.planning/` belong to the GSD workflow, and `_bmad/` / `_bmad-output/` to BMAD. Use them only when the user asks for those workflows. `_bmad-output/planning-artifacts/architecture.md` is still the reference for architecture decisions.
