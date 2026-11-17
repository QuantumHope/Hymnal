using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models.Runs;
using Hymnal.Core.Services;
using Hymnal.Core.Services.Runs;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>One-click writing runs: start card, timer in focus mode, recap, and the note to Future You.</summary>
[Collection("AvaloniaUi")]
public sealed class RunViewModelTests
{
    static RunViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private static readonly DateTimeOffset Evening = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);

    private static string ProfilePath(string root) => Path.Combine(root, ".test-writer-profile.json");

    private static DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(at.ToLocalTime().DateTime);

    private static async Task OpenChapterAsync(PlanTestContext context, string relativePath)
    {
        context.Workspace.SelectedNode = context.Node(relativePath);
        await PumpUntilAsync(() => context.Editor.ActiveNode?.RelativePath == relativePath);
    }

    private static async Task StartAsync(PlanTestContext context, RunViewModel run, int minutes)
    {
        context.Scheduler.AdvanceTo(Evening);
        await Within(run.OpenCommand.Execute().ToTask());
        Assert.Equal(RunStage.Choosing, run.Stage);
        await Within(run.SelectDurationCommand.Execute(minutes).ToTask());
        await Within(run.BeginCommand.Execute().ToTask());
        Assert.Equal(RunStage.Running, run.Stage);
    }

    [Fact]
    public async Task A_run_runs_in_focus_mode_and_its_timer_ends_it_with_a_recorded_recap()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nOne two three.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun();
                var window = context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");

                await StartAsync(context, run, 10);
                Assert.True(window.IsFocusMode);
                Assert.Equal(ShellMode.Write, window.ActiveMode);
                Assert.Equal("10:00", run.RemainingDisplay);

                context.Editor.Text += "Four five six seven.\n";
                Assert.Equal("+4 words", run.LiveNetDisplay);

                context.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5));
                Assert.Equal("05:00", run.RemainingDisplay);
                Assert.Equal(50, run.Progress, precision: 3);

                context.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5));
                await PumpUntilAsync(() => run.Stage == RunStage.Recap);

                Assert.False(window.IsFocusMode);
                var recap = run.Recap!;
                Assert.Equal("RUN COMPLETE", recap.Headline);
                Assert.Equal("+4", recap.Added);
                Assert.Equal("−0", recap.Cut);
                Assert.Equal("+4", recap.Net);
                Assert.Equal("10 min", recap.Minutes);
                Assert.Equal("+79 XP", recap.Xp); // 4 added + 5 x 10 minutes + 25 for finishing
                Assert.Equal("Fresh drafting", recap.ChurnLabel);
                Assert.Equal("Streak started — day 1", recap.Streak);
                Assert.False(recap.HasProfileError);

                var sessions = (await new WriterProfileStore(new MetadataStore(), ProfilePath(root)).LoadRunsAsync()).Value!;
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
                var run = context.CreateRun();
                context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, run, 15);

                context.Editor.Text = "# A\n\nAlpha beta gamma delta\n";
                await OpenChapterAsync(context, "b.md");
                context.Editor.Text = "# B\n\nGamma epsilon zeta eta theta\n";
                Assert.Equal("+4 words", run.LiveNetDisplay); // +2 in A; +3 −1 in B

                await Within(run.EndCommand.Execute().ToTask());
                Assert.Equal(RunStage.Recap, run.Stage);

                var recap = run.Recap!;
                Assert.Equal("RUN ENDED", recap.Headline);
                Assert.Equal("+5", recap.Added);
                Assert.Equal("−1", recap.Cut);
                Assert.Equal(
                    [new RunRecapChapter("A", "+2", "+2 / −0"), new RunRecapChapter("B", "+2", "+3 / −1")],
                    recap.Chapters);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_short_run_with_no_writing_is_discarded()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun();
                var window = context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, run, 25);

                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(20));
                await Within(run.EndCommand.Execute().ToTask());

                Assert.Equal(RunStage.Idle, run.Stage);
                Assert.False(window.IsFocusMode);
                Assert.Contains(context.Infos, m => m.Contains("wasn't recorded"));
                Assert.False(File.Exists(ProfilePath(root)));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_run_the_day_after_another_extends_the_streak()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            var profile = new WriterProfileStore(new MetadataStore(), ProfilePath(root));
            var yesterday = LocalDate(Evening).AddDays(-1).ToString("yyyy-MM-dd");
            await profile.AppendRunAsync(new RunRecord
            {
                Id = "earlier", StartedAt = Evening.AddDays(-1), LocalDate = yesterday,
                PlannedSeconds = 900, ActualSeconds = 900, Completed = true, WordsAdded = 10, WordsNet = 10, Chapters = 1,
            });

            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun(profile);
                context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, run, 10);

                context.Editor.Text += "More words.\n";
                await Within(run.EndCommand.Execute().ToTask());

                Assert.Equal("Streak extended to 2 days", run.Recap!.Streak);
                Assert.Equal("Best streak: 2 days", run.Recap.BestStreak);
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
                var run = context.CreateRun();
                context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, run, 10);

                context.Editor.Text += "More words.\n";
                await Within(run.EndCommand.Execute().ToTask());

                Assert.Equal(RunStage.Recap, run.Stage);
                Assert.False(run.Recap!.HasStreak);
                Assert.True(run.Recap.HasProfileError);
            });
            Assert.Equal("{ broken", File.ReadAllText(ProfilePath(root)));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task The_note_to_Future_You_is_saved_and_greets_the_next_run()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# The Letter\n\nShe closed the door.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun();
                context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, run, 10);

                context.Editor.Text += "\nThe envelope was still warm.\n";
                await Within(run.EndCommand.Execute().ToTask());
                await Within(run.ToFutureYouCommand.Execute().ToTask());

                Assert.Equal(RunStage.FutureYou, run.Stage);
                Assert.Equal("The Letter", run.LeftOffChapter);
                Assert.Equal("The envelope was still warm.", run.LeftOffLine);
                Assert.False(await run.SaveNoteCommand.CanExecute.FirstAsync());

                run.FutureYouText = "Mara opens the letter and knows the handwriting.";
                await Within(run.SaveNoteCommand.Execute().ToTask());
                Assert.Equal(RunStage.Idle, run.Stage);

                var saved = await new FutureYouNotesService(new MetadataStore()).LatestAsync(root);
                Assert.Equal("Mara opens the letter and knows the handwriting.", saved!.Text);
                Assert.Equal("a.md", saved.ChapterPath);

                await Within(run.OpenCommand.Execute().ToTask());
                Assert.True(run.HasPastYouNote);
                Assert.Equal(saved, run.PastYouNote);
                Assert.StartsWith("Earlier today · in The Letter", run.PastYouContext);
                Assert.Equal(10, run.SelectedMinutes); // the last length is remembered
            });
        }
        finally { DeleteFolder(root); }
    }

    private sealed class RecordingSoundscape : Hymnal.Audio.IRunSoundscape
    {
        public int Starts;
        public readonly List<double> Intensities = new();
        public bool? FinishedCompleted;

        public void Start() => Starts++;
        public void SetIntensity(double intensity) => Intensities.Add(intensity);
        public void Finish(bool completed) => FinishedCompleted = completed;
    }

    [Fact]
    public async Task The_dial_takes_any_length_in_range_and_remembers_it()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun();
                context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");
                context.Scheduler.AdvanceTo(Evening);
                await Within(run.OpenCommand.Execute().ToTask());

                run.SelectedMinutes = 500;
                Assert.Equal(RunViewModel.MaxMinutes, run.SelectedMinutes);
                run.SelectedMinutes = 0;
                Assert.Equal(RunViewModel.MinMinutes, run.SelectedMinutes);

                run.SelectedMinutes = 32;
                await Within(run.NudgeMinutesCommand.Execute(5).ToTask());
                Assert.Equal(37, run.SelectedMinutes);
                Assert.DoesNotContain(run.DurationOptions, o => o.IsSelected); // no preset is 37

                await Within(run.BeginCommand.Execute().ToTask());
                Assert.Equal("37:00", run.RemainingDisplay);
                await Within(run.EndCommand.Execute().ToTask()); // nothing written: discarded

                await Within(run.OpenCommand.Execute().ToTask());
                Assert.Equal(37, run.SelectedMinutes);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task The_final_minute_ramps_up_and_a_full_run_ends_in_a_celebration()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var sound = new RecordingSoundscape();
                var run = context.CreateRun(soundscape: sound);
                context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, run, 10);
                Assert.Equal(1, sound.Starts);

                context.Scheduler.AdvanceBy(TimeSpan.FromMinutes(8));
                Assert.False(run.IsFinalStretch);
                Assert.Equal(RunIntensity.Idle, run.Intensity);

                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(90)); // 30 s left
                Assert.True(run.IsFinalStretch);
                Assert.InRange(run.Intensity, RunIntensity.Idle + 0.01, 0.99);
                Assert.Equal(run.Intensity, sound.Intensities[^1]);

                context.Editor.Text += "One more line.\n";
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(30));
                await PumpUntilAsync(() => run.Stage == RunStage.Recap);

                Assert.True(sound.FinishedCompleted);
                Assert.Equal(1, run.Celebrations);
                Assert.False(run.IsFinalStretch);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Words_throw_sparks_and_combos_are_called_out_for_a_moment()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun();
                context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, run, 15);

                context.Editor.Text += "one two three\n";
                Assert.Equal(3, run.Sparks);
                Assert.False(run.HasCombo);

                context.Editor.Text += string.Join(" ", Enumerable.Repeat("word", 50)) + "\n";
                Assert.Equal(53, run.Sparks);
                Assert.Equal("COMBO · 50 WORDS", run.ComboText);

                context.Editor.Text = "# A\n\nText.\n"; // cutting throws no sparks
                Assert.Equal(53, run.Sparks);

                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(4));
                Assert.False(run.HasCombo);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task The_Logbook_lists_each_run_with_the_note_left_after_it()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun();
                context.CreateMainWindow(context.CreateHost(), run: run);
                await OpenChapterAsync(context, "a.md");
                await StartAsync(context, run, 10);

                context.Editor.Text += "Rain on the glass.\n";
                await Within(run.EndCommand.Execute().ToTask());
                await Within(run.ToFutureYouCommand.Execute().ToTask());
                run.FutureYouText = "Open with the knock at the door.";
                await Within(run.SaveNoteCommand.Execute().ToTask());

                var recorded = Assert.Single((await new WriterProfileStore(new MetadataStore(), ProfilePath(root)).LoadRunsAsync()).Value!);
                Assert.Equal(root, recorded.WorkspacePath);

                var logbook = await Within(run.ShowLogbookCommand.Execute().ToTask());
                Assert.False(logbook.HasError);
                Assert.Equal("1", logbook.CurrentStreak);
                var row = Assert.Single(logbook.Rows);
                Assert.Equal("+4", row.Net);
                Assert.Equal("Open with the knock at the door.", row.Note);
                Assert.Equal(84, logbook.Cells.Count);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task An_unreadable_profile_shows_the_Logbook_error()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nText.\n"));
        try
        {
            File.WriteAllText(ProfilePath(root), "{ broken");
            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun();
                var logbook = await Within(run.ShowLogbookCommand.Execute().ToTask());
                Assert.True(logbook.HasError);
                Assert.False(logbook.HasRuns);
                Assert.False(logbook.IsEmpty);
            });
        }
        finally { DeleteFolder(root); }
    }
}
