# Bundled spelling dictionaries

Hymnal's spell check (`Hymnal.Core/Spelling`) reads Hunspell dictionaries. Every `{id}.dic` + `{id}.aff`
pair in this folder is embedded in `Hymnal.Core` as `Hymnal.Dictionaries.{id}.dic` / `.aff` and shows up in
Settings → Spelling → Dictionary. The id is a culture name such as `en-US` or `en-GB`, which is also what the
settings store (the default is `en-US`).

Intended contents:

| Id | Language | Source |
|---|---|---|
| `en-US` | English (United States) | wooorm/dictionaries, `dictionaries/en` (SCOWL-based) |
| `en-GB` | English (United Kingdom) | wooorm/dictionaries, `dictionaries/en-GB` (SCOWL-based) |

To add or update one, copy the source's `index.dic` and `index.aff` here as `{id}.dic` and `{id}.aff`, and
its `license` file as `{id}.license`. Keep each licence next to its dictionary, because the files are
redistributed inside Hymnal.

With no dictionary here, Hymnal still builds and runs; a spelling check then says that no dictionary is installed
and where to put one.

Writers can add other languages without a rebuild by putting a `.dic`/`.aff` pair in their dictionaries
folder (`%AppData%\Hymnal\dictionaries` on Windows, `~/.config/Hymnal/dictionaries` on Linux). A pair there
with the same id replaces the bundled one.
