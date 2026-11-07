# Story Ideas Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let authors in Short Story and Anthology projects capture, browse and edit story ideas, and (in Anthology) promote an idea into a new story.

**Architecture:** A Core `StoryIdeasService` persists a single `.hymnal-data/ideas.json`; a Core `StoryIdeaPromoter` turns an idea into a story using the existing structure, registry, phase, target and notes services. An `IdeasViewModel` drives a new **Ideas** shell mode (nav button visible only when `ProjectProfile.ShowsIdeas`) and a quick-capture dialog.

**Tech Stack:** .NET 10, Avalonia 12, ReactiveUI, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-07-short-story-mode-design.md` §8 (depends on Plan 1: `docs/superpowers/plans/2026-11-07-short-story-mode-foundation.md`).

## Global Constraints

- Ideas exist only for `ProjectKind.ShortStory` and `ProjectKind.Anthology`; Novel UI is unchanged.
- File: `{workspaceRoot}/.hymnal-data/ideas.json`, shape `{ "schemaVersion": 1, "ideas": [...] }`, camelCase, enums as camelCase strings, written only via `IMetadataStore.WriteTextAtomicAsync`.
- `state` values: `new`, `developing`, `used`, `shelved`.
- Unreadable/malformed/unsupported-schema file → `Result.Fail`; the file is never overwritten in that session.
- Promote is Anthology-only. Promoted file content is exactly `# {Title}\n\n`; status = `profile.DefaultStatus` (`Planned`); notes = premise, blank line, notes (empty parts omitted).
- Services return `Result<T>`; no raw `File.*` writes in ViewModels.
- Tests: `dotnet test Hymnal.slnx`, run per class with `--filter "FullyQualifiedName~<Class>" --blame-hang-timeout 60s` (the combined ViewModels run has a pre-existing intermittent hang).
- No debug/tracing code committed.

## Review Focus

1. Malformed `ideas.json` → warning, view read-only, file bytes unchanged after any UI action (test in Task 3).
2. Promote with a file name that already exists on disk or in Book.txt → error, idea stays unchanged, no partial registry/notes writes (Task 2).
3. Title with only punctuation/non-ASCII → slug falls back to `untitled-story.md` (Task 2).
4. Switching the project to Novel while in Ideas mode → shell returns to Write and the nav button hides (Task 4).
5. Rapid edits in the idea editor → one debounced save containing the last value; switching selection flushes the pending save (Task 3).

---

### Task 1: Idea model, service, and profile flag

**Files:**
- Create: `src/Hymnal.Core/Models/StoryIdea.cs` (`StoryIdea` record + `IdeaState` enum)
- Create: `src/Hymnal.Core/Interfaces/IStoryIdeasService.cs`
- Create: `src/Hymnal.Core/Services/StoryIdeasService.cs`
- Modify: `src/Hymnal.Core/Models/ProjectProfile.cs` (add `ShowsIdeas`)
- Test: `tests/Hymnal.Core.Tests/Services/StoryIdeasServiceTests.cs`, add cases to `tests/Hymnal.Core.Tests/Models/ProjectProfileTests.cs`

**Interfaces:**
- Produces:
  - `public enum IdeaState { New, Developing, Used, Shelved }`
  - `public sealed record StoryIdea(string Id, string Title, string Premise, string Notes, IReadOnlyList<string> Tags, string? TargetPreset, IdeaState State, DateOnly CreatedAt, string? StoryId)` with `static StoryIdea Create(string title, string premise, DateOnly today)` (new GUID id, empty notes/tags, `New`).
  - `IStoryIdeasService`: `Task<Result<IReadOnlyList<StoryIdea>>> LoadAsync(string workspaceRoot)`; `Task<Result<Unit>> SaveAsync(string workspaceRoot, IReadOnlyList<StoryIdea> ideas)`.
  - `StoryIdeasService(IMetadataStore store)`: `public static string IdeasFilePath(string workspaceRoot)`; `public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags)`.
  - `ProjectProfile.ShowsIdeas` (`false` Novel, `true` ShortStory/Anthology).

