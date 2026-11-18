# Inkdeep Shell Redesign — Information Architecture and Shell Architecture

- **Date:** 2026-11-18
- **Branch:** `feat/roguelike-meta-design`
- **Status:** First draft for discussion. Nothing is decided until the author has answered §8.
- **Builds on:** `2026-11-17-writing-runs-and-gamification-design.md` (runs, built) and
  `2026-11-17-roguelike-meta-progression-design.md` (character sheet, loadout, Dungeon map, classes, economy, the
  rename to Inkdeep).

## 1. Why

The meta-progression spec adds a character sheet, a loadout, a Dungeon map, a skill bar, a shop, AI summaries and a
launch pad. Each would be one more mode tab, menu entry, overlay or `Is*` flag in a shell that is already strained.
Line counts on `feat/roguelike-meta-design` as of 2026-11-18:

- **The shell grows by special cases.** `MainWindowViewModel` is 1,233 lines (plus 249 in `.Palette.cs`), with 23
  public `Is*` flags, 66 `WhenAnyValue`/`CombineLatest` calls and 36 `ShellMode.` references. `MainWindow.axaml` is
  1,121 lines with 43 `IsVisible=` bindings. The `ActiveMode` setter pushes state into four child view models and
  raises eight properties by hand.
- **Modes aren't alike.** Only Write uses the shell's rails. Research rebuilds the Docs + editor + AI layout in its
  own grid, with headers and icons copied from the shell. Ideas, Plan and Manage each lay out their own centre. Edit
  is a disabled tab.
- **Commands are scattered.** The same command is wired separately into the menu bar, the toolbar split button, the
  palette and the key bindings. For example, F5 "Toggle Manage View" sits in View while the modes sit in Go.
- **Several flags are dead.** `IsCorkboardVisible`, `IsGanttVisible`, `IsResearchVisible`, `IsIdeasVisible`,
  `IsBothRightPanesOpen` and a few more are bound in no view.
- **The right rail is string-switched.** "info", "beats", "notes" and "chat" are switched in two places, with three
  conditional splitter flags.
- **The status strip shows no state.** It holds the theme's emblem, the title and the motto. That's where the
  character, streak, gold and goal want to live.

## 2. Principles

1. **The shell is a game loop.** You leave **camp**, pick a loadout, go into the **dungeon** (the manuscript), work in
   a room, come back with loot and leave a note. The information architecture should follow that loop, not the menu
   bar.
2. **Places, not flags.** Every top-level destination is a registered **place**, every side panel a registered
   **panel**, and every action a registered **command**. The shell renders whatever is registered and available for
   the project's profile. Nothing is hand-wired per mode.
3. **One layout system.** Every place gets the same left and right rails and chooses its default panels. Research
   stops being a copy of Write.
4. **The writing surface stays calm.** Game chrome lives at camp, on the map and in a slim **adventure bar**. The
   editor itself gains only the run HUD and the skill bar, and only during a run.
5. **A quiet vocabulary for professionals.** Every game term has a plain twin (Camp → Home, Delve → Session, Renovate
   → Revise). One setting switches between them, and an *Adventure off* switch hides the game layer altogether. The
   tool still works as a straight editor.

## 3. Proposed places

Seven places replace six modes. In order on the place rail:

| Place | Plain name | Replaces | Centre | Notes |
|---|---|---|---|---|
| **Camp** | Home | (new) plus the launch pad | Today's card: the Future You note, the last room, goals on offer and **Enter the dungeon**. Below it, the character sheet summary, the streak torch, recent runs and the projects list | Opens first when a project opens. Holds the character sheet, the Logbook and the shop as tabs: **Camp · Character · Logbook · Shop** |
| **Map** | Map | (new) Dungeon mode | The dungeon as tiles: floors, scene rooms and paragraph features | Clicking a room opens it in Write. The run recap shows the run's path here |
| **Plan** | Plan | Plan plus **Ideas** | Sub-tabs: Ideas, Details, Beats / Planner / Cards | Ideas becomes a Plan tab (only for project kinds that have ideas today) |
| **Write** | Write | Write plus **Research** | The editor | Research docs are a panel and a document kind, opened in a split or a second editor tab. No separate Research grid |
| **Edit** | Revise | the reserved Edit tab | The editor in **renovation** layout: issue sweep (spelling, echoes, trim), AI feedback panel, version compare | Home of the editing classes' tools outside runs. Masons and Seers start here |
| **Schedule** | Schedule | Manage | Gantt / timeline | Renamed for clarity. F5 stops being a "view" toggle |
| **Settings** | Settings | the settings overlay | Settings sections | Stays as the gear, plus a palette entry. Not on the rail |

