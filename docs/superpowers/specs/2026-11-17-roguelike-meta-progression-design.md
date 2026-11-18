# Roguelike Meta-Progression — Design

- **Date:** 2026-11-17
- **Branch:** `feat/roguelike-meta-design` (off `feat/writing-runs`)
- **Status:** Direction agreed with the author. Not yet broken into tasks. The name is undecided (§10).
- **Builds on:** `2026-11-17-writing-runs-and-gamification-design.md`, which covers the run loop (phase 1, built) and the
  north star: *the story is the dungeon*.
- **Research:** HopeLab `docs/research/2026-11-17-roguelike-writing-tool.html`, a deep-research report on gamified
  writing tools, roguelite meta-progression and the LitRPG audience. Findings are cited below as R-F1…R-F7.

## 1. Intent

Phase 1 made a run worth starting. This design makes the *choices before a run* and the *progress between runs*
feel like a roguelike: a character sheet for the author, a loadout picked before each run, a dungeon drawn from the
manuscript itself, skills that are real editor tools, and currencies to spend on them. It is aimed at people who
know dungeon crawlers, LitRPGs and RPGs, and it serves every project kind: short story, anthology, novella, novel and
series.

The research shapes three guardrails:

1. **Structure first, loot second (R-F3).** The goal you pick and the progress you see are the core. Currencies and
   cosmetics come on top.
2. **Unlocks add options, they don't inflate power (R-F5, R-F6).** Progression unlocks classes, skills and goal
   types, which also teaches the editor's features one at a time.
3. **No social comparison and no permadeath (R-F2, R-F4).** Nothing is ever lost.

## 2. Decisions

| Topic | Decision |
|---|---|
| Author identity | The author has a **character sheet** of their own: level, a **background**, the **perks** taken, **class levels** (multiclassing allowed), achievements, titles, relics and inventory. It lives in the global writer profile. **No attributes** (Might, Stamina and the like): the background and perks change how the editor behaves, and class levels unlock and improve editor features. |
| Before a run | Pick a **class**, which also sets the editor's style (colours, atmosphere, sound), then a **goal** from an offered set (usually three). Rerolling the offer costs currency. |
| Dungeon look | The **story** decides the tile set, not the character: the project's genre and setting, overridable per chapter. AI-generated tiles from scenes get a feature design of their own. |
| The dungeon | **Scenes are rooms**, and paragraphs are the features inside them. A floor is a chapter, the dungeon is the book, and a series is a world. For now the map is a new **mode on the rail**. |
| Room types | Each paragraph is given a **family** (dialogue, action, description…), and the family picks the feature drawn for it. A room's type comes from its mix of features. Families come from a classifier: instant heuristics first, then a cheap model, run when the paragraph is changed or after the caret leaves it. |
| AI | Cheap models (local Ollama, or a small hosted model, through the existing `ChatClientFactory`) **summarise the run** on the recap and, when cast as a spell, **give feedback on a paragraph**. AI never rewrites prose by itself. |
| Classes | Some classes are built for **drafting** and others for **editing**, and each has its own **skills**. AI analysis is a spell. Repeated-word search, synonyms and spell check are skills. |
| Currencies | Runs earn **XP** (levels, never spent) and **gold** (spent), and goals pay bonus gold. Spend it on cosmetics, real editor improvements (new skills, skill slots, classes, goal types) and rerolls. |
| Failure | **No permadeath.** A run that misses its goal is a *missed goal*: the words stay, and so do XP and the base gold. Only the goal's bonus is lost. The Logbook says "Goal missed" without judgement. |
| Name | **Hymnal will be renamed.** The preferred name is **Inkdeep**, with Wordcrawl as the backup (§10). |

## 3. The author's character sheet

The author is the player character, built the way a tabletop character is: a **background** chosen once, **perks**
picked along the way, and **class levels** taken as they level up. There are **no attributes**. Nothing on the sheet
is a number that makes goals easier. Everything on it is an editor behaviour, an editor feature or a record of what
you've done.

