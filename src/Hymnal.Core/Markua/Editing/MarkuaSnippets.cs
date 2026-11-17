using System;
using System.Collections.Generic;
using System.Linq;

namespace Hymnal.Core.Markua.Editing;

/// <summary>A snippet's keyboard shortcut: <see cref="Gesture"/> in Avalonia's KeyGesture syntax, <see cref="Display"/> as menus show it.</summary>
public sealed record MarkuaShortcut(string Gesture, string Display);

/// <summary>The Insert menu's groups, in menu order; a separator falls between groups.</summary>
public enum MarkuaSnippetSection
{
    Format,
    Reference,
    Paragraph,
    Callout,
    Break,
    Structure,
}

/// <summary>
/// One Insert menu command: a Markua construct and the edit that applies it to the editor's text and selection.
/// </summary>
/// <param name="Id">Stable identity (palette history, tests).</param>
/// <param name="Title">The menu item's text (inside its <paramref name="Submenu"/>, if any).</param>
/// <param name="Submenu">The submenu it sits in (Heading, Callout, Book Structure), or null for the top level.</param>
/// <param name="Syntax">The Markua it writes, shown beside it in the palette.</param>
public sealed record MarkuaSnippet(
    string Id,
    string Title,
    MarkuaSnippetSection Section,
    string? Submenu,
    string Syntax,
    MarkuaShortcut? Shortcut,
    Func<string, TextSpan, TextEdit> Apply)
{
    /// <summary>The title with its submenu, so a palette row stands on its own ("Callout: Warning").</summary>
    public string PaletteTitle => Submenu is null ? Title : $"{Submenu}: {Title}";
}

/// <summary>
/// The Insert menu's contents, in order: Markua as Leanpub reads it (bold <c>**</c>, italic <c>*</c>, callouts
/// <c>A&gt;</c> … <c>C&gt;</c>, <c>{pagebreak}</c>, <c>{class: part}</c>, …), matching what <see cref="MarkuaParser"/>
/// previews. The menu, the command palette and the editor's shortcuts are all built from this list.
/// </summary>
public static class MarkuaSnippets
{
    public static IReadOnlyList<MarkuaSnippet> All { get; } = Build();

    public static MarkuaSnippet Get(string id) => All.First(s => s.Id == id);

