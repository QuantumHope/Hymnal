# Hymnal — Agent Orientation

## What This App Does

Hymnal is a cross-platform desktop manuscript editor for authors using the **Book.txt / Markua** workflow.

- Opens a folder as a workspace and locates `Book.txt` at the root or under `manuscript/`
- Parses `Book.txt` lines into an ordered chapter list shown in a sidebar
- Opens chapter files in a single-buffer editor with save-before-switch and atomic file writes
- Watches open files for external changes
- Stores per-chapter notes under `.hymnal-data/notes/` in the workspace
- Persists last workspace and chapter between launches (settings in OS AppData)
- Treats files with `{class: part}` in first lines as non-selectable Part nodes in the sidebar

## Technology Basis

| Layer | Technology |
|---|---|
| Desktop UI | Avalonia 12 |
| Platform | .NET 10 (preview), C# |
| Reactive layer | ReactiveUI + DynamicData |
| DI | Microsoft.Extensions.DependencyInjection |
| Tests | xUnit + NSubstitute |
| Target platforms | Windows 10+, Linux (Ubuntu LTS, Fedora) |

## Project Layout

```
src/
  Hymnal/              # Avalonia desktop app — DI composition root, ViewModels, Views
  Hymnal.Core/         # Domain models, services, parsers, settings persistence
tests/
  Hymnal.Core.Tests/   # xUnit unit tests for Core behavior
```

## Key File Locations

| What | Path |
|---|---|
| App entry / DI wiring | `src/Hymnal/Program.cs`, `src/Hymnal/App.axaml.cs` |
| ViewModels | `src/Hymnal/ViewModels/` |
| Views (AXAML + code-behind) | `src/Hymnal/Views/` |
| Themes / styles | `src/Hymnal/Themes/` |
| Core models | `src/Hymnal.Core/Models/` |
| Core services | `src/Hymnal.Core/Services/` |
| Core infrastructure | `src/Hymnal.Core/Infrastructure/` |
| Service interfaces | `src/Hymnal.Core/Interfaces/` |
| Shared helpers (`Result<T>`, `Unit`, `PathHelper`) | `src/Hymnal.Core/Common/` |
| Unit tests | `tests/Hymnal.Core.Tests/` |
| Test fixtures (sample manuscripts) | `tests/Hymnal.Core.Tests/Fixtures/` |
| Architecture decisions | `_bmad-output/planning-artifacts/architecture.md` |

## Build & Run

```bash
# Build (from repo root)
dotnet build src/Hymnal/Hymnal.csproj

# Run desktop app
dotnet run --project src/Hymnal/Hymnal.csproj
```

## Test

```bash
# Must use solution file — bare 'dotnet test' fails with multiple projects
dotnet test Hymnal.slnx
```

Baseline: 761 tests (`dotnet test Hymnal.slnx --list-tests`). Measured 2026-11-07 on Linux with one run of `dotnet test Hymnal.slnx --filter "FullyQualifiedName!~GanttView_RowMoveShortcutHelper" --blame-hang-timeout 90s`: 760 pass, 0 fail, in about 10 s. The excluded test, `GanttView_RowMoveShortcutHelper_DelegatesForEditableChapterRowsAndIgnoresPartRows`, always hangs, so an unfiltered run aborts there (about 490 tests pass before the abort).

## Key Patterns

### ViewModels
- All ViewModels extend `ViewModelBase` which provides `Disposables` (`CompositeDisposable`) and `IActivatableViewModel` — use `subscription.DisposeWith(Disposables)` for cleanup
- Derived read-only properties use `ObservableAsPropertyHelper<T>` via `.ToProperty()` — never expose mutable props for computed state
- Commands are `ReactiveCommand.CreateFromTask()` or `ReactiveCommand.Create()` with a `canExecute` observable gate
- Debounced auto-save uses `Subject<string>` + `.Throttle(TimeSpan)` + `.Subscribe(async text => ...)` — see `NotesViewModel`
- `_isSwitching` bool guard in `WorkspaceViewModel` prevents re-entrant chapter switches — follow the same pattern for any async switch operation

