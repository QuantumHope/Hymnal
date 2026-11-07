# Embedded Story Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a single-file Short Story keep all of its Hymnal data (status, target, schedule, notes, beats) inside its own `.md` file, in a trailing HTML comment that Markua strips from output, with lossless migration to and from sidecar storage.

**Architecture:** Core gets `StoryFileFormat` (split/merge the trailing `<!-- hymnal:v1 … -->` block), `StoryFileWriter` (the single path for writing story `.md` files), and `IStoryMetadataStore` with Sidecar and Embedded implementations selected by `ProjectSettings.Storage`. The editor splits on load and merges on save, so the author sees only prose. View models stop calling `PhaseDataService`/`TargetsService`/`INotesService`/`IBeatBoardService` directly and go through `IStoryMetadataStore`.

**Tech Stack:** .NET 10, Avalonia 12, ReactiveUI, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-07-short-story-mode-design.md` §4.4–4.6, §5.3. Builds on Plans 1–3 on this branch.

## Global Constraints

- Block format, at the very end of the file, exactly one blank line after the prose:
  ```
  <!-- hymnal:v1
  { …json… }
  -->
  ```
  JSON (camelCase): `{ "id", "status", "phaseStartDate", "phaseEndDate", "schedule", "target": { "preset", "minWords", "maxWords" }, "notes", "beats": [ { "id", "title", "note", "scenePath" } ] }`. Absent fields mean "none".
- Only the **last** block whose opening line is exactly `<!-- hymnal:v1` (trimmed of trailing whitespace) and which is followed by nothing but whitespace is recognized.
- Split/merge preserve the prose bytes exactly, and the file's line ending (LF or CRLF, detected from the first line break; default LF).
- Malformed JSON inside a recognized block → defaults + `ShowInfo` warning; the malformed block is kept as prose (never deleted) and a fresh block is appended after it on the next metadata write.
- Embedded storage applies only to `ProjectKind.Story` projects whose `Book.txt` lists exactly one chapter file ("primary file"). If more chapter files appear while embedded, the primary file (the one that has a block, else the first in Book.txt) stays embedded; the others use sidecar data; show `ShowInfo("Keep everything in the story file only covers single-file stories; other files use project data.")` once per workspace open.
- In embedded mode the story board (`BeatBoardService.ShortStoryBoardKey`) lives in the primary file's block.
- Identity: the block's `id` is the registry UUID. On hydration, if a primary file's block id differs from its registry UUID, the registry entry adopts the block id.
- `StoryFileWriter` is the only code that writes story `.md` files for metadata, and the editor save path uses it. AGENTS.md rule becomes: "Don't modify chapter `.md` files outside of `StoryFileWriter`, which the editor save path uses." (Structural create/move in `BookTxtStructureService` stays as is.)
- The editor always hides a recognized block and restores it on save, in every project kind, so a block left after switching to Novel is preserved.
- Word counts, AI context and stats use prose only.
- Hymnal's own writes suppress the editor file watcher (existing `StopWatcher`/`StartWatcher` pattern) and the manuscript watcher where Book.txt is involved.
- Tests: one filter per run `timeout 400 dotnet test Hymnal.slnx --filter "<f>" --blame-hang-timeout 60s`; filtered full suite `--filter "FullyQualifiedName!~GanttView_RowMoveShortcutHelper"`; dispose watcher owners via `OwnedDisposables`. No debug code.

## Review Focus

1. A story file with CRLF endings and no trailing newline → after a metadata write, prose bytes and line endings are unchanged (Task 1/2).
2. Author edits prose (unsaved) while a status change is written for the same open file → unsaved prose is kept and saved with the new metadata (Task 2/4).
3. The file changes on disk outside Hymnal while open (e.g. git checkout) → editor reloads prose and metadata; no block text appears in the editor (Task 4).
4. Migration fails halfway (one file unwritable) → already-migrated files reported, `project.json` unchanged, no data lost in either location (Task 6).
5. A block-looking comment inside the prose (not at the end) → treated as prose, never stripped (Task 1).

---

### Task 1: StoryFileFormat

**Files:**
- Create: `src/Hymnal.Core/Services/StoryFileFormat.cs`, `src/Hymnal.Core/Models/EmbeddedStoryMetadata.cs`
- Test: `tests/Hymnal.Core.Tests/Services/StoryFileFormatTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record EmbeddedStoryMetadata(string? Id, ChapterStatus? Status, DateOnly? PhaseStartDate, DateOnly? PhaseEndDate, IReadOnlyDictionary<string, PhaseScheduleEntry>? Schedule, WordCountTarget? Target, string? Notes, IReadOnlyList<Beat>? Beats)` (`PhaseScheduleEntry` is the existing type used by `PhaseData.Schedule`; match its JSON).
  - `public sealed record StorySplit(string Prose, EmbeddedStoryMetadata? Metadata, bool HadMalformedBlock, string LineEnding)`
  - `public static class StoryFileFormat { StorySplit Split(string text); string Merge(string prose, EmbeddedStoryMetadata? metadata, string lineEnding); string SerializeMetadata(EmbeddedStoryMetadata m); }` — `Merge(prose, null, …)` returns prose unchanged; Merge ensures exactly one blank line between prose and block and a trailing line ending after `-->`.
- [ ] **Step 1: Write failing tests:** round-trip LF and CRLF (Split(Merge(p, m)) gives p and m; prose bytes identical); prose without trailing newline; file with no block → Metadata null, Prose == text; two blocks → only the last recognized, the earlier stays in prose; block followed by non-whitespace text → not recognized; `<!-- hymnal:v1` inside prose mid-file → prose; opening line with trailing spaces recognized; malformed JSON → Metadata null, `HadMalformedBlock` true, Prose == full text (block kept as prose); merging after a malformed split appends a new block after the old one; empty prose; unicode prose; Merge(prose, null) == prose.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~StoryFileFormatTests"` → FAIL.
- [ ] **Step 3: Implement** (string scanning from the end; no regex backtracking over the whole file).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): add story file format for embedded metadata`.

### Task 2: StoryFileWriter

**Files:**
- Create: `src/Hymnal.Core/Interfaces/IOpenStoryBuffer.cs`, `src/Hymnal.Core/Services/StoryFileWriter.cs`
- Test: `tests/Hymnal.Core.Tests/Services/StoryFileWriterTests.cs`

**Interfaces:**
- Consumes: Task 1; `IMetadataStore`.
- Produces:
  - `public interface IOpenStoryBuffer { bool HoldsFile(string absolutePath); EmbeddedStoryMetadata? CurrentMetadata { get; } Task<Result<Unit>> ApplyMetadataAsync(EmbeddedStoryMetadata metadata); }` (implemented by EditorViewModel in Task 4; `ApplyMetadataAsync` replaces held metadata and saves through the normal editor save path, preserving unsaved prose).
  - `public sealed class StoryFileWriter(IMetadataStore store) { void AttachBuffer(IOpenStoryBuffer? buffer); Task<Result<EmbeddedStoryMetadata?>> ReadMetadataAsync(string absolutePath); Task<Result<Unit>> UpdateMetadataAsync(string absolutePath, Func<EmbeddedStoryMetadata?, EmbeddedStoryMetadata> update); Task<Result<Unit>> WriteFileAsync(string absolutePath, string prose, EmbeddedStoryMetadata? metadata, string lineEnding); Task<Result<Unit>> RemoveMetadataAsync(string absolutePath); }`
  - Open file: `UpdateMetadataAsync` reads `buffer.CurrentMetadata`, applies `update`, calls `buffer.ApplyMetadataAsync`. Closed file: read → split → update → merge → atomic write; prose bytes unchanged. Malformed block: update starts from defaults and the new block is appended (old kept). `RemoveMetadataAsync` strips the recognized block (used by migration to sidecar).
- [ ] **Step 1: Write failing tests** (temp dir, real `MetadataStore`, fake buffer): closed-file update preserves prose bytes LF/CRLF/no-trailing-newline; update on a file without block appends one; open-file update goes to the buffer, never touches disk directly; malformed block kept and new block appended; remove strips block and leaves prose bytes; missing file → Fail; read returns metadata or null.
- [ ] **Step 2–4:** run `--filter "FullyQualifiedName~StoryFileWriterTests"` FAIL → implement → PASS.
- [ ] **Step 5: Commit** `feat(core): add story file writer`.

### Task 3: IStoryMetadataStore with Sidecar and Embedded implementations

**Files:**
- Create: `src/Hymnal.Core/Interfaces/IStoryMetadataStore.cs`, `src/Hymnal.Core/Services/SidecarStoryMetadataStore.cs`, `src/Hymnal.Core/Services/EmbeddedStoryMetadataStore.cs`, `src/Hymnal.Core/Services/StoryMetadataStoreFactory.cs`
- Test: `tests/Hymnal.Core.Tests/Services/StoryMetadataStoreTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2; `PhaseDataService`, `TargetsService`, `INotesService`, `IBeatBoardService`, `ChapterRegistryService`, `ProjectSettings`.
- Produces:
  - `public sealed record StoryRef(string Uuid, string RelativePath, string AbsolutePath)`
  - `IStoryMetadataStore`: `Task<Dictionary<string, PhaseData>> LoadPhaseDataAsync(string root, IReadOnlyList<StoryRef> stories)`; `Task UpsertPhaseDataAsync(string root, StoryRef story, Func<PhaseData?, PhaseData> update)`; `Task<Dictionary<string, WordCountTarget>> LoadTargetsAsync(string root, IReadOnlyList<StoryRef> stories)`; `Task SetTargetAsync(string root, StoryRef story, WordCountTarget? target)`; `Task<string> LoadNotesAsync(string root, StoryRef story)`; `Task SaveNotesAsync(string root, StoryRef story, string notes)`; `Task<Result<IReadOnlyList<Beat>>> LoadBeatsAsync(string root, string boardKey)`; `Task<Result<Unit>> SaveBeatsAsync(string root, string boardKey, IReadOnlyList<Beat> beats)`; `void SetPrimaryStory(StoryRef? primary)` (embedded only; null = none).
  - Sidecar: pure delegation to today's services (same files, same behaviour).
  - Embedded: for the primary story, reads/writes its block via `StoryFileWriter` (block `id` = story Uuid); everything else (non-primary files, other board keys) delegates to Sidecar.
  - `StoryMetadataStoreFactory.Create(ProjectSettings settings, …) → IStoryMetadataStore` (Embedded only when `Kind == Story && Storage == Embedded`).
  - `public static StoryRef? ResolvePrimary(IReadOnlyList<StoryRef> chapterFiles, Func<StoryRef, EmbeddedStoryMetadata?> blockOf)` (file with a block, else first).
