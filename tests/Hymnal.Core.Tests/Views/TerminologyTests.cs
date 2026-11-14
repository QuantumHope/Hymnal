using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Hymnal.Core.Tests.Views;

/// <summary>
/// Views take every structural noun (chapter, part, book…) from the project's profile, so a short story or
/// anthology never shows novel words. Book.txt, the file name, is allowed.
/// </summary>
public class TerminologyTests
{
    private static readonly Regex LiteralNovelWord = new(
        @"\b(Text|Header|ToolTip\.Tip|Content|Watermark|PlaceholderText|Title)=""(?<value>[^""{]*\b(chapters?|book|part|novel)\b[^""]*)""",
        RegexOptions.IgnoreCase);

    // The Schedule grid's first column: replaced from the profile in GanttView's code-behind.
    private static readonly string[] Allowed = ["GanttView.axaml:Header=\"CHAPTER\""];

    [Fact]
    public void Views_take_structural_nouns_from_the_profile()
    {
        var views = RepoPath("src/Hymnal/Views");
        var offenders = Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => LiteralNovelWord.Matches(File.ReadAllText(file))
                .Where(m => !m.Groups["value"].Value.Contains("Book.txt", StringComparison.Ordinal))
                .Select(m => $"{Path.GetFileName(file)}:{m.Value}"))
            .Where(hit => !Allowed.Contains(hit))
            .ToList();

        Assert.True(offenders.Count == 0, "Hard-coded novel wording:\n" + string.Join("\n", offenders));
    }

    private static string RepoPath(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Hymnal.slnx")))
                return Path.Combine(dir.FullName, relative);
        throw new DirectoryNotFoundException("Hymnal.slnx not found above " + AppContext.BaseDirectory);
    }
}
