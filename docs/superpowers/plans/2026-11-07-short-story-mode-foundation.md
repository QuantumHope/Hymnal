# Short Story Mode — Plan 1: Project Kind Foundation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-workspace project kind (Novel / Short Story / Anthology) with initialization, a profile that drives statuses, Gantt phases, target presets, labels, stats and AI prompts, and a Project section in Settings.

**Architecture:** A Core `ProjectProfile` (pure, immutable) is built from `.hymnal-data/project.json`. `WorkspaceViewModel` loads it before binding a workspace and exposes it as a reactive `Profile` property; every other view model reads `Profile` from the workspace (or from its `ChapterViewModel`). Existing static helpers (`PhaseCompletion`, `GanttProjection`) gain profile-taking overloads; their old signatures delegate to `ProjectProfile.Novel`, so novel behavior is unchanged. Changing the kind saves `project.json` and reloads the workspace.

**Tech Stack:** .NET 10, C#, Avalonia 12, ReactiveUI, DynamicData, xUnit, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-07-short-story-mode-design.md` (§2, §3, §5). Beat board (§4.1–4.3) is Plan 2; embedded storage (§4.4–4.6) is Plan 3.

## Global Constraints

- Solution file is `Hymnal.slnx`. Test command: `dotnet test Hymnal.slnx` (full suite is slow, >10 min; use `--filter` per task, full run at the end).
- `project.json` path: `<workspaceRoot>/.hymnal-data/project.json`; shape `{ "schemaVersion": 1, "kind": "novel" | "story" | "anthology", "storage": "sidecar" | "embedded" }`; camelCase enum strings.
- Missing `project.json` → Novel/Sidecar, no prompt. Unreadable/unknown kind → Novel/Sidecar + `INotificationService.ShowInfo` warning; never overwrite on read failure.
- All writes under `.hymnal-data` go through `IMetadataStore.WriteTextAtomicAsync`. Service methods return `Result<T>`.
- Novel output must be identical to today: 7 statuses, `GanttProjection.PhaseNames`, current `PhaseCompletion` sequence, current AI prompts byte-for-byte.
- Short Story / Anthology statuses: Idea=`Planned`, Drafting=`Drafting`, Revising=`Editing`, Done=`Done`. Gantt phases: `["Drafting", "Editing"]`. Display mapping: `Outlining`→`Planned`; `Polishing`,`Reviewing`→`Editing`. Stored values never rewritten by display mapping.
- Target presets (story/anthology only): `flash` Flash 0–1,000; `shortStory` Short Story 1,000–7,500; `novelette` Novelette 7,500–17,500; `novella` Novella 17,500–40,000; `custom` Custom (null/null).
- The "Keep everything in the story file" checkbox and `storage: embedded` behavior are **not** built in this plan (Plan 3). `storage` is still read/written and round-tripped.
- Logic in ViewModels/Core, not code-behind (code-behind may only open dialogs and forward results).

## Review Focus

1. **Existing novel workspace with no `project.json`** — opens with zero prompts and identical statuses, Gantt columns and AI prompts. Pinned by Task 2 (`Novel_profile_matches_legacy_lists`), Task 3 (legacy overload equality), Task 9 (`Novel_prompt_is_unchanged`), Task 6 (`Open_existing_workspace_without_project_json_uses_novel`).
2. **Novel → Short Story switch with stored `Polishing`/`Outlining` statuses** — shown as Revising/Idea, completing a phase from stored `Polishing` advances to `Done`, switching back shows `Polishing` again. Pinned by Task 2 (`Story_display_maps_legacy_statuses`) and Task 3 (`Story_complete_from_polishing_goes_to_done`).
3. **Malformed `project.json`** — app opens as Novel with a warning and the file is left byte-identical. Pinned by Task 1 (`Load_malformed_returns_fail_and_does_not_touch_file`) and Task 6 (`Open_with_malformed_project_json_warns_and_uses_novel`).
4. **Initialize a folder whose `.md` files have mixed-case extensions or spaces, or that has `manuscript/Book.txt`** — `.MD` included, names with spaces preserved, `manuscript/Book.txt` means no initialization prompt. Pinned by Task 4 (`Needs_initialization_false_when_manuscript_book_txt_exists`, `Initialize_lists_md_case_insensitively_sorted`).
5. **User confirms Initialize, then cancels the type dialog** — nothing is written to the folder. Pinned by Task 6 (`Cancel_type_choice_writes_nothing`).

---

### Task 1: Project settings model and service

**Files:**
- Create: `src/Hymnal.Core/Models/ProjectKind.cs` (`ProjectKind { Novel, Story, Anthology }`, `StorageMode { Sidecar, Embedded }`)
- Create: `src/Hymnal.Core/Models/ProjectSettings.cs`
- Create: `src/Hymnal.Core/Services/ProjectSettingsService.cs`
- Test: `tests/Hymnal.Core.Tests/Services/ProjectSettingsServiceTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ProjectSettings(ProjectKind Kind = ProjectKind.Novel, StorageMode Storage = StorageMode.Sidecar) { public static ProjectSettings Default { get; } }`
  - `public sealed class ProjectSettingsService(IMetadataStore store)`
    - `Task<Result<ProjectSettings>> LoadAsync(string workspaceRoot)` — missing file → `Ok(Default)`; malformed JSON, unsupported `schemaVersion`, or unknown `kind`/`storage` → `Fail(message)`.
    - `Task<Result<Unit>> SaveAsync(string workspaceRoot, ProjectSettings settings)`
    - `public static string ProjectFilePath(string workspaceRoot)`