### Services & Results
- Service methods return `Result<T>` (see `src/Hymnal.Core/Common/Result.cs`) — always propagate via `Result.Ok(value)` / `Result.Fail(message)`; never throw for expected failures
- `Unit` (see `src/Hymnal.Core/Common/Unit.cs`) is the return type for commands/observables with no meaningful value
- Atomic writes go through `IMetadataStore.WriteTextAtomicAsync()` (write temp → rename) — EditorViewModel and NotesViewModel both delegate to this

### Data Model
- `ManuscriptModel` holds chapters in a `SourceCache<ChapterNode, string>` (DynamicData) keyed by `RelativePath` — bind via `.Connect().AsObservableList()` or `.Bind()` to an `ObservableCollection`
- **Project kinds** — `.hymnal-data/project.json` (`{schemaVersion, kind: novel|story|anthology, storage: sidecar|embedded}`) is read/written by `ProjectSettingsService` (missing file = Novel/Sidecar). `ProjectProfile.For(settings)` turns it into the profile (statuses, Gantt phases, labels, target presets, feature flags). `WorkspaceViewModel.Profile` exposes the open workspace's profile; `ReloadProjectAsync()` re-reads it and rebuilds the chapter list. Story/anthology statuses relabel a subset of `ChapterStatus` (Planned/Drafting/Editing/Done shown as Idea/Drafting/Revising/Done) — stored values are never rewritten. Switch kind from Settings → Project.
- `ChapterNode` is an immutable `record` — `Key == RelativePath` (both are forward-slash normalized); `NodeKind.Part` nodes are non-selectable in the sidebar
- **Story ideas** (story/anthology only, `ProjectProfile.ShowsIdeas`) — `.hymnal-data/ideas.json` (`{schemaVersion: 1, ideas: [...]}`, state `new|developing|used|shelved`) is read/written only through `IStoryIdeasService`; a malformed file makes the Ideas view read-only and is never overwritten. `StoryIdeaPromoter` turns an idea into a new story (Anthology only). `IdeasViewModel` backs the IDEAS shell mode (`ShellMode.Ideas`, shown only while `MainWindowViewModel.IsIdeasModeAvailable`); Ctrl+Shift+I quick-captures an idea from any mode. Pending idea edits are flushed when the main window closes (`MainWindowViewModel.FlushPendingEditsBeforeCloseAsync`, bounded wait; on failure the window asks before closing).
- **Beat boards** (story/anthology only, `ProjectProfile.UsesBeatBoard`) — each story's ordered beats live in `.hymnal-data/beats/{boardKey}.json` (`{schemaVersion: 1, beats: [{id, title, note, scenePath}]}`, camelCase), read/written only through `IBeatBoardService` (`BeatBoardService`, atomic writes via `IMetadataStore`). Board keys: a Short Story project uses the constant `story` (`BeatBoardService.ShortStoryBoardKey`); an anthology story uses its chapter-registry UUID (the story's chapter, or its Part for a story told in several scenes), so keys survive renames. `scenePath` is a `ChapterNode.RelativePath` or null; a link to a scene no longer in the book shows as missing and can be unlinked. A missing file is an empty board; a malformed one makes that board read-only and is never overwritten. Beats never touch `Book.txt`, and touch story `.md` files only under embedded storage (below). Templates are embedded JSON (`src/Hymnal.Core/Resources/BeatTemplates/`, `BeatTemplateLoader`). `BeatBoardViewModel` shows one board at a time; `PlanHostViewModel` is Plan mode for story/anthology projects (Short Story → the `story` board plus a Files strip of the Corkboard when there are 2+ scene files; Anthology → the Corkboard, where double-click or **Beats** on a story card / Part divider raises `CorkboardViewModel.BeatsRequested` and drills in, **Open** opens the editor). It closes the board when the kind changes or the workspace closes; Novel Plan mode shows `CorkboardViewModel` as before. Pending beat edits are flushed with idea edits when the main window closes.

