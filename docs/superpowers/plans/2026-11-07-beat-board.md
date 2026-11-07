# Beat Board Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Short Story and Anthology projects a beat board: ordered planning cards seeded from built-in plot templates, shown in Plan mode (Short Story) or drilled into from a story card (Anthology).

**Architecture:** Core gets `Beat`/`BeatTemplate` models, a `BeatTemplateLoader` over embedded JSON, and a `BeatBoardService` that persists one sidecar file per board. The app gets a `BeatBoardViewModel` (editing, templates, scene links) and a `PlanHostViewModel` that decides what Plan mode shows per project kind. Novel keeps today's Corkboard unchanged.

**Tech Stack:** .NET 10, Avalonia 12, ReactiveUI, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-07-short-story-mode-design.md` §4.1–4.3. Builds on Plans 1–2 on this branch.

## Global Constraints

- Beats never touch `Book.txt` or story `.md` files.
- Board file: `{workspaceRoot}/.hymnal-data/beats/{boardKey}.json` = `{ "schemaVersion": 1, "beats": [ { "id", "title", "note", "scenePath" } ] }`, camelCase, written only via `IMetadataStore.WriteTextAtomicAsync`.
- Board keys: Short Story project → the constant `story`; Anthology story → that story node's chapter-registry UUID (chapter or Part node). Keys survive renames because registry UUIDs do.
- Missing board file → empty board. Malformed/unsupported file → `Result.Fail`; the board is read-only for the session and the file is never overwritten.
- `scenePath` is a `ChapterNode.RelativePath` (relative to the Book.txt folder) or null.
- Templates, exact beat titles, in order:
  - `threeAct` "Three-Act (short)": Hook, Inciting Incident, Rising Complications, Climax, Resolution
  - `tryFail` "Try/Fail Cycles": Goal, Try 1 (fail), Try 2 (fail, worse), Try 3 (success or final failure), Outcome
  - `sevenPoint` "Seven-Point": Hook, Plot Turn 1, Pinch 1, Midpoint, Pinch 2, Plot Turn 2, Resolution
  - `kishotenketsu` "Kishōtenketsu": Introduction, Development, Twist, Reconciliation
  - `freytag` "Freytag's Pyramid": Exposition, Rising Action, Climax, Falling Action, Dénouement
  - `mice` "MICE Quotient": parameter `thread` ∈ {Milieu, Inquiry, Character, Event}; beats `Open: {thread}`, `Close: {thread}`
  - `blank` "Blank": no beats
- Each template has a one-line `description`; each beat has a one-sentence prompt `note` (implementer writes the copy; plain, craft-neutral).
- Novel UI and behaviour unchanged; `SecondaryCentreContent` for Novel Plan mode stays `CorkboardViewModel`.
- Tests: `dotnet test Hymnal.slnx`, one filter per run with `timeout 400 … --blame-hang-timeout 60s`; dispose every VM that owns a file watcher (EditorViewModel, ManuscriptService, GitPanelViewModel) in test cleanup.
- No debug/tracing code.

## Review Focus

1. Rapid edits then switching boards (anthology drill-out/drill-in to another story) → pending edits saved to the board they belong to, never to the newly opened one (Task 3).
2. Malformed beats file → read-only board, file bytes unchanged after any UI action (Task 3).
3. Scene linked to a beat is renamed or removed from Book.txt → beat keeps its card, link shows as missing (not a crash) and can be unlinked (Task 3).
4. Applying Replace on a non-empty board without confirming → nothing changes (Task 3).
5. Switching project kind to Novel while a beat board is shown → Plan mode shows the normal Corkboard (Task 4).

---

### Task 1: Beat models and template loader

**Files:**
- Create: `src/Hymnal.Core/Models/Beat.cs` (`Beat` record), `src/Hymnal.Core/Models/BeatTemplate.cs` (`BeatTemplate`, `BeatTemplateBeat`, `BeatTemplateParameter`)
- Create: `src/Hymnal.Core/Services/BeatTemplateLoader.cs`
- Create: `src/Hymnal.Core/Resources/BeatTemplates/{threeAct,tryFail,sevenPoint,kishotenketsu,freytag,mice,blank}.json`
- Modify: `src/Hymnal.Core/Hymnal.Core.csproj` (`<EmbeddedResource Include="Resources\BeatTemplates\*.json" />`), `src/Hymnal.Core/Models/ProjectProfile.cs` (`UsesBeatBoard`: Novel false, Story/Anthology true)
- Test: `tests/Hymnal.Core.Tests/Services/BeatTemplateLoaderTests.cs`, ProjectProfileTests

**Interfaces:**
- Produces:
  - `public sealed record Beat(string Id, string Title, string Note, string? ScenePath)` + `static Beat Create(string title, string note = "")` (new GUID id).
  - `public sealed record BeatTemplateBeat(string Title, string Note)`; `public sealed record BeatTemplateParameter(string Name, IReadOnlyList<string> Options)`; `public sealed record BeatTemplate(string Key, string Name, string Description, IReadOnlyList<BeatTemplateBeat> Beats, BeatTemplateParameter? Parameter)`.
  - `BeatTemplate.Instantiate(string? parameterValue = null) → IReadOnlyList<Beat>` (replaces `{thread}`-style `{Name}` placeholders in titles and notes; throws `ArgumentException` when a parameter is required but missing or not in Options).
  - `public static class BeatTemplateLoader { public static IReadOnlyList<BeatTemplate> All { get; } public static BeatTemplate? Find(string key); }` — order as in Global Constraints; loads once (lazy).
  - `ProjectProfile.UsesBeatBoard`.

- [ ] **Step 1: Write failing tests:** `All_has_seven_templates_in_order` (keys exactly as listed); theory per template asserting exact beat titles; `Every_template_has_description_and_every_beat_a_note`; `Blank_has_no_beats`; `Mice_instantiate_with_thread` (`"Character"` → `["Open: Character", "Close: Character"]`); `Mice_without_parameter_throws` and `Mice_with_unknown_parameter_throws`; `Instantiate_gives_fresh_ids_each_call`; `UsesBeatBoard` theory per kind.
- [ ] **Step 2: Run** `timeout 400 dotnet test Hymnal.slnx --filter "FullyQualifiedName~BeatTemplateLoaderTests|FullyQualifiedName~ProjectProfileTests" --blame-hang-timeout 60s` → FAIL.
- [ ] **Step 3: Implement.** Load via `typeof(BeatTemplateLoader).Assembly.GetManifestResourceStream`, same pattern as `RolePromptProvider`; camelCase JSON.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): add beat templates`.