- [ ] **Step 1: Write failing tests** (temp dir per test, `try/finally` cleanup, real `MetadataStore`):
  - `Load_missing_returns_default` → `IsSuccess`, `Value == ProjectSettings.Default`.
  - `Save_then_load_round_trips` for each `ProjectKind` × `StorageMode`.
  - `Save_writes_camelcase_schema_v1` → file text contains `"schemaVersion": 1` (or `"schemaVersion":1`), `"kind": "anthology"`, `"storage": "sidecar"`.
  - `Load_unknown_kind_returns_fail` with file `{"schemaVersion":1,"kind":"poem","storage":"sidecar"}`.
  - `Load_malformed_returns_fail_and_does_not_touch_file` — write `{not json`, call `LoadAsync`, assert `!IsSuccess` and file bytes unchanged.
- [ ] **Step 2: Run** `dotnet test Hymnal.slnx --filter FullyQualifiedName~ProjectSettingsServiceTests` — expect compile failure / FAIL.
- [ ] **Step 3: Implement.** Mirror `TargetsService` JSON options (`JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)`), private DTO `{ SchemaVersion, Kind, Storage }`. Catch `JsonException` → `Fail`.
- [ ] **Step 4: Run** the filter again — PASS.
- [ ] **Step 5: Commit** `feat(core): add project settings model and service`.

---

### Task 2: ProjectProfile

**Files:**
- Create: `src/Hymnal.Core/Models/StatusOption.cs` — `public sealed record StatusOption(ChapterStatus Value, string Label);`
- Create: `src/Hymnal.Core/Models/TargetPreset.cs` — `public sealed record TargetPreset(string Key, string Label, int? MinWords, int? MaxWords);`
- Create: `src/Hymnal.Core/Models/ProjectProfile.cs`
- Test: `tests/Hymnal.Core.Tests/Models/ProjectProfileTests.cs`

**Interfaces:**
- Consumes: `ProjectKind`, `StorageMode`, `ProjectSettings` (Task 1).
- Produces `public sealed class ProjectProfile`:
  - `static ProjectProfile Novel { get; }`, `static ProjectProfile For(ProjectSettings settings)`
  - `ProjectKind Kind`, `StorageMode Storage`
  - `string UnitNoun` (Chapter / Scene / Story), `string PartNoun` (Part / Part / Story with scenes), `string CollectionNoun` (Book / Story / Collection), `string InfoPanelTitle` (Chapter Info / Story Info / Story Info)
  - `string BookStatsTitle` ("BOOK STATS" / "" / "COLLECTION STATS"), `string BookStatsBarLabel` ("BY PART" / "" / "BY STORY"), `string PartStatsTitle` ("PART STATS" / "" / "STORY STATS"), `string PartStatsBarLabel` ("BY CHAPTER" / "" / "BY SCENE")
  - `bool ShowsBookStats` (true / false / true), `bool AllowsPartTargets` (false / false / true)
  - `IReadOnlyList<StatusOption> VisibleStatuses`
  - `IReadOnlyList<string> GanttPhases`
  - `ChapterStatus DefaultStatus` (Novel `Outlining`; others `Planned`)
  - `ChapterStatus DisplayStatus(ChapterStatus stored)`
  - `string LabelFor(ChapterStatus stored)` — label of `DisplayStatus(stored)`
  - `ChapterStatus? NextStatus(ChapterStatus stored)` — advance from `DisplayStatus(stored)` within `VisibleStatuses`; `null` at Done
  - `bool IsTrackablePhase(ChapterStatus stored)` — `GanttPhases` contains `stored.ToString()`
  - `IReadOnlyList<TargetPreset> TargetPresets` (empty for Novel)
  - `string? PresetLabel(string? key)`

Novel labels are the enum names (`"Planned"`, `"Outlining"`, …) so existing UI text is unchanged.

