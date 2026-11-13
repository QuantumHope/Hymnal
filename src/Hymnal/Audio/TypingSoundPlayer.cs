using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Hymnal.Core.Audio;
using ReactiveUI;

namespace Hymnal.Audio;

/// <summary>Whether typing sounds can play right now.</summary>
public enum TypingSoundStatus
{
    /// <summary>Turned off in settings.</summary>
    Off,

    /// <summary>Turned on; opening the audio device and rendering the sounds.</summary>
    Starting,

    Ready,

    /// <summary>Turned on, but there is no usable audio output.</summary>
    Unavailable,
}

/// <summary>
/// Plays the typewriter sounds for keystrokes on the Write surface. Follows <see cref="SoundPreferences"/>:
/// when typing sounds are turned on it opens the audio engine and renders the theme's sounds in the
/// background; when they are turned off it closes the engine again, so the app holds no audio device.
/// </summary>
public sealed class TypingSoundPlayer : ReactiveObject, IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly SoundPreferences _preferences;
    private readonly Func<TimeSpan> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, TypingSoundBank> _banks = new(StringComparer.Ordinal);
    private readonly IDisposable _subscriptions;

    private Task _work = Task.CompletedTask;
    private string _themeId;
    private Dictionary<TypingSoundKind, IAudioClip[]>? _clips;
    private TypingSoundVoicer _voicer = new(1);
    private TimeSpan _lastKeystroke = TimeSpan.MinValue;
    private TypingSoundStatus _status;
    private bool _disposed;

    public TypingSoundPlayer(
        IAudioEngine engine,
        SoundPreferences preferences,
        string themeId = ThemeSoundProfiles.SynthwaveThemeId,
        Func<TimeSpan>? clock = null)
    {
        _engine = engine;
        _preferences = preferences;
        _themeId = themeId;
        var stopwatch = Stopwatch.StartNew();
        _clock = clock ?? (() => stopwatch.Elapsed);

        _subscriptions = new System.Reactive.Disposables.CompositeDisposable(
            preferences.WhenAnyValue(p => p.TypingVolume)
                .Subscribe(volume => _engine.SetBusGain(AudioBus.Effects, PerceivedGain(volume))),
            preferences.WhenAnyValue(p => p.TypingSoundsEnabled)
                .Subscribe(enabled =>
                {
                    if (enabled)
                        Start();
                    else
                        Shutdown();
                }));
    }

    public TypingSoundStatus Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>The theme whose typewriter is playing.</summary>
    public string ThemeId => _themeId;

    /// <summary>Completes when the last start, stop or theme change has finished.</summary>
    public Task WhenIdle
    {
        get { lock (_gate) return _work; }
    }

    /// <summary>
    /// Plays the sound for one keystroke. <paramref name="pan"/> places it left to right (see
    /// <see cref="TypingSoundVoicer.CarriagePan"/>). Does nothing while sounds are off or still starting.
    /// </summary>
    public void Play(TypingSoundKind kind, float pan = 0f)
    {
        Dictionary<TypingSoundKind, IAudioClip[]>? clips;
        TypingSoundVoice voice;
        lock (_gate)
        {
            clips = _clips;
            if (clips == null || !_preferences.TypingSoundsEnabled)
                return;

            var now = _clock();
            var since = _lastKeystroke == TimeSpan.MinValue ? TimeSpan.MaxValue : now - _lastKeystroke;
            _lastKeystroke = now;
            voice = _voicer.Next(kind, since);
        }

        var variants = clips[kind];
        _engine.Play(variants[voice.Variant % variants.Length],
            new PlaybackOptions(AudioBus.Effects, voice.Gain, voice.Pitch, pan));
    }

    /// <summary>
    /// Types a short phrase so the user can hear the current sounds and volume: a few words, a correction
    /// and a carriage return. Plays nothing while sounds are off.
    /// </summary>
    public async Task PreviewAsync()
    {
        await WhenIdle.ConfigureAwait(true);

        // Each keystroke and the milliseconds before it; the caret moves right along the line as it types.
        (TypingSoundKind Kind, int DelayMs)[] phrase =
        [
            (TypingSoundKind.Key, 0), (TypingSoundKind.Key, 140), (TypingSoundKind.Key, 110), (TypingSoundKind.Key, 165),
            (TypingSoundKind.Space, 150), (TypingSoundKind.Key, 210), (TypingSoundKind.Key, 120), (TypingSoundKind.Key, 95),
            (TypingSoundKind.Key, 140), (TypingSoundKind.Backspace, 330), (TypingSoundKind.Key, 260),
            (TypingSoundKind.Key, 130), (TypingSoundKind.Return, 380),
        ];

        for (var i = 0; i < phrase.Length; i++)
        {
            var (kind, delay) = phrase[i];
            if (delay > 0)
                await Task.Delay(delay).ConfigureAwait(true);
            var position = kind == TypingSoundKind.Return ? 0 : (i + 4) / 20.0;
            Play(kind, TypingSoundVoicer.CarriagePan(position));
        }
    }

    /// <summary>Switches to another theme's typewriter.</summary>
    public void SetTheme(string themeId)
    {
        lock (_gate)
        {
            if (_themeId == themeId)
                return;
            _themeId = themeId;
        }

        if (_preferences.TypingSoundsEnabled)
            Enqueue(LoadSounds);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _subscriptions.Dispose();
        lock (_gate)
            _clips = null;
        Enqueue(() => _engine.Stop());
    }

    /// <summary>Volume sliders feel even when the gain follows the square of the position.</summary>
    internal static float PerceivedGain(double volume) => (float)Math.Pow(Math.Clamp(volume, 0, 1), 2);

    private void Start()
    {
        SetStatus(TypingSoundStatus.Starting);
        Enqueue(() =>
        {
            if (!_preferences.TypingSoundsEnabled)
                return;

            if (!_engine.TryStart())
            {
                SetStatus(TypingSoundStatus.Unavailable);
                return;
            }

            _engine.SetBusGain(AudioBus.Effects, PerceivedGain(_preferences.TypingVolume));
            LoadSounds();
        });
    }

    private void Shutdown()
    {
        lock (_gate)
            _clips = null;
        SetStatus(TypingSoundStatus.Off);
        Enqueue(() =>
        {
            if (!_preferences.TypingSoundsEnabled)
                _engine.Stop();
        });
    }

    /// <summary>Renders (once per profile) and uploads the current theme's sounds. Runs on the work queue.</summary>
    private void LoadSounds()
    {
        if (!_engine.IsRunning)
            return;

        string themeId;
        lock (_gate)
            themeId = _themeId;

        var profile = ThemeSoundProfiles.ForTheme(themeId);
        if (!_banks.TryGetValue(profile.Id, out var bank))
            _banks[profile.Id] = bank = TypingSoundBank.Render(profile);

        var clips = new Dictionary<TypingSoundKind, IAudioClip[]>();
        foreach (var kind in TypingSoundBank.Kinds)
        {
            var renders = bank[kind];
            var uploaded = new IAudioClip[renders.Count];
            for (var i = 0; i < renders.Count; i++)
            {
                var clip = _engine.CreateClip(renders[i], bank.SampleRate);
                if (clip == null)
                {
                    SetStatus(TypingSoundStatus.Unavailable);
                    return;
                }

                uploaded[i] = clip;
            }

            clips[kind] = uploaded;
        }

        lock (_gate)
        {
            if (!_preferences.TypingSoundsEnabled || _disposed)
                return;
            _clips = clips;
            _voicer = new TypingSoundVoicer(profile.Variants);
        }

        SetStatus(TypingSoundStatus.Ready);
    }

    /// <summary>Runs engine work in order, off the UI thread (opening a device and rendering take a moment).</summary>
    private void Enqueue(Action action)
    {
        lock (_gate)
        {
            _work = _work.ContinueWith(_ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Typing sounds: {ex}");
                    SetStatus(TypingSoundStatus.Unavailable);
                }
            }, TaskScheduler.Default);
        }
    }

    private void SetStatus(TypingSoundStatus status)
    {
        // Background work can finish after the user has turned sounds off again: Off then wins.
        void Apply() => Status = status != TypingSoundStatus.Off && !_preferences.TypingSoundsEnabled
            ? TypingSoundStatus.Off
            : status;

        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }
}