### Task 2: Beat board service

**Files:**
- Create: `src/Hymnal.Core/Interfaces/IBeatBoardService.cs`, `src/Hymnal.Core/Services/BeatBoardService.cs`, `src/Hymnal.Core/Services/BeatBoardOperations.cs`
- Test: `tests/Hymnal.Core.Tests/Services/BeatBoardServiceTests.cs`, `BeatBoardOperationsTests.cs`

**Interfaces:**
- Consumes: `Beat`, `BeatTemplate` (Task 1).
- Produces:
  - `public const string ShortStoryBoardKey = "story";` on `BeatBoardService`; `public static string BoardFilePath(string workspaceRoot, string boardKey)`.
  - `IBeatBoardService`: `Task<Result<IReadOnlyList<Beat>>> LoadAsync(string workspaceRoot, string boardKey)`; `Task<Result<Unit>> SaveAsync(string workspaceRoot, string boardKey, IReadOnlyList<Beat> beats)`.
  - `public enum TemplateApplyMode { Append, Replace }`
  - `public static class BeatBoardOperations`: `ApplyTemplate(IReadOnlyList<Beat> current, IReadOnlyList<Beat> templateBeats, TemplateApplyMode mode)`, `Move(IReadOnlyList<Beat> beats, int from, int to)`, `Remove(…, string id)`, `Update(…, Beat updated)`, `Insert(…, Beat beat, int index)` — all pure, return new lists; out-of-range indexes clamp; unknown id → unchanged list.

