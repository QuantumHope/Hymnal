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
| Author identity | The author has a **character sheet** of their own: level, class mastery, stats, titles, relics and inventory. It lives in the global writer profile. |
| Before a run | Pick a **class** and a **theme**, then a **goal** from an offered set (usually three). Rerolling the offer costs currency. |
| The dungeon | Shown as **tiles**. A **room is a paragraph**, a floor is a chapter (or scene file), the dungeon is the book, and a series is a world of dungeons. |
| Room types | Each paragraph is given a **family** (dialogue, action, description…), and the family picks its tile graphic. Families come from a classifier: instant heuristics first, then a cheap model, run when the paragraph is changed or after the caret leaves it. |
| AI | Cheap models (local Ollama, or a small hosted model, through the existing `ChatClientFactory`) **summarise the run** on the recap and, when cast as a spell, **give feedback on a paragraph**. AI never rewrites prose by itself. |
| Classes | Some classes are built for **drafting** and others for **editing**, and each has its own **skills**. AI analysis is a spell. Repeated-word search, synonyms and spell check are skills. |
| Currencies | Runs earn **XP** (levels, never spent) and **gold** (spent), and goals pay bonus gold. Spend it on cosmetics, real editor improvements (new skills, skill slots, classes, goal types) and rerolls. |
| Failure | **No permadeath.** A run that misses its goal is a *missed goal*: the words stay, and so do XP and the base gold. Only the goal's bonus is lost. The Logbook says "Goal missed" without judgement. |
| Name | **Hymnal will be renamed.** The candidates are in §10. |

## 3. The author's character sheet

