using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Hymnal.Core.Audio;
using Hymnal.Core.Infrastructure;
using Xunit;

namespace Hymnal.Core.Tests.Audio;

public sealed class SoundSchemeTests
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

    // ── Every scheme, every kind ────────────────────────────────────────

    public static TheoryData<string, TypingSoundKind> SchemesAndKinds()
    {
        var data = new TheoryData<string, TypingSoundKind>();
        foreach (var scheme in SoundSchemes.All)
        {
            foreach (var kind in Enum.GetValues<TypingSoundKind>())
                data.Add(scheme.Id, kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SchemesAndKinds))]
    public void Render_IsDeterministicForASeed(string schemeId, TypingSoundKind kind)
    {
        var scheme = SoundSchemes.Find(schemeId)!;
        Assert.Equal(scheme.Render(kind, 42), scheme.Render(kind, 42));
    }

    [Theory]
    [MemberData(nameof(SchemesAndKinds))]
    public void Render_PeaksAtTheSchemesLevel_WithFiniteSamples(string schemeId, TypingSoundKind kind)
    {
        var scheme = SoundSchemes.Find(schemeId)!;
        var samples = scheme.Render(kind, 7);

        Assert.All(samples, s => Assert.True(float.IsFinite(s)));
        Assert.Equal(scheme.LevelFor(kind), samples.Max(MathF.Abs), 3);
    }

    [Theory]
    [MemberData(nameof(SchemesAndKinds))]
    public void Render_StartsAtOnce_EndsSilently_AndStaysShortEnough(string schemeId, TypingSoundKind kind)
    {
        var samples = SoundSchemes.Find(schemeId)!.Render(kind, 3);
        var peak = samples.Max(MathF.Abs);

        // Audible within the first 3 ms (no latency baked into the sound), with no click at either end.
        var firstAudible = Array.FindIndex(samples, s => MathF.Abs(s) > peak * 0.05f);
        Assert.InRange(firstAudible, 0, SoundVoice.SampleRate * 3 / 1000);
        Assert.True(MathF.Abs(samples[0]) < peak * 0.05f);
        Assert.True(MathF.Abs(samples[^1]) < peak * 0.001f);

        var ms = samples.Length * 1000 / SoundVoice.SampleRate;
        Assert.InRange(ms, 30, kind == TypingSoundKind.Return ? 2200 : 1300);
    }

    [Theory]
    [MemberData(nameof(SchemesAndKinds))]
    public void Render_DiffersBetweenSeeds(string schemeId, TypingSoundKind kind)
    {
        var scheme = SoundSchemes.Find(schemeId)!;
        Assert.NotEqual(scheme.Render(kind, 1), scheme.Render(kind, 2));
    }

    [Fact]
    public void Schemes_HaveUniqueIds_NamesAndDescriptions()
    {
        Assert.Equal(SoundSchemes.All.Count, SoundSchemes.All.Select(s => s.Id).Distinct().Count());
        Assert.All(SoundSchemes.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        Assert.All(SoundSchemes.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
        Assert.DoesNotContain(SoundSchemes.All, s => s.Id is SoundSettings.MatchTheme or SoundSettings.Silent);
        Assert.Same(SoundSchemes.Forge, SoundSchemes.Find("FORGE"));
        Assert.Null(SoundSchemes.Find("no-such-scheme"));
    }

    [Theory]
    [InlineData("manual-typewriter")]
    [InlineData("electric-typewriter")]
    [InlineData("mechanical-keyboard")]
    [InlineData("soft-keys")]
    public void KeyStrikes_AreBroadbandKnocks_NotRinging(string schemeId)
    {
        // Spectral flatness of the strike above 1 kHz: pure ringing tones (metal) score near 0, broadband impacts
        // (wood, plastic, rubber) much higher. The first typewriter sound, which read as tinny, scored about 0.1.
        var flatness = HighBandFlatness(SoundSchemes.Find(schemeId)!.Render(TypingSoundKind.Key, 11));
        Assert.True(flatness > 0.15, $"{schemeId} key flatness {flatness:F3}");
    }

    [Fact]
    public void TheForge_RingsLikeMetal()
    {
        var forge = HighBandFlatness(SoundSchemes.Forge.Render(TypingSoundKind.Key, 11));
        var manual = HighBandFlatness(SoundSchemes.ManualTypewriter.Render(TypingSoundKind.Key, 11));
        Assert.True(forge < manual / 10, $"forge {forge:F3} vs manual {manual:F3}");
    }

    [Fact]
    public void SoftKeys_AreTheQuietestScheme()
    {
        foreach (var kind in Enum.GetValues<TypingSoundKind>())
        {
            var soft = SoundSchemes.SoftKeys.LevelFor(kind);
            Assert.All(SoundSchemes.All.Where(s => s != SoundSchemes.SoftKeys), s => Assert.True(s.LevelFor(kind) > soft));
        }
    }

    [Fact]
    public void Bank_RendersEveryVariantOfEveryKind_TheSameEachTime()
    {
        var first = TypingSoundBank.Render(SoundSchemes.ElectricTypewriter);
        var second = TypingSoundBank.Render(SoundSchemes.ElectricTypewriter);

        foreach (var kind in TypingSoundBank.Kinds)
        {
            Assert.Equal(SoundSchemes.ElectricTypewriter.Variants, first[kind].Count);
            Assert.Equal(first[kind][2], second[kind][2]);
            Assert.NotEqual(first[kind][0], first[kind][1]);
        }
    }

    [Fact]
    public void StableHash_DoesNotDependOnTheProcess() =>
        Assert.Equal(unchecked((int)0xE40C292C), TypingSoundBank.StableHash("a")); // FNV-1a of "a"

    // ── Themes ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("synthwave", "electric-typewriter")]
    [InlineData("mordor-scriptorium", "forge")]
    [InlineData("cora-mechanicus", "forge")]
    [InlineData("cora-muther", "mechanical-keyboard")]
    [InlineData("cora-lcars", "mechanical-keyboard")]
    [InlineData("cora-hc-dark", "soft-keys")]
    [InlineData("foxen-light", "soft-keys")]
    [InlineData("cora-eliza", "manual-typewriter")]
    [InlineData("SYNTHWAVE", "electric-typewriter")]
    [InlineData("no-such-theme", "manual-typewriter")]
    [InlineData(null, "manual-typewriter")]
    public void ThemeSchemes_MapThemesAndFallBackToTheDefault(string? themeId, string schemeId) =>
        Assert.Equal(schemeId, ThemeSoundSchemes.ForTheme(themeId).Id);

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

    [Theory]
    [InlineData(SoundSettings.MatchTheme, "synthwave", "electric-typewriter")]
    [InlineData(SoundSettings.MatchTheme, "mordor-scriptorium", "forge")]
    [InlineData("soft-keys", "synthwave", "soft-keys")]
    [InlineData("FORGE", "synthwave", "forge")]
    [InlineData("a-scheme-since-removed", "synthwave", "electric-typewriter")]
    [InlineData(null, "synthwave", "electric-typewriter")]
    [InlineData(SoundSettings.Silent, "synthwave", null)]
    public void TypingSoundChoice_ResolvesToASchemeOrSilence(string? choice, string themeId, string? expected) =>
        Assert.Equal(expected, SoundSettings.ResolveTypingSound(choice, themeId)?.Id);

    [Fact]
    public async Task Settings_DefaultToOffAndMatchingTheTheme_AndRoundTripThroughTheSettingsFile()
    {
        var dir = Directory.CreateTempSubdirectory("hymnal-sound-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            using (var store = new AppSettingsStore(path))
            {
                var service = new SoundSettingsService(store);
                var defaults = await service.LoadAsync();
                Assert.False(defaults.SoundEnabled);
                Assert.Equal(SoundSettings.MatchTheme, defaults.TypingSound);
                Assert.Equal(SoundSettings.DefaultTypingVolume, defaults.TypingVolume);

                await service.SaveAsync(new SoundSettings { SoundEnabled = true, TypingSound = "forge", TypingVolume = 0.35 });
                await store.FlushPendingAsync();
            }

            using (var reopened = new AppSettingsStore(path))
            {
                var loaded = await new SoundSettingsService(reopened).LoadAsync();
                Assert.True(loaded.SoundEnabled);
                Assert.Equal("forge", loaded.TypingSound);
                Assert.Equal(0.35, loaded.TypingVolume);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Settings_RepairAHandEditedFile_AndFillInMissingValues()
    {
        var dir = Directory.CreateTempSubdirectory("hymnal-sound-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            await File.WriteAllTextAsync(path,
                """{ "schemaVersion": 1, "values": { "sound": { "soundEnabled": true, "typingSound": "kazoo", "typingVolume": 7 } } }""");
            using (var store = new AppSettingsStore(path))
            {
                var loaded = await new SoundSettingsService(store).LoadAsync();
                Assert.Equal(1.0, loaded.TypingVolume);
                Assert.Equal(SoundSettings.MatchTheme, loaded.TypingSound);
            }

            await File.WriteAllTextAsync(path, """{ "schemaVersion": 1, "values": { "sound": { "soundEnabled": true } } }""");
            using (var store = new AppSettingsStore(path))
            {
                var loaded = await new SoundSettingsService(store).LoadAsync();
                Assert.True(loaded.SoundEnabled);
                Assert.Equal(SoundSettings.MatchTheme, loaded.TypingSound);
                Assert.Equal(SoundSettings.DefaultTypingVolume, loaded.TypingVolume);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static double Db(float gain) => 20 * Math.Log10(gain);

    /// <summary>Spectral flatness of the first 4096 samples between 1 and 10 kHz (Hann window, FFT).</summary>
    private static double HighBandFlatness(float[] samples)
    {
        const int n = 4096;
        var data = new Complex[n];
        for (var i = 0; i < n; i++)
        {
            var s = i < samples.Length ? samples[i] : 0f;
            data[i] = s * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        }

        Fft(data);
        var lo = 1000 * n / SoundVoice.SampleRate;
        var hi = 10000 * n / SoundVoice.SampleRate;
        double logSum = 0, sum = 0;
        for (var k = lo; k < hi; k++)
        {
            var p = data[k].Magnitude * data[k].Magnitude + 1e-12;
            logSum += Math.Log(p);
            sum += p;
        }

        var count = hi - lo;
        return Math.Exp(logSum / count) / (sum / count);
    }

    private static void Fft(Complex[] a)
    {
        var n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (a[i], a[j]) = (a[j], a[i]);
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (var i = 0; i < n; i += len)
            {
                var wn = Complex.One;
                for (var j = 0; j < len / 2; j++)
                {
                    var u = a[i + j];
                    var v = a[i + j + len / 2] * wn;
                    a[i + j] = u + v;
                    a[i + j + len / 2] = u - v;
                    wn *= w;
                }
            }
        }
    }
}