- [ ] **Step 1: Write failing tests** (temp dirs, real `MetadataStore`): `Load_missing_returns_empty`; `Save_then_load_round_trips_order_and_fields` (including null and non-null `scenePath`); `Saved_json_shape` (`"schemaVersion": 1`, `"beats"`, `"scenePath"`); `Load_malformed_fails_and_leaves_file`; `Load_unsupported_schema_fails`; `Load_null_entry_or_missing_id_or_title_fails`; `Missing_note_becomes_empty`; `Boards_are_independent_per_key`; `Key_with_path_characters_is_rejected` (`"../x"`, `"a/b"` → Fail on load and save, nothing written). Operations: append keeps existing then adds; replace returns only template beats; move forward/backward/clamp; remove/update by id; insert at index/clamp.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~BeatBoard"` → FAIL.
- [ ] **Step 3: Implement**, mirroring `StoryIdeasService` (JSON options, catch set, null-entry/missing-field handling).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): add beat board service`.

### Task 3: BeatBoardViewModel

**Files:**
- Create: `src/Hymnal/ViewModels/BeatBoardViewModel.cs`, `src/Hymnal/ViewModels/BeatCardViewModel.cs`
- Test: `tests/Hymnal.Core.Tests/ViewModels/BeatBoardViewModelTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2; `WorkspaceViewModel.Nodes`, `WorkspaceRoot`, `Profile`, `WorkspaceChanged`; `ChapterViewModel.WordCount`, `StatusLabel`, `Node.RelativePath`, `Node.Title`; `INotificationService`.
- Produces:
  - `BeatBoardViewModel(WorkspaceViewModel workspace, IBeatBoardService service, INotificationService notifications, IScheduler? saveScheduler = null)`
  - `Task OpenBoardAsync(string boardKey, string title, IReadOnlyList<string> sceneScope)` — flushes the previous board to its own root+key first; `sceneScope` is the list of `RelativePath`s that may be linked.
  - `Task CloseBoardAsync()` (flush, clear); `Task<bool> FlushAsync()`.
  - `string? BoardKey`, `string BoardTitle`, `bool IsReadOnly`, `bool IsEmpty`, `ReadOnlyObservableCollection<BeatCardViewModel> Beats`, `BeatCardViewModel? Selected`.
  - `IReadOnlyList<BeatTemplate> Templates` (= `BeatTemplateLoader.All`), `IReadOnlyList<SceneOption> SceneOptions` (`record SceneOption(string RelativePath, string Title)` from `sceneScope`).
  - Commands: `AddBeatCommand` (inserts after Selected, or at end; selects it), `DeleteSelectedCommand` (via `Func<string, Task<bool>> ConfirmAsync`), `MoveBeatCommand` (`(int from, int to)`), `ApplyTemplateCommand` (`ApplyTemplateRequest(string Key, TemplateApplyMode Mode, string? Parameter)`; Replace on non-empty board asks `ConfirmAsync` first), `LinkSceneCommand` (`(string beatId, string? relativePath)` — null unlinks).
  - `BeatCardViewModel`: editable `Title`, `Note`; read-only `ScenePath`, `SceneTitle`, `SceneWordCount` (int?), `SceneStatusLabel`, `IsSceneMissing` (path set but not in `workspace.Nodes`).
- [ ] **Step 1: Write failing tests** (temp workspace opened the way `IdeasViewModelTests` does it, `HistoricalScheduler` for debounce, dispose watchers):
  - `Open_empty_board_is_empty_and_lists_templates`.
  - `Apply_template_to_empty_board_creates_beats_and_saves` (threeAct → five titles on disk).
  - `Apply_mice_with_thread`.
  - `Replace_requires_confirmation` (confirm false → unchanged on disk and in memory; true → template beats only).
  - `Append_keeps_existing`.
  - `Edits_debounce_into_one_save` (assert save count and content right after `AdvanceBy`, before any flush).
  - `Switching_board_flushes_to_previous_key` (edit on board A, open board B immediately → A's file has the edit, B's file untouched).
  - `Move_beat_reorders_and_saves`; `Delete_requires_confirmation`.
  - `Link_scene_shows_word_count_and_status_label`; `Unlink_scene_clears_link`.
  - `Linked_scene_removed_from_book_shows_missing` (link, then remove the file from Book.txt and reload workspace → `IsSceneMissing`, no exception; unlink works).
  - `Malformed_board_is_read_only_and_never_written` (all commands disabled; file bytes unchanged).
  - `Workspace_close_flushes_and_clears`.
- [ ] **Step 2: Run** `timeout 400 dotnet test Hymnal.slnx --filter "FullyQualifiedName~BeatBoardViewModelTests" --blame-hang-timeout 60s` → FAIL.
- [ ] **Step 3: Implement.** Reuse the lessons in `IdeasViewModel`: every save captures the loaded root+key synchronously with its snapshot; a failed load clears the loaded key so nothing can be written; reload subscriptions `ObserveOn(AvaloniaScheduler.Instance)`; refresh `SceneWordCount`/`SceneStatusLabel` when `workspace.Nodes` changes or a linked chapter's `WordCount`/`Status` changes.
- [ ] **Step 4: Run** the filter 3× → PASS each time.
- [ ] **Step 5: Commit** `feat(app): add beat board view model`.

