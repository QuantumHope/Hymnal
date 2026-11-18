using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hymnal.Core.Audio;
using Hymnal.Core.Services.Runs;
using Hymnal.ViewModels;

namespace Hymnal.Audio;

/// <summary>What a writing run sounds like. The run drives it; implementations decide whether anything plays.</summary>
public interface IRunSoundscape
{
    /// <summary>A run began: start the background loop and the heartbeat.</summary>
    void Start();

    /// <summary>
    /// About once a second: how far through the run it is (0–1) and its intensity (<see cref="RunIntensity"/>).
    /// Together they set the heartbeat's pace (<see cref="RunIntensity.PulseBpm"/>) and the loop's volume.
    /// </summary>
    void Update(double progress, double intensity);

    /// <summary>The run is over. When the timer ran out, <paramref name="completed"/> plays the finish chime.</summary>
    void Finish(bool completed);

    /// <summary>A few seconds of what a run sounds like (Settings → Runs), so the volume can be checked.</summary>
    void Preview();
}

/// <summary>No sound (tests, and hosts without audio).</summary>
public sealed class SilentRunSoundscape : IRunSoundscape
{
    public void Start() { }
    public void Update(double progress, double intensity) { }
    public void Finish(bool completed) { }
    public void Preview() { }
}

/// <summary>
/// Plays a run's ambience on <see cref="AudioBus.Ambience"/>: the background loop (louder as the run nears its
/// end), a heartbeat that beats from the start, climbs slowly through the run and races through the final stretch,
/// and the finish chime. Plays only while Settings →
/// Sound and Settings → Runs → Ambience are both on. The engine is shared with typing sounds: if they restart it,
/// the loop is picked up again on the next intensity update, and the engine is closed afterwards only when typing
/// sounds aren't using it.
/// </summary>
public sealed class RunAmbiencePlayer : IRunSoundscape, IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly SoundPreferences _sound;
    private readonly RunAtmospherePreferences _atmosphere;
    private readonly Func<bool> _typingUsesEngine;
    private readonly object _gate = new();

    private Task _work = Task.CompletedTask;
    private RunAmbienceBank? _bank;
    private IAudioClip? _bedClip, _heartClip, _chimeClip;
    private IAudioVoice? _bed;
    private bool _active;
    private double _intensity = RunIntensity.Idle;
    private double _progress;
    private Timer? _heartbeat;
    private bool _disposed;
    private CancellationTokenSource? _preview;

    public RunAmbiencePlayer(IAudioEngine engine, SoundPreferences sound, RunAtmospherePreferences atmosphere,
        Func<bool> typingUsesEngine)
    {
        _engine = engine;
        _sound = sound;
        _atmosphere = atmosphere;
        _typingUsesEngine = typingUsesEngine;
    }

    private bool Wanted => _sound.SoundEnabled && _atmosphere.Ambience;

    public void Start()
    {
        CancelPreview();
        lock (_gate)
        {
            _active = true;
            _intensity = RunIntensity.Idle;
            _progress = 0;
        }
        Enqueue(EnsurePlaying);
    }

    public void Update(double progress, double intensity)
    {
        lock (_gate)
        {
            if (!_active)
                return;
            _intensity = intensity;
            _progress = progress;
        }
        Enqueue(EnsurePlaying);
    }

    public void Finish(bool completed)
    {
        CancelPreview();
        lock (_gate)
            _active = false;
        Enqueue(() =>
        {
            StopVoices();
            if (completed && Wanted && _engine.IsRunning && _chimeClip != null)
            {
                _engine.SetBusGain(AudioBus.Ambience, TypingSoundPlayer.PerceivedGain(_atmosphere.AmbienceVolume));
                _engine.Play(_chimeClip, new PlaybackOptions(AudioBus.Ambience, 0.9f));
                Thread.Sleep(TimeSpan.FromSeconds(3.3)); // let it ring out before the engine may close
            }
            ReleaseEngine();
        });
    }

    /// <summary>
    /// Plays the sample once. Clicks while one is queued or playing are ignored, and starting or ending a run (or
    /// disposing) cuts it short, so samples never hold up a run's own sounds.
    /// </summary>
    public void Preview()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_preview != null || _disposed)
                return;
            _preview = new CancellationTokenSource();
            token = _preview.Token;
        }

        Enqueue(() =>
        {
            try
            {
                PlayPreview(token);
            }
            finally
            {
                lock (_gate)
                {
                    _preview?.Dispose();
                    _preview = null;
                }
            }
        });
    }

    private void CancelPreview()
    {
        lock (_gate)
            _preview?.Cancel();
    }

    private void PlayPreview(CancellationToken token)
    {
        {
            if (token.IsCancellationRequested || !_sound.SoundEnabled || (!_engine.IsRunning && !_engine.TryStart()))
                return;

            // The fire and drone under four beats that quicken, then the chime.
            _engine.SetBusGain(AudioBus.Ambience, BusGain(_atmosphere.AmbienceVolume, 1));
            var bedOnce = new PlaybackOptions(AudioBus.Ambience, 1f);
            var loop = _bedClip == null ? null : _engine.Play(_bedClip, bedOnce);
            if (loop == null)
            {
                if (!UploadClips())
                    return;
                loop = _engine.Play(_bedClip!, bedOnce);
            }
            if (_heartClip is not { } heart || _chimeClip is not { } chime)
                return;
            foreach (var bpm in new[] { 60.0, 80, 110, 140 })
            {
                _engine.Play(heart, new PlaybackOptions(AudioBus.Ambience, HeartbeatGain(1)));
                if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(60 / bpm)))
                {
                    loop?.Stop();
                    return; // a run started or ended: its sounds take over
                }
            }
            loop?.Stop();
            var ring = _engine.Play(chime, new PlaybackOptions(AudioBus.Ambience, 0.9f));
            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3.3)))
            {
                ring?.Stop();
                return;
            }
            ReleaseEngine();
        }
    }

    public void Dispose()
    {
        CancelPreview();
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _active = false;
        }
        Enqueue(StopVoices);
    }

    /// <summary>Starts or keeps the loop playing at the current intensity. Runs on the work queue.</summary>
    private void EnsurePlaying()
    {
        double intensity;
        lock (_gate)
        {
            if (!_active || _disposed)
                return;
            intensity = _intensity;
        }

        if (!Wanted)
        {
            StopVoices();
            return;
        }

        if (!_engine.IsRunning && !_engine.TryStart())
            return;

        if (_bed is not { IsPlaying: true })
        {
            var loop = new PlaybackOptions(AudioBus.Ambience, 1f, Loop: true);
            // Reuse the uploaded clips while they are valid: the engine keeps every clip until it stops, so
            // uploading afresh for each run would pile up buffers while typing sounds keep the engine open.
            _bed = _bedClip == null ? null : _engine.Play(_bedClip, loop);
            if (_bed == null)
            {
                // Clips die with an engine restart, so upload them again.
                if (!UploadClips())
                    return;
                _bed = _engine.Play(_bedClip!, loop);
            }
        }

        // The loop swells to full volume as the run nears its end.
        _engine.SetBusGain(AudioBus.Ambience, BusGain(_atmosphere.AmbienceVolume, intensity));

        lock (_gate)
        {
            _heartbeat ??= new Timer(_ => Beat(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private void Beat()
    {
        double intensity, progress;
        lock (_gate)
        {
            if (!_active || _heartbeat == null)
                return;
            intensity = _intensity;
            progress = _progress;
        }

        // A quiet, steady beat under the whole run that grows louder as it races at the end.
        if (Wanted && _heartClip != null)
            _engine.Play(_heartClip, new PlaybackOptions(AudioBus.Ambience, HeartbeatGain(intensity)));

        var interval = TimeSpan.FromSeconds(60 / RunIntensity.PulseBpm(progress, intensity));
        lock (_gate)
            _heartbeat?.Change(interval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The ambience bus volume: the Settings slider, from about two thirds at the start to full at the end.</summary>
    internal static float BusGain(double volume, double intensity) =>
        TypingSoundPlayer.PerceivedGain(volume) * (float)(0.65 + 0.35 * Math.Clamp(intensity, 0, 1));

    /// <summary>Each beat's volume on that bus: steady under the run, full in the last seconds.</summary>
    internal static float HeartbeatGain(double intensity) => (float)(0.8 + 0.2 * Math.Clamp(intensity, 0, 1));

    /// <summary>Uploads the bed, heartbeat and chime. False when the bed couldn't be uploaded. Runs on the work queue.</summary>
    private bool UploadClips()
    {
        _bank ??= RunAmbienceBank.Render();
        _bedClip = _engine.CreateClip(_bank.Bed, _bank.SampleRate);
        _heartClip = _engine.CreateClip(_bank.Heartbeat, _bank.SampleRate);
        _chimeClip = _engine.CreateClip(_bank.Chime, _bank.SampleRate);
        return _bedClip != null;
    }

    private void StopVoices()
    {
        lock (_gate)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
        }
        _bed?.Stop();
        _bed = null;
    }

    /// <summary>Closes the engine when nothing else is using it.</summary>
    private void ReleaseEngine()
    {
        lock (_gate)
        {
            if (_active)
                return;
        }
        if (!_typingUsesEngine())
            _engine.Stop();
    }

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
                    Debug.WriteLine($"Run ambience: {ex}");
                }
            }, TaskScheduler.Default);
        }
    }
}
