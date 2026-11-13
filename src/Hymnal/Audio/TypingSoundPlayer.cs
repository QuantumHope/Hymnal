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
    /// <summary>Sound is off, or the typing sound is Silent.</summary>
    Off,

    /// <summary>Opening the audio device and rendering the sounds.</summary>
    Starting,

    Ready,

    /// <summary>Sound is on, but there is no usable audio output.</summary>
    Unavailable,
}

/// <summary>
/// Plays the typing sounds for keystrokes on the Write surface. Follows <see cref="SoundPreferences"/> and the
/// theme: while sound is on and the typing sound isn't Silent, it opens the audio engine and renders the chosen
/// scheme (or the theme's) in the background; otherwise it closes the engine again, so the app holds no audio
/// device it isn't using.
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
    private SoundScheme? _scheme;
    private SoundScheme? _loadedScheme;
    private Dictionary<TypingSoundKind, IAudioClip[]>? _clips;
    private TypingSoundVoicer _voicer = new(1);
    private TimeSpan _lastKeystroke = TimeSpan.MinValue;
    private TypingSoundStatus _status;
    private bool _disposed;

    public TypingSoundPlayer(
        IAudioEngine engine,
        SoundPreferences preferences,
        string themeId = ThemeSoundSchemes.SynthwaveThemeId,
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
            preferences.WhenAnyValue(p => p.SoundEnabled, p => p.TypingSound)
                .Subscribe(_ => Update()));
    }

    public TypingSoundStatus Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>The scheme keystrokes play; null while sound is off or the typing sound is Silent.</summary>
    public SoundScheme? Scheme
    {
        get => _scheme;
        private set => this.RaiseAndSetIfChanged(ref _scheme, value);
    }

    /// <summary>The scheme the current theme sounds like (what "Match the theme" plays).</summary>
    public SoundScheme ThemeScheme => ThemeSoundSchemes.ForTheme(_themeId);

    /// <summary>The theme the typing sound follows when it matches the theme.</summary>
    public string ThemeId => _themeId;

    /// <summary>Completes when the last start, stop or scheme change has finished.</summary>
    public Task WhenIdle
    {
        get { lock (_gate) return _work; }
    }

    /// <summary>
    /// Plays the sound for one keystroke. <paramref name="pan"/> places it left to right (see
    /// <see cref="TypingSoundVoicer.CarriagePan"/>). Does nothing while sound is off, silent or still starting.
    /// </summary>
    public void Play(TypingSoundKind kind, float pan = 0f)
    {
        Dictionary<TypingSoundKind, IAudioClip[]>? clips;
        TypingSoundVoice voice;
        lock (_gate)
        {
            clips = _clips;
            if (clips == null)
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
    /// Types a short phrase so the user can hear the current sound and volume: a few words, a correction and a
    /// carriage return. Plays nothing while sound is off or silent. Cancelling stops it between keystrokes.
    /// </summary>
    public async Task PreviewAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        await WhenIdle.WaitAsync(cancellationToken).ConfigureAwait(true);

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
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            var position = kind == TypingSoundKind.Return ? 0 : (i + 4) / 20.0;
            Play(kind, TypingSoundVoicer.CarriagePan(position));
        }
    }

    /// <summary>Follows another theme: when the typing sound matches the theme, switches to that theme's scheme.</summary>
    public void SetTheme(string themeId)
    {
        lock (_gate)
        {
            if (_themeId == themeId)
                return;
            _themeId = themeId;
        }

        this.RaisePropertyChanged(nameof(ThemeId));
        this.RaisePropertyChanged(nameof(ThemeScheme));
        Update();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _clips = null;
        }

        _subscriptions.Dispose();
        Enqueue(() => _engine.Stop());
    }

    /// <summary>Volume sliders feel even when the gain follows the square of the position.</summary>
    internal static float PerceivedGain(double volume) => (float)Math.Pow(Math.Clamp(volume, 0, 1), 2);

    /// <summary>The scheme the preferences and theme ask for; null for no sound.</summary>
    private SoundScheme? WantedScheme()
    {
        if (!_preferences.SoundEnabled)
            return null;
        lock (_gate)
            return SoundSettings.ResolveTypingSound(_preferences.TypingSound, _themeId);
    }

    /// <summary>Starts, switches or stops to match the preferences and theme.</summary>
    private void Update()
    {
        var wanted = WantedScheme();
        Scheme = wanted;

        if (wanted is null)
        {
            lock (_gate)
            {
                _clips = null;
                _loadedScheme = null;
            }

            SetStatus(TypingSoundStatus.Off);
            Enqueue(() =>
            {
                if (WantedScheme() is null)
                    _engine.Stop();
            });
            return;
        }

        lock (_gate)
        {
            if (_loadedScheme == wanted && _clips != null)
                return;
        }

        SetStatus(TypingSoundStatus.Starting);
        Enqueue(Load);
    }

    /// <summary>Opens the engine and uploads the wanted scheme's sounds. Runs on the work queue.</summary>
    private void Load()
    {
        var scheme = WantedScheme();
        if (scheme is null)
            return;

        lock (_gate)
        {
            if (_loadedScheme == scheme && _clips != null)
            {
                SetStatus(TypingSoundStatus.Ready);
                return;
            }
        }

        // A fresh start drops the clips of the previous scheme along with the device.
        _engine.Stop();
        if (!_engine.TryStart())
        {
            SetStatus(TypingSoundStatus.Unavailable);
            return;
        }

        _engine.SetBusGain(AudioBus.Effects, PerceivedGain(_preferences.TypingVolume));

        if (!_banks.TryGetValue(scheme.Id, out var bank))
            _banks[scheme.Id] = bank = TypingSoundBank.Render(scheme);

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
            // The choice changed while this was loading: the next queued load picks it up.
            if (_disposed || WantedScheme() != scheme)
                return;
            _clips = clips;
            _loadedScheme = scheme;
            _voicer = new TypingSoundVoicer(scheme.Variants);
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
        // Background work can finish after the user has turned sound off again: Off then wins.
        void Apply() => Status = status != TypingSoundStatus.Off && WantedScheme() is null
            ? TypingSoundStatus.Off
            : status;

        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }
}
