using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Submission;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>Export for submission: which story is exported, its word count, and the remembered author.</summary>
[Collection("AvaloniaUi")]
public sealed class SubmissionExportViewModelTests
{
    static SubmissionExportViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string Lighthouse = "lighthouse.md";
    private const string LongPart = "long-story/part.md";
    private const string LongSceneA = "long-story/scene-a.md";
    private const string LongSceneB = "long-story/scene-b.md";

    private sealed class FakeSavePicker : ISaveFilePickerService
    {
        public string? Path { get; set; }
        public string? SuggestedName { get; private set; }

        public Task<string?> PickSaveFileAsync(string suggestedName, string? suggestedStartDirectory, string typeName, string extension)
        {
            SuggestedName = suggestedName;
            return Task.FromResult(Path);
        }
    }

    private static SubmissionExportViewModel Create(PlanTestContext context, FakeSavePicker picker) =>
        new(context.Workspace, new SubmissionExportService(), context.Settings, picker, context.Notifications,
            editor: context.Editor);

    private static string DocumentXml(string docx)
    {
        using var zip = ZipFile.OpenRead(docx);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Novel_offers_no_submission_export()
    {
        var root = CreateWorkspace(NovelJson, ("chapter-one.md", "# Chapter One\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var export = Create(context, new FakeSavePicker());
                Assert.False(export.IsAvailable);
                Assert.False(context.Workspace.Profile.SupportsSubmissionExport);
                Assert.False(await Within(export.PrepareAsync()));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_exports_the_story_a_scene_belongs_to_with_all_its_scenes()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var export = Create(context, new FakeSavePicker());

                Assert.True(await Within(export.PrepareAsync(context.Node(LongSceneB))));
                Assert.Equal("The Long Story", export.Title);
                Assert.Equal("2 words", export.WordCountDescription);
                Assert.Equal("Flash fiction · 2 words · 2 scenes", export.Summary);

                Assert.True(await Within(export.PrepareAsync(context.Node(Lighthouse))));
                Assert.Equal("Lighthouse", export.Title);
                Assert.Equal("Flash fiction · 3 words", export.Summary);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_with_nothing_selected_asks_for_a_story()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                context.Workspace.SelectedNode = null;
                var export = Create(context, new FakeSavePicker());

                Assert.False(await Within(export.PrepareAsync()));
                Assert.Contains(context.Infos, m => m.Contains("Select a story"));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Short_story_exports_every_scene_and_remembers_the_author()
    {
        var root = CreateWorkspace(StoryJson,
            ("one.md", "# Grounds for Termination\n\nThe build *passed*.\n"),
            ("two.md", "# Later\n\nIt failed.\n"));
        var output = Path.Combine(root, "out", "manuscript.docx");
        try
        {
            await Ui(root, 2, async context =>
            {
                var picker = new FakeSavePicker { Path = output };
                var export = Create(context, picker);

                Assert.True(await Within(export.PrepareAsync()));
                Assert.Equal("Grounds for Termination", export.Title);
                Assert.Equal("", export.LegalName);

                export.LegalName = "Jo Writer";
                export.Contact = "jo@example.com";
                export.UseCourier = true;
                Assert.Equal("Writer / Grounds for Termination / 2", export.HeaderPreview);

                Assert.Equal(output, await Within(export.ExportAsync()));
                Assert.Equal("Grounds for Termination - Writer.docx", picker.SuggestedName);
                Assert.True(export.HasExported);
                var xml = DocumentXml(output);
                Assert.Contains("The build ", xml);
                Assert.Contains("It failed.", xml);
                Assert.DoesNotContain(">Later<", xml);

                // A second export (another story, another day) starts from the remembered details.
                var again = Create(context, picker);
                Assert.True(await Within(again.PrepareAsync()));
                Assert.Equal("Jo Writer", again.LegalName);
                Assert.Equal("jo@example.com", again.Contact);
                Assert.True(again.UseCourier);
                Assert.False(again.HasExported);
                Assert.Equal(Path.GetDirectoryName(output),
                    await context.Settings.GetAsync<string>(SubmissionExportViewModel.FolderSettingsKey));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_writes_nothing()
    {
        var root = CreateWorkspace(StoryJson, ("one.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var export = Create(context, new FakeSavePicker { Path = null });

                Assert.True(await Within(export.PrepareAsync()));
                Assert.Null(await Within(export.ExportAsync()));
                Assert.False(export.HasExported);
                Assert.False(export.IsBusy);
                Assert.Empty(Directory.GetFiles(root, "*.docx", SearchOption.AllDirectories));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_missing_scene_stops_the_export_and_names_the_file()
    {
        var root = CreateWorkspace(StoryJson, ("one.md", "# Story\n\nFirst.\n"));
        // Book.txt lists a second scene that isn't on disk.
        File.WriteAllText(Path.Combine(root, "Book.txt"), "one.md\ntwo.md\n");
        try
        {
            await Ui(root, 2, async context =>
            {
                var export = Create(context, new FakeSavePicker());

                Assert.False(await Within(export.PrepareAsync()));
                Assert.Contains(context.Errors, m => m.Contains("two.md") && m.Contains("missing"));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_story_with_a_missing_scene_is_not_exported_short()
    {
        var root = CreateAnthology();
        File.WriteAllText(Path.Combine(root, "Book.txt"),
            $"{Lighthouse}\n{LongPart}\n{LongSceneA}\nlong-story/gone.md\n{LongSceneB}\n");
        try
        {
            await Ui(root, 5, async context =>
            {
                var export = Create(context, new FakeSavePicker());

                Assert.False(await Within(export.PrepareAsync(context.Node(LongSceneA))));
                Assert.Contains(context.Errors, m => m.Contains("long-story/gone.md"));
            });
        }
        finally { DeleteFolder(root); }
    }

    private static string CreateAnthology() =>
        CreateWorkspace(AnthologyJson,
            (Lighthouse, "# Lighthouse\n\nThe lamp turns.\n"),
            (LongPart, "{class: part}\n# The Long Story\n"),
            (LongSceneA, "# Scene A\n\nArrival.\n"),
            (LongSceneB, "# Scene B\n\nDeparture.\n"));
}
