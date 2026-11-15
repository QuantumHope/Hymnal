using Hymnal.Core.Infrastructure;
using Hymnal.Core.Spelling;

namespace Hymnal.Core.Tests.Spelling;

public sealed class ProjectDictionaryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hymnal-projdict-tests", Guid.NewGuid().ToString("N"));
    private readonly ProjectDictionaryService _service = new(new MetadataStore());

    public ProjectDictionaryServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string FilePath => ProjectDictionaryService.PathFor(_root);

    [Fact]
    public async Task A_missing_file_is_an_empty_dictionary()
    {
        var loaded = await _service.LoadAsync(_root);

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value!);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Adding_creates_a_sorted_commented_file_in_hymnal_data()
    {
        await _service.AddAsync(_root, "wyvern");
        var result = await _service.AddAsync(_root, "  Elara ");
        await _service.AddAsync(_root, "Elara"); // duplicate

        Assert.True(result.IsSuccess);
        Assert.Equal(["Elara", "wyvern"], result.Value!);
        Assert.Equal(Path.Combine(_root, ".hymnal-data", "dictionary.txt"), FilePath);

        var text = await File.ReadAllTextAsync(FilePath);
        Assert.StartsWith("# Hymnal project dictionary", text);
        Assert.EndsWith("Elara\nwyvern\n", text);
    }

    [Fact]
    public async Task Removing_drops_the_exact_word()
    {
        await _service.AddAsync(_root, "Elara");
        await _service.AddAsync(_root, "elara");

        var result = await _service.RemoveAsync(_root, "Elara");

        Assert.Equal(["elara"], result.Value!);
        Assert.Equal(["elara"], (await _service.LoadAsync(_root)).Value!);
    }

    [Fact]
    public async Task Changes_keep_words_added_to_the_file_by_hand()
    {
        await _service.AddAsync(_root, "Elara");
        await File.AppendAllTextAsync(FilePath, "Thornwick\n# a note\n\n");

        var result = await _service.AddAsync(_root, "wyvern");

        Assert.Equal(["Elara", "Thornwick", "wyvern"], result.Value!);
    }

    [Theory]
    [InlineData("", "Enter a word")]
    [InlineData("   ", "Enter a word")]
    [InlineData("two words", "one word")]
    [InlineData("#tag", "#")]
    public async Task Rejects_what_is_not_one_word(string word, string message)
    {
        var result = await _service.AddAsync(_root, word);

        Assert.False(result.IsSuccess);
        Assert.Contains(message, result.Error);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Rejects_overlong_words()
    {
        Assert.NotNull(ProjectDictionaryService.Validate(new string('a', ProjectDictionaryService.MaxWordLength + 1)));
        Assert.Null(ProjectDictionaryService.Validate(new string('a', ProjectDictionaryService.MaxWordLength)));
    }

    [Fact]
    public void Parse_skips_comments_blanks_and_duplicates_and_handles_crlf()
    {
        var words = ProjectDictionaryService.Parse("# header\r\nzeta\r\n\r\nAlpha\r\nzeta\r\n  beta  \r\n");

        Assert.Equal(["Alpha", "beta", "zeta"], words);
    }

    [Fact]
    public void Format_round_trips()
    {
        var text = ProjectDictionaryService.Format(["b", "A", "a"]);

        Assert.Equal(["A", "a", "b"], ProjectDictionaryService.Parse(text));
    }
}
