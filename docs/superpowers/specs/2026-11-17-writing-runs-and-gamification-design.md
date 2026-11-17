# Writing Runs and Gamification — Design

- **Date:** 2026-11-17
- **Branch:** `feat/writing-runs`
- **Status:** Brainstormed and ranked with the author; phase 1 in progress on this branch

## 1. Intent

The hardest part of writing is **starting a session**. Hymnal already records the raw material for motivation (per-chapter
daily word counts, targets, statuses, Gantt dates, beat boards), but nothing turns it into a reason to sit down, a feeling
of progress, or a reward for showing up.

**Goal:** a playful, arcade-flavoured layer (it suits the Synthwave theme) built around **one-click writing runs**, that
celebrates what a session achieved, shows where the project stands, keeps streaks, and hands the next session a note from
"Past You". Points earned buy cosmetic unlocks in a web-shaped unlock tree.

It must work for any size of project, from a blog post or flash piece to a novel or a series, so every reward scales
with the project (percentages, not absolute word counts) and progress is kept in a **global writer profile**, not per
workspace.

### Ranked decisions from brainstorming

| Question | Decision |
|---|---|
| Where writing stalls | Starting a session |
| Tone | Playful arcade: XP, levels, achievements, celebration moments, unlocks |
| Ship first | One-click run |
| After a run | Celebrate, show session stats, project progress, the session's impact (churn), streaks kept or started; then a dedicated "note to Future You" screen; later, spend points in a spiderweb unlock tree |
| Where progress lives | Global writer profile (AppData), feeding one streak across every project; each project keeps its own goals |
| Revision currency | A mix: words added, words cut, focused time, and (later) checklist items, weighted by phase |
| Unlocks | Themes and palettes, titles and cosmetics, sounds and effects, and decorative editor companions ("zen garden") |

## 2. Phases

| Phase | Contents | Status |
|---|---|---|
| **1. Run loop** | One-click run (timer + focus mode + HUD), session tracking (added / cut / net), recap screen (celebration, stats, churn, project progress, streak, XP), "Future You" note screen, global session log; after the first feedback round (§3.8), the minute dial, the RUN button, the Logbook with its heatmap, the run atmosphere and the menu tidy | This branch |
| 2. Launch pad | On opening a project: the latest Future You note, where you stopped, the next unchecked beat, and a big **Start run** button; minimum viable session (50 words or 5 minutes keeps the streak) | Next |
| 3. Progression | Writer level from lifetime XP, achievements, celebration effects and sounds (reusing the typing-sound engine), heatmap calendar, streak grace (weekly goal, freeze days) | Later |
| 4. Unlock web | A spiderweb tree spending points earned from XP: palettes, titles, sound schemes, editor companions and zen-garden decorations that grow with your sessions | Later |
| 5. Project goals | Daily goal ring with pace derived from targets and Gantt deadlines; beat and scene checklists (a linked drafted scene ticks its beat); milestones at 10/25/50/75/100 % of target; revision-pass checklists; phase-weighted XP | Later |

## 3. Phase 1 — the run loop

### 3.1 Flow

1. **Start.** *Write → Start Run…* (menu), the command palette (*Start Writing Run*) or **Ctrl+Alt+S** opens the
   run card: duration presets **10 / 15 / 25 / 45** minutes (15 preselected; the last choice is remembered), the
   latest **note from Past You** if there is one, and **Start**. A run needs an open workspace.
2. **Run.** Starting switches to Write and enters focus mode. A small HUD floats at the top right: time left, a thin
   progress bar, live net words, and **End**. Leaving focus mode (Esc / F11) does not stop the run; the HUD stays.
   Switching chapters is allowed; every chapter edited is tracked.
3. **Recap.** When the timer runs out (or **End** is pressed) Hymnal leaves focus mode and shows the recap:
   - Headline: **RUN COMPLETE** (timer ran out) or **RUN ENDED** (ended early), with a one-line summary.
   - Tiles: words added, words cut, net words, minutes, words per minute, XP earned.
   - **Impact:** a bar splitting added vs cut, labelled by churn — *Fresh drafting* (under 15 % cut), *Drafting and
     shaping* (15–60 %), *Deep revision* (over 60 %) — and the chapters touched with their net change.
   - **Project progress:** words before → after; when chapters have targets, percent of the total target before →
     after, calling out a 10/25/50/75/100 % milestone crossed in this run.
   - **Streak:** *Streak started*, *Streak extended to N days*, *Streak kept alive — N days* (already wrote today) or
     *New streak — your last one was N days*; plus the best streak.
   - Buttons: **Leave a note for Future You →** (primary) and **Done**.
