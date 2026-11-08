using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using NSubstitute;

namespace Hymnal.Core.Tests.Services;

public class RefusingStoryMetadataStoreTests
{
    private static readonly StoryRef Story = new("uuid-1", "story.md", "/root/story.md");

    [Fact]
    public async Task Writes_are_refused_except_in_the_flow_that_was_permitted_until_the_permit_ends()
    {
        var inner = Substitute.For<IStoryMetadataStore>();
        inner.SaveBeatsAsync(default!, default!, default!).ReturnsForAnyArgs(Result<Unit>.Ok(Unit.Default));
        var store = new RefusingStoryMetadataStore(inner, "Moving.");

        Assert.Equal("Moving.", (await Assert.ThrowsAsync<IOException>(() => store.SaveNotesAsync("/root", Story, "a"))).Message);

        Task otherFlow;
        using (store.PermitWritesInThisFlow())
        {
            await store.SaveNotesAsync("/root", Story, "permitted");
            await store.SetTargetAsync("/root", Story, null);
            await store.UpsertPhaseDataAsync("/root", Story, _ => new PhaseData());
            Assert.True((await store.SaveBeatsAsync("/root", "short-story", [])).IsSuccess);
            // Awaited calls made from this flow carry the permit along.
            await Task.Run(() => store.SaveNotesAsync("/root", Story, "child"));

            using (ExecutionContext.SuppressFlow())
                otherFlow = Task.Run(() => store.SaveNotesAsync("/root", Story, "other"));
            await Assert.ThrowsAsync<IOException>(() => otherFlow);
        }

        await Assert.ThrowsAsync<IOException>(() => store.SaveNotesAsync("/root", Story, "after"));
        Assert.False((await store.SaveBeatsAsync("/root", "short-story", [])).IsSuccess);
        await inner.Received(1).SaveNotesAsync("/root", Story, "permitted");
        await inner.Received(1).SaveNotesAsync("/root", Story, "child");
        await inner.DidNotReceive().SaveNotesAsync("/root", Story, "other");
        await inner.DidNotReceive().SaveNotesAsync("/root", Story, "after");
        await inner.Received(1).SetTargetAsync("/root", Story, null);
        await inner.ReceivedWithAnyArgs(1).UpsertPhaseDataAsync(default!, default!, default!);
        await inner.ReceivedWithAnyArgs(1).SaveBeatsAsync(default!, default!, default!);
    }
}