- **Per-story metadata** — status/schedule, target, notes and beats go only through `IStoryMetadataStore` (read `WorkspaceViewModel.MetadataStore` at call time; it is rebuilt on every open/reload and waits while being prepared). `SidecarStoryMetadataStore` delegates to `PhaseDataService`, `TargetsService`, `INotesService` and `IBeatBoardService`; `StoryMetadataStoreFactory` picks the embedded store for a Short Story set to embedded storage. Board moves stay on `IBeatBoardService.MoveBoardAsync` (sidecar only).
- **Embedded storage** ("Keep everything in the story file", Short Story only, `storage: embedded`) — the primary story's data (including the `story` beat board) lives in one `<!-- hymnal:v1 … -->` JSON block at the very end of its `.md` file (`StoryFileFormat.Split`/`Merge`; only the last block followed by nothing but whitespace counts; prose bytes and line endings are preserved; a malformed block is kept as prose and a fresh one is appended on the next write). The primary is the first Book.txt chapter file with a block, else the first; other files use the sidecar (with a one-time info notice). The block `id` is the story's identity: the registry adopts it on load. An unreadable primary, or none known yet, refuses its writes (never routed to the sidecar). All story-file writes go through the `StoryFileWriter` singleton: a file open in the editor is changed through the editor buffer (saving unsaved prose with it), a closed one is rewritten atomically. The editor hides a recognized block in every project kind and restores it on save; word counts, AI context and stats use prose only. Non-UTF-8 files open as plain text and can't hold a block.
- **Storage migration** — Settings → Project shows the checkbox for Short Story, enabled only when Book.txt lists exactly one chapter file (missing ones count; it can always be turned off; the reason is shown while it is disabled). The current mode is the source of truth and the destination mirrors it exactly (fields the source lacks are cleared there). Apply reads the current kind/storage from `project.json` on disk (never the possibly stale in-memory profile), then: flush pending notes/schedule/beat edits (refuse if that fails; flushes wait for a debounced save already writing); for Anthology → Short Story in the story file, move the anthology story's board to `story` first; `StorageMigrator.WriteDestinationAsync` (all source reads first, so a failed read never clears anything; on failure stop with project.json unchanged); save `project.json` in the new mode — **the commit point**; then `CleanupSourceAsync` removes the old copies best effort (a failure only shows an info: leftovers are ignored in the new mode and replaced by the next mirror); then `ReloadProjectAsync()` (if it fails, `WorkspaceViewModel.ResetMetadataStore` points story data at the new mode and sets `NeedsReopen`, which blocks Apply and the checkbox with "Reopen the project to continue." until a successful open/reload). Migration never deletes data before the commit: if a step is interrupted, the data is still on disk (for the plain storage switch, in the mode `project.json` names; in the Anthology → Short Story window, the board may already be under `story`) and a retry picks it up. Switching kind away from Short Story while embedded migrates back to the sidecar the same way (abandoned if the destination write fails), saving `storage: sidecar` with the new kind, then runs the board move.

### DI Registration
- Registration order in `App.axaml.cs` matters: `EditorViewModel` must be registered before `WorkspaceViewModel`; they are both `AddSingleton`
- `FolderPickerService` requires a `Func<TopLevel?>` accessor — see `App.axaml.cs` for the factory registration pattern
- `MainWindowViewModel` takes `IdeasViewModel` and `PlanHostViewModel` as optional trailing parameters (null → no Ideas mode / Plan mode always shows the Corkboard); `PlanHostViewModel` depends on the `CorkboardViewModel` and `BeatBoardViewModel` singletons

### Tests
- **Dispose watcher owners.** Tests must dispose `ManuscriptService`, `EditorViewModel` and `GitPanelViewModel`, before deleting their temp folders. Each one owns a `FileSystemWatcher`, and on Linux every live watcher holds an inotify instance. The per-user limit (`fs.inotify.max_user_instances`, often 128) is shared with the IDE and every other process. Leaked watchers made a full run fail with "The configured user limit (128) on the number of inotify instances has been reached", and those failures then stalled the headless session.
  - Track them with `OwnedDisposables` (`tests/Hymnal.Core.Tests/Infrastructure/`). Dispose the tracker first, then delete folders. Inside `try { … } finally { Delete… }`, declare contexts with `using var`, so they are disposed before the `finally` runs.
  - `WatcherLeakGuardTests` (Linux only) checks that each owner releases its inotify descriptor on dispose.