A run can start from any place. Starting a run moves to Write (drafting classes) or Edit (editing classes), as the
class decides, and enters focus mode.

### Adventure bar (replaces the status strip)

A slim strip along the bottom, hidden in focus mode except for a minimal version during runs:

- **left:** portrait, level and XP bar, class split (for example *Delver 4 / Mason 2*)
- **centre:** the current goal, or the next goal on offer, with its progress
- **right:** the streak torch (days), gold, and today's words
- With *Adventure off*, it shows plain writing stats: words today, chapter words against target, and the save and
  git state.

### Top bar

- **Left:** project name and the collection breadcrumb (taken from CollectionBar, so it's shown everywhere, not only
  in Plan and Manage).
- **Centre:** the place rail.
- **Right:** **ENTER** / **NEW RUN** (the primary action), the git sync pill, the palette button and the gear.
- **Menu bar:** kept, but generated from the command registry (below). It becomes File / Edit / View / Go / Insert /
  Run / Tools / Help, with an Edit menu once Edit is a place. Places are listed only in **Go**.

### During a run

- Focus mode stays.
- The HUD gains the **skill bar** (the class's skills, hotkeys 1–4 with Ctrl+Alt, charges and mana).
- The editor takes the class's **style**.
- The rails can be summoned (as today), but they open with the class's default panels: Survey for the Cartographer,
  the issue sweep for the Mason, the AI panel for the Seer.

## 4. Shell architecture

Three registries replace the flags. They follow the pattern that `MarkuaSnippets` already proves out: one catalog,
read by the menu, the palette and the key handling.

### Places

```csharp
public interface IShellPlace
{
    string Id { get; }                       // "camp", "map", "plan", "write", "edit", "schedule"
    string Title(Vocabulary v);              // "Camp" / "Home"
    string Icon { get; }
    bool IsAvailable(ProjectProfile p);
    object CreateContent();                  // the centre view model (resolved through ViewLocator)
    PlaceLayout DefaultLayout { get; }       // which panels each rail opens with
}
```

- `ShellViewModel` (the slimmed-down `MainWindowViewModel`) holds `Places`, `ActivePlace` and `Content`. Mode
  changes become `Navigate(placeId)`, with no per-mode properties.
- Each place's centre view model implements `IPlaceLifecycle` (`OnEnter`, `OnLeave`), replacing what the
  `ActiveMode` setter pushes by hand today.

### Panels

```csharp
public interface IShellPanel
{
    string Id { get; }                       // "chapters", "docs", "info", "beats", "notes", "ai", "sweep", "survey"
    string Title(Vocabulary v);
    string Icon { get; }
    KeyGesture? Shortcut { get; }
    RailSide DefaultSide { get; }
    bool IsAvailable(ProjectProfile p, string placeId);
    object CreateContent();
}
```

- The rails become one generic `RailViewModel`: an ordered stack of open panels, collapse to an icon strip, sizes,
  and splitters generated between open sections. That replaces `IsInfoBeatsSplitterVisible` and the like, and the
  string switches in `OpenRightPane` / `ToggleRightPaneCommand`.
- Layout per place is stored in app settings (`layout.{placeId}`), extending today's `leftRailCollapsed` /
  `rightRailCollapsed`.
- `MainWindow.Rails.cs` (animation, clamping, snap) carries over into `RailView` almost unchanged.

### Commands

```csharp
public sealed record ShellCommand(
    string Id, string Title, string Menu, string? Group,
    KeyGesture? Gesture, ICommand Command, Func<ShellContext, bool> IsAvailable,
    string? GameTitle = null);
```

- One `CommandCatalog` feeds the menu bar, the palette, the key bindings, the toolbar's primary action and Help's
  shortcut list. Adding a command means one catalog entry.
- Skills (§5 of the meta spec) are commands too, tagged with a class and a rank, so the skill bar, the palette and
  the keys stay consistent.

### State the shell shows

- An `IAdventureState` service (Core) exposes level, XP, class split, streak, gold and the current goal as one
  observable snapshot. It is computed from the writer profile and the active run. The adventure bar, camp and the
  HUD all bind to it, rather than each asking `RunViewModel` and the stores.
- **Vocabulary:** `IVocabulary` (Game / Plain), from app settings. Places, panels, commands and run screens take
  their titles through it. It sits beside `ProjectProfile`'s nouns (Chapter / Scene / Story), which keep working as
  they do.

## 5. What happens to today's pieces

| Today | Becomes |
|---|---|
| Research mode, `ResearchView` grid | A **Docs** panel plus research documents opened in Write (split or tab). The duplicated grid and headers go |
| Ideas mode | Plan → **Ideas** tab. *New Idea* stays in File and the palette |
| Manage mode, F5 | **Schedule** place. F5 = go to Schedule (a Go command) |
| Edit (disabled) | **Edit / Revise** place: issue sweep, AI feedback, version compare |
| Run split button | Primary **ENTER / NEW RUN** button; Logbook moves to Camp → Logbook (Ctrl+Alt+L kept) |
| Logbook dialog | Camp → **Logbook** tab (the dialog goes) |
| Settings overlay | Kept as an overlay (gear, Ctrl+,); sections gain *Adventure* (vocabulary, adventure off, atmosphere) |
| Status strip (emblem, title, motto) | **Adventure bar**. The emblem and motto move to Camp's header |
| CollectionBar (Plan, Manage only) | Breadcrumb in the top bar, every place |
| `Is*Visible` flags, dead flags | Gone; places, panels and availability replace them |

## 6. Migration

Each step ships on its own and keeps the app working:

1. **Registries behind today's look.**
   - Introduce `CommandCatalog` and generate the menu bar and palette from it.
   - Introduce `IShellPanel` and the generic rail for Write's existing four right-hand panels and two left-hand
     sections.
   - Delete the dead flags.
   - No visible change; covered by the existing headless view tests plus catalog tests.
2. **Places.**
   - `IShellPlace` for the existing modes; `ActiveMode` becomes `Navigate`.
   - Research folds into Write (the Docs panel) and Ideas into Plan.
   - Manage is renamed Schedule.
3. **Camp.** The launch pad (phase 2 of the meta spec), the character sheet tab and the Logbook tab. Starts on
   project open.
4. **Adventure bar** and `IAdventureState`, the vocabulary switch and *Adventure off*.
5. **Edit / Revise place** with the issue sweep (Spellward, Echo sight), alongside phase 3's skills.
6. **Map place**, with phase 4 of the meta spec.
7. **Rename to Inkdeep** (its own spec), after the trademark search.

Steps 1–2 are pure refactoring and can start at once. Steps 3–6 interleave with the meta spec's phases.

## 7. Tests

- Catalog tests:
  - every command has a unique id
  - no two commands share a gesture where both are available
  - every menu has at least one available entry for every project kind
- Place and panel availability per `ProjectProfile` (Novel / Story / Anthology), replacing today's scattered
  checks.
- `RailViewModel`: open, close, collapse, order, splitter generation, clamping, and layout persisted per place.
- Headless view tests: each place renders for each project kind, the rails render the registered panels, and focus
  mode hides the chrome.
- Vocabulary: every place, panel and command has both a Game and a Plain title.

## 8. Questions for the author

1. Is **Camp** the place a project opens on, or does a project open where you left it (Write) with Camp one click
   away?
2. Should **Research fold into Write** (a Docs panel and split) and **Ideas fold into Plan**, or stay as places of
   their own?
3. Is the **Edit / Revise place** wanted outside runs, or should editing tools live only in Write's panels and the
   skill bar?
4. **Vocabulary:** is the plain twin plus *Adventure off* worth the cost, or is Inkdeep proudly game-first?
5. Is the **menu bar** kept (generated), or replaced by the palette plus the top bar, game-style?