- [ ] **Step 1: Write failing tests:**
  - `Novel_profile_matches_legacy_lists` — `VisibleStatuses.Select(s => s.Value)` equals `Enum.GetValues<ChapterStatus>()`; labels equal `ToString()`; `GanttPhases` equals `GanttProjection.PhaseNames`; `TargetPresets` empty; `DefaultStatus == Outlining`; for every status `NextStatus(s) == PhaseCompletion.GetNextStatus(s)`.
  - `Story_visible_statuses` — values `[Planned, Drafting, Editing, Done]`, labels `["Idea", "Drafting", "Revising", "Done"]`; same for Anthology.
  - `Story_display_maps_legacy_statuses` — `DisplayStatus(Outlining) == Planned`, `DisplayStatus(Polishing) == Editing`, `DisplayStatus(Reviewing) == Editing`, `LabelFor(Polishing) == "Revising"`.
  - `Story_next_status_sequence` — `Planned→Drafting`, `Drafting→Editing`, `Editing→Done`, `Polishing→Done`, `Outlining→Drafting`, `Done→null`.
  - `Story_gantt_phases` — `["Drafting", "Editing"]`; `IsTrackablePhase(Polishing) == false`.
  - `Story_target_presets` — keys `["flash","shortStory","novelette","novella","custom"]` with the Global Constraints min/max; `PresetLabel("novelette") == "Novelette"`, `PresetLabel(null) == null`.
  - `Labels_per_kind` — `[Theory]` over the three kinds asserting `UnitNoun`, `PartNoun`, `InfoPanelTitle`, `ShowsBookStats`, `AllowsPartTargets` per the table above.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~ProjectProfileTests` — FAIL.
- [ ] **Step 3: Implement** with a private constructor and two static instances built once (`Novel`, `ShortForm(kind)`), `For` choosing by `Kind`. Story and Anthology share status/phase/preset data and differ only in labels and switches.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat(core): add ProjectProfile`.

---

### Task 3: Profile-aware phase completion, Gantt projection, default phase data

**Files:**
- Modify: `src/Hymnal.Core/Services/PhaseCompletion.cs`
- Modify: `src/Hymnal.Core/Services/GanttProjection.cs`
- Modify: `src/Hymnal.Core/Services/PhaseDataService.cs:37`
- Test: `tests/Hymnal.Core.Tests/Services/PhaseCompletionTests.cs`, `tests/Hymnal.Core.Tests/Services/GanttProjectionProfileTests.cs` (new)

**Interfaces:**
- Consumes: `ProjectProfile` (Task 2).
- Produces:
  - `PhaseCompletion.CompleteAndAdvance(PhaseData current, DateOnly today, ProjectProfile profile)`; existing `CompleteAndAdvance(PhaseData, DateOnly)` delegates with `ProjectProfile.Novel`. `GetNextStatus`/`IsTrackablePhase` keep their signatures (Novel) — profile code calls `profile.NextStatus`/`profile.IsTrackablePhase`.
  - `GanttProjection.Project(ChapterNode node, PhaseData? phase, ProjectProfile profile)` and `BuildSegments(PhaseData pd, ProjectProfile profile)`; existing overloads delegate with `ProjectProfile.Novel`. `PhaseNames` unchanged.
  - `PhaseDataService.DefaultPhaseDataFor(ProjectProfile profile) => new() { Status = profile.DefaultStatus }`; `DefaultPhaseData` unchanged.

Note: `ProjectProfile.Novel.GanttPhases` must reference the same values as `GanttProjection.PhaseNames` — define the array once in `ProjectProfile` and have `PhaseNames` return it, or vice versa, avoiding a static-init cycle (keep the literal in `ProjectProfile`).

- [ ] **Step 1: Write failing tests:**
  - `Story_complete_from_drafting_goes_to_editing` — Drafting with no schedule, today 2026-11-07 → `Status == Editing`, `Schedule["Drafting"]` has `EndDate "2026-11-07"`, `Progress 100`.
  - `Story_complete_from_polishing_goes_to_done` — stored `Polishing` under story profile → `Status == Done`; `Schedule` has no `"Polishing"` key added.
  - `Story_complete_from_planned_goes_to_drafting`.
  - `Legacy_overload_equals_novel_profile` — for each non-Done status, `CompleteAndAdvance(pd, d)` and `CompleteAndAdvance(pd, d, ProjectProfile.Novel)` produce equal `Status`, dates and schedule keys.
  - `Story_segments_only_drafting_and_editing` — `BuildSegments(pd, story).Select(s => s.PhaseName)` equals `["Drafting", "Editing"]`.
  - `Default_phase_data_for_story_is_planned`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~PhaseCompletionTests|FullyQualifiedName~GanttProjectionProfileTests"` — FAIL.
- [ ] **Step 3: Implement** by threading `profile` through: replace `GetNextStatus(current.Status)` with `profile.NextStatus(current.Status)`, `IsTrackablePhase(x)` with `profile.IsTrackablePhase(x)`, the `Done` check with `profile.DisplayStatus(current.Status) == Done`, and the `PhaseNames` loop in `BuildSegments` with `profile.GanttPhases`.
- [ ] **Step 4: Run** filter plus existing `GanttViewModelTests` and `PhaseCompletionTests` — all PASS.
- [ ] **Step 5: Commit** `feat(core): make phase completion and gantt projection profile-aware`.

---

### Task 4: Target preset field and project initializer

**Files:**
- Modify: `src/Hymnal.Core/Models/WordCountTarget.cs` — add `public string? Preset { get; init; }`
- Create: `src/Hymnal.Core/Services/ProjectInitializer.cs`
- Test: `tests/Hymnal.Core.Tests/Services/TargetsServiceTests.cs` (add cases), `tests/Hymnal.Core.Tests/Services/ProjectInitializerTests.cs`