- **Hangs (pre-existing):**
  - `GanttViewSmokeTests.GanttView_RowMoveShortcutHelper_DelegatesForEditableChapterRowsAndIgnoresPartRows` hangs every time, even run alone (it blocks the UI thread on an async move command). Exclude it with `--filter "FullyQualifiedName!~GanttView_RowMoveShortcutHelper"`.
  - About one filtered run in ten still stalls the shared headless session. This is not inotify-related. Tests start failing with "UI-thread operation did not complete within 40s" (seen in `TargetPresetAndStatsTests`, ending with `StructuralConsistencyUatTests.CrossSurfaceReplay_PersistsOneUuidBackedStructureAfterFreshReload` not completing). Re-run when that happens. If a stall repeats, run one namespace per command, e.g. `--filter "FullyQualifiedName~Hymnal.ViewModels"`.
  - Once a UI-thread job hangs, every later headless test in the run stalls behind it. `--blame-hang-timeout` reports the culprit in `TestResults/*/Sequence_*.xml` (look for `Completed="False"`).
- **Headless isolation:** `ReactiveUiTestBootstrap` uses the default (PerTest) isolation. Outside a `Dispatch`, `Application.Current` is null, and production code checks for that to run dispatcher work inline. PerAssembly keeps one Application alive, which deadlocks the tests that run off the UI thread, so don't switch to it. Each `Dispatch` gets a fresh dispatcher. So await any background UI work (such as `WorkspaceViewModel` hydration) inside the same `Dispatch` that started it. Bound async UI work with `RunOnUiThreadAsync(action, timeout)`.
- Test fixtures (sample manuscripts) live in `tests/Hymnal.Core.Tests/Fixtures/SampleManuscripts/` — load via `AppContext.BaseDirectory`
- Prefer hand-rolled fakes (e.g., `FakeNotificationService`) over NSubstitute for simple interfaces; use NSubstitute for complex interfaces with many members
- Infra tests that touch the file system use temp directories and clean up in `try/finally`

## Behavioral Guidelines

**Do:**
- Follow the MVVM pattern — logic belongs in ViewModels or Core services, not code-behind
- Use ReactiveUI (`WhenAnyValue`, `ReactiveCommand`, etc.) for bindings and commands
- Write or update unit tests in `Hymnal.Core.Tests` for any Core service change
- Use atomic writes for file operations (write to temp, then rename/replace)
- Access files only through the service/interface layer — no raw `File.*` calls in ViewModels
- Use `ICredentialStore` for any secret storage — never write credentials or API keys to disk

**Don't:**
- Put business logic directly in Views or code-behind files
- Don't modify chapter `.md` files outside of `StoryFileWriter`, which the editor save path uses.
- Read or write per-story status/schedule, target, notes or beats other than through `IStoryMetadataStore` (`WorkspaceViewModel.MetadataStore`); only `StorageMigrator` moves them between storages
- Bypass `INotesService`, `IChapterRegistryService`, or `IMetadataStore` to write `.hymnal-data` directly
- Assume `dotnet test` alone works — always target the solution file

## Planned Features

These areas are designed but not yet fully implemented:

- **Markua syntax highlighting** — inline highlighting and validation in the editor
- **Project management** — chapter status lifecycle, live word count, per-chapter targets
- **Gantt phase view** — custom time-axis rendering with inline date editing
- **Corkboard view** — card layout with drag-to-reorder (Late V1)
- **Supplemental docs** — file/folder sidebar for non-chapter assets
- **Lightweight Git** — stage-all → commit → push via system Git binary
- **AI editorial** — provider config, chapter summaries, chat panel, issue flagging
- **Credential store** — currently an in-memory stub; will use OS credential manager (Windows Credential Manager / libsecret)
