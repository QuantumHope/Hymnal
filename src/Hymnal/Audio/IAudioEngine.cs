using System;

namespace Hymnal.Audio;

/// <summary>
/// Mix groups with their own volume. Typing sounds play on <see cref="Effects"/>; <see cref="Ambience"/> is for
/// looping background audio, so the two can be balanced separately.
/// </summary>
public enum AudioBus
{
    Effects,
    Ambience,
}

/// <summary>How to play a clip. <paramref name="Pan"/> runs from -1 (left) to 1 (right).</summary>
public readonly record struct PlaybackOptions(
    AudioBus Bus = AudioBus.Effects,
    float Gain = 1f,
    float Pitch = 1f,
    float Pan = 0f,
    bool Loop = false);

/// <summary>Mono audio uploaded to the engine. Valid until the engine stops.</summary>
public interface IAudioClip
{
    TimeSpan Duration { get; }
}

/// <summary>A clip that is playing. A one-shot can be cut off early when every voice is busy.</summary>
public interface IAudioVoice
{
    bool IsPlaying { get; }

    void Stop();
}

/// <summary>
/// The app's audio output. Nothing is opened until <see cref="TryStart"/>, so the app holds no audio device
/// while sound is off. All members are thread-safe.
/// </summary>
public interface IAudioEngine : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Opens the default output device. False when there is no usable device or audio library.</summary>
    bool TryStart();

    /// <summary>Stops every voice and closes the device. Clips created before this are no longer valid.</summary>
    void Stop();

    /// <summary>Uploads mono samples (-1 to 1). Null when the engine isn't running.</summary>
    IAudioClip? CreateClip(float[] samples, int sampleRate);

    /// <summary>Plays a clip. Null when the engine isn't running or the clip belongs to an earlier start.</summary>
    IAudioVoice? Play(IAudioClip clip, PlaybackOptions options);

    /// <summary>Sets a bus's volume (0 to 1), including for voices already playing on it.</summary>
    void SetBusGain(AudioBus bus, float gain);
}

/// <summary>An engine with no output, for hosts without audio (tests, headless runs).</summary>
public sealed class NullAudioEngine : IAudioEngine
{
    public bool IsRunning => false;

    public bool TryStart() => false;

    public void Stop() { }

    public IAudioClip? CreateClip(float[] samples, int sampleRate) => null;

    public IAudioVoice? Play(IAudioClip clip, PlaybackOptions options) => null;

    public void SetBusGain(AudioBus bus, float gain) { }

    public void Dispose() { }
}