**Interfaces:**
- Consumes: `ProjectSettingsService`, `ProjectSettings` (Task 1), `IMetadataStore`.
- Produces `public sealed class ProjectInitializer(IMetadataStore store, ProjectSettingsService settings)`:
  - `static bool NeedsInitialization(string folderPath)` — true when neither `<folder>/Book.txt` nor `<folder>/manuscript/Book.txt` exists.
  - `Task<Result<Unit>> InitializeAsync(string folderPath, ProjectKind kind)` — writes root `Book.txt` (one filename per line, `\n`, trailing newline) listing top-level files whose extension equals `.md` ignoring case, sorted `StringComparer.OrdinalIgnoreCase`; if none, creates the starter file (`chapter-01.md` Novel, `story.md` Story, `story-01.md` Anthology) with content `# Untitled\n` and lists it; then saves `new ProjectSettings(kind)`. Fails (without writing) if `Book.txt` already exists.

- [ ] **Step 1: Write failing tests:**
  - `Targets_round_trip_preset` — upsert `{ MinWords 1000, MaxWords 7500, Preset "shortStory" }`, load, `Preset == "shortStory"`.
  - `Targets_load_without_preset_field` — existing v1 JSON without `preset` loads with `Preset == null`.
  - `Needs_initialization_true_for_empty_folder`; `Needs_initialization_false_when_root_book_txt_exists`; `Needs_initialization_false_when_manuscript_book_txt_exists`.
  - `Initialize_lists_md_case_insensitively_sorted` — files `b story.md`, `A.MD`, `notes.txt`, sub-folder `x/c.md` → `Book.txt` lines exactly `["A.MD", "b story.md"]`.
  - `Initialize_empty_folder_creates_starter_per_kind` — `[Theory]` Novel→`chapter-01.md`, Story→`story.md`, Anthology→`story-01.md`; file exists, `Book.txt` lists it.
  - `Initialize_writes_project_json` — `ProjectSettingsService.LoadAsync` returns the chosen kind, `Storage == Sidecar`, and `.hymnal-data` was created.
  - `Initialize_fails_when_book_txt_exists` — returns `!IsSuccess`, `project.json` not created.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ProjectInitializerTests|FullyQualifiedName~TargetsServiceTests"` — FAIL.
- [ ] **Step 3: Implement.** Book.txt and starter file are new files in an un-initialized folder; write them via `IMetadataStore.WriteTextAtomicAsync` too.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat(core): add target presets field and project initializer`.

---

### Task 5: AI prompt addenda

**Files:**
- Create: `src/Hymnal.Core/Resources/Prompts/short-story.txt`, `src/Hymnal.Core/Resources/Prompts/anthology.txt` (picked up by the existing `Resources\Prompts\*.txt` glob)
- Modify: `src/Hymnal.Core/Interfaces/IRolePromptProvider.cs`, `src/Hymnal.Core/Services/Ai/RolePromptProvider.cs`
- Modify: `src/Hymnal/ViewModels/Ai/AiChatViewModel.cs:225`
- Test: `tests/Hymnal.Core.Tests/Services/Ai/RolePromptProviderTests.cs`

**Interfaces:**
- Produces: `string GetSystemPrompt(AiRole role, ProjectKind kind)` on `IRolePromptProvider`. Existing `GetSystemPrompt(AiRole)` returns the same as `kind: Novel`. For Story/Anthology: Novel prompt + `"\n\n"` + addendum (trimmed). Update any test doubles implementing `IRolePromptProvider` (`tests/Hymnal.Core.Tests/TestDoubles/AiTestDoubles.cs`).
- Consumes (App): `AiChatViewModel` calls `_roleProvider.GetSystemPrompt(SelectedRole, _workspace.Profile.Kind)` (Profile added in Task 6; until then pass `ProjectKind.Novel` and switch in Task 6).

`short-story.txt` exact copy:

```text
## Project type: short story

This manuscript is a single short story, not a novel. Judge it by short-form standards:
- Economy: every scene and sentence should earn its place; flag digressions a novel could afford but a short story cannot.
- A single, focused arc with few point-of-view characters and a tight time span.
- The opening line and first paragraph must establish voice and a question quickly.
- The ending must pay off what the opening set up; flag endings that stop rather than resolve.
- When the author has a length target (flash, short story, novelette, novella), weigh suggestions against it.
```

`anthology.txt` exact copy:

```text
## Project type: anthology

This manuscript is a collection of short stories. Apply short-form standards to each story:
- Economy, a single focused arc, a strong opening, and an ending that pays off its setup.
- Weigh suggestions against each story's length target when one is set.
Across the collection, also consider:
- Variety of voice, tone, length and theme from story to story.
- The order of stories: the opener and closer, pacing, and how adjacent stories play off each other.
```

- [ ] **Step 1: Write failing tests:** `Novel_prompt_is_unchanged` (for each role, `GetSystemPrompt(role, Novel) == GetSystemPrompt(role)`), `Story_prompt_appends_addendum` (ends with the trimmed short-story text, starts with the Novel prompt), `Anthology_prompt_appends_addendum`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~RolePromptProviderTests` — FAIL.
- [ ] **Step 3: Implement** — load `short-story` / `anthology` in the constructor via existing `LoadResource`.
- [ ] **Step 4: Run** — PASS; build `src/Hymnal/Hymnal.csproj`.
- [ ] **Step 5: Commit** `feat(ai): add short story and anthology prompt addenda`.