    private static List<MarkuaSnippet> Build()
    {
        var list = new List<MarkuaSnippet>();

        void Add(string id, string title, MarkuaSnippetSection section, string syntax,
            Func<string, TextSpan, TextEdit> apply, string? gesture = null, string? display = null,
            string? submenu = null) =>
            list.Add(new MarkuaSnippet(id, title, section, submenu, syntax,
                gesture == null ? null : new MarkuaShortcut(gesture, display ?? gesture), apply));

        void Inline(string id, string title, string marker, string? gesture = null, string? display = null) =>
            Add(id, title, MarkuaSnippetSection.Format, $"{marker}text{marker}",
                (t, s) => MarkuaEdits.ToggleInline(t, s, marker), gesture, display);

        // Format: inline styles, which wrap or unwrap the selection.
        Inline("bold", "Bold", "**", "Ctrl+B");
        Inline("italic", "Italic", "*", "Ctrl+I");
        Inline("strikethrough", "Strikethrough", "~~", "Ctrl+Shift+X");
        Inline("code", "Inline Code", "`", "Ctrl+E");
        Inline("superscript", "Superscript", "^", "Ctrl+Shift+OemPlus", "Ctrl+Shift+=");
        Inline("subscript", "Subscript", "~", "Ctrl+OemPlus", "Ctrl+=");

        // Links, images and notes.
        Add("link", "Link", MarkuaSnippetSection.Reference, "[text](url)", MarkuaEdits.InsertLink, "Ctrl+K");
        Add("image", "Image", MarkuaSnippetSection.Reference, "![Caption](image.png)",
            (t, s) => MarkuaEdits.InsertBlock(t, s, ["![Caption](image.png)"], "image.png"));
        Add("footnote", "Footnote", MarkuaSnippetSection.Reference, "[^1] … [^1]: note",
            MarkuaEdits.InsertFootnote, "Ctrl+Alt+F");

        // Paragraph styles, which apply to every line the selection touches.
        const string heading = "Heading";
        Add("heading-1", "Chapter (#)", MarkuaSnippetSection.Paragraph, "# Title",
            (t, s) => MarkuaEdits.SetHeading(t, s, 1), "Ctrl+D1", "Ctrl+1", heading);
        Add("heading-2", "Section (##)", MarkuaSnippetSection.Paragraph, "## Title",
            (t, s) => MarkuaEdits.SetHeading(t, s, 2), "Ctrl+D2", "Ctrl+2", heading);
        Add("heading-3", "Subsection (###)", MarkuaSnippetSection.Paragraph, "### Title",
            (t, s) => MarkuaEdits.SetHeading(t, s, 3), "Ctrl+D3", "Ctrl+3", heading);
        Add("heading-4", "Sub-subsection (####)", MarkuaSnippetSection.Paragraph, "#### Title",
            (t, s) => MarkuaEdits.SetHeading(t, s, 4), "Ctrl+D4", "Ctrl+4", heading);
        Add("heading-none", "Normal Text", MarkuaSnippetSection.Paragraph, "no heading",
            (t, s) => MarkuaEdits.SetHeading(t, s, 0), "Ctrl+D0", "Ctrl+0", heading);
        Add("bulleted-list", "Bulleted List", MarkuaSnippetSection.Paragraph, "- item",
            (t, s) => MarkuaEdits.ToggleList(t, s, numbered: false), "Ctrl+Shift+D8", "Ctrl+Shift+8");
        Add("numbered-list", "Numbered List", MarkuaSnippetSection.Paragraph, "1. item",
            (t, s) => MarkuaEdits.ToggleList(t, s, numbered: true), "Ctrl+Shift+D7", "Ctrl+Shift+7");
        Add("blockquote", "Block Quote", MarkuaSnippetSection.Paragraph, "> quote",
            MarkuaEdits.ToggleBlockQuote, "Ctrl+Shift+OemPeriod", "Ctrl+Shift+.");
        Add("code-block", "Code Block", MarkuaSnippetSection.Paragraph, "``` … ```",
            (t, s) => MarkuaEdits.WrapLines(t, s, "```", "```", "code"));
        Add("table", "Table", MarkuaSnippetSection.Paragraph, "| Header | Header |",
            (t, s) => MarkuaEdits.InsertBlock(t, s,
                ["| Header | Header |", "| ------ | ------ |", "| Cell   | Cell   |"], "Header"));

        // Callouts: Markua's lettered blocks, which Leanpub sets as boxes with an icon.
        const string callout = "Callout";
        foreach (var (letter, name) in new[]
                 {
                     ('A', "Aside"), ('B', "Blurb"), ('C', "Centered Blurb"), ('T', "Tip"), ('I', "Information"),
                     ('W', "Warning"), ('E', "Error"), ('Q', "Question"), ('D', "Discussion"), ('X', "Exercise"),
                 })
        {
            var l = letter;
            Add($"callout-{char.ToLowerInvariant(l)}", $"{name} ({l}>)", MarkuaSnippetSection.Callout, $"{l}> text",
                (t, s) => MarkuaEdits.ToggleCallout(t, s, l), submenu: callout);
        }

        // Breaks and comments.
        Add("scene-break", "Scene Break", MarkuaSnippetSection.Break, "* * *",
            (t, s) => MarkuaEdits.InsertBlock(t, s, ["* * *"]), "Ctrl+Shift+Enter");
        Add("page-break", "Page Break", MarkuaSnippetSection.Break, "{pagebreak}",
            (t, s) => MarkuaEdits.InsertBlock(t, s, ["{pagebreak}"]), "Ctrl+Enter");
        Add("comment", "Comment", MarkuaSnippetSection.Break, "%% not in the book",
            MarkuaEdits.ToggleComment, "Ctrl+OemQuestion", "Ctrl+/");

        // Book structure: parts and the front / main / back matter markers Leanpub uses for numbering.
        const string structure = "Book Structure";
        Add("part", "Part Heading", MarkuaSnippetSection.Structure, "{class: part}",
            (t, s) => MarkuaEdits.InsertBlock(t, s, ["{class: part}", "# Part Title"], "Part Title"),
            submenu: structure);
        Add("frontmatter", "Front Matter", MarkuaSnippetSection.Structure, "{frontmatter}",
            (t, s) => MarkuaEdits.InsertBlock(t, s, ["{frontmatter}"]), submenu: structure);
        Add("mainmatter", "Main Matter", MarkuaSnippetSection.Structure, "{mainmatter}",
            (t, s) => MarkuaEdits.InsertBlock(t, s, ["{mainmatter}"]), submenu: structure);
        Add("backmatter", "Back Matter", MarkuaSnippetSection.Structure, "{backmatter}",
            (t, s) => MarkuaEdits.InsertBlock(t, s, ["{backmatter}"]), submenu: structure);

        return list;
    }
}
