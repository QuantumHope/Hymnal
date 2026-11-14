using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>The beat board as a book: numbered beats, and a page that turns from one to the next.</summary>
[Collection("AvaloniaUi")]
public sealed class BeatBoardPageTests
{
    static BeatBoardPageTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    [InlineData(40, "XL")]
    [InlineData(0, "")]
    public void Numerals_are_roman(int number, string numeral) =>
        Assert.Equal(numeral, BeatCardViewModel.ToRoman(number));

    [Fact]
    public async Task Beats_are_numbered_in_order_and_the_page_turns_between_them()
    {
        var root = CreateWorkspace(StoryJson, ("scene-one.md", "# Scene One\n\nDusk.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                context.CreateHost();
                var board = context.Board;
                await PumpUntilAsync(() => board.BoardKey == BeatBoardService.ShortStoryBoardKey);
                await Within(board.ApplyTemplateCommand
                    .Execute(new ApplyTemplateRequest("threeAct", TemplateApplyMode.Append, null)).ToTask());

                Assert.Equal([1, 2, 3, 4, 5], board.Beats.Select(b => b.Number));
                Assert.Equal("V", board.Beats[4].Numeral);

                board.Selected = board.Beats[0];
                Assert.Equal("BEAT I OF V", board.SelectedPosition);
                Assert.False(await board.SelectPreviousCommand.CanExecute.FirstAsyncTask());

                await Within(board.SelectNextCommand.Execute().ToTask());
                await Within(board.SelectNextCommand.Execute().ToTask());
                Assert.Same(board.Beats[2], board.Selected);
                Assert.Equal("BEAT III OF V", board.SelectedPosition);

                // Moving a beat renumbers the board.
                var moved = board.Beats[2];
                await Within(board.MoveBeatCommand.Execute((2, 0)).ToTask());
                Assert.Equal(1, moved.Number);
                Assert.Equal([1, 2, 3, 4, 5], board.Beats.Select(b => b.Number));

                board.Selected = board.Beats[4];
                Assert.False(await board.SelectNextCommand.CanExecute.FirstAsyncTask());
            });
        }
        finally { DeleteFolder(root); }
    }
}