---

### Task 6: Workspace profile loading and initialization flow

**Files:**
- Create: `src/Hymnal.Core/Interfaces/IProjectInitPrompt.cs`
- Modify: `src/Hymnal/ViewModels/WorkspaceViewModel.cs` (constructor ~169, `ReloadWorkspaceAsync` ~513, `OpenWorkspaceAsync` ~534, `InitAsync` ~600, `new ChapterViewModel(` ~738)
- Modify: `src/Hymnal/ViewModels/ChapterViewModel.cs` (constructor 248; status default 261; `CompletePhaseAsync` ~502; segments ~564)
- Modify: `src/Hymnal/ViewModels/Ai/AiChatViewModel.cs:225` (switch to `_workspace.Profile.Kind`)
- Test: `tests/Hymnal.Core.Tests/ViewModels/WorkspaceProjectKindTests.cs`

**Interfaces:**
- Produces:
  - `public interface IProjectInitPrompt { Task<bool> ConfirmInitializeAsync(string folderPath); Task<ProjectKind?> ChooseProjectKindAsync(); }`
  - `WorkspaceViewModel`: new optional trailing ctor params `ProjectSettingsService? projectSettings = null, ProjectInitializer? projectInitializer = null, IProjectInitPrompt? initPrompt = null` (existing tests keep compiling; null settings service → always Novel; null prompt → old error behavior).
  - `WorkspaceViewModel.Profile` (`ProjectProfile`, raises `PropertyChanged`; default `ProjectProfile.Novel`; reset to Novel on close).
  - `public Task<Result<Unit>> ReloadProjectAsync()` — re-reads `project.json`, sets `Profile`, then `ReloadWorkspaceAsync(WorkspaceRoot, BookTxtPath)`.
  - `ChapterViewModel`: new optional trailing ctor param `ProjectProfile? profile = null` → `public ProjectProfile Profile { get; }` (Novel when null); `public string StatusLabel` (= `Profile.LabelFor(Status)`, raised when `Status` changes).

Behavior:
- Before every `BindModel` (open, restore, reload), load settings; on `Fail`, `ShowInfo($"Couldn't read project settings ({error}). Opening as a novel.")` and use Novel. Profile must be set **before** `BindModel` so `ChapterViewModel`s are constructed with it.
- `OpenWorkspaceAsync`: after picking a folder, if `ProjectInitializer.NeedsInitialization(path)` and `initPrompt != null`: `ConfirmInitializeAsync` false → return (no error); `ChooseProjectKindAsync` null → return; else `InitializeAsync(path, kind)` (on Fail → `ShowError`, return), then continue the normal load.
- `InitAsync` (restore last workspace) never prompts.
- `ChapterViewModel`: default status `profile.DefaultStatus`; `CompletePhaseAsync` calls `CompleteAndAdvance(base, today, Profile)` and `PhaseDataService.DefaultPhaseDataFor(Profile)`; segments use `BuildSegments(pd, Profile)`.