- [ ] **Step 1: Write failing tests:** sidecar round-trips equal today's services' files (assert the same JSON files are written); embedded round-trips status/schedule/target/notes/beats into the primary file's block and writes no sidecar entries for it; non-primary file in embedded mode uses sidecar; story board beats go to the block; other board keys go to sidecar; `ResolvePrimary` rules; factory selection per kind/storage.
- [ ] **Step 2–4:** run `--filter "FullyQualifiedName~StoryMetadataStoreTests"` FAIL → implement → PASS.
- [ ] **Step 5: Commit** `feat(core): add story metadata store abstraction`.

### Task 4: Editor split/merge and prose-only counting

**Files:**
- Modify: `src/Hymnal/ViewModels/EditorViewModel.cs` (implements `IOpenStoryBuffer`; split on `OpenChapterAsync`/external reload, `Text` = prose, holds `CurrentMetadata` + line ending; `SaveAsync` writes via `StoryFileWriter.WriteFileAsync(path, Text, CurrentMetadata, lineEnding)`; `ApplyMetadataAsync` sets metadata and saves — if the buffer is dirty the unsaved prose is saved too; `OpenArbitraryFileAsync`/Book.txt unchanged), `src/Hymnal/ViewModels/WorkspaceViewModel.cs` (hydration word count on `StoryFileFormat.Split(content).Prose`), `src/Hymnal.Core/Infrastructure/Ai/ManuscriptContextReader.cs` (return prose only), `src/Hymnal/App.axaml.cs` (register `StoryFileWriter`, attach the editor as buffer)
- Test: `tests/Hymnal.Core.Tests/ViewModels/EditorEmbeddedBlockTests.cs`, add cases to existing ManuscriptContextReader tests