- [ ] **Step 1: Write failing tests** (temp dir, `try/finally` cleanup, real `MetadataStore`):
  - `Load_missing_file_returns_empty` → Ok, Count 0.
  - `Save_then_load_round_trips_all_fields` → two ideas (one with tags `["horror"]`, `TargetPreset = "flash"`, `State = Used`, `StoryId = "abc"`, `CreatedAt = 2026-11-07`) equal after reload, order preserved.
  - `Saved_json_uses_camelcase_strings` → file contains `"schemaVersion": 1`, `"state": "used"`, `"targetPreset": "flash"`, `"createdAt": "2026-11-07"`.
  - `Load_malformed_json_fails_and_leaves_file` → write `{ not json`, Load is Fail, bytes unchanged.
  - `Load_unsupported_schema_fails` → `{"schemaVersion":2,"ideas":[]}` → Fail.
  - `Load_unknown_state_fails` → `"state":"archived"` → Fail.
  - `Load_null_tags_become_empty` → idea JSON without `tags` → `Tags` empty, not null.
  - `NormalizeTags_trims_dedupes_case_insensitively_keeps_first_spelling` → `[" Horror", "horror", "", "SF "]` → `["Horror", "SF"]`.
  - `SaveAsync` normalizes tags before writing (assert via reload).
  - ProjectProfileTests: `ShowsIdeas` theory — Novel false, ShortStory true, Anthology true.
- [ ] **Step 2: Run** `timeout 300 dotnet test Hymnal.slnx --filter "FullyQualifiedName~StoryIdeasServiceTests|FullyQualifiedName~ProjectProfileTests" --blame-hang-timeout 60s` → FAIL (types missing).
- [ ] **Step 3: Implement.** Follow `ProjectSettingsService` exactly for JSON options (`JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)`) and the catch set (JsonException, IOException, UnauthorizedAccessException → Fail). Add `ShowsIdeas` as a constructor-set property like `ShowsBookStats`.
- [ ] **Step 4: Run** the same command → PASS.
- [ ] **Step 5: Commit** `feat(core): add story ideas model and service`.

### Task 2: Idea promoter

**Files:**
- Create: `src/Hymnal.Core/Services/StoryIdeaPromoter.cs`
- Test: `tests/Hymnal.Core.Tests/Services/StoryIdeaPromoterTests.cs`

**Interfaces:**
- Consumes: `IStoryIdeasService`, `StoryIdea` (Task 1); `IBookTxtStructureService.ReadNormalizedEntriesAsync` / `CreateNewChapterAsync`; `ChapterRegistryService.LoadAsync/AssignUuid/SaveAsync`; `PhaseDataService.UpsertAsync` + `DefaultPhaseDataFor(profile)`; `TargetsService.UpsertAsync`; `INotesService.SaveAsync` + `INotesService.DeriveNotesPath`; `ProjectProfile.TargetPresets`.
- Produces:
  - `StoryIdeaPromoter(IBookTxtStructureService structure, ChapterRegistryService registry, PhaseDataService phases, TargetsService targets, INotesService notes, IStoryIdeasService ideas)`
  - `public static string SuggestFileName(string title)`
  - `public Task<Result<string>> PromoteAsync(string workspaceRoot, string bookTxtPath, StoryIdea idea, string fileName, ProjectProfile profile)` → Ok(relative path of the new story, relative to the Book.txt directory, i.e. `fileName`).

- [ ] **Step 1: Write failing tests** (temp workspace with `Book.txt` listing `one.md`, real services; one variant with Book.txt under `manuscript/`):
  - `SuggestFileName` theory: `"The Lighthouse"` → `the-lighthouse.md`; `"  Salt -- Debt! "` → `salt-debt.md`; `"???"` → `untitled-story.md`; `"Café Noir"` → `caf-noir.md` (ASCII letters/digits only; others become `-`, runs collapsed, trimmed).
  - `Promote_creates_file_and_appends_to_book_txt` → file content `# The Lighthouse\n\n`; Book.txt entries end with `the-lighthouse.md`.
  - `Promote_sets_status_target_and_notes` → registry has entry for `the-lighthouse.md`; phase status `Planned`; target `MinWords = 0, MaxWords = 1000, Preset = "flash"` (the `flash` entry of `ProjectProfile.For(Anthology).TargetPresets`); notes file at `DeriveNotesPath(root, "the-lighthouse.md")` equals `"{premise}\n\n{notes}"`.
  - `Promote_without_preset_writes_no_target` and `Promote_with_empty_premise_writes_only_notes`.
  - `Promote_marks_idea_used_with_story_id` → reloaded idea `State == Used`, `StoryId` == registry UUID; other ideas unchanged.
  - `Promote_in_manuscript_subfolder_creates_file_beside_book_txt`.
  - `Promote_existing_file_fails_and_leaves_idea_unchanged` → pre-create `the-lighthouse.md` → Fail; Book.txt unchanged; idea still `New`; no registry entry added.
  - `Promote_rejects_bad_file_names` theory: `""`, `"a/b.md"`, `"..\\x.md"`, `"story.txt"` → Fail, nothing written.
  - `Promote_fails_for_short_story_profile` and `Promote_fails_for_used_idea`.
  - `Promote_failure_after_file_creation_leaves_idea_unchanged` → make the ideas file unreadable-for-save by passing a fake `IStoryIdeasService` whose `SaveAsync` returns Fail → result Fail with message naming the created file; idea not marked used.