- [ ] **Step 1: Write failing tests** (reuse the harness pattern in `WorkspaceSidebarRenameTests.cs:385`; temp workspaces; fake `IProjectInitPrompt` recording calls; fake folder picker returning the temp path):
  - `Open_existing_workspace_without_project_json_uses_novel` — `Profile == ProjectProfile.Novel`, prompt never called.
  - `Open_story_workspace_sets_profile_and_chapter_profiles` — `project.json` kind story → `Profile.Kind == Story`, every `Nodes[i].Profile.Kind == Story`, a chapter with no phase data has `StatusLabel == "Idea"`.
  - `Open_with_malformed_project_json_warns_and_uses_novel` — fake notification service received one `ShowInfo` containing `"Opening as a novel"`; file bytes unchanged.
  - `Open_uninitialized_folder_confirm_and_choose_story_initializes_and_opens` — `HasWorkspace`, `Book.txt` exists, `Profile.Kind == Story`.
  - `Decline_initialize_writes_nothing` — folder contents unchanged, `HasWorkspace == false`, no `ShowError`.
  - `Cancel_type_choice_writes_nothing` — confirm true, choose null → folder contents unchanged.
  - `Reload_project_picks_up_new_kind` — open as novel, save story settings via `ProjectSettingsService`, `ReloadProjectAsync()` → `Profile.Kind == Story`.
  - `Story_chapter_complete_phase_from_drafting_goes_to_editing` — via `ChapterViewModel.CompleteCurrentPhaseAsync()`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~WorkspaceProjectKindTests` — FAIL.
- [ ] **Step 3: Implement** per Behavior above.
- [ ] **Step 4: Run** filter plus `FullyQualifiedName~ViewModels.Workspace` — PASS.
- [ ] **Step 5: Commit** `feat: load project profile into workspace and add initialization flow`.

---

### Task 7: Project type dialog, icons, prompt implementation, DI

**Files:**
- Modify: `src/Hymnal/Themes/Icons.axaml` — `StreamGeometry` keys `Icon.ProjectNovel` (closed book), `Icon.ProjectStory` (single page), `Icon.ProjectAnthology` (stacked pages); simple 24×24 outline paths
- Create: `src/Hymnal/ViewModels/ProjectTypeCatalog.cs`, `src/Hymnal/ViewModels/ProjectTypeCardViewModel.cs`, `src/Hymnal/ViewModels/ProjectTypeDialogViewModel.cs`
- Create: `src/Hymnal/Views/ProjectTypeDialog.axaml(.cs)`, `src/Hymnal/Views/ConfirmInitializeDialog.axaml(.cs)` (or reuse an existing confirm dialog pattern from `CorkboardView.axaml.cs`)
- Create: `src/Hymnal/Infrastructure/ProjectInitPrompt.cs` (implements `IProjectInitPrompt`, takes `Func<Window?>` owner accessor like `FolderPickerService`)
- Modify: `src/Hymnal/App.axaml.cs` — register `ProjectSettingsService`, `ProjectInitializer`, `IProjectInitPrompt`; pass them to `WorkspaceViewModel`
- Test: `tests/Hymnal.Core.Tests/ViewModels/ProjectTypeDialogViewModelTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ProjectTypeInfo(ProjectKind Kind, string Title, string Description, string Impact, string IconKey);`
  - `public static class ProjectTypeCatalog { public static IReadOnlyList<ProjectTypeInfo> All { get; } public static string SwitchSummary(ProjectKind target); }`
  - `ProjectTypeCardViewModel(ProjectTypeInfo info)` with `Info`, `IsSelected` (reactive).
  - `ProjectTypeDialogViewModel` with `Cards`, `SelectedKind` (`ProjectKind?`), `SelectCommand: ReactiveCommand<ProjectTypeCardViewModel, Unit>`, `CreateCommand: ReactiveCommand<Unit, ProjectKind>` (enabled only when `SelectedKind` has a value).

Catalog copy (exact):

| Kind | Title | Description | Impact | IconKey |
|---|---|---|---|---|
| Novel | Novel | One long work in chapters | Full toolset: 7-stage chapter lifecycle, phase scheduling, chapter targets and book-wide stats. | Icon.ProjectNovel |
| Story | Short Story | One standalone story | Simpler 4-stage status, story-length targets, and a Corkboard of beats from a plot template. | Icon.ProjectStory |
| Anthology | Anthology | A collection of stories | Short story tools for each story, plus collection order, Parts for long stories and total word count. | Icon.ProjectAnthology |

`SwitchSummary`: Novel → `"Statuses and tools will switch to the full novel toolset. No data is lost."`; Story → `"Status labels and Corkboard will switch to short story tools. No data is lost."`; Anthology → `"Status labels, targets and stats will switch to anthology tools. No data is lost."`

Confirm dialog copy: title `Initialize project`, body `This folder isn't a Hymnal project yet. Initialize it?`, buttons `Initialize` / `Cancel`. Type dialog: title `Choose project type`, button `Create` / `Cancel`.

