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
    public async Task WhileSoundIsOff_NothingOpensTheAudioDevice_OrPlays()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync();
        using var player = new TypingSoundPlayer(engine, prefs);

        await player.WhenIdle;
        player.Play(TypingSoundKind.Key);

        Assert.Equal(0, engine.Starts);
        Assert.Empty(engine.Plays);
        Assert.Null(player.Scheme);
        Assert.Equal(TypingSoundStatus.Off, player.Status);
    }

    [Fact]
    public async Task TurningSoundOn_PlaysTheThemesScheme()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync();
        using var player = new TypingSoundPlayer(engine, prefs, ThemeSoundSchemes.SynthwaveThemeId);

        prefs.SoundEnabled = true;
        await player.WhenIdle;

        Assert.Equal(TypingSoundStatus.Ready, player.Status);
        Assert.Same(SoundSchemes.ElectricTypewriter, player.Scheme);
        Assert.Equal(TypingSoundBank.Kinds.Count * SoundSchemes.ElectricTypewriter.Variants, engine.Clips.Count);

        player.Play(TypingSoundKind.Return, pan: -0.2f);
        var play = Assert.Single(engine.Plays);
        Assert.Equal(TypingSoundKind.Return, engine.KindOf(play.Clip));
        Assert.Equal(AudioBus.Effects, play.Options.Bus);
        Assert.Equal(-0.2f, play.Options.Pan);
        Assert.False(play.Options.Loop);
    }

    [Fact]
    public async Task ChoosingAScheme_SwitchesToIt_AndMatchingTheThemeFollowsThemeChanges()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync(enabled: true);
        using var player = new TypingSoundPlayer(engine, prefs, ThemeSoundSchemes.SynthwaveThemeId);
        await player.WhenIdle;

        prefs.TypingSound = SoundSchemes.Forge.Id;
        await player.WhenIdle;
        Assert.Same(SoundSchemes.Forge, player.Scheme);
        Assert.Equal(TypingSoundStatus.Ready, player.Status);

        // A chosen scheme ignores the theme...
        player.SetTheme("cora-hc-dark");
        await player.WhenIdle;
        Assert.Same(SoundSchemes.Forge, player.Scheme);

        // ...matching the theme follows it.
        prefs.TypingSound = SoundSettings.MatchTheme;
        await player.WhenIdle;
        Assert.Same(SoundSchemes.SoftKeys, player.Scheme);
        player.SetTheme("mordor-scriptorium");
        await player.WhenIdle;
        Assert.Same(SoundSchemes.Forge, player.Scheme);
        Assert.Same(SoundSchemes.Forge, player.ThemeScheme);

        player.Play(TypingSoundKind.Key);
        Assert.Single(engine.Plays);
    }

    [Fact]
    public async Task SwitchingBackWhileAnotherSchemeLoads_ReloadsInsteadOfKeepingDeadClips()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync(enabled: true);
        using var player = new TypingSoundPlayer(engine, prefs, ThemeSoundSchemes.SynthwaveThemeId);
        await player.WhenIdle;

        // Choose Forge, and hold its load after it has stopped the engine (invalidating the theme's clips)...
        using var gate = new System.Threading.ManualResetEventSlim(false);
        engine.StartGate = gate;
        engine.StartEntered.Reset();
        prefs.TypingSound = SoundSchemes.Forge.Id;
        Assert.True(engine.StartEntered.Wait(TimeSpan.FromSeconds(10)));

        // ...then go back to the theme's scheme before it finishes.
        prefs.TypingSound = SoundSettings.MatchTheme;
        engine.StartGate = null;
        gate.Set();
        await player.WhenIdle;

        Assert.Same(SoundSchemes.ElectricTypewriter, player.Scheme);
        Assert.Equal(TypingSoundStatus.Ready, player.Status);
        player.Play(TypingSoundKind.Key);
        Assert.Single(engine.Plays);
    }

    [Theory]
    [InlineData(false, SoundSettings.MatchTheme)]
    [InlineData(true, SoundSettings.Silent)]
    public async Task SoundOffOrSilent_ClosesTheDevice_AndSilencesKeystrokes(bool soundEnabled, string typingSound)
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync(enabled: true);
        using var player = new TypingSoundPlayer(engine, prefs);
        await player.WhenIdle;
        Assert.True(engine.IsRunning);

        prefs.SoundEnabled = soundEnabled;
        prefs.TypingSound = typingSound;
        player.Play(TypingSoundKind.Key);
        await player.WhenIdle;

        Assert.False(engine.IsRunning);
        Assert.Empty(engine.Plays);
        Assert.Null(player.Scheme);
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
        Assert.Equal(SoundSettingsViewModel.Unavailable, SoundSettingsViewModel.StatusText(player.Status));
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

        prefs.SoundEnabled = true;
        prefs.TypingSound = "soft-keys";
        prefs.TypingVolume = 0.8;

        var reloaded = new SoundPreferences(new SoundSettingsService(store));
        await reloaded.Loaded;
        Assert.True(reloaded.SoundEnabled);
        Assert.Equal("soft-keys", reloaded.TypingSound);
        Assert.Equal(0.8, reloaded.TypingVolume);

        reloaded.TypingSound = "not-a-scheme";
        Assert.Equal(SoundSettings.MatchTheme, reloaded.TypingSound);
    }

    // ── Settings → Sound ─────────────────────────────────────────────────

    [Fact]
    public async Task SoundSection_OffersMatchTheme_Silent_AndEveryScheme()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync();
        using var player = new TypingSoundPlayer(engine, prefs, ThemeSoundSchemes.SynthwaveThemeId);
        using var section = new SoundSettingsViewModel(prefs, player);

        Assert.Equal(
            [SoundSettings.MatchTheme, SoundSettings.Silent, .. SoundSchemes.All.Select(s => s.Id)],
            section.TypingSoundOptions.Select(o => o.Id));
        Assert.Equal("Match the theme (Electric typewriter)", section.MatchThemeOption.Label);
        Assert.Same(section.MatchThemeOption, section.SelectedTypingSound);

        player.SetTheme("mordor-scriptorium");
        Assert.Equal("Match the theme (Forge)", section.MatchThemeOption.Label);
        Assert.Contains(SoundSchemes.Forge.Description, section.MatchThemeOption.Description);
    }

    [Fact]
    public async Task SoundSection_EnablesChoicesWhileSoundIsOn_AndVolumeWhileAudible()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync();
        using var player = new TypingSoundPlayer(engine, prefs);
        using var section = new SoundSettingsViewModel(prefs, player);

        Assert.False(section.CanChooseTypingSound);
        Assert.False(section.IsTypingSoundAudible);
        Assert.False(await FirstAsync(section.PreviewCommand.CanExecute));

        prefs.SoundEnabled = true;
        Assert.True(section.CanChooseTypingSound);
        Assert.True(section.IsTypingSoundAudible);
        Assert.True(await FirstAsync(section.PreviewCommand.CanExecute));

        section.SelectedTypingSound = section.TypingSoundOptions.Single(o => o.Id == SoundSettings.Silent);
        Assert.Equal(SoundSettings.Silent, prefs.TypingSound);
        Assert.True(section.CanChooseTypingSound);
        Assert.False(section.IsTypingSoundAudible);
        Assert.False(await FirstAsync(section.PreviewCommand.CanExecute));
    }

    [Fact]
    public async Task SoundSection_ChoosingASound_SavesIt_AndPlaysASampleOfIt()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync(enabled: true);
        using var player = new TypingSoundPlayer(engine, prefs);
        using var section = new SoundSettingsViewModel(prefs, player);
        await player.WhenIdle;

        section.SelectedTypingSound = section.TypingSoundOptions.Single(o => o.Id == SoundSchemes.MechanicalKeyboard.Id);
        Assert.Equal(SoundSchemes.MechanicalKeyboard.Id, prefs.TypingSound);

        await WaitUntil(() => engine.Plays.Count >= 13);
        var kinds = engine.Plays.Select(p => engine.KindOf(p.Clip)).ToList();
        Assert.Contains(TypingSoundKind.Key, kinds);
        Assert.Contains(TypingSoundKind.Space, kinds);
        Assert.Contains(TypingSoundKind.Backspace, kinds);
        Assert.Equal(TypingSoundKind.Return, kinds[^1]);
        Assert.Same(SoundSchemes.MechanicalKeyboard, player.Scheme);
    }

    [Fact]
    public async Task SoundSection_FollowsChangesMadeElsewhere_WithoutPreviewing()
    {
        var engine = new FakeAudioEngine();
        var (prefs, _) = await PreferencesAsync(enabled: true);
        using var player = new TypingSoundPlayer(engine, prefs);
        using var section = new SoundSettingsViewModel(prefs, player);

        prefs.TypingSound = SoundSchemes.SoftKeys.Id;
        await player.WhenIdle;
        await Task.Delay(300);

        Assert.Equal(SoundSchemes.SoftKeys.Id, section.SelectedTypingSound.Id);
        Assert.Empty(engine.Plays);
    }

    [Fact]
    public async Task SettingsViewModel_HasTheSoundSection_OnlyWhenTheHostHasSound()
    {
        var (prefs, _) = await PreferencesAsync();
        Assert.True(Settings(prefs).HasSound);
        Assert.False(Settings(null).HasSound);
        Assert.Null(Settings(null).Sound);
    }

    private static SettingsViewModel Settings(SoundPreferences? prefs) => new(
        NSubstitute.Substitute.For<Hymnal.Core.Interfaces.IProviderProfileService>(),
        NSubstitute.Substitute.For<Hymnal.Core.Interfaces.ICredentialStore>(),
        NSubstitute.Substitute.For<Hymnal.Core.Interfaces.IAiChatService>(),
        NSubstitute.Substitute.For<Hymnal.Core.Interfaces.INotificationService>(),
        sound: prefs);

    private static async Task<(SoundPreferences Prefs, InMemorySoundStore Store)> PreferencesAsync(bool enabled = false)
    {
        var store = new InMemorySoundStore();
        if (enabled)
            await store.SetAsync(SoundSettings.StoreKey, new SoundSettings { SoundEnabled = true });
        var prefs = new SoundPreferences(new SoundSettingsService(store));
        await prefs.Loaded;
        return (prefs, store);
    }

    private static Task<bool> FirstAsync(IObservable<bool> source) =>
        System.Reactive.Linq.Observable.FirstAsync(source).ToTask();

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition(), "timed out waiting");
    }

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

        /// <summary>Which kind a clip holds, recovered from upload order (kinds are uploaded in enum order, 8 variants each).</summary>
        public TypingSoundKind KindOf(IAudioClip clip)
        {
            lock (_gate)
                return TypingSoundBank.Kinds[Clips.IndexOf(clip) / 8];
        }

        /// <summary>While set, TryStart waits for it, so a test can change preferences mid-load.</summary>
        public System.Threading.ManualResetEventSlim? StartGate { get; set; }

        /// <summary>Signalled when TryStart is entered.</summary>
        public System.Threading.ManualResetEventSlim StartEntered { get; } = new();

        public bool TryStart()
        {
            StartEntered.Set();
            StartGate?.Wait(TimeSpan.FromSeconds(10));
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
