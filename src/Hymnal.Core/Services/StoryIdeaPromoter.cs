using System.Text;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>Turns a story idea into a new story file in an Anthology project.</summary>
public sealed class StoryIdeaPromoter
{
    private readonly IBookTxtStructureService _structure;
    private readonly ChapterRegistryService _registry;
    private readonly PhaseDataService _phases;
    private readonly TargetsService _targets;
    private readonly INotesService _notes;
    private readonly IStoryIdeasService _ideas;

    public StoryIdeaPromoter(
        IBookTxtStructureService structure,
        ChapterRegistryService registry,
        PhaseDataService phases,
        TargetsService targets,
        INotesService notes,
        IStoryIdeasService ideas)
    {
        _structure = structure;
        _registry = registry;
        _phases = phases;
        _targets = targets;
        _notes = notes;
        _ideas = ideas;
    }

    /// <summary>Builds a file name from a title: ASCII letters/digits only, other runs become '-'.</summary>
    public static string SuggestFileName(string title)
    {
        var slug = new StringBuilder();
        var pendingDash = false;
        foreach (var c in title)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                if (pendingDash && slug.Length > 0)
                    slug.Append('-');
                pendingDash = false;
                slug.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingDash = true;
            }
        }

        return slug.Length == 0 ? "untitled-story.md" : slug + ".md";
    }

    /// <summary>
    /// Creates the story file, registers it, and marks the idea used.
    /// Returns the story's path relative to the Book.txt directory.
    /// </summary>
    public async Task<Result<string>> PromoteAsync(
        string workspaceRoot, string bookTxtPath, StoryIdea idea, string fileName, ProjectProfile profile)
    {
        if (profile.Kind != ProjectKind.Anthology)
            return Result<string>.Fail("Ideas can only be promoted to stories in an Anthology project.");
        if (!IsBareMarkdownName(fileName))
            return Result<string>.Fail($"'{fileName}' is not a valid story file name. Use a plain name ending in .md.");

        var preload = await _ideas.LoadAsync(workspaceRoot).ConfigureAwait(false);
        if (!preload.IsSuccess)
            return Result<string>.Fail(preload.Error!);
        var persisted = preload.Value!.FirstOrDefault(i => i.Id == idea.Id);
        if (persisted is null)
            return Result<string>.Fail($"Idea '{idea.Title}' was not found in the ideas file.");
        if (persisted.State == IdeaState.Used)
            return Result<string>.Fail($"Idea '{persisted.Title}' has already been used.");
        idea = persisted;

        var entries = await _structure.ReadNormalizedEntriesAsync(bookTxtPath).ConfigureAwait(false);
        if (!entries.IsSuccess)
            return Result<string>.Fail(entries.Error!);

        var create = await _structure
            .CreateNewChapterAsync(bookTxtPath, fileName, $"# {idea.Title}\n\n", entries.Value!.Count)
            .ConfigureAwait(false);
        if (!create.IsSuccess)
            return Result<string>.Fail(create.Error!);

        try
        {
            var registry = await _registry.LoadAsync(workspaceRoot).ConfigureAwait(false);
            var (uuid, _) = _registry.AssignUuid(registry, fileName, idea.Title);
            await _registry.SaveAsync(workspaceRoot, registry).ConfigureAwait(false);

            await _phases.UpsertAsync(workspaceRoot, uuid, _ => PhaseDataService.DefaultPhaseDataFor(profile))
                .ConfigureAwait(false);

            var preset = profile.TargetPresets.FirstOrDefault(p => p.Key == idea.TargetPreset);
            if (preset is not null)
            {
                await _targets.UpsertAsync(workspaceRoot, uuid,
                    new WordCountTarget { MinWords = preset.MinWords, MaxWords = preset.MaxWords, Preset = preset.Key })
                    .ConfigureAwait(false);
            }

            var notes = string.Join("\n\n", new[] { idea.Premise, idea.Notes }.Where(s => !string.IsNullOrEmpty(s)));
            if (notes.Length > 0)
                await _notes.SaveAsync(INotesService.DeriveNotesPath(workspaceRoot, fileName), notes).ConfigureAwait(false);

            var updated = preload.Value!
                .Select(i => i.Id == idea.Id ? i with { State = IdeaState.Used, StoryId = uuid } : i)
                .ToList();
            var saved = await _ideas.SaveAsync(workspaceRoot, updated).ConfigureAwait(false);
            if (!saved.IsSuccess)
                return CreatedButFailed(fileName, saved.Error!);

            return Result<string>.Ok(fileName);
        }
        catch (Exception ex)
        {
            return CreatedButFailed(fileName, ex.Message);
        }
    }

    private static Result<string> CreatedButFailed(string fileName, string error) =>
        Result<string>.Fail($"Story file '{fileName}' was created but finishing setup failed: {error}");

    private static bool IsBareMarkdownName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.Length > ".md".Length
        && fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && fileName.IndexOfAny(['/', '\\']) < 0
        && !fileName.StartsWith('.')
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
