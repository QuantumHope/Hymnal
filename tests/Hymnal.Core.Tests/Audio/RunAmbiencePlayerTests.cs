using System;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Audio;
using Hymnal.Core.Audio;
using Hymnal.ViewModels;
using Xunit;
using FakeAudioEngine = Hymnal.Core.Tests.Audio.TypingSoundPlayerTests.FakeAudioEngine;

namespace Hymnal.Core.Tests.Audio;

public sealed class RunAmbiencePlayerTests
{
    static RunAmbiencePlayerTests() => Hymnal.Core.Tests.Infrastructure.ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Sample_clicks_coalesce_and_starting_a_run_cuts_a_sample_short()
    {
        var engine = new FakeAudioEngine();
        var store = new TypingSoundPlayerTests.InMemorySoundStore();
        await store.SetAsync(SoundSettings.StoreKey, new SoundSettings { SoundEnabled = true });
        var prefs = new SoundPreferences(new SoundSettingsService(store));
        await prefs.Loaded;
        var player = new RunAmbiencePlayer(engine, prefs, new RunAtmospherePreferences(), () => false);
        try
        {
            // Three quick clicks: one sample (about six seconds) is queued, not three.
            player.Preview();
            player.Preview();
            player.Preview();
            await WaitUntil(() => SampleBeats(engine) >= 1);

            // A run starting cuts the sample short; its own loop starts at once rather than after the sample.
            var started = DateTime.UtcNow;
            player.Start();
            await WaitUntil(() => Plays(engine).Any(p => p.Options.Loop));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "the run's sound waited for the sample");

            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.InRange(SampleBeats(engine), 1, 2); // the sample stopped, and no queued second sample followed
        }
        finally
        {
            player.Finish(completed: false);
            player.Dispose();
        }
    }

    private static (IAudioClip Clip, PlaybackOptions Options)[] Plays(FakeAudioEngine engine)
    {
        lock (engine.Plays)
            return engine.Plays.ToArray();
    }

    /// <summary>The sample's beats: the short heartbeat clip at the sample's full beat volume.</summary>
    private static int SampleBeats(FakeAudioEngine engine) =>
        Plays(engine).Count(p => p.Clip.Duration < TimeSpan.FromSeconds(1)
                                 && Math.Abs(p.Options.Gain - RunAmbiencePlayer.HeartbeatGain(1)) < 0.001f);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition(), "timed out waiting");
    }
}
