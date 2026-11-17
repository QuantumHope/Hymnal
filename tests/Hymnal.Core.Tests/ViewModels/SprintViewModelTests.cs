using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models.Sprints;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>One-click writing sprints: start card, timer in focus mode, recap, and the note to Future You.</summary>
[Collection("AvaloniaUi")]
public sealed class SprintViewModelTests
{
    static SprintViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private static readonly DateTimeOffset Evening = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);

    private static string ProfilePath(string root) => Path.Combine(root, ".test-writer-profile.json");

    private static DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(at.ToLocalTime().DateTime);

    private static async Task OpenChapterAsync(PlanTestContext context, string relativePath)
    {
        context.Workspace.SelectedNode = context.Node(relativePath);
        await PumpUntilAsync(() => context.Editor.ActiveNode?.RelativePath == relativePath);
    }

    private static async Task StartAsync(PlanTestContext context, SprintViewModel sprint, int minutes)
    {
        context.Scheduler.AdvanceTo(Evening);
        await Within(sprint.OpenCommand.Execute().ToTask());
        Assert.Equal(SprintStage.Choosing, sprint.Stage);
        await Within(sprint.SelectDurationCommand.Execute(minutes).ToTask());
        await Within(sprint.BeginCommand.Execute().ToTask());
        Assert.Equal(SprintStage.Running, sprint.Stage);
    }

    [Fact]
    public async Task A_sprint_runs_in_focus_mode_and_its_timer_ends_it_with_a_recorded_recap()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nOne two three.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var sprint = context.CreateSprint();
                var window = context.CreateMainWindow(context.CreateHost(), sprint: sprint);
                await OpenChapterAsync(context, "a.md");

                await StartAsync(context, sprint, 10);
                Assert.True(window.IsFocusMode);
                Assert.Equal(ShellMode.Write, window.ActiveMode);
                Assert.Equal("10:00", sprint.RemainingDisplay);

                context.Editor.Text += "Four five six seven.\n";
                Assert.Equal("+4 words", sprint.LiveNetDisplay);

                context.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5));
                Assert.Equal("05:00", sprint.RemainingDisplay);
                Assert.Equal(50, sprint.Progress, precision: 3);

                context.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5));
                await PumpUntilAsync(() => sprint.Stage == SprintStage.Recap);

                Assert.False(window.IsFocusMode);
                var recap = sprint.Recap!;
                Assert.Equal("SPRINT COMPLETE", recap.Headline);
                Assert.Equal("+4", recap.Added);
                Assert.Equal("−0", recap.Cut);
                Assert.Equal("+4", recap.Net);
                Assert.Equal("10 min", recap.Minutes);
                Assert.Equal("+79 XP", recap.Xp); // 4 added + 5 x 10 minutes + 25 for finishing
                Assert.Equal("Fresh drafting", recap.ChurnLabel);
                Assert.Equal("Streak started — day 1", recap.Streak);
                Assert.False(recap.HasProfileError);

                var sessions = (await new WriterProfileStore(new MetadataStore(), ProfilePath(root)).LoadSessionsAsync()).Value!;
                var session = Assert.Single(sessions);
                Assert.True(session.Completed);
                Assert.Equal(600, session.ActualSeconds);
                Assert.Equal(4, session.WordsAdded);
                Assert.Equal(LocalDate(Evening).ToString("yyyy-MM-dd"), session.LocalDate);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Edits_across_a_chapter_switch_count_for_the_chapter_they_were_made_in()
    {
        var root = CreateWorkspace(NovelJson,
            ("a.md", "# A\n\nAlpha beta\n"),
            ("b.md", "# B\n\nGamma delta epsilon\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var sprint = context.CreateSprint();
                context.CreateMainWindow(context.CreateHost(), sprint: sprint);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, sprint, 15);

                context.Editor.Text = "# A\n\nAlpha beta gamma delta\n";
                await OpenChapterAsync(context, "b.md");
                context.Editor.Text = "# B\n\nGamma epsilon zeta eta theta\n";
                Assert.Equal("+4 words", sprint.LiveNetDisplay); // +2 in A; +3 −1 in B

                await Within(sprint.EndCommand.Execute().ToTask());
                Assert.Equal(SprintStage.Recap, sprint.Stage);

                var recap = sprint.Recap!;
                Assert.Equal("SPRINT ENDED", recap.Headline);
                Assert.Equal("+5", recap.Added);
                Assert.Equal("−1", recap.Cut);
                Assert.Equal(
                    [new SprintRecapChapter("A", "+2", "+2 / −0"), new SprintRecapChapter("B", "+2", "+3 / −1")],
                    recap.Chapters);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_short_sprint_with_no_writing_is_discarded()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var sprint = context.CreateSprint();
                var window = context.CreateMainWindow(context.CreateHost(), sprint: sprint);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, sprint, 25);

                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(20));
                await Within(sprint.EndCommand.Execute().ToTask());

                Assert.Equal(SprintStage.Idle, sprint.Stage);
                Assert.False(window.IsFocusMode);
                Assert.Contains(context.Infos, m => m.Contains("wasn't recorded"));
                Assert.False(File.Exists(ProfilePath(root)));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_sprint_the_day_after_another_extends_the_streak()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            var profile = new WriterProfileStore(new MetadataStore(), ProfilePath(root));
            var yesterday = LocalDate(Evening).AddDays(-1).ToString("yyyy-MM-dd");
            await profile.AppendSessionAsync(new SprintSessionRecord
            {
                Id = "earlier", StartedAt = Evening.AddDays(-1), LocalDate = yesterday,
                PlannedSeconds = 900, ActualSeconds = 900, Completed = true, WordsAdded = 10, WordsNet = 10, Chapters = 1,
            });

            await Ui(root, 1, async context =>
            {
                var sprint = context.CreateSprint(profile);
                context.CreateMainWindow(context.CreateHost(), sprint: sprint);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, sprint, 10);

                context.Editor.Text += "More words.\n";
                await Within(sprint.EndCommand.Execute().ToTask());

                Assert.Equal("Streak extended to 2 days", sprint.Recap!.Streak);
                Assert.Equal("Best streak: 2 days", sprint.Recap.BestStreak);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task An_unreadable_profile_still_shows_the_recap_and_is_left_alone()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            File.WriteAllText(ProfilePath(root), "{ broken");
            await Ui(root, 1, async context =>
            {
                var sprint = context.CreateSprint();
                context.CreateMainWindow(context.CreateHost(), sprint: sprint);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, sprint, 10);

                context.Editor.Text += "More words.\n";
                await Within(sprint.EndCommand.Execute().ToTask());

                Assert.Equal(SprintStage.Recap, sprint.Stage);
                Assert.False(sprint.Recap!.HasStreak);
                Assert.True(sprint.Recap.HasProfileError);
            });
            Assert.Equal("{ broken", File.ReadAllText(ProfilePath(root)));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task The_note_to_Future_You_is_saved_and_greets_the_next_sprint()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# The Letter\n\nShe closed the door.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var sprint = context.CreateSprint();
                context.CreateMainWindow(context.CreateHost(), sprint: sprint);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, sprint, 10);

                context.Editor.Text += "\nThe envelope was still warm.\n";
                await Within(sprint.EndCommand.Execute().ToTask());
                await Within(sprint.ToFutureYouCommand.Execute().ToTask());

                Assert.Equal(SprintStage.FutureYou, sprint.Stage);
                Assert.Equal("The Letter", sprint.LeftOffChapter);
                Assert.Equal("The envelope was still warm.", sprint.LeftOffLine);
                Assert.False(await sprint.SaveNoteCommand.CanExecute.FirstAsync());

                sprint.FutureYouText = "Mara opens the letter and knows the handwriting.";
                await Within(sprint.SaveNoteCommand.Execute().ToTask());
                Assert.Equal(SprintStage.Idle, sprint.Stage);

                var saved = await new FutureYouNotesService(new MetadataStore()).LatestAsync(root);
                Assert.Equal("Mara opens the letter and knows the handwriting.", saved!.Text);
                Assert.Equal("a.md", saved.ChapterPath);

                await Within(sprint.OpenCommand.Execute().ToTask());
                Assert.True(sprint.HasPastYouNote);
                Assert.Equal(saved, sprint.PastYouNote);
                Assert.StartsWith("Earlier today · in The Letter", sprint.PastYouContext);
                Assert.Equal(10, sprint.SelectedMinutes); // the last length is remembered
            });
        }
        finally { DeleteFolder(root); }
    }
}
