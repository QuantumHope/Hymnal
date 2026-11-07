# Short Story Mode — Design

- **Date:** 2026-11-07
- **Branch:** `short-stories`
- **Status:** Approved in brainstorming; awaiting written-spec review

## 1. Intent

Hymnal today treats every workspace as a novel: a 7-stage chapter lifecycle, per-phase Gantt scheduling, per-chapter word targets, a file-based Corkboard, and novel-oriented AI roles. Short stories and anthologies need less planning and management, use different plot structures and word targets, and are published differently.

**Goal:** add a per-workspace *project kind* — Novel, Short Story, Anthology — that tailors statuses, targets, planning and labels, without changing anything for existing novel workspaces.

**Success criteria**

1. Opening a folder with no `Book.txt` offers to initialize it and lets the author pick a project kind.
2. Existing workspaces open exactly as before (Novel), with no new prompts.
3. Short Story / Anthology projects show a 4-stage status list, story-length target presets, and a beat-template Corkboard.
4. A short story can optionally keep *all* of its Hymnal data inside its single `.md` file, invisible in Leanpub output.
5. Switching kind or storage is lossless.

**Out of scope (later specs)**

- **Publishing** (spec #2): export, blog-style publish targets (Ghost, Substack, Medium, Hugo), light tracking of commercial placements.
- Custom user-defined beat templates.
- Feeding beats into AI context builders.
- Embedded storage for Anthology projects.

## 2. Project kind and initialization

### 2.1 `project.json`

Stored at `.hymnal-data/project.json`, read/written by a new `ProjectSettingsService` (Core) via `IMetadataStore.WriteTextAtomicAsync`.

```json
{ "schemaVersion": 1, "kind": "novel", "storage": "sidecar" }
```

| Field | Values | Default |
|---|---|---|
| `kind` | `novel` \| `story` \| `anthology` | `novel` |
| `storage` | `sidecar` \| `embedded` | `sidecar` |

- `storage: embedded` is honored only when `kind == story`; for other kinds it is treated as `sidecar` (and the Settings UI does not offer it).
- Missing file → `novel` / `sidecar`. No prompt.
- Unreadable file or unknown `kind` → `novel` / `sidecar` with a warning notification; the file is not overwritten until the author saves project settings.
- Service methods return `Result<T>`.

### 2.2 Initialization flow

`ManuscriptService.LoadWorkspaceAsync` currently fails when no `Book.txt` exists (at root or `manuscript/`). New behavior, orchestrated by the workspace-open path in the ViewModel layer:

1. **Confirm** — "This folder isn't a Hymnal project yet. Initialize it?" — **Initialize** / **Cancel**. Cancel leaves the folder untouched and keeps the previous state.
2. **Choose type** — `ProjectTypeDialog` with three selectable cards:

| Icon | Kind | Description | Feature impact sentence |
|---|---|---|---|
| Closed book | **Novel** | One long work in chapters | Full toolset: 7-stage chapter lifecycle, phase scheduling, chapter targets and book-wide stats. |
| Single page | **Short Story** | One standalone story | Simpler 4-stage status, story-length targets, and a Corkboard of beats from a plot template. |
| Stacked pages | **Anthology** | A collection of stories | Short story tools for each story, plus collection order, Parts for long stories and total word count. |

   The Short Story card includes a checkbox: **Keep everything in the story file** (sets `storage: embedded`).

3. **Create** — a new Core service method (`ProjectInitializer.InitializeAsync(folder, kind, storage)`):
   - Writes `Book.txt` at the folder root listing existing top-level `.md` files in ordinal-ignore-case alphabetical order.
   - If there are no `.md` files, creates one starter file — `chapter-01.md` (Novel), `story.md` (Short Story), `story-01.md` (Anthology) — and lists it.
   - Writes `project.json`.
   - Then the workspace opens normally.

### 2.3 Icons

`src/Hymnal/Themes/Icons.axaml` is currently an empty placeholder. Add three `StreamGeometry` resources (`Icon.ProjectNovel`, `Icon.ProjectStory`, `Icon.ProjectAnthology`) rendered with `PathIcon`.

### 2.4 `ProjectProfile`

An immutable Core object built from `kind` (+ `storage`) by a static `ProjectProfile.For(kind, storage)`. View models read it from a singleton `IProjectContext` that exposes `IObservable<ProjectProfile>` and `Current`, updated when a workspace loads or project settings change.

| Member | Purpose |
|---|---|
| `Kind`, `Storage` | Echo of settings |
| `UnitNoun`, `CollectionNoun`, `InfoPanelTitle` | Labels (§5.1) |
| `VisibleStatuses` | Ordered `ChapterStatus` values with display labels |
| `GanttPhases` | Ordered phase names used by Gantt |
| `NextStatus(ChapterStatus)` | Replaces hardcoded sequence in `PhaseCompletion` |
| `DisplayStatus(ChapterStatus)` | Maps a stored status to the nearest visible one |
| `DefaultStatus` | Status for newly created units |
| `TargetPresets` | §3.2 |
| `UsesBeatBoard`, `ShowsCollectionStats` | Feature switches |

## 3. Statuses and targets

### 3.1 Statuses

Short Story and Anthology show four stages that relabel existing `ChapterStatus` values. Stored data is unchanged.

| Display | Stored | Gantt phase |
|---|---|---|
| Idea | `Planned` | no |
| Drafting | `Drafting` | yes |
| Revising | `Editing` | yes |
| Done | `Done` | no |

- `NextStatus`: Idea → Drafting → Revising → Done.
- `DisplayStatus` for non-visible stored values: `Outlining` → Idea; `Polishing`, `Reviewing` → Revising. Stored values are **not** rewritten until the author changes the status, so switching back to Novel restores them.
- `DefaultStatus`: `Planned` (Idea) for story/anthology; Novel keeps `Outlining` (current `PhaseDataService.DefaultPhaseData`).
- Novel profile reproduces today's behavior exactly: 7 statuses, `GanttPhases = [Outlining, Drafting, Editing, Polishing, Reviewing]`, current `PhaseCompletion` sequence.
- Refactors: `GanttProjection` (hardcoded phase list at `GanttProjection.cs:17`) and `PhaseCompletion` take phases/sequence from the profile. `CardViewModel.StatusDisplay` and status pickers use profile labels. Status brush keys stay keyed by stored value.

### 3.2 Target presets

For story/anthology, the target editor gains a preset picker that fills the existing `MinWords` / `MaxWords`:

| Preset key | Label | Min | Max |
|---|---|---|---|
| `flash` | Flash | 0 | 1,000 |
| `shortStory` | Short Story | 1,000 | 7,500 |
| `novelette` | Novelette | 7,500 | 17,500 |
| `novella` | Novella | 17,500 | 40,000 |
| `custom` | Custom | author | author |

- `WordCountTarget` gains optional `string? Preset`. `targets.json` stays at `schemaVersion: 1` (field is additive; older builds ignore it).
- Display example: "Short Story · 3,240 / 1,000–7,500".

### 3.3 Story rollups (Anthology)

- A story is either one file or a `{class: part}` file followed by scene files.
- For Part-based stories, the target belongs to the Part's registry UUID; story word count = sum of its scene files. Scenes may still carry their own targets.
- Collection Stats (renamed Book Stats) shows collection word count, story counts by display status, and the optional collection target (existing `TotalTarget`).
- Short Story projects hide the book-level stats panel.

## 4. Beat templates and the story Corkboard

### 4.1 Beats

A beat is a planning card, not a file:

```json
{ "id": "…", "title": "Hook", "note": "…", "scenePath": null }
```

- Never touches `Book.txt`.
- Sidecar storage: `.hymnal-data/beats/<story-uuid>.json` (`{ schemaVersion: 1, beats: [...] }`), keyed by chapter-registry UUID so it survives renames.
- Accessed through `IBeatBoardService` (Core), which delegates persistence to `IStoryMetadataStore` (§4.4).
- A beat linked to a scene (`scenePath`) shows that scene's word count and display status.

### 4.2 Plan mode by kind

- **Short Story:** Corkboard shows the story's beat board (ordered beat cards). If the story has multiple scene files, a collapsible "Files" strip shows today's file cards so scene reordering still works.
- **Anthology:** top level is today's file Corkboard (stories as cards, Parts for long stories). Opening a story card (double-click or "Beats" button) drills into its beat board, with a breadcrumb back.
- **Novel:** unchanged.

### 4.3 Templates

Built-in, shipped as embedded JSON resources (same mechanism as AI prompts). Each: `key`, `name`, one-line `description`, ordered beats with `title` and prompt `note`.

| Template | Beats |
|---|---|
| Three-Act (short) | Hook, Inciting Incident, Rising Complications, Climax, Resolution |
| Try/Fail Cycles | Goal, Try 1 (fail), Try 2 (fail, worse), Try 3 (success or final failure), Outcome |
| Seven-Point | Hook, Plot Turn 1, Pinch 1, Midpoint, Pinch 2, Plot Turn 2, Resolution |
| Kishōtenketsu | Introduction, Development, Twist, Reconciliation |
| Freytag's Pyramid | Exposition, Rising Action, Climax, Falling Action, Dénouement |
| MICE Quotient | Open/Close pair for a chosen thread: Milieu, Inquiry, Character, or Event |
| Blank | (none) |

- Empty board → template picker shown inline.
- Non-empty board → **Apply template…** with **Append** or **Replace** (Replace requires confirmation).
- After applying, beats are free-form: add, delete, rename, edit note, drag-reorder, link/unlink scene.

### 4.4 `IStoryMetadataStore`

Single abstraction for per-story status, target, schedule, notes and beats.

- `SidecarMetadataStore` — delegates to existing `PhaseDataService`, `TargetsService`, `INotesService`, and the beats sidecar.
- `EmbeddedMetadataStore` — reads/writes the in-file block (§4.5).

Selected by `ProjectProfile.Storage`. View models depend only on the interface.

### 4.5 Embedded storage ("everything in one file")

Markua removes HTML comments from output (`<!-- … -->`, Markua spec "No HTML blocks"). Hymnal keeps one marked comment block at the **end** of the story file (the start is reserved for Markua settings blocks):

```markdown
…last paragraph.

<!-- hymnal:v1
{ "id": "…", "status": "drafting",
  "target": { "preset": "shortStory", "minWords": 1000, "maxWords": 7500 },
  "schedule": { … }, "notes": "…",
  "beats": [ { "id": "…", "title": "Hook", "note": "…", "scenePath": null } ] }
-->
```

- **Parsing:** `StoryFileFormat.Split(text) → (prose, metadataJson?)` and `Merge(prose, metadata) → text`. Only the *last* block whose opening line is exactly `<!-- hymnal:v1` is recognized. Line endings of the original file are preserved; exactly one blank line separates prose and block.
- **Editor:** splits on load and merges on save, so the author sees only prose. Word counts are computed on prose only.
- **Errors:** missing block → defaults. Malformed JSON → defaults plus warning; the malformed block is left in place as prose (never deleted) and a fresh block is appended after it on next write.
- **Identity:** `id` replaces the registry UUID for embedded stories.
- **Writes:** all writes to a story `.md` go through one `StoryFileWriter` (also used by editor save):
  - File open in editor → update the editor's held metadata and save via the normal editor path (unsaved prose preserved).
  - File not open → read, split, replace metadata, merge, atomic write; prose bytes unchanged.
  - The file watcher is suppressed for Hymnal's own writes (existing suppression pattern).
- **AGENTS.md rule change (approved):** "Don't modify chapter `.md` files outside of the editor save path" becomes "…outside of `StoryFileWriter`, which the editor save path uses."

### 4.6 Migration

Changing `storage` in Settings migrates in either direction (sidecar → write blocks and remove sidecar entries; embedded → write sidecar entries and strip blocks), then reloads the workspace. Migration is per-file atomic; on failure, already-migrated files are reported and `project.json` is not updated.

## 5. Labels, AI, Settings

### 5.1 Labels

| Place | Novel | Short Story | Anthology |
|---|---|---|---|
| Sidebar header / "New …" button and dialog | Chapter | Scene | Story (Part: "Story with scenes") |
| Stats panel | Book Stats | hidden | Collection Stats |
| Info panel | Chapter Info | Story Info | Story Info |

Shell mode names (Research / Plan / Write / Manage) are unchanged.

### 5.2 AI roles

- Available roles per mode are unchanged.
- `RolePromptProvider.GetSystemPrompt(role, profile)` appends a kind addendum from embedded resources `short-story.md` / `anthology.md`. Novel output is identical to today.
- Short story addendum: economy, single arc, strong opening line, ending payoff, awareness of the story's length category.
- Anthology addendum: the above plus variety of voice and theme across stories and collection ordering.

### 5.3 Settings

New **Project** section: the same three kind cards and (for Short Story) the "Keep everything in the story file" checkbox. Changing either shows a one-line impact summary (e.g. "Status labels and Corkboard will switch to short story tools. No data is lost.") and, on confirm, saves `project.json`, runs storage migration if needed, and reloads the workspace.

### 5.4 Room for publishing

- `project.json` is versioned; a future `publishing` section is additive.
- The embedded block is versioned (`hymnal:v1`) and can later hold per-story publish records.

## 6. Components summary

**Core (new):** `ProjectKind`, `StorageMode`, `ProjectSettings`, `ProjectSettingsService`, `ProjectInitializer`, `ProjectProfile`, `IProjectContext`, `TargetPreset`, `Beat`, `BeatTemplate`, `BeatTemplateLoader`, `IBeatBoardService` / `BeatBoardService`, `IStoryMetadataStore`, `SidecarMetadataStore`, `EmbeddedMetadataStore`, `StoryFileFormat`, `StoryFileWriter`.

**Core (changed):** `WordCountTarget` (+`Preset`), `GanttProjection`, `PhaseCompletion`, `PhaseDataService` (default status from profile), `RolePromptProvider`, `WordCountService` (prose-only counting for embedded files).

**App (new):** `ProjectTypeDialog` + VM, beat board view/VM, Project settings section, three icons.

**App (changed):** workspace-open flow, `CorkboardViewModel` (board mode + drill-in), `EditorViewModel` (split/merge, `StoryFileWriter`), `ChapterInfoViewModel` (preset picker, labels), `BookStatsViewModel` (collection mode / hidden), sidebar and New dialog labels, `SettingsViewModel`, DI registration in `App.axaml.cs`.

**Docs:** AGENTS.md — project kinds, profile, embedded storage, `StoryFileWriter` rule.

## 7. Testing

All in `tests/Hymnal.Core.Tests`; file-system tests use temp dirs with `try/finally` cleanup.

- `ProjectSettingsService`: round-trip, missing → defaults, malformed/unknown kind → defaults + warning, no overwrite on read failure.
- `ProjectInitializer`: `Book.txt` from existing `.md` files (ordering), starter file per kind when empty, `project.json` written.
- `ProjectProfile`: statuses, labels, Gantt phases, `NextStatus`, `DisplayStatus` mapping, presets, per kind.
- `GanttProjection` / `PhaseCompletion`: existing tests unchanged under Novel; new cases under Short Story.
- Targets: preset → min/max; `Preset` JSON round-trip; old JSON without `preset` loads.
- Rollup: Part story word count sums scenes.
- `BeatBoardService`: round-trip, missing → empty, append vs replace, reorder, link/unlink.
- `BeatTemplateLoader`: every template parses; all except Blank have ≥1 beat.
- `StoryFileFormat`: split/merge round-trip with prose bytes unchanged (LF and CRLF), block at end, only last `hymnal:v1` block recognized, malformed block preserved, word count excludes block.
- `StoryFileWriter`: write while open in editor vs closed; watcher suppression.
- Storage migration both directions; partial-failure leaves `project.json` unchanged.
- `RolePromptProvider`: Novel prompts byte-identical to current; addenda present for story/anthology.
- ViewModels: project-type dialog selection/Create; Corkboard board selection per kind and anthology drill-in/back; Settings kind/storage change triggers migration + reload; Project settings smoke test.

Existing baseline must stay green: `dotnet test Hymnal.sln`.

## 8. Story ideas (addendum, approved 2026-11-07)

A place to capture ideas for stories that are not yet in the manuscript. Short Story and Anthology only; Novel is unchanged.

### 8.1 Model

```json
{ "id": "…", "title": "The Lighthouse", "premise": "A keeper's widow hears ships that sank decades ago.",
  "notes": "…markdown…", "tags": ["horror"], "targetPreset": "flash",
  "state": "new", "createdAt": "2026-11-07", "storyId": null }
```

- `state`: `new` | `developing` | `used` | `shelved`.
- `targetPreset`: optional; one of the §3.2 preset keys (no custom range).
- `storyId`: registry UUID (or embedded `id`, §4.5) of the story it was promoted to; set only when `state` is `used`.
- `tags`: trimmed, case-insensitive unique, stored as entered.

### 8.2 Storage

- One project-level file: `.hymnal-data/ideas.json` = `{ schemaVersion: 1, ideas: [...] }`, written via `IMetadataStore.WriteTextAtomicAsync`, accessed only through `IStoryIdeasService` (Core).
- Ideas are project-level, not per-story, so they stay in this file under embedded storage too (§4.5 "everything in one file" covers a story's own data, not the idea backlog). Storage migration (§4.6) does not touch it.
- Missing file → empty list. Unreadable/malformed or unsupported `schemaVersion` → `Result.Fail`; the UI shows a warning, the view is read-only for the session, and the file is never overwritten.
- Switching the project to Novel keeps the file; the view is hidden.

### 8.3 UI

- **Ideas** view, alongside Corkboard and Gantt, shown only when `ProjectProfile.ShowsIdeas` (story/anthology).
- Card grid: title, premise (truncated), tags, target label. Filter by tag and state; `used` and `shelved` hidden by default (toggle to show). Newest first.
- Selecting a card opens a side editor for every field; edits save with the same debounced auto-save pattern as notes. Delete requires confirmation.
- **+ New idea** button and a global quick-capture shortcut open a small dialog (title required, premise optional) that creates a `new` idea without leaving the current view. The plan picks a shortcut that does not collide with existing bindings.

### 8.4 Promote to story (Anthology only)

1. Prompt for a file name, prefilled with a slug of the title (`the-lighthouse.md`), placed beside existing story files (root or `manuscript/`, matching `Book.txt` location). Name collision → error, nothing written.
2. Create the file containing `# <Title>` and a blank line, and append it to `Book.txt` via `IBookTxtStructureService`.
3. Set the new story's status to Idea (`Planned`), its target to the idea's preset (if any), and its notes to the premise followed by the idea's notes — through the normal per-story stores (sidecar or embedded, whichever is active).
4. Mark the idea `used` with `storyId`, save, and open the story in the editor.

If any step after file creation fails, report it via `ShowError`; the idea stays unchanged (not `used`) so the author can retry or fix manually. Short Story projects have one story, so there is no Promote action; ideas act as a "what's next" list.

### 8.5 Components and tests

- **Core (new):** `StoryIdea`, `IdeaState`, `IStoryIdeasService` / `StoryIdeasService`, `StoryIdeaPromoter`; `ProjectProfile.ShowsIdeas`.
- **App (new):** Ideas view + VM, idea editor, quick-capture dialog, DI registration.
- **Tests:** `StoryIdeasService` round-trip, missing → empty, malformed → Fail with no overwrite, tag normalization, ordering; `StoryIdeaPromoter` slug, collision, Book.txt append, status/target/notes applied, idea marked used, failure after file creation leaves the idea unchanged; `ShowsIdeas` per kind; Ideas VM filtering (state/tag defaults) and promote hidden for Short Story.