### Task 4: Plan mode host, views, anthology drill-in, DI, docs

**Files:**
- Create: `src/Hymnal/ViewModels/PlanHostViewModel.cs`, `src/Hymnal/Views/PlanHostView.axaml(.cs)`, `src/Hymnal/Views/BeatBoardView.axaml(.cs)`
- Modify: `src/Hymnal/ViewModels/MainWindowViewModel.cs` (Plan → `PlanHostViewModel` when `Profile.UsesBeatBoard`, else `CorkboardViewModel`; optional trailing ctor param), `src/Hymnal/ViewModels/CorkboardViewModel.cs` (`BeatsRequested` observable; in Anthology, double-click / `OpenCardCommand` on a story card or Part divider raises `BeatsRequested` instead of opening the editor; an **Open** button/menu item keeps opening the editor), `src/Hymnal/Views/CorkboardView.axaml` (Beats/Open buttons shown only in Anthology), `src/Hymnal/App.axaml.cs` (register `IBeatBoardService`, `BeatBoardViewModel`, `PlanHostViewModel`), `AGENTS.md` (beat boards: storage path, keys, `IBeatBoardService`)
- Test: `tests/Hymnal.Core.Tests/ViewModels/PlanHostViewModelTests.cs`, view smoke test `tests/Hymnal.Core.Tests/Views/BeatBoardViewSmokeTests.cs`

**Interfaces:**
- Consumes: Task 3; `CorkboardViewModel`; `WorkspaceViewModel.Profile`.
- Produces:
  - `PlanHostViewModel(WorkspaceViewModel workspace, CorkboardViewModel corkboard, BeatBoardViewModel beatBoard)`
  - `object Content` (`BeatBoardViewModel` or `CorkboardViewModel`), `bool ShowBreadcrumb`, `string BreadcrumbParent` (`"Collection"`), `ReactiveCommand BackToCollectionCommand`, `bool ShowFilesStrip`, `bool IsFilesStripExpanded` (default false), `CorkboardViewModel Corkboard` (for the strip).
  - Short Story: on open / profile change → `beatBoard.OpenBoardAsync("story", <first chapter title or "Story">, <all chapter RelativePaths>)`; `Content = beatBoard`; `ShowFilesStrip = true` when the story has 2+ chapter files.
  - Anthology: `Content = corkboard`; `BeatsRequested(chapterVm)` → open board keyed by `chapterVm.Uuid` with title `chapterVm.Node.Title` and scope = that node (single file) or the chapters inside that Part; `Content = beatBoard`, `ShowBreadcrumb = true`; Back → flush, `Content = corkboard`.
  - Profile switch to Novel or workspace close → `beatBoard.CloseBoardAsync()`; MainWindow maps Plan back to `CorkboardViewModel`.
  - BeatBoardView: inline template picker when `IsEmpty` (cards with name + description; MICE asks for thread via a small ComboBox); otherwise ordered beat cards (drag to reorder via `MoveBeatCommand`, Add, Delete, title/note editing, scene link ComboBox from `SceneOptions` + "None", missing-link warning), **Apply template…** flyout with Append/Replace.
- [ ] **Step 1: Write failing tests:** `Novel_plan_content_is_corkboard`; `Story_plan_opens_story_board`; `Story_with_two_files_shows_files_strip`; `Anthology_beats_request_drills_in_and_back_returns`; `Anthology_part_story_scope_is_its_chapters`; `Anthology_double_click_requests_beats_not_editor` (CorkboardViewModel); `Switching_to_novel_returns_plan_to_corkboard_and_closes_board`; smoke test: BeatBoardView/PlanHostView AXAML compile and bind (pattern from existing `*ViewSmokeTests`).
- [ ] **Step 2: Run** each new filter → FAIL.
- [ ] **Step 3: Implement** VMs, views, DI, AGENTS.md.
- [ ] **Step 4: Run** new filters, `FullyQualifiedName~Corkboard`, `FullyQualifiedName~MainWindow`, `FullyQualifiedName~Ideas` → PASS; `dotnet build src/Hymnal/Hymnal.csproj --no-incremental` → 0 warnings; launch with `DISPLAY=:1` to confirm startup.
- [ ] **Step 5: Commit** `feat(app): add beat board plan mode and anthology drill-in`.