- [ ] **Step 2: Run** `timeout 300 dotnet test Hymnal.slnx --filter "FullyQualifiedName~StoryIdeaPromoterTests" --blame-hang-timeout 60s` → FAIL.
- [ ] **Step 3: Implement.** Order: validate (profile Anthology, idea not Used, file name is a bare `*.md` name) → `ReadNormalizedEntriesAsync` for the append index → `CreateNewChapterAsync(bookTxtPath, fileName, "# {Title}\n\n", entries.Count)` → registry assign+save → phase upsert → target upsert (only when `idea.TargetPreset` matches a profile preset) → notes save (skip when both empty) → load ideas, replace this idea with `State = Used, StoryId = uuid`, save. Wrap post-creation steps so any failure/exception returns `Fail("Story file '{fileName}' was created but … : {error}")` without marking the idea used.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): add story idea promoter`.

### Task 3: IdeasViewModel

**Files:**
- Create: `src/Hymnal/ViewModels/IdeasViewModel.cs`, `src/Hymnal/ViewModels/IdeaCardViewModel.cs`
- Modify: `src/Hymnal/ViewModels/WorkspaceViewModel.cs` (add `public bool SelectChapterByPath(string relativePath)`: finds the chapter VM whose `Node.RelativePath` equals the path (ordinal-ignore-case) and sets `SelectedNode`; returns false if none)
- Test: `tests/Hymnal.Core.Tests/ViewModels/IdeasViewModelTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2; `WorkspaceViewModel.Profile`, `WorkspaceRoot`, `BookTxtPath`, `ReloadProjectAsync()` (Plan 1 Task 6); `INotificationService`.
- Produces:
  - `IdeasViewModel(WorkspaceViewModel workspace, IStoryIdeasService ideas, StoryIdeaPromoter promoter, INotificationService notifications, IScheduler? saveScheduler = null)`
  - `ReadOnlyObservableCollection<IdeaCardViewModel> Cards` (filtered, newest `CreatedAt` first, then title)
  - `IReadOnlyList<string> AllTags`; `string? TagFilter`; `bool ShowUsedAndShelved` (default false)
  - `IdeaCardViewModel? Selected`; editable `Title`, `Premise`, `Notes`, `TagsText` (comma-separated), `TargetPreset`, `State` on the card VM
  - `bool IsReadOnly` (true after a failed load); `bool CanPromote` (`Profile.Kind == Anthology && Selected?.State != Used && !IsReadOnly`)
  - Commands: `NewIdeaCommand` (takes `(string title, string premise)`), `DeleteSelectedCommand` (asks `Func<string, Task<bool>> ConfirmAsync`, settable for tests), `PromoteSelectedCommand` (asks `Func<string, Task<string?>> AskFileNameAsync` prefilled with `SuggestFileName`), `LoadAsync()`
  - Reloads on workspace change / `Profile` change; clears when `!Profile.ShowsIdeas`.

- [ ] **Step 1: Write failing tests** (temp workspace, real `StoryIdeasService`, fake notifications, `TestScheduler` for debounce):
  - `Load_hides_used_and_shelved_by_default` / toggling `ShowUsedAndShelved` shows all.
  - `Cards_sorted_newest_first`.
  - `Tag_filter_is_case_insensitive`.
  - `New_idea_saves_and_selects` → file contains new idea with `State == New`.
  - `Edits_debounce_into_one_save` → three `Title` edits within the window; advance scheduler 1s; exactly one write (check file content = last title).
  - `Changing_selection_flushes_pending_save`.
  - `Delete_requires_confirmation` → ConfirmAsync false keeps idea; true removes it.
  - `Malformed_file_sets_read_only_and_never_writes` → ShowInfo called; NewIdeaCommand/Delete/Promote cannot execute; file bytes unchanged.
  - `CanPromote_false_for_short_story_profile`.
  - `Promote_success_reloads_workspace_and_selects_story` → `workspace.SelectedNode.Node.RelativePath == "the-lighthouse.md"`; card now Used (hidden by default).
  - `Promote_failure_shows_error` → ShowError called, idea still New.
  - `Profile_switch_to_novel_clears_cards`.