- [ ] **Step 1: Write failing tests:** `Catalog_has_three_kinds_in_order` (Novel, Story, Anthology with the exact copy above), `Create_disabled_until_selection`, `Select_marks_only_one_card_selected`, `Create_returns_selected_kind`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~ProjectTypeDialogViewModelTests` — FAIL.
- [ ] **Step 3: Implement** VMs, then views (cards as a horizontal `ItemsControl` of toggle-style buttons showing `PathIcon` with `Data="{DynamicResource {Binding IconKey}}"`-equivalent lookup done in the card VM or a converter, title, description, impact). `ProjectInitPrompt` shows dialogs on the UI thread and returns results.
- [ ] **Step 4: Run** tests — PASS. Run the app (`dotnet run --project src/Hymnal/Hymnal.csproj`), open an empty temp folder, confirm both dialogs appear and that choosing Short Story opens a workspace with `story.md`.
- [ ] **Step 5: Commit** `feat: add project type dialog and initialization prompt`.

---

### Task 8: Profile-driven statuses, phases and labels in the UI

**Files:**
- Modify: `src/Hymnal/ViewModels/ChapterInfoViewModel.cs` (static `AllStatuses` line 35 → instance `StatusOptions`; schedule rows from `PhaseNames` line 106 and checks at 555/569 → `Profile.GanttPhases`; rebuild rows when `_workspaceViewModel.Profile` changes; `PanelTitle` → `Profile.InfoPanelTitle`)
- Modify: `src/Hymnal/ViewModels/ChapterDetailViewModel.cs` (lines 38, 131, 295, 308 — same, from `chapterVm.Profile`)
- Modify: `src/Hymnal/ViewModels/GanttViewModel.cs` (line 78 `AvailableStatuses`; `GanttProjection.Project(..., Profile)` at 759/785/822; `DefaultPhaseDataFor(Profile)` at 523/652/708), `src/Hymnal/ViewModels/GanttRowViewModel.cs:65`
- Modify: `src/Hymnal/ViewModels/CardViewModel.cs:40` (`StatusDisplay => _chapter.StatusLabel`)
- Modify: `src/Hymnal/ViewModels/WorkspaceViewModel.cs` / `ChapterViewModel.cs` — status summaries (`BookStatusSummary`, `PartStatusSummary`) bucket by `Profile.DisplayStatus`
- Modify views: `ChapterInfoView.axaml:48`, `ChapterInfoWidget.axaml:22`, `GanttView.axaml:166` (bind `ItemsSource` to `StatusOptions`/`AvailableStatuses` of `StatusOption`, `DisplayMemberBinding="{Binding Label}"`, `SelectedValueBinding="{Binding Value}"`, `SelectedValue="{Binding Status}"`); `SidebarView.axaml:349`, `EditorChapterBar.axaml:25` (`{Binding StatusLabel}`); `Views/Converters/StatusToBrushConverter.cs:89` tooltip uses label when available
- Modify labels: `MainWindow.axaml.cs:409,456`, `CorkboardView.axaml.cs:508-616`, `CorkboardView.axaml:491`, `NewChapterDialog.axaml:4`, `MainWindow.axaml:128,482,539`, sidebar header text in `SidebarView.axaml` — use `Profile.UnitNoun` / `PartNoun` / `InfoPanelTitle` (`"New {UnitNoun}…"`, `"New {PartNoun}…"`, `"Toggle {InfoPanelTitle}"`)
- Test: `tests/Hymnal.Core.Tests/ViewModels/ProfileDrivenUiTests.cs`; update existing tests that referenced static `AllStatuses`

**Interfaces:**
- Consumes: `WorkspaceViewModel.Profile`, `ChapterViewModel.Profile`/`StatusLabel` (Task 6); `ProjectProfile` (Task 2).
- Produces: `ChapterInfoViewModel.StatusOptions`, `ChapterDetailViewModel.StatusOptions`, `GanttViewModel.AvailableStatuses`, `GanttRowViewModel.AvailableStatuses` — all `IReadOnlyList<StatusOption>`; `ChapterInfoViewModel.PanelTitle` (`string`).

- [ ] **Step 1: Write failing tests:**
  - `Story_chapter_info_status_options_are_four` — labels `["Idea","Drafting","Revising","Done"]`; schedule row phase names `["Drafting","Editing"]`.
  - `Novel_chapter_info_status_options_are_seven` and schedule rows equal `GanttProjection.PhaseNames`.
  - `Story_gantt_rows_have_two_phase_segments`.
  - `Story_card_status_display_maps_polishing_to_revising`.
  - `Story_book_status_summary_buckets_by_display_status` — chapters stored Polishing + Editing → one bucket `Editing` with count 2.
  - `Story_chapter_info_panel_title_is_story_info`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~ProfileDrivenUiTests` — FAIL.
- [ ] **Step 3: Implement**, then fix compile errors in existing tests/views that used `AllStatuses`.
- [ ] **Step 4: Run** filter, plus `FullyQualifiedName~Gantt|FullyQualifiedName~Corkboard|FullyQualifiedName~Sidebar` — PASS. Manually open a story workspace and check the sidebar, Chapter Info, Corkboard card and Gantt show short story labels.
- [ ] **Step 5: Commit** `feat: drive statuses, phases and labels from project profile`.

---

### Task 9: Target presets, Part targets, and collection stats

**Files:**
- Modify: `src/Hymnal/ViewModels/ChapterViewModel.cs` (target flyout ~224–236, `SetTargetAsync`/`ConfirmTargetAsync` ~460–480, `ProximityFill` ~292–301)
- Modify: `src/Hymnal/ViewModels/ChapterInfoViewModel.cs` (`SetTargetAsync(int?)` ~688, `FormatTargetDisplay`), `src/Hymnal/ViewModels/CardViewModel.cs` (`FormatTargetDisplay`)
- Modify: `src/Hymnal/ViewModels/WorkspaceViewModel.cs` (`OpenTargetFlyout` — allow Part nodes when `Profile.AllowsPartTargets`)
- Modify: `src/Hymnal/ViewModels/BookStatsViewModel.cs` (lines 140–235, 368)
- Modify views: the target flyout in `SidebarView.axaml` and target section in `ChapterInfoView.axaml:188-225` — preset `ComboBox` shown when `TargetPresets` non-empty
- Test: `tests/Hymnal.Core.Tests/ViewModels/TargetPresetAndStatsTests.cs`

**Interfaces:**
- Consumes: `ProjectProfile.TargetPresets`, `PresetLabel`, `AllowsPartTargets`, `ShowsBookStats`, stats titles (Task 2); `WordCountTarget.Preset` (Task 4).
- Produces on `ChapterViewModel`:
  - `IReadOnlyList<TargetPreset> TargetPresets => Profile.TargetPresets`, `bool HasTargetPresets`
  - `TargetPreset? PendingPreset` (reactive); setting it to a non-custom preset sets `PendingMinWords`/`PendingMaxWords` to its values
  - `ConfirmTargetAsync` saves `Preset = PendingPreset?.Key`, except when Pending min/max no longer equal the preset's values → `"custom"`
  - `int EffectiveWordCount` — `PartTotalWordCount` for Part nodes, else `WordCount`; `ProximityFill` uses it
