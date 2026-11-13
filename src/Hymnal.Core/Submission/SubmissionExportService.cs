using Hymnal.Core.Common;
using Hymnal.Core.Markua;
using Hymnal.Core.Services;

namespace Hymnal.Core.Submission;

/// <summary>A story read for submission: its scene files parsed, and the word count the manuscript states.</summary>
public sealed record SubmissionDraft(IReadOnlyList<MarkuaDocument> Scenes, int WordCount)
{
    public string WordCountDescription => ManuscriptWordCount.Describe(WordCount);
    public string Category => ManuscriptWordCount.Category(WordCount);
}

/// <summary>
/// Reads a story's scene files and writes its Standard Manuscript Format .docx (see
/// <see cref="StandardManuscriptWriter"/>). Only the prose is read: an embedded <c>hymnal:v1</c> block is
/// left out. The story files themselves are never written.
/// </summary>
public sealed class SubmissionExportService
{
    /// <summary>Reads and parses <paramref name="relativePaths"/> (Book.txt paths under the manuscript root).</summary>
    public async Task<Result<SubmissionDraft>> ReadAsync(string manuscriptRoot, IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
            return Result<SubmissionDraft>.Fail("This story has no files to export yet.");

        var scenes = new List<MarkuaDocument>();
        foreach (var relative in relativePaths)
        {
            var path = Path.Combine(manuscriptRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                scenes.Add(MarkuaParser.Parse(StoryFileFormat.Split(text).Prose));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result<SubmissionDraft>.Fail($"'{relative}' couldn't be read: {ex.Message}");
            }
        }
        return Result<SubmissionDraft>.Ok(new SubmissionDraft(scenes, ManuscriptWordCount.Count(scenes)));
    }

    /// <summary>
    /// Writes the manuscript to <paramref name="destination"/>, replacing it only once the new file is complete
    /// (a temp file beside it is moved into place), so a failed export never leaves half a document.
    /// </summary>
    public async Task<Result<string>> ExportAsync(ManuscriptRequest request, string destination)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return Result<string>.Fail("Give the story a title before exporting it.");

        var temp = destination + ".hymnal-tmp";
        try
        {
            var folder = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            var bytes = StandardManuscriptWriter.ToBytes(request);
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, destination, overwrite: true);
            return Result<string>.Ok(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            var hint = ex is IOException && File.Exists(destination) ? " Is it open in Word?" : "";
            return Result<string>.Fail($"The manuscript couldn't be saved: {ex.Message}{hint}");
        }
    }

    /// <summary>A file name for the manuscript: "Title - Surname.docx", without characters file systems refuse.</summary>
    public static string SuggestedFileName(string title, AuthorProfile author)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Manuscript" : title.Trim();
        if (author.EffectiveSurname.Length > 0)
            name += " - " + author.EffectiveSurname;
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        return (clean.Length > 0 ? clean : "Manuscript") + ".docx";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temp file; the next export replaces it.
        }
    }
}
