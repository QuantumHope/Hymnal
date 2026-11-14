# Hymnal

Hymnal is a desktop editor for Book.txt-driven manuscripts. It is built with Avalonia UI and a .NET core service layer, and is intended to support structured writing workflows where chapter order comes from a manifest file.

## Status

- Current stage: active development
- Frameworks: .NET 10 (preview), Avalonia 12
- App type: cross-platform desktop UI
- License: add your OSS license file if not already present

## Install

Every change merged to `main` is released on [GitHub](https://github.com/QuantumHope/Hymnal/releases/latest). From the latest release, download one of:

- **Windows:** `Hymnal-<version>-win-x64-setup.exe`, a per-user setup (no admin rights). It installs to `%LOCALAPPDATA%\Programs\Hymnal` with a Start menu entry, and appears in Apps & features.
- **Debian, Ubuntu, Pop!_OS:** `hymnal_<version>_amd64.deb` (`sudo apt install ./hymnal_<version>_amd64.deb`). It installs to `/opt/hymnal` with an app-menu entry.
- The `win-x64` zip or `linux-x64` tar.gz, to unpack anywhere.

Or install with one command (no admin rights; running it again updates in place, and `-Uninstall` / `--uninstall` removes Hymnal and keeps your settings):

```powershell
# Windows: %LOCALAPPDATA%\Programs\Hymnal and a Start menu entry
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/QuantumHope/MattEland.HopeLab.Shared/main/installers/install.ps1))) -Repo QuantumHope/Hymnal
```

```bash
# Linux: ~/.local/share/hymnal/app and an app-menu entry
curl -fsSL https://raw.githubusercontent.com/QuantumHope/MattEland.HopeLab.Shared/main/installers/install.sh | bash -s -- --repo QuantumHope/Hymnal
```

### Updating

An installed Hymnal checks GitHub a little after it starts and every few hours (turn this off in **Settings → Updates**). When a new version is out, a banner at the top of the window offers to install it: Hymnal saves your work, replaces its files where it is installed, and reopens. **Help → Check for Updates** and **Settings → Updates** check on demand. A `.deb` install is updated with `apt-get`, so you're asked for your password. A copy built from source doesn't update itself.

Update downloads go in `~/.hymnal/updates`. Hymnal's releases are public, so no GitHub token is needed; for a private fork, set `HYMNAL_GITHUB_TOKEN` or save a token in Settings → Updates (kept in `~/.hymnal/github-token`).

## What Hymnal Does

- Opens a manuscript workspace by selecting a folder.
- Locates Book.txt at either:
	- workspace-root/Book.txt
	- workspace-root/manuscript/Book.txt
- Parses Book.txt lines into a chapter list.
- Opens chapter files in a single-buffer editor.
- Saves chapter files atomically (temp file + replace).
- Tracks dirty state and performs save-before-switch behavior.
- Watches open chapter files and detects external edits.
- Provides per-chapter notes saved under .hymnal-data/notes.
- Persists last opened workspace and chapter between launches.

## Core Concepts

### Workspace Structure

At minimum, a valid workspace must contain Book.txt in one of the two supported locations.

Example:

```text
MyProject/
	Book.txt
	chapter-one.md
	chapter-two.md
```

or:

```text
MyProject/
	manuscript/
		Book.txt
		chapter-one.md
```

### Book.txt Format

- Each non-blank line in Book.txt is treated as a relative chapter path.
- Blank lines are ignored.
- Missing files are shown as missing entries in the chapter list.

Example:

```text
part-one/part.md
part-one/chapter-one.md
chapter-two.md
```

### Part Nodes

If a file contains the marker {class: part} in its first non-blank lines, Hymnal treats it as a Part node (non-selectable section marker in the sidebar).

### Chapter Titles

- If a file has a first-level markdown heading (# Heading), that heading becomes the display title.
- Otherwise, the file name without extension is used.

## Keyboard Shortcuts

- Ctrl+B: toggle chapter sidebar
- F4: toggle notes panel
- Ctrl+S: save active chapter

## Architecture Overview

The repository is split into UI, core domain/services, and tests:

- src/Hymnal
	- Avalonia desktop app
	- Dependency injection composition root
	- ViewModels and Views
- src/Hymnal.Core
	- Core models, parser, and file services
	- App settings persistence
	- Notes and atomic metadata writes
- tests/Hymnal.Core.Tests
	- xUnit test coverage for core behavior

## Technology Stack

- .NET SDK 10.0 preview
- Avalonia 12
- ReactiveUI
- DynamicData
- Microsoft.Extensions.DependencyInjection
- xUnit + NSubstitute

## Build And Test

From repository root:

```bash
dotnet build Hymnal.slnx
dotnet test Hymnal.slnx --filter "FullyQualifiedName!~GanttView_RowMoveShortcutHelper" --blame-hang-timeout 90s
```

The filter skips a test that always hangs (see AGENTS.md).

## Run The App

From repository root:

```bash
dotnet run --project src/Hymnal/Hymnal.csproj
```

## Data And Persistence

- App settings are stored in the user profile AppData location.
	- Windows path pattern: %AppData%/Hymnal/settings.json
- Per-chapter notes are stored in each workspace at:
	- .hymnal-data/notes/
- Notes file names are derived from chapter relative paths with path separators replaced by underscores.
- Update downloads and an optional GitHub token are kept in `~/.hymnal/`.

## Limitations And Known Gaps

- Credential store is currently an in-memory stub.
- Workspace reload prompt on Book.txt change is informational only at this stage.

## Documentation

- Getting started guide: GETTING_STARTED.md

## Contributing

Contributions are welcome. For code changes:

1. Create or update tests in tests/Hymnal.Core.Tests.
2. Keep behavior aligned with Book.txt and chapter workflow expectations.
3. Run build and tests locally before opening a PR.

## Repository Quick Map

```text
src/
	Hymnal/            # Desktop UI app
	Hymnal.Core/       # Core services and models
tests/
	Hymnal.Core.Tests/ # Unit tests
docs/
	research/          # Research and reference docs
```
