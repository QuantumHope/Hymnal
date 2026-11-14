using System.Globalization;
using System.Text.Json.Serialization;
using Hymnal.Core.Markua;

namespace Hymnal.Core.Submission;

/// <summary>The typeface a submission manuscript is set in. Most markets accept either.</summary>
public enum ManuscriptFont
{
    TimesNewRoman,
    CourierNew,
}

/// <summary>
/// Who the manuscript is from, as Standard Manuscript Format puts it on the first page: the legal name and
/// contact details top left, the byline under the title, and the surname in every later page's header.
/// Kept in the app settings (not the project), since it is the same for every story an author sends out.
/// </summary>
public sealed record AuthorProfile
{
    /// <summary>App settings key the profile is stored under.</summary>
    public const string SettingsKey = "submissionAuthor";

    /// <summary>The name contracts and payments go to.</summary>
    public string LegalName { get; init; } = "";

    /// <summary>The name the story is published under; blank means the legal name.</summary>
    public string Byline { get; init; } = "";

    /// <summary>The surname for the page header; blank means the last word of the byline.</summary>
    public string Surname { get; init; } = "";

    /// <summary>Postal address, phone, email, memberships: one item per line.</summary>
    public string Contact { get; init; } = "";

    public ManuscriptFont Font { get; init; } = ManuscriptFont.TimesNewRoman;

    [JsonIgnore]
    public string EffectiveByline => !string.IsNullOrWhiteSpace(Byline) ? Byline.Trim() : LegalName.Trim();

    [JsonIgnore]
    public string EffectiveSurname
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Surname))
                return Surname.Trim();
            var words = EffectiveByline.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 0 ? words[^1] : "";
        }
    }

    /// <summary>The contact block's lines, blank lines dropped.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ContactLines =>
        Contact.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
}

/// <summary>A story laid out for submission: its title, who it's from and its scene files, in order.</summary>
public sealed record ManuscriptRequest(string Title, AuthorProfile Author, IReadOnlyList<MarkuaDocument> Scenes);

/// <summary>
/// Word counts as a submission states them. The count is of the words a reader sees (Markua markup, the
/// scene files' own headings and directives don't count). Standard Manuscript Format rounds a story's count
/// to the nearest hundred; flash fiction (under 1,000 words) gives the exact count, since markets' limits
/// for it are tight.
/// </summary>
public static class ManuscriptWordCount
{
    public const int FlashLimit = 1000;

    public static int Round(int words) =>
        words < FlashLimit ? words : (int)Math.Round(words / 100.0, MidpointRounding.AwayFromZero) * 100;

    /// <summary>"742 words", "about 3,400 words".</summary>
    public static string Describe(int words)
    {
        var rounded = Round(words);
        var number = rounded.ToString("N0", CultureInfo.InvariantCulture);
        if (words < FlashLimit)
            return words == 1 ? "1 word" : $"{number} words";
        return $"about {number} words";
    }

    /// <summary>The length category a word count falls in, by the SFWA/Nebula boundaries.</summary>
    public static string Category(int words) => words switch
    {
        < FlashLimit => "Flash fiction",
        < 7500 => "Short story",
        < 17500 => "Novelette",
        < 40000 => "Novella",
        _ => "Novel",
    };

    /// <summary>Counts the words in the scenes as they will be set (see <see cref="StandardManuscriptWriter"/>).</summary>
    public static int Count(IEnumerable<MarkuaDocument> scenes) =>
        scenes.Sum(scene => StandardManuscriptWriter.StoryText(scene)
            .Sum(text => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Count(token => token.Any(char.IsLetterOrDigit))));
}
