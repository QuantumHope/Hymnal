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
    /// Returns the story's path relative to the Book.txt directory; on failure,
    /// <see cref="PromoteResult.FileCreated"/> tells whether the story file was already created.
    /// </summary>
    public async Task<PromoteResult> PromoteAsync(
        string workspaceRoot, string bookTxtPath, StoryIdea idea, string fileName, ProjectProfile profile)
    {
        if (profile.Kind != ProjectKind.Anthology)
            return PromoteResult.Fail("Ideas can only be promoted to stories in an Anthology project.");
        if (!IsBareMarkdownName(fileName))
            return PromoteResult.Fail($"'{fileName}' is not a valid story file name. Use a plain name ending in .md.");

        var preload = await _ideas.LoadAsync(workspaceRoot).ConfigureAwait(false);
        if (!preload.IsSuccess)
            return PromoteResult.Fail(preload.Error!);
        var persisted = preload.Value!.FirstOrDefault(i => i.Id == idea.Id);
        if (persisted is null)
            return PromoteResult.Fail($"Idea '{idea.Title}' was not found in the ideas file.");
        if (persisted.State == IdeaState.Used)
            return PromoteResult.Fail($"Idea '{persisted.Title}' has already been used.");
        idea = persisted;

        var entries = await _structure.ReadNormalizedEntriesAsync(bookTxtPath).ConfigureAwait(false);
        if (!entries.IsSuccess)
            return PromoteResult.Fail(entries.Error!);

        var create = await _structure
            .CreateNewChapterAsync(bookTxtPath, fileName, $"# {idea.Title}\n\n", entries.Value!.Count)
            .ConfigureAwait(false);
        if (!create.IsSuccess)
            return PromoteResult.Fail(create.Error!);

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

            // Re-read so ideas written since the pre-flight load survive; change only this idea.
            var current = await _ideas.LoadAsync(workspaceRoot).ConfigureAwait(false);
            if (!current.IsSuccess)
                return CreatedButFailed(fileName, current.Error!);
            var ideaId = idea.Id;
            var latest = current.Value!.FirstOrDefault(i => i.Id == ideaId);
            if (latest is null)
                return CreatedButFailed(fileName, $"idea '{idea.Title}' is no longer in the ideas file");
            // Another writer promoted it meanwhile: keep their StoryId rather than overwrite it.
            if (latest.State == IdeaState.Used || latest.StoryId is not null)
                return CreatedButFailed(fileName, $"idea '{latest.Title}' was used by another story meanwhile");
            var updated = current.Value!
                .Select(i => i.Id == ideaId ? i with { State = IdeaState.Used, StoryId = uuid } : i)
                .ToList();
            var saved = await _ideas.SaveAsync(workspaceRoot, updated).ConfigureAwait(false);
            if (!saved.IsSuccess)
                return CreatedButFailed(fileName, saved.Error!);

            return PromoteResult.Ok(fileName);
        }
        catch (Exception ex)
        {
            return CreatedButFailed(fileName, ex.Message);
        }
    }

    private static PromoteResult CreatedButFailed(string fileName, string error) =>
        PromoteResult.FailAfterCreate($"Story file '{fileName}' was created but finishing setup failed: {error}");

    private static bool IsBareMarkdownName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.Length > ".md".Length
        && fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && fileName.IndexOfAny(['/', '\\']) < 0
        && !fileName.StartsWith('.')
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
