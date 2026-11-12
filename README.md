# Hymnal

Hymnal is a desktop editor for Book.txt-driven manuscripts. It is built with Avalonia UI and a .NET core service layer, and is intended to support structured writing workflows where chapter order comes from a manifest file.

## Status

- Current stage: active development
- Frameworks: .NET 10 (preview), Avalonia 12
- App type: cross-platform desktop UI
- License: add your OSS license file if not already present

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
- F7: toggle the Beats panel (Short Story and Anthology projects): the story's beats beside the draft
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
dotnet build src/Hymnal/Hymnal.csproj
dotnet test Hymnal.sln
```

Latest verified test result in this repository context:

- 31 tests passed
- 0 failed

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

## Limitations And Known Gaps

- Credential store is currently an in-memory stub.
- No packaged release installer documented yet.
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