- [ ] **Step 2: Run** `timeout 300 dotnet test Hymnal.slnx --filter "FullyQualifiedName~IdeasViewModelTests" --blame-hang-timeout 60s` → FAIL.
- [ ] **Step 3: Implement.** Debounce like `NotesViewModel` (`Subject<Unit>` + `Throttle(TimeSpan.FromMilliseconds(500), scheduler)`); persist the whole list via `SaveAsync`. Tags parsed from `TagsText` with `StoryIdeasService.NormalizeTags`. On Load Fail: `ShowInfo("Couldn't read ideas.json — ideas are read-only until it is fixed.")`, `IsReadOnly = true`. Promote: on Ok → `await workspace.ReloadProjectAsync()` then `workspace.SelectChapterByPath(path)`; on Fail → `ShowError(error)`; reload ideas either way.
- [ ] **Step 4: Run** → PASS; also run `--filter "FullyQualifiedName~WorkspaceProjectKindTests"` → PASS.
- [ ] **Step 5: Commit** `feat(app): add ideas view model`.

### Task 4: Ideas shell mode, views, quick capture, DI, docs

**Files:**
- Modify: `src/Hymnal/ViewModels/ShellMode.cs` (add `Ideas`), `src/Hymnal/ViewModels/MainWindowViewModel.cs` (ctor takes `IdeasViewModel`; `SelectIdeasCommand`; `IsIdeasVisible`; `IsIdeasModeAvailable` ⇐ `WorkspaceViewModel.Profile.ShowsIdeas && HasWorkspace`; `SecondaryCentreContent` maps `Ideas → IdeasViewModel`; when availability turns false while `ActiveMode == Ideas`, set `ActiveMode = Write`; `QuickCaptureIdeaCommand` enabled only when available)
- Create: `src/Hymnal/Views/IdeasView.axaml(.cs)` (card grid: title, premise trimmed to 3 lines, tags, `Profile.PresetLabel(TargetPreset)`; filter bar with tag `ComboBox` + "Show used & shelved" `CheckBox`; side editor for the selected card with Title, Premise, Notes (multi-line), Tags, Target (`ComboBox` of `Profile.TargetPresets` + none), State; **Promote to story** button bound to `PromoteSelectedCommand`, visible only when `Profile.Kind == Anthology`; Delete button)
- Create: `src/Hymnal/Views/QuickIdeaDialog.axaml(.cs)` (Title required, Premise optional; Create/Cancel) and the confirm/file-name dialogs, reusing existing dialog patterns (`NewChapterDialog`)
- Modify: `src/Hymnal/Views/MainWindow.axaml` — `IDEAS` nav `ToggleButton` between RESEARCH and PLAN, `IsVisible="{Binding IsIdeasModeAvailable}"`; `<KeyBinding Gesture="Ctrl+Shift+I" Command="{Binding QuickCaptureIdeaCommand}" />`
- Modify: `src/Hymnal/App.axaml.cs` — register `IStoryIdeasService`→`StoryIdeasService`, `StoryIdeaPromoter`, `IdeasViewModel` (singletons), pass `IdeasViewModel` to `MainWindowViewModel`
- Modify: `AGENTS.md` — add story ideas (`.hymnal-data/ideas.json` via `IStoryIdeasService`) to the data-model notes
- Test: `tests/Hymnal.Core.Tests/ViewModels/IdeasShellModeTests.cs`

**Interfaces:**
- Consumes: Task 3 `IdeasViewModel`; Plan 1 `WorkspaceViewModel.Profile`.

- [ ] **Step 1: Write failing tests** (existing MainWindowViewModel test harness pattern):
  - `Ideas_mode_unavailable_for_novel` / `available_for_story_and_anthology`.
  - `Selecting_ideas_sets_secondary_content_to_ideas_vm`.
  - `Switching_profile_to_novel_while_in_ideas_returns_to_write`.
  - `Quick_capture_creates_new_idea` (dialog seam: `Func<Task<(string title, string premise)?>> ShowQuickIdeaDialogAsync` on MainWindowViewModel, set by the view; test sets it) and `Quick_capture_cancel_creates_nothing`.
- [ ] **Step 2: Run** `timeout 300 dotnet test Hymnal.slnx --filter "FullyQualifiedName~IdeasShellModeTests" --blame-hang-timeout 60s` → FAIL.
- [ ] **Step 3: Implement** the VM changes, views, DI and AGENTS.md text.
- [ ] **Step 4: Run** the filter → PASS; `dotnet build src/Hymnal/Hymnal.csproj` → 0 errors; run per-class filters for `MainWindowViewModel`, `IdeasViewModelTests`, `WorkspaceProjectKindTests` → PASS.
- [ ] **Step 5: Commit** `feat(app): add ideas shell mode and quick capture`.