The sheet lives in `writer-profile.json`, the global profile from phase 1. The parts that are **earned** (level, XP,
achievements) are derived from raw run records, so a formula change is re-applied to all history. The parts that
are **chosen** (background, perks, class levels) are stored as choices. They can be **respecced**: free at first,
and later for gold.

### Level

Level comes from lifetime XP. Use the curve from the runs spec, phase 3 (about weekly level-ups at first for a
15-minute daily habit). Levelling never ends.

**Rule: every level-up grants something concrete.**

- **Every level** grants one **class level**, to put into any class you've unlocked (§5). Every class level unlocks
  or ranks up a real editor feature, so no level is empty.
- **Every 3 levels** (3, 6, 9…) also grants a **perk pick**.
- When every unlocked class is at its cap, a level-up instead grants a **prestige** option (below), or a new class
  to unlock, chosen from those you don't have yet.

### Class caps and prestige

Each class is capped at **level 5**, its capstone. Character level is endless. There are always more classes to
take, and a capped class can be **prestiged**:

- **Prestiging a class** resets it to level 1 and adds a **prestige star** to it (★, ★★…). Its skills relock and
  come back as it re-levels.
- What each star grants:
  - a **prestige perk pick**, from a pool open only to prestiged classes: stronger editor behaviours, such as
    *Echo sight* covering the whole book or a third skill-bar slot
  - a prestige title ("Delver ★★")
  - a cosmetic for the class: a portrait frame, or a variant of its editor style
- Prestige is optional and never forced. A writer who would rather keep a capped class goes on taking levels in
  other classes, or banks them as gold.

### Background

Chosen when the sheet is created; it says what kind of writer you are. It gives one starting perk and the default
loadout. It also sets the tone of the launch pad and the run summaries.

| Background | Flavour | Starting perk (an editor behaviour) |
|---|---|---|
| **Wanderer** (discovery writer) | Writes to find out what happens | *Open road*: the run card offers a prompt card by default, and the outline pane starts collapsed |
| **Architect** (plotter) | Outlines first | *Blueprint*: the chapter's beats are pinned above the editor in Write |
| **Gardener** (reviser) | Grows a draft, then prunes it | *Pruning shears*: words you cut stay in a per-chapter "clippings" bin for 30 days, so cutting feels safe |
| **Chronicler** (series writer) | Keeps a long story straight | *Ledger*: the chapter pane shows the previous chapter's last paragraph |
| **Poet** (short-form, poetry, flash) | Small, finished pieces | *Encore*: finishing a short piece (Done status) triggers the full celebration |

### Perks

Picked at perk levels from a pool, and some need a background, a class level or an achievement first. A perk is a
**change to how the editor behaves**: always on and passive, with an off switch in Settings so a perk never traps
anyone. Examples:

- *Typewriter scroll*: the line being typed stays centred vertically.
- *Ember trail*: the last sentence you typed glows faintly, to help you pick the thread back up.
- *Quiet margins*: notes and beat markers fade while you type and come back when you pause.
- *Lantern*: on reopening a chapter, the caret goes to the last edit, not the top.
- *Deep pockets*: one more skill-bar slot.
- *Scavenger*: the goal offer shows four goals.
- *Second draft*: once per run, see the previous version of a paragraph side by side.

### Achievements, titles and relics

- **Achievements** are on the sheet as a trophy shelf, for example "First floor cleared", "100 rooms renovated" or
  "30-day torch". They are rules over the run history, so they can also be awarded for past runs.
- **Titles** come from achievements, for example "Delver of the Third Floor" or "Mason of a Hundred Rooms". You equip
  one and it shows on the run card and the recap.
- **Relics** are achievement rewards with a small effect on *options*, never on how hard a goal is. For example, one
  free reroll a day, or a skill charge carried between runs.
- **Inventory** holds owned cosmetics, unlocked classes, skills and goal types.