4. **Future You.** A dedicated screen: "Where you left off" (chapter title and the last line written), and a text box
   "What should Future You do next?". **Save note** stores it; **Skip** closes. The latest note is shown on the run
   card (and, in phase 2, the launch pad).

A run with no edits that lasted under a minute is discarded without a recap. Ending is never blocked by unsaved
prose; the run reads the editor buffer, and saving stays the author's (or auto-save's) job.

### 3.2 Counting

- **Tokens** follow `WordCountService` (whitespace-delimited, Markua `{…}` attribute lines skipped). A recognized
  embedded Hymnal block is already hidden from the editor buffer, so it never counts.
- The tracker keeps, per chapter (keyed by `ChapterNode.RelativePath`), the **baseline** text the first time the chapter
  is seen in the run and the **latest** text from user edits. Book.txt, research and supplemental documents are not
  tracked.
- **Live net** = Σ (count(latest) − count(baseline)) — cheap enough for every keystroke.
- **Added / cut** come from a word-level diff at the end: trim the common prefix and suffix, then an LCS over the
  middle. When the middle is too large (over 4 M cells), fall back to a multiset difference (counts each word's
  surplus/deficit), which never undercounts net and is close for real edits.
- **Words touched** = added + cut.
- **Attribution:** while a chapter loads, the editor sets the new text before `ActiveNode`, so the run listens to a
  new `EditorViewModel.ProseEdited` signal (raised only for user edits, carrying the node they were made in), to
  `ActiveNode` changes (baseline capture), and to `Saved` (catches a revert to the saved text).
- **Project progress** before = the workspace total at the start of the run; after = before + net. The target is the
  sum of chapter `MinWords` (else `MaxWords`), as the Book stats panel uses.

### 3.3 XP (phase 1 formula, recomputable)

Sessions store raw numbers, never XP, so the formula can change and be re-applied to history.

`xp = added + ⌈cut / 2⌉ + 5 × focused minutes`, with **+25** for a run that ran its full time. Phase 5 weights the
terms by the story's phase (drafting favours added, revising favours cut and time).

### 3.4 Streaks

A day counts when it has at least one recorded run (local date). The current streak is the run of consecutive days
ending today or yesterday. Phase 2 lowers the bar to the minimum viable session; phase 3 adds grace.

### 3.5 Storage

**Global writer profile** — `%AppData%/Hymnal/writer-profile.json` (the folder `settings.json` lives in), read and
written only through `IWriterProfileStore` (`WriterProfileStore`, atomic temp-then-rename):

```json
{
  "schemaVersion": 1,
  "sessions": [
    {
      "id": "6f1c…",
      "startedAt": "2026-11-17T20:01:00+00:00",
      "localDate": "2026-11-17",
      "plannedSeconds": 900,
      "actualSeconds": 900,
      "completed": true,
      "workspace": "ShortStories",
      "wordsAdded": 512,
      "wordsCut": 87,
      "wordsNet": 425,
      "chapters": 2
    }
  ]
}
```

A missing file is an empty profile. A malformed file or an unknown `schemaVersion` is never overwritten: the recap still
shows, the session is not recorded, and an error says why. Unknown fields are ignored on read (phases 3–4 add
`unlocks`, `pointsSpent`, …; the format only ever gains fields).

**Future You notes** — per workspace, `.hymnal-data/future-you.json`, through `IFutureYouNotesService`
(`FutureYouNotesService`, atomic writes via `IMetadataStore`):

```json
{ "schemaVersion": 1, "notes": [ { "createdAt": "…", "text": "…", "chapterPath": "…", "chapterTitle": "…", "lastLine": "…" } ] }
```

Notes are appended (history is kept for a future "letters from Past You" view); the newest is "the" note. Same
malformed-file rule: refuse to write, never overwrite.

The run duration preference uses `IAppSettingsStore` key `run.lastMinutes`.

### 3.6 Code shape

| Piece | Where |
|---|---|
| `WordDiff`, `RunTracker`, `StreakCalculator`, `RunXp`, `RunSummary`, `ChurnKind` | `src/Hymnal.Core/Services/Runs/` (pure, unit-tested; the tracker takes the clock as an argument) |
| `RunRecord`, `WriterProfile`, `FutureYouNote` | `src/Hymnal.Core/Models/Runs/` |
| `IWriterProfileStore` / `WriterProfileStore`; `IFutureYouNotesService` / `FutureYouNotesService` | `Interfaces/`, `Infrastructure/`, `Services/` |
| `RunViewModel` (state machine Idle → Choosing → Running → Recap → FutureYou → Idle; ticks from an injectable `IScheduler`) | `src/Hymnal/ViewModels/` |
| `RunOverlayView` (card / recap / Future You), `RunHudView` | `src/Hymnal/Views/` |
| Wiring: `MainWindowViewModel.Run`, focus-mode enter/leave, menu, palette, Ctrl+Alt+S | `MainWindowViewModel`, `MainWindow.axaml` |

