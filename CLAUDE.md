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

## Test project scope

`tests/Hymnal.Core.Tests` is the only test project, but it references both `Hymnal.Core` and the `Hymnal` app and uses `Avalonia.Headless`. ViewModel and View tests (`ViewModels/`, `Views/`) live there next to the Core tests, so the headless isolation and watcher-disposal rules in AGENTS.md apply to UI tests too.

## Other agent tooling in the repo

`.github/copilot-instructions.md`, `.github/skills/` and `.planning/` belong to the GSD workflow, and `_bmad/` / `_bmad-output/` to BMAD. Use them only when the user asks for those workflows. `_bmad-output/planning-artifacts/architecture.md` is still the reference for architecture decisions.