**Interfaces:**
- Consumes: Tasks 1–2.
- [ ] **Step 1: Write failing tests:** opening a file with a block shows prose only and `IsDirty == false`; saving writes prose + the same block (bytes of the block region equal after re-split); editing prose then save keeps the block; `ApplyMetadataAsync` on a dirty buffer saves both new prose and new metadata; external change with a new block reloads both; external change while dirty keeps the existing conflict behaviour; word count excludes block text (hydration and live); AI context reader excludes block; file without block behaves exactly as before (byte-identical save).
- [ ] **Step 2–4:** run `--filter "FullyQualifiedName~EditorEmbeddedBlockTests"` and `--filter "FullyQualifiedName~Editor"` FAIL → implement → PASS.
- [ ] **Step 5: Commit** `feat(app): hide embedded metadata in the editor and count prose only`.

### Task 5: Route per-story data through IStoryMetadataStore

**Files:**
- Modify: `ChapterViewModel.cs`, `ChapterInfoViewModel.cs`, `ChapterDetailViewModel.cs`, `GanttViewModel.cs`, `NotesViewModel.cs`, `BookStatsViewModel.cs`, `WorkspaceViewModel.cs` (hydration loads phase data/targets via the store; builds `StoryRef`s; calls `SetPrimaryStory`; adopts block ids into the registry; shows the multi-file info once), `BeatBoardViewModel.cs`, `StoryIdeaPromoter.cs` (accept `IStoryMetadataStore` for status/target/notes), `App.axaml.cs`
- Test: `tests/Hymnal.Core.Tests/ViewModels/EmbeddedStorageIntegrationTests.cs`