### 3.7 Tests

- `WordDiff`: insert, delete, replace, move, prefix/suffix trimming, the multiset fallback, Markua attribute lines.
- `RunTracker`: baseline captured once, several chapters, live net, summary totals.
- `StreakCalculator`: started / extended / kept / restarted, best streak, gaps.
- `RunXp`: formula and completion bonus.
- `WriterProfileStore` / `FutureYouNotesService`: round trip, missing file, malformed file refused and untouched.
- `RunViewModel`: start enters focus mode, edits are attributed to the right chapter across a chapter switch, the
  timer completes the run, the session is recorded with the right streak outcome, the note is saved, an empty short
  run is discarded.

### 3.8 First feedback round (2026-11-17)

After trying phase 1, the author asked for the following. All of it is built on this branch.

| Feedback | Decision |
|---|---|
| "Sprint" carries agile baggage | Call it a **run**, roguelike-style ("Start a Run", "RUN COMPLETE", "Logbook"). A theme-dependent name ("Run" in arcade themes, "Session" in quieter ones) is a possible later touch. |
| Non-view items in the View menu | A full tidy of the menu bar: **File** (workspace, save, new idea) / **View** (sidebars, panels, focus mode, Markua preview, layout) / **Go** (Go To, command palette, modes) / **Insert** / **Run** (start, end, Logbook) / **Tools** (spelling, sound, Settings) / **Help**. There is no Edit menu, because Hymnal has no app-level edit commands; undo and the clipboard live in the editor. |
| A visible way to start | A **RUN** split button in the toolbar, with Start a Run and Logbook in its menu. Ctrl+Alt+R starts a run and Ctrl+Alt+L opens the Logbook. |
| Custom lengths | A **minute dial** (kitchen timer: one turn is an hour, extra turns are inner rings, 1–180 minutes) with ±5 buttons and presets of 10/15/25/45/60. Drag, scroll, arrow keys or typed digits all work; the last length is remembered. |
| Seeing past runs | The **Logbook** window: current and best streak, lifetime totals (runs, time, net words, XP, full runs), a 12-week Monday-first heatmap (levels by minutes that day), and every run, newest first, with the note left after it. This pulls the heatmap forward from phase 3. |
| "Something to make it feel epic", ramping up at the end | Four layers, each switchable in Settings → Runs: **flames and embers** in the bottom corners, an **edge glow and vignette**, **sparks** off the word count with **combo callouts** (50/100/250/500/1,000/2,000 net words), and an **ambient sound bed** (synthesised drone and crackling fire). Through the **final stretch** (the last 60 s, or the last quarter of a run under four minutes), intensity eases in from 0.15 to 1: more and taller flames, a tighter vignette, a pulsing edge, a hot HUD timer, the loop swelling, and a heartbeat rising from 60 to 132 bpm. A run that goes the distance ends with a **chime and a burst**. |

Storage additions are additive: `RunRecord.workspacePath` (so the Logbook can find notes) and `FutureYouNote.runId`.
The profile keeps its `sessions` array name, so runs recorded earlier still load.

## 4. Later phases — notes for when they're specced

- **Launch pad (2).** Shown in Write when a project opens and nothing is being edited yet, or as a dismissible card above
  the editor. It reuses the Future You note and the `WriteBeatsViewModel` beat list for "next beat".
- **Progression (3).** Level curve `level = ⌊√(xp / 100)⌋ + 1`-ish, tuned so a 15-minute daily habit levels up about weekly
  at first. Achievements are data (id, title, rule over sessions/profile) evaluated after each run, so history can
  award them retroactively. Celebrations use `IAudioEngine` and a short neon burst, both with an off switch in Settings.
- **Unlock web (4).** A radial "spiderweb" graph: the centre node is free, and each spoke is a category (Palettes,
  Titles, Sounds, Garden). Nodes cost points (earned = XP / 10), need a neighbour unlocked, and are purely cosmetic.
  Garden companions live in the editor's side margin and change with streaks (a plant that grows with each run and
  wilts politely, never dies, after a lapse).
- **Project goals (5).** A goal ring in the status strip; pace = remaining target words ÷ days to the Gantt end date;
  beats gain a `done` flag (schema-additive); milestones generalise phase 1's percentage callouts.