The sheet is a view of its own (Run → Character, plus a palette command). It shows:
- the portrait (the background plus the equipped class's style)
- the level and XP bar
- the class levels as a split bar (for example "Delver 4 / Mason 2")
- the perks taken
- the achievements shelf, the equipped title and relics
- the wallet

## 4. The run loadout

The run card (phase 1's Choosing state) becomes a two-step **loadout**. Each step remembers the last pick, so the
loadout is one click once you've chosen:

1. **Class.** A drafting or editing class (§5). It decides:
   - which skills are on hand during the run
   - the **style of the editor**: its colour theme, the run atmosphere and the soundscape. Each class has a signature
     style, for example torchlit amber for the Delver, cool stone for the Mason and violet starlight for the Seer.
     Owned variants of that style (bought, or earned through prestige) can be swapped in on this step. Styles reuse
     `ThemeCatalog`, the run atmosphere and `IRunSoundscape`.
2. **Goal.** Usually three goals, drawn from a deck and built from the manuscript's state, plus the run length (the
   minute dial). **Reroll** swaps the offer for gold. A "no goal" free run is always allowed.

The **dungeon's look is not part of the loadout.** Room textures and tile sets belong to the *story*, not the
character, so a book looks the same whichever class you play (§6, "Tile sets").

### Goal deck (first draft)

Each goal names its room or floor, so the map can light it up.

| Goal | Fits | Met when |
|---|---|---|
| **Excavate** *chapter* | Drafting | The chapter gains N net words (scaled to its target and the run length) |
| **Break ground** | Drafting | A Planned chapter gets its first words |
| **Lay foundations** | Drafting | The next unchecked beat on the board gets a linked scene with words in it |
| **Renovate** *chapter* | Editing | The chapter's status moves up one step |
| **Clear rubble** | Editing | A chapter over its target shrinks by N % without losing a scene break |
| **Clear the echoes** | Editing | The repeated-word skill reports no flagged echoes left in a chapter |
| **Proofread a floor** | Editing | A chapter has no spelling issues left |
| **Map the depths** | Planning | N beats or cards added or completed on a board |

Goals are data (id, title, family, a rule over the run summary and the manuscript's state, a reward), the same way the
runs spec plans achievements. Revision goals are judged by results (status changes, net cuts, fewer issues), not raw
churn, so they don't reward busywork (an open question in the research).

### Missing a goal

When the timer ends with the goal unmet, the recap says **GOAL MISSED** in calm colours, then shows how close you got
and what the run still earned (XP, base gold, streak). No words or progress are removed. Next time, the same goal
can be offered again at a small discount.

## 5. Classes and skills

A class is mainly an **editor configuration**: what's on screen and what can be reached during the run. This has a
real craft benefit. A drafting class keeps the editing tools out of the way, so they can't pull you out of the
draft, and an editing class keeps them close at hand.

**Rule:** tools Hymnal already has (spell check today) stay available outside runs, whatever you've unlocked. A
class only decides what is *in your hands during a run*. Unlocks add *new* skills and variants, and nothing that
exists today is locked away.

### Class levels and multiclassing

Each character level grants one **class level**, which goes into any unlocked class. Multiclassing is expected: a
writer who drafts and revises might be *Delver 5 / Mason 3*. A class's levels **unlock** its skills, then **improve**
them (rank II, rank III). Each rank is a real step up in the editor feature, not a bigger number on a goal.

Every level of every class grants something:

| Class | Leaning | Lv 1 | Lv 2 | Lv 3 | Lv 4 | Lv 5 (capstone) |
|---|---|---|---|---|---|---|
| **Delver** | Drafting | *Tunnel vision*: spelling squiggles hidden, focus mode on | *Second wind*: adds 5 minutes, once per run | *Prompt card*: a random prompt for the stuck moment | *Tunnel vision II*: the word count is also hidden until the run ends, and you drop past paragraphs from view | *Momentum*: combo multiplier for unbroken typing |
| **Cartographer** | Planning and drafting | *Survey*: the next beat and the chapter outline pinned above the editor | *Waypoint*: drop a TODO marker and keep writing | *Beat link*: link the scene being drafted to its beat | *Waypoint II*: a list of waypoints across the book, to jump between them | *Survey II*: the whole floor's beats as a strip, with the drafted ones ticked |
| **Mason** | Editing | *Spellward*: spell check, with a sweep through issues one by one | *Echo sight*: repeated words within a paragraph | *Thesaurus*: synonyms for the word at the caret | *Trim*: filler words and adverbs flagged | *Echo sight II*: repeated phrases across the chapter |
| **Seer** | Editing with AI | *Read the room*: AI names the paragraph's family and mood | *Scry*: AI feedback on one paragraph | *Echoes of the past*: AI continuity check against earlier chapters | *Scry II*: compares two versions of a paragraph | *Oracle*: a pacing read of the whole chapter |

- The class you **play** in a run is the one whose skills sit on the skill bar. With multiclassing, the loadout can
  also slot one skill from a second class. The *Deep pockets* perk adds a slot.
- **Skills** are commands with a hotkey and a slot on the **skill bar** in the run HUD. Some have **charges** per
  run, mainly the AI spells, which also caps cost. Spells use a **mana** pool that refills each run. Ranks raise the
  number of charges.
- **Classes beyond these four** can be unlocked later, for example a dialogue-focused **Bard** or a world-building
  **Loremaster** who works in research docs.
- **Implementation:** skills wrap existing services. Spell check is `Spelling/SpellChecker`. Echo sight and Trim
  are new pure Core scanners built on `ProseWordScanner`. Thesaurus needs a bundled offline thesaurus (licence to
  check). Scry and Read the room call `IChatClient` through `ChatClientFactory`.

## 6. The dungeon map

A new **Dungeon mode** on the mode rail, beside Plan, Write and Manage, draws the manuscript as tiles. The launch pad
and the run recap show a mini-map taken from it. This placement is for now only: all of this will probably need an
overall UX redesign (§12).

### Scale

| Level | Manuscript | Map |
|---|---|---|
| World | Series (several books / collections) | An overworld of dungeon entrances |
| Dungeon | Book / collection (Book.txt) | Floors stacked in order |
| Floor | Chapter, or a story in an anthology | A grid of rooms |
| Room | **Scene**: the text between scene breaks, or a whole scene file | A tile, sized by word count |
| Feature | **Paragraph** inside the scene | A piece of furniture or a prop drawn inside the room |

Short stories become a single compact floor, and an anthology is a cluster of small side dungeons. Scene breaks are
doorways and chapter headings are stairs. Planned chapters with no words are **unexcavated rock** with a dotted
outline, sized by their target. A chapter with no scene breaks is one large room.

Scenes as rooms keep the map readable: a dialogue-heavy chapter doesn't turn into a hundred tiny tiles. The
paragraphs still show, as the furnishing of each room.

### What a room shows

- **Features (paragraphs).** Each paragraph is classified into a **family**, and the family picks the prop the
  story's tile set draws for it:

  | Family | Feature | Detected by |
  |---|---|---|
  | Dialogue | A table with mugs, a speaking-stone | Mostly quoted speech |
  | Action | A weapon rack, scorch marks | Short sentences, motion verbs |
  | Description | A window, a tapestry | Concrete nouns and modifiers, few verbs of motion |
  | Interiority | Candles at a small shrine | First-person or close-third thought, cognition verbs |
  | Exposition / lore | A bookshelf | Long sentences, few named actors |
  | Transition | An archway | A short paragraph that moves time or place |

- **Room type** comes from the mix of its features, weighted by words. A room is a **tavern** when dialogue
  dominates, an **arena** for action, a **gallery** for description, a **shrine** for interiority and a **library**
  for exposition. A room with no dominant family is a **hall** with zones.
- **Condition** follows renovation: rough (drafted, never revised) → dug → renovated (edited in a later run or
  pass) → polished (the chapter is Done). It shows as wear on the tile.
- **Freshness** shows how recently the room was touched, and **heat** shows how much it changed in the last run.
  The recap can show the run's path through the rooms.
- **Notes and beats** pinned to the scene appear as markers. A beat linked to a scene names its room.

Selecting a room opens its scene in Write. Hovering shows the first line, the room type, its feature mix, word count
and condition. Zooming into a room lists its features, one per paragraph, and clicking a feature jumps to that
paragraph.

### Tile sets

The dungeon's look belongs to the **story**, so it stays the same whichever class the author plays:

- The **project** has a genre and setting in `project.json`, for example fantasy, sci-fi, horror, contemporary,
  historical, noir or romance. That picks the default **tile set**, which supplies the floor, wall, door and
  feature art. A fantasy book gets stone halls and a tavern with mugs. A sci-fi book gets corridors and a mess deck.
- A **chapter** (or a story in an anthology) can override the setting, for example a dream sequence, a flashback or
  the one story in an anthology set on a ship. The override is stored with the chapter's metadata through
  `IStoryMetadataStore`.
- A series can set the world's default, and each book can override it.
- Tile sets are cosmetics. A few ship built in, one per common genre, and more can be bought. They are kept separate
  from the editor styles that classes carry.
- **Later, in its own feature design:** generated tiles. A cheap image model would turn a scene's setting
  (classified or summarised) into its own room texture, cached per scene fingerprint. It needs its own spec for cost,
  privacy, consistency within a book and moderation.

### Identity: scenes and paragraphs

Neither scenes nor paragraphs have IDs, and adding IDs to the prose is out of the question. They are matched between
versions instead:

- Each paragraph's **fingerprint** is a hash of its normalised text.
- On each save, the new paragraph list is aligned with the stored one. Exact hash matches come first. Then comes
  similarity between neighbours (word-level `WordDiff` similarity above a threshold), so an edited paragraph keeps
  its family and history.
- A **scene** keeps its room id when most of its paragraphs come through the alignment (the best overlap wins), so
  splitting, merging or moving scenes keeps their history as far as possible.
- The data lives in a sidecar per chapter, `.hymnal-data/rooms/{chapterUuid}.json`:
  `{ schemaVersion, rooms: [{ id, condition, firstSeen, lastEdited, edits, features: [{ fingerprint, family, familySource: heuristic|model }] }] }`.
  Writes go through `IMetadataStore` (atomic), and the usual rules apply: a malformed file is read-only and is never
  overwritten.

### Classification

Classification runs in two tiers. It never blocks typing:

1. **Heuristic (always on, offline).** A pure Core classifier over `ProseWordScanner` tokens: the share of quoted
   speech, sentence length, pronoun mix and lists of verb classes. It's instant, runs on every changed paragraph, and
   is good enough to draw the map.
2. **Model (opt-in).** When the caret leaves a paragraph that changed, it is queued and debounced. A cheap model
   (Ollama locally, or a small hosted model) returns `{ family, mood, confidence }`, and the result is cached by
   fingerprint, so an unchanged paragraph is never sent twice. Paragraphs are sent in batches. There's a monthly
   token budget in Settings, and the map shows which families came from the model.

AI is off until the author turns it on and chooses a provider. With AI off, the heuristic map still works.

## 7. AI in runs

- **Run summary (recap).** After a run, the run's diff is sent as text: each touched chapter with its added and
  removed passages, cut down to a budget. The model returns a short "what you did" summary, in the delve-log voice of
  the chosen class, plus up to three observations. It is stored with the run in the Logbook. If the model call fails,
  the recap stays as it is.
- **Paragraph feedback (Scry).** A spell cast on the paragraph at the caret. It returns notes in a side panel and
  never edits text. Each cast uses mana or a charge.
- **Writer's choice:** AI features are opt-in, per provider and per project (a project can say "never send this
  manuscript"). The prompts sit in Core with tests over their inputs. The existing AI chat panel is unaffected.

## 8. Economy

| Currency | Earned by | Spent on |
|---|---|---|
| **XP** | Every run (phase 1 formula) | Nothing. It sets the level. |
| **Gold** | Every run (from XP), plus the goal bonus, plus first-time achievements | The shop |

The shop is phase 4's unlock web, extended:

- **Cosmetics:** editor-style variants for classes, story tile sets, portraits, titles, sound schemes, the editor companion (zen garden).
- **Editor improvements:** unlock a new class so it can take levels, extra spell charges, new goal types for the
  deck. Skills themselves come from class levels, and perks come from perk picks. These add options and don't loosen
  goals.
- **Rerolls and conveniences:** reroll the goal offer, add a fourth goal to an offer, a streak freeze, and a
  **respec** (move class levels or swap perks) after the first free one.

Prices are data. There's no real-money currency, and gold can't be bought. The wallet is stored as raw earnings and
purchases, so the balance is recomputed and a formula change can be applied to history.

## 9. Storage additions (all additive)

- `writer-profile.json` gains:
  - per run: `classId`, `styleId`, `goal { id, target, met }`, `goldEarned`, `summary`
  - at the top level: `character { background, perks[], classLevels { classId: n }, respecs }`, `purchases[]`,
    `equipped { title, relics[], skillBar }`, `achievements[]`

  The format keeps its rule of only gaining fields, and old runs read as "no class, no goal".
- `.hymnal-data/rooms/{chapterUuid}.json`: scene rooms and their paragraph features (§6).
- `.hymnal-data/project.json` gains `ai.allowManuscript`, `genre` and `setting` (the default tile set). Chapter metadata gains an optional `setting` override.
- App settings gain `run.loadout` (the last class, style and goal family), `ai.classifier`, `ai.summary` and
  `ai.budget`.

## 10. The name

Hymnal will be renamed to fit the motif. The author prefers **Inkdeep** (ink plus depth, read as a place you go down
into), with **Wordcrawl** as the backup. Also considered: Delve, Folio & Flame, Scriptorium Below, Quillcrawl.

**Availability check (2026-11-17, informal):**

| | Inkdeep | Wordcrawl |
|---|---|---|
| Existing product with the name (web search) | None found (near names: InkJot, InkMark, Inkline) | None found (near names: WordSpiral, Word Craft) |
| `.com` | Registered since 2000: a personal portfolio site ("inkdeep.:.tikisurf"), not a product | Registered |
| `.app` / `.io` / `.dev` | Looked unregistered (RDAP 404) | `.app` registered; `.io` looked unregistered |

**Still to do before committing to the name:**
- a trademark search (USPTO, and EUIPO/UKIPO if it ships there)
- register `inkdeep.app` (or `.dev`)
- check the GitHub org/repo and the NuGet and package-manager ids (winget, `.deb`)

A rename touches the solution, namespaces, AppData paths (`%AppData%/Hymnal`, so a one-time migration),
`.hymnal-data/` (read the old folder name forever), the embedded `<!-- hymnal:v1 -->` marker (keep reading it),
`hopelab-app.json`, the installers and the GitHub repo. It should get its own spec once a name is chosen.

## 11. Phasing

This replaces and extends phases 2–5 of the runs spec:

| Phase | Contents |
|---|---|
| 2. Launch pad + loadout | Launch pad (Future You note, last room, **Enter**). Loadout with the class and its editor style (Delver and Mason to start) and the goal deck (Excavate, Break ground, Renovate, Clear rubble). Missed-goal recap. `goal` stored on runs. |
| 3. Character sheet + progression | Character creation (background), level, class levels with multiclassing, perk picks and the first perks, achievements shelf, titles and relics, the free first respec. The skill bar with the Delver and Mason level 1–2 skills (Tunnel vision, Second wind, Spellward, Echo sight). |
| 4. Dungeon map | The Dungeon mode on the rail, scene rooms with paragraph features and fingerprints, the heuristic classifier, the tile view (floors, doors, rock), condition and freshness, and the run path on the recap. |
| 5. Economy + shop | Gold, the shop (the old unlock web) with cosmetics, editor improvements and rerolls, and the Cartographer class. |
| 6. AI layer | Provider opt-in per project, the model classifier, run summaries, the Seer class (Scry, Read the room) and the token budget. |
| 7. Rename to Inkdeep | Its own spec, after the trademark search. Can happen at any point. |
| Later | The series overworld, more classes (Bard, Loremaster), and a LitRPG tracker for the characters' stats (R-F7, kept separate from the author's sheet). |

## 12. Open questions

1. **The app's overall UX.** Runs, the loadout, the character sheet, the Dungeon mode and the shop will crowd the
   current shell (mode rail, menus and right-hand panes). This probably needs its own redesign and rearchitecture
   spec, alongside the rename to Inkdeep. For now, the map is a new mode on the rail. **Drafted:**
   `2026-11-18-inkdeep-shell-redesign.md`.
2. Thesaurus source and licence for an offline synonym list.
