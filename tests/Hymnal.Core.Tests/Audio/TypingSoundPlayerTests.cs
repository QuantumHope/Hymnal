using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Audio;
using Hymnal.Core.Audio;
using Hymnal.ViewModels;
using Xunit;

namespace Hymnal.Core.Tests.Audio;

public sealed class TypingSoundPlayerTests
{
    static TypingSoundPlayerTests() => Hymnal.Core.Tests.Infrastructure.ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task WhileOff_NothingOpensTheAudioDevice_OrPlays()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync();
        using var player = new TypingSoundPlayer(engine, prefs);

        await player.WhenIdle;
        player.Play(TypingSoundKind.Key);

        Assert.Equal(0, engine.Starts);
        Assert.Empty(engine.Plays);
        Assert.Equal(TypingSoundStatus.Off, player.Status);
    }

    [Fact]
    public async Task TurningOn_OpensTheDevice_UploadsEveryVariant_AndPlays()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync();
        using var player = new TypingSoundPlayer(engine, prefs);

        prefs.TypingSoundsEnabled = true;
        await player.WhenIdle;

        Assert.Equal(TypingSoundStatus.Ready, player.Status);
        Assert.Equal(1, engine.Starts);
        Assert.Equal(TypingSoundBank.Kinds.Count * TypewriterProfile.Manual.Variants, engine.Clips.Count);

        player.Play(TypingSoundKind.Return, pan: -0.2f);
        var play = Assert.Single(engine.Plays);
        Assert.Equal(TypingSoundKind.Return, engine.KindOf(play.Clip));
        Assert.Equal(AudioBus.Effects, play.Options.Bus);
        Assert.Equal(-0.2f, play.Options.Pan);
        Assert.False(play.Options.Loop);
    }

    [Fact]
    public async Task TurningOff_ClosesTheDevice_AndSilencesKeystrokes()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync(enabled: true);
        using var player = new TypingSoundPlayer(engine, prefs);
        await player.WhenIdle;
        Assert.True(engine.IsRunning);

        prefs.TypingSoundsEnabled = false;
        player.Play(TypingSoundKind.Key);
        await player.WhenIdle;

        Assert.False(engine.IsRunning);
        Assert.Empty(engine.Plays);
        Assert.Equal(TypingSoundStatus.Off, player.Status);
    }

    [Fact]
    public async Task NoAudioOutput_ReportsUnavailable_AndStaysQuiet()
    {
        var engine = new FakeAudioEngine { CanStart = false };
        var (prefs, _) = await PreferencesAsync(enabled: true);
        using var player = new TypingSoundPlayer(engine, prefs);
        await player.WhenIdle;

        player.Play(TypingSoundKind.Key);

        Assert.Equal(TypingSoundStatus.Unavailable, player.Status);
        Assert.Empty(engine.Plays);
        Assert.Equal(SettingsViewModel.TypingSoundsUnavailable, SettingsViewModel.TypingSoundStatusText(player.Status));
    }

    [Fact]
    public async Task RepeatedKeystrokes_UseDifferentVariants()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync(enabled: true);
        using var player = new TypingSoundPlayer(engine, prefs);
        await player.WhenIdle;

        for (var i = 0; i < 40; i++)
            player.Play(TypingSoundKind.Key);

        var clips = engine.Plays.Select(p => p.Clip).ToList();
        for (var i = 1; i < clips.Count; i++)
            Assert.NotSame(clips[i - 1], clips[i]);
        Assert.True(clips.Distinct().Count() > 4);
        Assert.True(engine.Plays.Select(p => p.Options.Pitch).Distinct().Count() > 30);
    }

    [Fact]
    public async Task Volume_SetsTheEffectsBus_OnAPerceptualCurve()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync();
        using var player = new TypingSoundPlayer(engine, prefs);

        prefs.TypingVolume = 0.5;

        Assert.Equal(0.25f, engine.BusGains[AudioBus.Effects], 4);
        Assert.Equal(0f, TypingSoundPlayer.PerceivedGain(0));
        Assert.Equal(1f, TypingSoundPlayer.PerceivedGain(1));
    }

    [Fact]
    public async Task Preferences_SaveEveryChange_AndLoadThemNextTime()
    {
        var (prefs, store) = await PreferencesAsync();

        prefs.TypingSoundsEnabled = true;
        prefs.TypingVolume = 0.8;

        var reloaded = new SoundPreferences(new SoundSettingsService(store));
        await reloaded.Loaded;
        Assert.True(reloaded.TypingSoundsEnabled);
        Assert.Equal(0.8, reloaded.TypingVolume);
    }

    [Fact]
    public async Task SettingsViewModel_ExposesTheSoundPreferences_AndPreviewsOnlyWhileOn()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync();
        using var player = new TypingSoundPlayer(engine, prefs);
        var settings = new SettingsViewModel(
            NSubstitute.Substitute.For<Hymnal.Core.Interfaces.IProviderProfileService>(),
            NSubstitute.Substitute.For<Hymnal.Core.Interfaces.ICredentialStore>(),
            NSubstitute.Substitute.For<Hymnal.Core.Interfaces.IAiChatService>(),
            NSubstitute.Substitute.For<Hymnal.Core.Interfaces.INotificationService>(),
            sound: prefs,
            typingSounds: player);

        Assert.True(settings.HasSound);
        Assert.Same(prefs, settings.Sound);
        Assert.False(await FirstAsync(settings.PreviewTypingSoundsCommand.CanExecute));

        prefs.TypingSoundsEnabled = true;
        await player.WhenIdle;
        Assert.True(await FirstAsync(settings.PreviewTypingSoundsCommand.CanExecute));
        Assert.Null(settings.TypingSoundStatusMessage);

        await settings.PreviewTypingSoundsCommand.Execute().ToTask();
        var kinds = engine.Plays.Select(p => engine.KindOf(p.Clip)).ToList();
        Assert.Contains(TypingSoundKind.Key, kinds);
        Assert.Contains(TypingSoundKind.Space, kinds);
        Assert.Contains(TypingSoundKind.Backspace, kinds);
        Assert.Equal(TypingSoundKind.Return, kinds[^1]);
    }

    private static async Task<(SoundPreferences Prefs, InMemorySoundStore Store)> PreferencesAsync(bool enabled = false)
    {
        var store = new InMemorySoundStore();
        if (enabled)
            await store.SetAsync(SoundSettings.StoreKey, new SoundSettings { TypingSoundsEnabled = true });
        var prefs = new SoundPreferences(new SoundSettingsService(store));
        await prefs.Loaded;
        return (prefs, store);
    }

    private static Task<bool> FirstAsync(IObservable<bool> source) =>
        System.Reactive.Threading.Tasks.TaskObservableExtensions.ToTask(System.Reactive.Linq.Observable.FirstAsync(source));

    internal sealed class InMemorySoundStore : Hymnal.Core.Interfaces.IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new();

        public Task<T?> GetAsync<T>(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var v) && v is T t ? t : default);

        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeAudioEngine : IAudioEngine
    {
        private readonly object _gate = new();
        private int _generation;

        public bool CanStart { get; init; } = true;
        public int Starts { get; private set; }
        public bool IsRunning { get; private set; }
        public List<IAudioClip> Clips { get; } = new();
        public List<(IAudioClip Clip, PlaybackOptions Options)> Plays { get; } = new();
        public Dictionary<AudioBus, float> BusGains { get; } = new();

        /// <summary>Which kind a clip holds, recovered from upload order (kinds are uploaded in enum order).</summary>
        public TypingSoundKind KindOf(IAudioClip clip) =>
            TypingSoundBank.Kinds[Clips.IndexOf(clip) / TypewriterProfile.Manual.Variants];

        public bool TryStart()
        {
            lock (_gate)
            {
                Starts++;
                IsRunning = CanStart;
                return CanStart;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                IsRunning = false;
                _generation++;
                Clips.Clear();
            }
        }

        public IAudioClip? CreateClip(float[] samples, int sampleRate)
        {
            lock (_gate)
            {
                if (!IsRunning)
                    return null;
                var clip = new Clip(_generation, TimeSpan.FromSeconds(samples.Length / (double)sampleRate));
                Clips.Add(clip);
                return clip;
            }
        }

        public IAudioVoice? Play(IAudioClip clip, PlaybackOptions options)
        {
            lock (_gate)
            {
                if (!IsRunning || ((Clip)clip).Generation != _generation)
                    return null;
                Plays.Add((clip, options));
                return null;
            }
        }

        public void SetBusGain(AudioBus bus, float gain)
        {
            lock (_gate)
                BusGains[bus] = gain;
        }

        public void Dispose() => Stop();

        private sealed record Clip(int Generation, TimeSpan Duration) : IAudioClip;
    }
}