- `FormatTargetDisplay` (both VMs): when `Profile.PresetLabel(target.Preset)` is non-null and the preset isn't `custom`, prefix `"{label} · "` to the existing text.
- `BookStatsViewModel`: `IsVisible` false whenever `!_workspace.Profile.ShowsBookStats`; titles/bar labels from profile; `ContextTitle` fallback `Profile.CollectionNoun`; status loop at 368 iterates `Profile.VisibleStatuses` with `DisplayStatus` bucketing.

- [ ] **Step 1: Write failing tests:**
  - `Story_chapter_has_five_presets`; `Novel_chapter_has_no_presets`.
  - `Selecting_preset_fills_pending_min_max` — `novelette` → 7500/17500.
  - `Confirm_with_preset_saves_preset_key` — reload `TargetsService` → `Preset == "novelette"`.
  - `Editing_min_after_preset_saves_custom`.
  - `Target_display_prefixes_preset_label` — story, Preset `shortStory`, min 1000 max 7500 → text starts with `"Short Story · "`.
  - `Anthology_part_proximity_uses_part_total` — Part with two scenes 2,000 + 1,750 words, target 1,000–7,500 → `EffectiveWordCount == 3750`.
  - `Anthology_allows_part_target_flyout`; `Novel_does_not_open_part_target_flyout`.
  - `Story_hides_book_stats`; `Anthology_book_stats_titles` (`"COLLECTION STATS"`, `"BY STORY"`; Part mode `"STORY STATS"`, `"BY SCENE"`).
- [ ] **Step 2: Run** `--filter FullyQualifiedName~TargetPresetAndStatsTests` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter plus `FullyQualifiedName~TargetsService` — PASS. Manually set a preset target on a story and on an anthology Part.
- [ ] **Step 5: Commit** `feat: add story target presets, part targets and collection stats`.

---

### Task 10: Project section in Settings, docs, full verification

**Files:**
- Modify: `src/Hymnal/ViewModels/SettingsViewModel.cs` (ctor adds `WorkspaceViewModel workspace, ProjectSettingsService projectSettings`)
- Modify: `src/Hymnal/Views/SettingsView.axaml` (new "Project" section above AI providers, reusing the card template from `ProjectTypeDialog.axaml` — extract it to a shared `DataTemplate` resource in `src/Hymnal/Themes/ControlStyles.axaml` if needed)
- Modify: `src/Hymnal/App.axaml.cs:215` (SettingsViewModel factory args)
- Modify: `AGENTS.md`
- Test: `tests/Hymnal.Core.Tests/ViewModels/SettingsProjectSectionTests.cs`

**Interfaces:**
- Consumes: `ProjectTypeCatalog`, `ProjectTypeCardViewModel` (Task 7), `WorkspaceViewModel.Profile`/`ReloadProjectAsync` (Task 6), `ProjectSettingsService` (Task 1).
- Produces on `SettingsViewModel`: `bool HasWorkspace`, `IReadOnlyList<ProjectTypeCardViewModel> ProjectCards`, `ProjectKind? SelectedProjectKind` (initially `workspace.Profile.Kind`), `string? ProjectChangeSummary` (`ProjectTypeCatalog.SwitchSummary(selected)` when selected ≠ current, else null), `ReactiveCommand<Unit, Unit> ApplyProjectKindCommand` (enabled when selected ≠ current; saves `new ProjectSettings(selected, workspace.Profile.Storage)`, then `ReloadProjectAsync()`; on Fail → `ShowError`).

- [ ] **Step 1: Write failing tests:** `No_workspace_hides_project_section`, `Initial_selection_is_current_kind`, `Changing_selection_shows_summary` (Story → exact Story summary string), `Apply_saves_and_reloads` (afterwards `workspace.Profile.Kind == Story` and `project.json` says story, storage preserved), `Apply_disabled_when_unchanged`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~SettingsProjectSectionTests` — FAIL.
- [ ] **Step 3: Implement** VM, view section and DI.
- [ ] **Step 4: Update `AGENTS.md`:** test command → `dotnet test Hymnal.slnx`; add a "Project kinds" bullet under Key Patterns › Data Model describing `project.json`, `ProjectProfile`, `WorkspaceViewModel.Profile`, and that statuses for story/anthology relabel a subset of `ChapterStatus`; update the baseline test count to the number reported in Step 5.
- [ ] **Step 5: Run full suite** `dotnet test Hymnal.slnx` (allow >10 min) — 0 failures. Build `dotnet build src/Hymnal/Hymnal.csproj` — 0 errors.
- [ ] **Step 6: Manual check:** open a novel workspace → Settings → Project → switch to Short Story → Apply; statuses relabel and nothing is lost; switch back → original statuses return.
- [ ] **Step 7: Commit** `feat: add project section to settings; document project kinds`.

---

## Follow-on plans

- **Plan 2 — Beat board:** spec §4.1–4.3 (`IBeatBoardService`, templates, story Corkboard, anthology drill-in). Consumes `ProjectProfile.Kind` and adds `UsesBeatBoard`.
- **Plan 3 — Embedded storage:** spec §4.4–4.6 (`IStoryMetadataStore`, `StoryFileFormat`, `StoryFileWriter`, migration, the "Keep everything in the story file" checkbox, AGENTS.md rule change).