**Interfaces:**
- Consumes: Tasks 3–4. `WorkspaceViewModel` exposes `IStoryMetadataStore MetadataStore` (rebuilt on every workspace open/reload from `ProjectSettings`); consumers read it at call time.
- [ ] **Step 1: Write failing tests** (real temp workspace, Story kind, `storage: embedded`, one file): status change from sidebar → block in file updated, no `phases.json` entry for the story; target set → block; notes typed → block; beats edited → block; all reload correctly after reopening the workspace; same flows in sidecar mode write the same sidecar files as before (regression); renaming the file outside Hymnal and reopening keeps the data (identity via block id); a second chapter file added → info shown once, second file's status goes to sidecar.
- [ ] **Step 2–4:** run the new filter plus `Corkboard`, `Gantt`, `ChapterInfo`, `Ideas`, `BeatBoard`, `MainWindow` filters → PASS.
- [ ] **Step 5: Commit** `feat: route per-story metadata through the story metadata store`.

### Task 6: Migration, Settings checkbox, docs

**Files:**
- Create: `src/Hymnal.Core/Services/StorageMigrator.cs`
- Modify: `src/Hymnal/ViewModels/SettingsViewModel.cs` + `SettingsView.axaml` (Project section: **Keep everything in the story file** checkbox, visible for Story kind, enabled only when Book.txt lists exactly one chapter file, with a one-line explanation otherwise; Apply migrates then saves `project.json` then `ReloadProjectAsync`), `AGENTS.md` (embedded storage section; `StoryFileWriter` rule change verbatim from Global Constraints)
- Test: `tests/Hymnal.Core.Tests/Services/StorageMigratorTests.cs`, `tests/Hymnal.Core.Tests/ViewModels/SettingsStorageTests.cs`

**Interfaces:**
- Produces: `StorageMigrator.MigrateAsync(string root, StoryRef primary, StorageMode from, StorageMode to) → Task<Result<Unit>>`. Sidecar → embedded: read the story's phase data, target, notes and story board from sidecar; write the block; then remove those sidecar entries (phase/target entry by uuid, notes file, `beats/story.json`). Embedded → sidecar: write sidecar entries first, then `StoryFileWriter.RemoveMetadataAsync`. Each step atomic; on failure stop, report what was migrated, and do not update `project.json`; never delete a source before its destination is written.
- [ ] **Step 1: Write failing tests:** both directions lossless (all fields); failure injected at the destination write → source intact, `project.json` unchanged, Fail; failure at source cleanup → destination written, Fail reported, data present in both (no loss); Settings checkbox hidden for Novel/Anthology, disabled with 2+ files, Apply runs migration then reload, error → `ShowError`.
- [ ] **Step 2–4:** run new filters FAIL → implement → PASS; filtered full suite → PASS; `dotnet build src/Hymnal/Hymnal.csproj --no-incremental` 0 warnings.
- [ ] **Step 5: Commit** `feat: add embedded storage migration and settings option`.
