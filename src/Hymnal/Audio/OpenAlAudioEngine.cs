using System;
using System.Collections.Generic;
using System.Diagnostics;
using Silk.NET.OpenAL;

namespace Hymnal.Audio;

/// <summary>
/// <see cref="IAudioEngine"/> over OpenAL Soft (bundled for Windows, Linux and macOS by Silk.NET). OpenAL mixes
/// in its own thread with low latency; this class keeps a fixed pool of sources, steals the oldest one-shot
/// when all are busy (a held-down key never piles up voices), and places each voice left-to-right by its pan.
/// </summary>
public sealed unsafe class OpenAlAudioEngine : IAudioEngine
{
    /// <summary>Enough for fast typing with long carriage returns ringing out, plus a few ambient loops.</summary>
    internal const int VoiceCount = 24;

    private readonly object _gate = new();
    private readonly float[] _busGains = [1f, 1f];

    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private Source[] _sources = [];
    private readonly List<uint> _buffers = new();
    private int _generation;
    private long _playSerial;

    public bool IsRunning
    {
        get { lock (_gate) return _context != null; }
    }

    public bool TryStart()
    {
        lock (_gate)
        {
            if (_context != null)
                return true;

            try
            {
                _alc = ALContext.GetApi(soft: true);
                _al = AL.GetApi(soft: true);

                _device = _alc.OpenDevice(string.Empty);
                if (_device == null)
                    return Fail("no audio output device");

                _context = _alc.CreateContext(_device, null);
                if (_context == null || !_alc.MakeContextCurrent(_context))
                    return Fail("could not create an audio context");

                // Voices are placed by direction only; distance never changes their level.
                _al.DistanceModel(DistanceModel.None);

                _sources = new Source[VoiceCount];
                for (var i = 0; i < VoiceCount; i++)
                {
                    var id = _al.GenSource();
                    _al.SetSourceProperty(id, SourceBoolean.SourceRelative, true);
                    _sources[i] = new Source(id);
                }

                if (_al.GetError() != AudioError.NoError)
                    return Fail("could not create audio voices");

                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                           or InvalidOperationException or PlatformNotSupportedException)
            {
                return Fail(ex.Message);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
            Shutdown();
    }

    public IAudioClip? CreateClip(float[] samples, int sampleRate)
    {
        lock (_gate)
        {
            if (_context == null || _al == null)
                return null;

            var pcm = new short[samples.Length];
            for (var i = 0; i < samples.Length; i++)
                pcm[i] = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);

            var buffer = _al.GenBuffer();
            _al.BufferData(buffer, BufferFormat.Mono16, pcm, sampleRate);
            if (_al.GetError() != AudioError.NoError)
            {
                _al.DeleteBuffer(buffer);
                return null;
            }

            _buffers.Add(buffer);
            return new Clip(buffer, _generation, TimeSpan.FromSeconds(samples.Length / (double)sampleRate));
        }
    }

    public IAudioVoice? Play(IAudioClip clip, PlaybackOptions options)
    {
        lock (_gate)
        {
            if (_context == null || _al == null || clip is not Clip c || c.Generation != _generation)
                return null;

            var source = PickSource();
            if (source == null)
                return null;

            var al = _al;
            al.SourceStop(source.Id);
            al.SetSourceProperty(source.Id, SourceInteger.Buffer, c.Buffer);
            al.SetSourceProperty(source.Id, SourceBoolean.Looping, options.Loop);
            al.SetSourceProperty(source.Id, SourceFloat.Pitch, Math.Clamp(options.Pitch, 0.5f, 2f));

            // A point on the unit circle in front of the listener: x is the pan.
            var pan = Math.Clamp(options.Pan, -1f, 1f);
            al.SetSourceProperty(source.Id, SourceVector3.Position, pan, 0f, -MathF.Sqrt(1 - pan * pan));

            source.Bus = options.Bus;
            source.Gain = Math.Max(0, options.Gain);
            source.Loop = options.Loop;
            source.Serial = ++_playSerial;
            ApplyGain(source);

            al.SourcePlay(source.Id);
            return new Voice(this, source, source.Serial);
        }
    }

    public void SetBusGain(AudioBus bus, float gain)
    {
        lock (_gate)
        {
            _busGains[(int)bus] = Math.Clamp(gain, 0f, 1f);
            if (_context == null)
                return;
            foreach (var source in _sources)
            {
                if (source.Bus == bus)
                    ApplyGain(source);
            }
        }
    }

    public void Dispose() => Stop();

    // ── Internals (called under _gate) ──────────────────────────────────

    /// <summary>A free source; otherwise the oldest one-shot. Loops are never stolen.</summary>
    private Source? PickSource()
    {
        Source? oldest = null;
        foreach (var source in _sources)
        {
            if (!IsSourcePlaying(source.Id))
                return source;
            if (!source.Loop && (oldest == null || source.Serial < oldest.Serial))
                oldest = source;
        }

        return oldest;
    }

    private bool IsSourcePlaying(uint id)
    {
        _al!.GetSourceProperty(id, GetSourceInteger.SourceState, out int state);
        return state == (int)SourceState.Playing;
    }

    private void ApplyGain(Source source) =>
        _al!.SetSourceProperty(source.Id, SourceFloat.Gain, source.Gain * _busGains[(int)source.Bus]);

    private void StopVoice(Source source, long serial)
    {
        lock (_gate)
        {
            if (_context != null && source.Serial == serial)
                _al!.SourceStop(source.Id);
        }
    }

    private bool IsVoicePlaying(Source source, long serial)
    {
        lock (_gate)
            return _context != null && source.Serial == serial && IsSourcePlaying(source.Id);
    }

    private bool Fail(string reason)
    {
        Debug.WriteLine($"Audio unavailable: {reason}");
        Shutdown();
        return false;
    }

    private void Shutdown()
    {
        if (_al != null && _context != null)
        {
            foreach (var source in _sources)
            {
                _al.SourceStop(source.Id);
                _al.DeleteSource(source.Id);
            }

            foreach (var buffer in _buffers)
                _al.DeleteBuffer(buffer);
        }

        _sources = [];
        _buffers.Clear();
        _generation++;

        if (_alc != null)
        {
            if (_context != null)
            {
                _alc.MakeContextCurrent(null);
                _alc.DestroyContext(_context);
            }

            if (_device != null)
                _alc.CloseDevice(_device);
        }

        _context = null;
        _device = null;
    }

    private sealed class Source(uint id)
    {
        public uint Id { get; } = id;
        public AudioBus Bus { get; set; }
        public float Gain { get; set; }
        public bool Loop { get; set; }
        public long Serial { get; set; }
    }

    private sealed record Clip(uint Buffer, int Generation, TimeSpan Duration) : IAudioClip;

    private sealed class Voice(OpenAlAudioEngine engine, Source source, long serial) : IAudioVoice
    {
        public bool IsPlaying => engine.IsVoicePlaying(source, serial);

        public void Stop() => engine.StopVoice(source, serial);
    }
}
