using System.IO.Compression;
using Hymnal.Core.Submission;

namespace Hymnal.Core.Tests.Submission;

public class SubmissionExportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hymnal-submission-" + Guid.NewGuid().ToString("N"));
    private readonly SubmissionExportService _service = new();

    public SubmissionExportServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task Read_LeavesOutTheEmbeddedMetadataBlock()
    {
        Write("story.md", "# Story\n\nOne two three.\n\n<!-- hymnal:v1\n{\"id\": \"abc\", \"notes\": \"secret words here\"}\n-->\n");

        var result = await _service.ReadAsync(_root, ["story.md"]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(3, result.Value!.WordCount);
        Assert.Equal("3 words", result.Value.WordCountDescription);
        Assert.Equal("Flash fiction", result.Value.Category);
    }

    [Fact]
    public async Task Read_FailsClearly_ForAMissingFileOrAnEmptyStory()
    {
        var missing = await _service.ReadAsync(_root, ["scenes/gone.md"]);
        var empty = await _service.ReadAsync(_root, []);

        Assert.False(missing.IsSuccess);
        Assert.Contains("scenes/gone.md", missing.Error);
        Assert.False(empty.IsSuccess);
    }

    [Fact]
    public async Task Export_WritesADocx_AndReplacesAnEarlierOne()
    {
        Write("a.md", "Some prose.");
        var draft = (await _service.ReadAsync(_root, ["a.md"])).Value!;
        var destination = Path.Combine(_root, "out", "Story - Writer.docx");
        var request = new ManuscriptRequest("Story", new AuthorProfile { LegalName = "Jo Writer" }, draft.Scenes);

        var first = await _service.ExportAsync(request, destination);
        var second = await _service.ExportAsync(request with { Title = "Story, Revised" }, destination);

        Assert.True(first.IsSuccess, first.Error);
        Assert.True(second.IsSuccess, second.Error);
        Assert.False(File.Exists(destination + ".hymnal-tmp"));
        using var zip = ZipFile.OpenRead(destination);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        Assert.Contains("Story, Revised", reader.ReadToEnd());
    }

    [Fact]
    public async Task Export_RefusesAnUntitledStory()
    {
        var result = await _service.ExportAsync(
            new ManuscriptRequest("  ", new AuthorProfile(), []), Path.Combine(_root, "x.docx"));

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(_root, "x.docx")));
    }

    [Fact]
    public async Task AuthorProfile_RoundTripsThroughTheAppSettingsStore()
    {
        var author = new AuthorProfile
        {
            LegalName = "Jo Writer", Byline = "J. W.", Surname = "Writer",
            Contact = "1 Road\njo@example.com", Font = ManuscriptFont.CourierNew,
        };
        using (var store = new Hymnal.Core.Infrastructure.AppSettingsStore(Path.Combine(_root, "settings.json")))
        {
            await store.SetAsync(AuthorProfile.SettingsKey, author);
            await store.FlushPendingAsync();
        }

        using var reopened = new Hymnal.Core.Infrastructure.AppSettingsStore(Path.Combine(_root, "settings.json"));
        var read = await reopened.GetAsync<AuthorProfile>(AuthorProfile.SettingsKey);
        Assert.NotNull(read);
        Assert.Equal(
            (author.LegalName, author.Byline, author.Surname, author.Contact, author.Font),
            (read.LegalName, read.Byline, read.Surname, read.Contact, read.Font));
        Assert.DoesNotContain("effective", File.ReadAllText(Path.Combine(_root, "settings.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Grounds for Termination", "Jo Writer", "Grounds for Termination - Writer.docx")]
    [InlineData("What? No: \"Really\"", "Jo Writer", "What No Really - Writer.docx")]
    [InlineData("", "", "Manuscript.docx")]
    public void SuggestedFileName_IsReadableAndSafe(string title, string legalName, string expected) =>
        Assert.Equal(expected, SubmissionExportService.SuggestedFileName(title, new AuthorProfile { LegalName = legalName }));
}
