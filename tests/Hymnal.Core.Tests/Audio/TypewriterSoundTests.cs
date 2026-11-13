using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Audio;
using Hymnal.Core.Infrastructure;
using Xunit;

namespace Hymnal.Core.Tests.Audio;

public sealed class TypewriterSoundTests
{
    // ── Classifier ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("a", TypingSoundKind.Key)]
    [InlineData("É", TypingSoundKind.Key)]
    [InlineData(".", TypingSoundKind.Key)]
    [InlineData("ab", TypingSoundKind.Key)]
    [InlineData(" ", TypingSoundKind.Space)]
    [InlineData("\t", TypingSoundKind.Space)]
    [InlineData("\n", TypingSoundKind.Return)]
    [InlineData("\r\n", TypingSoundKind.Return)]
    public void ForTypedText_ClassifiesTypedText(string text, TypingSoundKind expected) =>
        Assert.Equal(expected, TypingSoundClassifier.ForTypedText(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\b")]
    [InlineData("\u001b")]
    public void ForTypedText_IsSilentForNothingOrControlCharacters(string? text) =>
        Assert.Null(TypingSoundClassifier.ForTypedText(text));

    // ── Synthesis ────────────────────────────────────────────────────────

    public static TheoryData<TypingSoundKind> Kinds => new(Enum.GetValues<TypingSoundKind>());

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Render_IsDeterministicForASeed(TypingSoundKind kind)
    {
        var synth = new TypewriterSynth(TypewriterProfile.Manual);
        Assert.Equal(synth.Render(kind, 42), synth.Render(kind, 42));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Render_PeaksAtTheProfileLevel_WithFiniteSamples(TypingSoundKind kind)
    {
        var profile = TypewriterProfile.Manual;
        var samples = new TypewriterSynth(profile).Render(kind, 7);

        Assert.All(samples, s => Assert.True(float.IsFinite(s)));
        Assert.Equal(profile.LevelFor(kind), samples.Max(MathF.Abs), 3);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Render_StartsAtOnceAndEndsSilently(TypingSoundKind kind)
    {
        var samples = new TypewriterSynth(TypewriterProfile.Manual).Render(kind, 3);
        var peak = samples.Max(MathF.Abs);

        // Audible within the first 3 ms (no latency baked into the sound), with no click at either end.
        var firstAudible = Array.FindIndex(samples, s => MathF.Abs(s) > peak * 0.05f);
        Assert.InRange(firstAudible, 0, TypewriterSynth.SampleRate * 3 / 1000);
        Assert.True(MathF.Abs(samples[0]) < peak * 0.05f);
        Assert.True(MathF.Abs(samples[^1]) < peak * 0.001f);
    }

    [Theory]
    [InlineData(TypingSoundKind.Key, 60, 400)]
    [InlineData(TypingSoundKind.Space, 60, 400)]
    [InlineData(TypingSoundKind.Backspace, 40, 400)]
    [InlineData(TypingSoundKind.Return, 350, 2000)]
    public void Render_HasAPlausibleLength(TypingSoundKind kind, int minMs, int maxMs)
    {
        var samples = new TypewriterSynth(TypewriterProfile.Manual).Render(kind, 11);
        var ms = samples.Length * 1000 / TypewriterSynth.SampleRate;
        Assert.InRange(ms, minMs, maxMs);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Render_DiffersBetweenSeeds(TypingSoundKind kind)
    {
        var synth = new TypewriterSynth(TypewriterProfile.Manual);
        Assert.NotEqual(synth.Render(kind, 1), synth.Render(kind, 2));
    }

    [Fact]
    public void Render_TheKindsSoundDifferent()
    {
        var synth = new TypewriterSynth(TypewriterProfile.Manual);
        var key = synth.Render(TypingSoundKind.Key, 5);
        var space = synth.Render(TypingSoundKind.Space, 5);

        // The space bar is duller: less of its energy is in fast sample-to-sample changes.
        Assert.True(Brightness(space) < Brightness(key), $"space {Brightness(space):F3} vs key {Brightness(key):F3}");
        // The carriage return is much the longest.
        Assert.True(synth.Render(TypingSoundKind.Return, 5).Length > 2 * key.Length);
    }

    [Fact]
    public void Bank_RendersEveryVariantOfEveryKind_TheSameEachTime()
    {
        var profile = TypewriterProfile.Manual with { Variants = 3 };
        var first = TypingSoundBank.Render(profile);
        var second = TypingSoundBank.Render(profile);

        foreach (var kind in TypingSoundBank.Kinds)
        {
            Assert.Equal(3, first[kind].Count);
            Assert.Equal(first[kind][2], second[kind][2]);
            Assert.NotEqual(first[kind][0], first[kind][1]);
        }
    }

    [Fact]
    public void StableHash_DoesNotDependOnTheProcess() =>
        Assert.Equal(unchecked((int)0xE40C292C), TypingSoundBank.StableHash("a")); // FNV-1a of "a"

    [Fact]
    public void ThemeProfiles_MapTheThemeAndFallBackToTheDefault()
    {
        Assert.Same(TypewriterProfile.Manual, ThemeSoundProfiles.ForTheme(ThemeSoundProfiles.SynthwaveThemeId));
        Assert.Same(ThemeSoundProfiles.Default, ThemeSoundProfiles.ForTheme("no-such-theme"));
        Assert.Same(ThemeSoundProfiles.Default, ThemeSoundProfiles.ForTheme(null));
    }

    // ── Voicing ──────────────────────────────────────────────────────────

    [Fact]
    public void Voicer_NeverRepeatsEitherOfTheLastTwoVariants()
    {
        var voicer = new TypingSoundVoicer(8, seed: 1);
        var picks = Enumerable.Range(0, 500)
            .Select(_ => voicer.Next(TypingSoundKind.Key, TimeSpan.FromMilliseconds(150)).Variant)
            .ToList();

        for (var i = 2; i < picks.Count; i++)
        {
            Assert.NotEqual(picks[i - 1], picks[i]);
            Assert.NotEqual(picks[i - 2], picks[i]);
        }

        // ...and still uses every variant.
        Assert.Equal(8, picks.Distinct().Count());
    }

    [Fact]
    public void Voicer_WithTwoVariants_Alternates_AndWithOne_RepeatsIt()
    {
        var two = new TypingSoundVoicer(2, seed: 3);
        var picks = Enumerable.Range(0, 6).Select(_ => two.Next(TypingSoundKind.Key, TimeSpan.Zero).Variant).ToList();
        for (var i = 1; i < picks.Count; i++)
            Assert.NotEqual(picks[i - 1], picks[i]);

        var one = new TypingSoundVoicer(1, seed: 3);
        Assert.Equal(0, one.Next(TypingSoundKind.Key, TimeSpan.Zero).Variant);
        Assert.Equal(0, one.Next(TypingSoundKind.Key, TimeSpan.Zero).Variant);
    }

    [Fact]
    public void Voicer_TracksEachKindSeparately()
    {
        var voicer = new TypingSoundVoicer(3, seed: 9);
        var key = voicer.Next(TypingSoundKind.Key, TimeSpan.FromSeconds(1)).Variant;
        // A space in between doesn't let the key variant come straight back.
        voicer.Next(TypingSoundKind.Space, TimeSpan.FromSeconds(1));
        Assert.NotEqual(key, voicer.Next(TypingSoundKind.Key, TimeSpan.FromSeconds(1)).Variant);
    }

    [Fact]
    public void Voicer_DetunesAndVariesLevelWithinBounds()
    {
        var voicer = new TypingSoundVoicer(8, seed: 5);
        var voices = Enumerable.Range(0, 300).Select(_ => voicer.Next(TypingSoundKind.Key, TimeSpan.FromMilliseconds(200))).ToList();

        Assert.All(voices, v => Assert.InRange(v.Pitch, 1 - TypingSoundVoicer.PitchSpread, 1 + TypingSoundVoicer.PitchSpread));
        Assert.All(voices, v => Assert.InRange(Db(v.Gain), TypingSoundVoicer.MinGainDb - 1e-3, TypingSoundVoicer.MaxGainDb + 1e-3));
        Assert.True(voices.Select(v => v.Pitch).Distinct().Count() > 100, "pitch should vary from strike to strike");

        var returns = Enumerable.Range(0, 100).Select(_ => voicer.Next(TypingSoundKind.Return, TimeSpan.FromSeconds(1)));
        Assert.All(returns, v => Assert.InRange(v.Pitch, 1 - TypingSoundVoicer.ReturnPitchSpread, 1 + TypingSoundVoicer.ReturnPitchSpread));
    }

    [Fact]
    public void Voicer_SoftensFlurries_AndFirmsUpTheFirstKeyAfterAPause()
    {
        var flurry = new TypingSoundVoicer(8, seed: 2);
        var steady = new TypingSoundVoicer(8, seed: 2);
        var pause = new TypingSoundVoicer(8, seed: 2);

        for (var i = 0; i < 50; i++)
        {
            var steadyDb = Db(steady.Next(TypingSoundKind.Key, TimeSpan.FromMilliseconds(200)).Gain);
            Assert.Equal(steadyDb + TypingSoundVoicer.FlurryGainDb, Db(flurry.Next(TypingSoundKind.Key, TimeSpan.FromMilliseconds(40)).Gain), 3);
            Assert.Equal(steadyDb + TypingSoundVoicer.PauseGainDb, Db(pause.Next(TypingSoundKind.Key, TimeSpan.FromSeconds(3)).Gain), 3);
        }
    }

    [Theory]
    [InlineData(0.0, -TypingSoundVoicer.MaxCarriagePan)]
    [InlineData(0.5, 0.0)]
    [InlineData(1.0, TypingSoundVoicer.MaxCarriagePan)]
    [InlineData(-3.0, -TypingSoundVoicer.MaxCarriagePan)]
    [InlineData(9.0, TypingSoundVoicer.MaxCarriagePan)]
    [InlineData(double.NaN, 0.0)]
    public void CarriagePan_FollowsTheCaretAcrossTheLine(double fraction, double expected) =>
        Assert.Equal(expected, TypingSoundVoicer.CarriagePan(fraction), 5);

    // ── Settings ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Settings_DefaultToOff_AndRoundTripThroughTheSettingsFile()
    {
        var dir = Directory.CreateTempSubdirectory("hymnal-sound-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            using (var store = new AppSettingsStore(path))
            {
                var service = new SoundSettingsService(store);
                var defaults = await service.LoadAsync();
                Assert.False(defaults.TypingSoundsEnabled);
                Assert.Equal(SoundSettings.DefaultTypingVolume, defaults.TypingVolume);

                await service.SaveAsync(new SoundSettings { TypingSoundsEnabled = true, TypingVolume = 0.35 });
                await store.FlushPendingAsync();
            }

            using (var reopened = new AppSettingsStore(path))
            {
                var loaded = await new SoundSettingsService(reopened).LoadAsync();
                Assert.True(loaded.TypingSoundsEnabled);
                Assert.Equal(0.35, loaded.TypingVolume);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Settings_ClampAHandEditedVolume_AndFillInMissingValues()
    {
        var dir = Directory.CreateTempSubdirectory("hymnal-sound-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            await File.WriteAllTextAsync(path, """{ "schemaVersion": 1, "values": { "sound": { "typingSoundsEnabled": true, "typingVolume": 7 } } }""");
            using (var store = new AppSettingsStore(path))
                Assert.Equal(1.0, (await new SoundSettingsService(store).LoadAsync()).TypingVolume);

            await File.WriteAllTextAsync(path, """{ "schemaVersion": 1, "values": { "sound": { "typingSoundsEnabled": true } } }""");
            using (var store = new AppSettingsStore(path))
            {
                var loaded = await new SoundSettingsService(store).LoadAsync();
                Assert.True(loaded.TypingSoundsEnabled);
                Assert.Equal(SoundSettings.DefaultTypingVolume, loaded.TypingVolume);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static double Db(float gain) => 20 * Math.Log10(gain);

    /// <summary>Share of the signal's energy in its first difference: higher for brighter sounds.</summary>
    private static double Brightness(float[] samples)
    {
        double energy = 0, diff = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            energy += samples[i] * samples[i];
            var d = samples[i] - samples[i - 1];
            diff += d * d;
        }

        return diff / energy;
    }
}