The author is the player character. The sheet lives in `writer-profile.json`, the global profile from phase 1. It
stays **derived**: runs store raw numbers, and the sheet is recomputed from them, so a change to the formulas is
re-applied to all history (the same rule as phase 1's XP).

- **Level** comes from lifetime XP. Use the curve from the runs spec, phase 3 (about weekly level-ups at first for a
  15-minute daily habit).
- **Stats** describe how you write. They don't gate anything:

  | Stat | Derived from | Flavour |
  |---|---|---|
  | Might | Words added per focused minute (rolling 30 runs) | Drafting power |
  | Precision | Share of words cut in revision runs, and goals met in editing classes | Renovation skill |
  | Stamina | Median run length | How deep you delve |
  | Resolve | Current and best streak | The torch you keep lit |
  | Insight | Spells cast and suggestions accepted (only when AI is on) | Reading the walls |

- **Class mastery** gives each class its own level from the XP earned while playing it. Mastery unlocks that class's
  skills (§5).
- **Titles** come from achievements, for example "Delver of the Third Floor" or "Mason of a Hundred Rooms". You equip
  one and it shows on the run card and the recap.
- **Relics** are achievement rewards with a small passive effect that changes *options*, never makes goals easier.
  For example, one more goal offered, one free reroll a day, or a skill charge carried between runs.
- **Inventory** holds owned cosmetics, unlocked classes, skills and goal types.

The sheet is a view of its own (Run → Character, plus a palette command). It shows the portrait for the equipped
class and theme, the stats as bars, mastery levels, the equipped title and relics, and the wallet.

## 4. The run loadout

The run card (phase 1's Choosing state) becomes a three-step **loadout**. Each step remembers the last pick, so the
loadout is one click once you've chosen:

1. **Class.** A drafting or editing class (§5). It decides which skills are on hand during the run.
2. **Theme.** The biome: the dungeon's tile set, the colour theme, the atmosphere and the soundscape. Themes reuse
   `ThemeCatalog`, the run atmosphere and `IRunSoundscape`. A theme is cosmetic.
3. **Goal.** Usually three goals, drawn from a deck and built from the manuscript's state, plus the run length (the
   minute dial). **Reroll** swaps the offer for gold. A "no goal" free run is always allowed.

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

| Class | Leaning | Starting skills | Mastery unlocks (examples) |
|---|---|---|---|
| **Delver** | Drafting | *Tunnel vision* (spelling squiggles hidden, focus mode on), *Second wind* (adds 5 minutes, once per run) | *Prompt card* (a random prompt for the stuck moment), *Momentum* (combo multiplier for unbroken typing) |
| **Cartographer** | Planning and drafting | *Survey* (the next beat and the chapter's outline pinned above the editor) | *Waypoint* (drop a TODO marker and keep writing), *Beat link* |
| **Mason** | Editing | *Echo sight* (highlights repeated words and phrases nearby), *Spellward* (spell check, with a sweep through issues one by one) | *Thesaurus* (synonyms for the word at the caret), *Trim* (flags filler words and adverbs) |
| **Seer** | Editing with AI | *Scry* (AI feedback on one paragraph), *Read the room* (AI names the paragraph's family and mood) | *Echoes of the past* (AI continuity check against earlier chapters), *Oracle* (a pacing read of the whole chapter) |

- **Skills** are commands with a hotkey and a slot on a **skill bar** in the run HUD. Some have **charges** per
  run, mainly the AI spells, which also caps cost. Spells use a **mana** pool that refills each run.
- **Classes beyond these four** can be unlocked later, for example a dialogue-focused **Bard** or a world-building
  **Loremaster** who works in research docs.
- **Implementation:** skills wrap existing services. Spell check is `Spelling/SpellChecker`. Echo sight and Trim
  are new pure Core scanners built on `ProseWordScanner`. Thesaurus needs a bundled offline thesaurus (licence to
  check). Scry and Read the room call `IChatClient` through `ChatClientFactory`.

## 6. The dungeon map

A new view (Plan → **Dungeon**, or a mode of its own once the name and the mode rail settle) draws the manuscript as
tiles.

### Scale

| Level | Manuscript | Map |
|---|---|---|
| World | Series (several books / collections) | An overworld of dungeon entrances |
| Dungeon | Book / collection (Book.txt) | Floors stacked in order |
| Floor | Chapter, scene file or story | A grid of rooms |
| Room | **Paragraph** | One tile (larger paragraphs take more cells) |

Short stories become a single compact floor, and an anthology is a cluster of small side dungeons. Headings and
scene breaks are doors and stairs. Planned chapters with no words are **unexcavated rock** with a dotted outline,
sized by their target.

### What a room shows

- **Family** picks its graphic (the theme's tile set supplies the art):

  | Family | Tile | Detected by |
  |---|---|---|
  | Dialogue | Tavern / hall of voices | Mostly quoted speech |
  | Action | Arena | Short sentences, motion verbs |
  | Description | Vista / gallery | Concrete nouns and modifiers, few verbs of motion |
  | Interiority | Shrine | First-person or close-third thought, cognition verbs |
  | Exposition / lore | Library | Long sentences, few named actors |
  | Transition | Corridor | Short paragraph that moves time or place |
  | Break / heading | Stairs, door | Markua structure |

- **Condition** follows renovation: rough (drafted, never revised) → dug → renovated (edited in a later run or
  pass) → polished (the chapter is Done). It shows as wear on the tile.
- **Freshness** shows how recently the room was touched, and **heat** shows how much it changed in the last run.
  The recap can show the run's path through the rooms.
- **Notes and beats** pinned to the room appear as small markers.

Selecting a room opens the paragraph in Write. Hovering shows its first line, family, word count and condition.

### Paragraph identity

Paragraphs have no IDs, and adding them to the prose is out of the question. Rooms are matched between versions
instead:

- Each paragraph's **fingerprint** is a hash of its normalised text.
- On each save, the new paragraph list is aligned with the stored one. Use exact hash matches first, then
  similarity between neighbours (word-level `WordDiff` similarity above a threshold), so an edited paragraph keeps
  its room, family and history.
- The data lives in a sidecar per chapter, `.hymnal-data/rooms/{chapterUuid}.json`:
  `{ schemaVersion, rooms: [{ id, fingerprint, family, familySource: heuristic|model, condition, firstSeen, lastEdited, edits }] }`.
  Writes use `IMetadataStore` (atomic), and the usual rules apply: a malformed file is read-only and never
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
  the chosen theme, plus up to three observations. It is stored with the run in the Logbook. If the model call fails,
  the recap stays as it is.
- **Paragraph feedback (Scry).** A spell cast on the paragraph at the caret. It returns notes in a side panel and
  never edits text. Each cast uses mana or a charge.
- **Writer's choice:** AI features are opt-in, per provider and per project (a project can say "never send this
  manuscript"). The prompts sit in Core with tests over their inputs. The existing AI chat panel is unaffected.

## 8. Economy

| Currency | Earned by | Spent on |
|---|---|---|
| **XP** | Every run (phase 1 formula) | Nothing. It sets the level. |
| **Class XP** | XP earned while playing a class | Nothing. It sets class mastery. |
| **Gold** | Every run (from XP), plus the goal bonus, plus first-time achievements | The shop |

The shop is phase 4's unlock web, extended:

- **Cosmetics:** themes, tile sets, portraits, titles, sound schemes, the editor companion (zen garden).
- **Editor improvements:** new skills for a class, a second or third skill-bar slot, extra spell charges, new goal
  types for the deck, new classes. These add options and don't loosen goals.
- **Rerolls and conveniences:** reroll the goal offer, add a fourth goal to an offer, a streak freeze.

Prices are data. There's no real-money currency, and gold can't be bought. The wallet is stored as raw earnings and
purchases, so the balance is recomputed and a formula change can be applied to history.

## 9. Storage additions (all additive)

- `writer-profile.json` gains:
  - per run: `classId`, `themeId`, `goal { id, target, met }`, `goldEarned`, `summary`
  - at the top level: `purchases[]`, `equipped { title, relics[], skillBar }`, `achievements[]`

  The format keeps its rule of only gaining fields, and old runs read as "no class, no goal".
- `.hymnal-data/rooms/{chapterUuid}.json`: the paragraph rooms (§6).
- `.hymnal-data/project.json` gains `ai.allowManuscript`.
- App settings gain `run.loadout` (the last class, theme and goal family), `ai.classifier`, `ai.summary` and
  `ai.budget`.

## 10. The name

Hymnal will be renamed to fit the motif. Requirements:
- reads well as a desktop app and a command
- hints at both writing and delving
- isn't the name of an existing writing tool (availability **not yet checked**)

Candidates to look at:

| Name | Why |
|---|---|
| **Delve** | Runs are delves. Short and a verb. Probably crowded as a product name. |
| **Inkdeep** | Ink plus depth. Reads as a place. |
| **Wordcrawl** | A dungeon crawl made of words. Clearly genre-flavoured. |
| **Folio & Flame** | Manuscript plus torch (the streak). More literary. |
| **Scriptorium Below** | The monastery writing room plus a dungeon. Longer. |
| **Quillcrawl** | Like Wordcrawl, with a more fantasy feel. |

A rename touches the solution, namespaces, AppData paths (`%AppData%/Hymnal`, so a one-time migration),
`.hymnal-data/` (read the old folder name forever), the embedded `<!-- hymnal:v1 -->` marker (keep reading it),
`hopelab-app.json`, the installers and the GitHub repo. It should get its own spec once a name is chosen.

## 11. Phasing

This replaces and extends phases 2–5 of the runs spec:

| Phase | Contents |
|---|---|
| 2. Launch pad + loadout | Launch pad (Future You note, last room, **Enter**). Loadout with the class (Delver and Mason to start), the theme and the goal deck (Excavate, Break ground, Renovate, Clear rubble). Missed-goal recap. `goal` stored on runs. |
| 3. Character sheet + progression | Level, stats, class mastery, titles, achievements and relics. The skill bar with the starting skills (Tunnel vision, Second wind, Echo sight, Spellward). |
| 4. Dungeon map | Paragraph rooms and fingerprints, the heuristic classifier, the tile view (floors, doors, rock), condition and freshness, and the run path on the recap. |
| 5. Economy + shop | Gold, the shop (the old unlock web) with cosmetics, editor improvements and rerolls, and the Cartographer class. |
| 6. AI layer | Provider opt-in per project, the model classifier, run summaries, the Seer class (Scry, Read the room) and the token budget. |
| 7. Rename | Its own spec once a name is chosen. Can happen at any point. |
| Later | The series overworld, more classes (Bard, Loremaster), and a LitRPG tracker for the characters' stats (R-F7, kept separate from the author's sheet). |

## 12. Open questions

1. Do stats ever feed back into play (for example Stamina raising the dial's maximum), or do they only describe the
   writer? Only describing is safer (R-F5).
2. Is a paragraph the right room for very short paragraphs, such as a line of dialogue? Merge consecutive
   same-family paragraphs under N words into one room?
3. Where does the dungeon map live: Plan, its own mode, or the launch pad?
4. Is the theme separate from the class, or does each class have a default biome?
5. Thesaurus source and licence for an offline synonym list.
