#:project ../../src/Hymnal.Core/Hymnal.Core.csproj
#:property PublishAot=false

// Renders Hymnal's typing sound schemes to WAV files, to listen to them while tuning the schemes in
// src/Hymnal.Core/Audio/SoundScheme.cs. For each scheme it writes every variant of every kind (key-0.wav,
// return-3.wav, ...) and typing.wav, a short typed phrase played through the same variation and stereo carriage
// pan the app uses.
//
// Run from the repo root:  dotnet run tools/TypewriterSounds/TypewriterSounds.cs -- [outDir] [schemeId]
// (defaults: artifacts/typewriter-sounds, every scheme). The output is not committed.

using System.Text;
using Hymnal.Core.Audio;

var root = args.Length > 0 ? args[0] : Path.Combine("artifacts", "typewriter-sounds");
IReadOnlyList<SoundScheme> schemes = args.Length > 1
    ? [SoundSchemes.Find(args[1]) ?? throw new ArgumentException(
        $"No scheme '{args[1]}'. Schemes: {string.Join(", ", SoundSchemes.All.Select(s => s.Id))}")]
    : SoundSchemes.All;
var rate = SoundVoice.SampleRate;

foreach (var scheme in schemes)
{
    var outDir = Path.Combine(root, scheme.Id);
    Directory.CreateDirectory(outDir);
    var bank = TypingSoundBank.Render(scheme);
    foreach (var kind in TypingSoundBank.Kinds)
    {
        for (var i = 0; i < bank[kind].Count; i++)
            WriteWav(Path.Combine(outDir, $"{kind.ToString().ToLowerInvariant()}-{i}.wav"), bank[kind][i], null);
    }

    WritePhrase(Path.Combine(outDir, "typing.wav"), bank);
    Console.WriteLine($"{scheme.Name}: {Path.GetFullPath(outDir)}");
}

// A phrase with a typo fixed by backspacing and two carriage returns, at an uneven human pace.
void WritePhrase(string path, TypingSoundBank bank)
{
    var rng = new Random(1);
    var voicer = new TypingSoundVoicer(bank.Scheme.Variants, seed: 1);
    var events = new List<(double AtMs, TypingSoundKind Kind, double Line)>();
    var t = 300.0;
    foreach (var line in new[] { "The quick brwn\b\b\bown fox\n", "jumps over the lazy dog.\n" })
    {
        var column = 0;
        foreach (var c in line)
        {
            var kind = c switch
            {
                '\b' => TypingSoundKind.Backspace,
                '\n' => TypingSoundKind.Return,
                ' ' => TypingSoundKind.Space,
                _ => TypingSoundKind.Key,
            };
            column = kind == TypingSoundKind.Backspace ? column - 1 : column + 1;
            events.Add((t, kind, kind == TypingSoundKind.Return ? 0 : column / 40.0));
            t += kind == TypingSoundKind.Return ? 900 : 80 + rng.NextDouble() * 160 + (kind == TypingSoundKind.Space ? 60 : 0);
        }
    }

    var left = new float[(int)((t + 2000) * rate / 1000)];
    var right = new float[left.Length];
    var previousAt = double.NaN;
    foreach (var (atMs, kind, line) in events)
    {
        var since = double.IsNaN(previousAt) ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(atMs - previousAt);
        previousAt = atMs;
        var voice = voicer.Next(kind, since);
        var samples = bank[kind][voice.Variant];
        // Equal-power pan, as OpenAL places a mono source.
        var angle = (TypingSoundVoicer.CarriagePan(line) + 1) * Math.PI / 4;
        var (gl, gr) = ((float)Math.Cos(angle), (float)Math.Sin(angle));
        var start = (int)(atMs * rate / 1000);
        // Resample for the pitch jitter (linear interpolation is plenty for a few percent).
        for (var n = 0; ; n++)
        {
            var src = n * voice.Pitch;
            var i = (int)src;
            if (i + 1 >= samples.Length || start + n >= left.Length)
                break;
            var s = (float)(samples[i] + (samples[i + 1] - samples[i]) * (src - i)) * voice.Gain;
            left[start + n] += s * gl;
            right[start + n] += s * gr;
        }
    }

    WriteWav(path, left, right);
}

void WriteWav(string path, float[] mono, float[]? second)
{
    var channels = second is null ? 1 : 2;
    using var w = new BinaryWriter(File.Create(path));
    var dataBytes = mono.Length * channels * 2;
    w.Write(Encoding.ASCII.GetBytes("RIFF"));
    w.Write(36 + dataBytes);
    w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    w.Write(16);
    w.Write((short)1);
    w.Write((short)channels);
    w.Write(rate);
    w.Write(rate * channels * 2);
    w.Write((short)(channels * 2));
    w.Write((short)16);
    w.Write(Encoding.ASCII.GetBytes("data"));
    w.Write(dataBytes);
    for (var i = 0; i < mono.Length; i++)
    {
        w.Write(Pcm(mono[i]));
        if (second is not null)
            w.Write(Pcm(second[i]));
    }
}

static short Pcm(float s) => (short)Math.Round(Math.Clamp(s, -1f, 1f) * short.MaxValue);
