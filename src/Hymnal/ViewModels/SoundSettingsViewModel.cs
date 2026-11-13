using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hymnal.Audio;
using Hymnal.Core.Audio;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>One entry in Settings → Sound → Typing sound.</summary>
public sealed class TypingSoundOption : ReactiveObject
{
    private string _label;
    private string _description;

    public TypingSoundOption(string id, string label, string description)
    {
        Id = id;
        _label = label;
        _description = description;
    }

    /// <summary><see cref="SoundSettings.MatchTheme"/>, <see cref="SoundSettings.Silent"/> or a scheme id.</summary>
    public string Id { get; }

    public string Label
    {
        get => _label;
        internal set => this.RaiseAndSetIfChanged(ref _label, value);
    }

    public string Description
    {
        get => _description;
        internal set => this.RaiseAndSetIfChanged(ref _description, value);
    }

    public override string ToString() => Label;
}

/// <summary>
/// Settings → Sound: whether Hymnal makes sound at all, which typing sound plays (match the theme, silent, or a
/// specific scheme), its volume, and a preview. Choosing a typing sound plays a sample of it.
/// </summary>
public sealed class SoundSettingsViewModel : ViewModelBase, IDisposable
{
    /// <summary>Shown while the sounds are getting ready.</summary>
    public const string Starting = "Getting the sounds ready…";

    /// <summary>Shown when there is no audio output to play on.</summary>
    public const string Unavailable = "No audio output is available, so Hymnal can't play sounds.";

    private readonly TypingSoundPlayer? _player;
    private readonly ObservableAsPropertyHelper<string?> _statusMessage;
    private readonly ObservableAsPropertyHelper<bool> _canChooseTypingSound;
    private readonly ObservableAsPropertyHelper<bool> _isTypingSoundAudible;
    private CancellationTokenSource? _preview;
    private TypingSoundOption _selectedTypingSound;
    private bool _syncing;

    public SoundSettingsViewModel(SoundPreferences preferences, TypingSoundPlayer? player = null)
    {
        Preferences = preferences;
        _player = player;

        var themeScheme = player?.ThemeScheme ?? ThemeSoundSchemes.ForTheme(ThemeSoundSchemes.SynthwaveThemeId);
        MatchThemeOption = new TypingSoundOption(SoundSettings.MatchTheme, MatchThemeLabel(themeScheme), MatchThemeDescription(themeScheme));
        TypingSoundOptions =
        [
            MatchThemeOption,
            new TypingSoundOption(SoundSettings.Silent, "Silent", "No typing sound. Other sounds still play while sound is on."),
            .. SoundSchemes.All.Select(s => new TypingSoundOption(s.Id, s.Name, s.Description)),
        ];
        _selectedTypingSound = OptionFor(preferences.TypingSound);

        // Follow changes made elsewhere (loading, the View menu) without previewing them.
        Disposables.Add(preferences.WhenAnyValue(p => p.TypingSound).Subscribe(choice =>
        {
            _syncing = true;
            try
            {
                SelectedTypingSound = OptionFor(choice);
            }
            finally
            {
                _syncing = false;
            }
        }));

        if (player is not null)
        {
            Disposables.Add(player.WhenAnyValue(p => p.ThemeScheme).Subscribe(scheme =>
            {
                MatchThemeOption.Label = MatchThemeLabel(scheme);
                MatchThemeOption.Description = MatchThemeDescription(scheme);
            }));
        }

        _canChooseTypingSound = preferences.WhenAnyValue(p => p.SoundEnabled)
            .ToProperty(this, x => x.CanChooseTypingSound);
        Disposables.Add(_canChooseTypingSound);

        var audible = preferences.WhenAnyValue(p => p.SoundEnabled, p => p.TypingSound,
            (on, choice) => on && choice != SoundSettings.Silent);
        _isTypingSoundAudible = audible.ToProperty(this, x => x.IsTypingSoundAudible);
        Disposables.Add(_isTypingSoundAudible);

        _statusMessage = (player is null
                ? Observable.Return<string?>(null)
                : player.WhenAnyValue(p => p.Status).Select(StatusText))
            .ToProperty(this, x => x.StatusMessage);
        Disposables.Add(_statusMessage);

        PreviewCommand = ReactiveCommand.CreateFromTask(PreviewAsync, audible);
        Disposables.Add(PreviewCommand.ThrownExceptions.Subscribe(_ => { }));
        Disposables.Add(System.Reactive.Disposables.Disposable.Create(() => _preview?.Cancel()));
    }

    public SoundPreferences Preferences { get; }

    public IReadOnlyList<TypingSoundOption> TypingSoundOptions { get; }

    /// <summary>The first option: follow the theme. Its label names the theme's scheme.</summary>
    public TypingSoundOption MatchThemeOption { get; }

    /// <summary>The chosen typing sound. Choosing one (while sound is on) plays a sample of it.</summary>
    public TypingSoundOption SelectedTypingSound
    {
        get => _selectedTypingSound;
        set
        {
            if (value is null || ReferenceEquals(_selectedTypingSound, value))
                return;
            this.RaiseAndSetIfChanged(ref _selectedTypingSound, value);
            if (_syncing)
                return;

            Preferences.TypingSound = value.Id;
            if (IsTypingSoundAudible)
                _ = PreviewAsync();
        }
    }

    /// <summary>The typing sound can be chosen while sound is on.</summary>
    public bool CanChooseTypingSound => _canChooseTypingSound.Value;

    /// <summary>Sound is on and the typing sound isn't Silent: volume and preview apply.</summary>
    public bool IsTypingSoundAudible => _isTypingSoundAudible.Value;

    /// <summary>A line about the sounds' state (starting, no audio output); null when there's nothing to say.</summary>
    public string? StatusMessage => _statusMessage.Value;

    /// <summary>Plays a short typed phrase with the current typing sound.</summary>
    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }

    /// <summary>Stops following the preferences and player, and cuts off a preview still playing.</summary>
    public void Dispose() => Disposables.Dispose();

    internal static string? StatusText(TypingSoundStatus status) => status switch
    {
        TypingSoundStatus.Starting => Starting,
        TypingSoundStatus.Unavailable => Unavailable,
        _ => null,
    };

    internal static string MatchThemeLabel(SoundScheme scheme) => $"Match the theme ({scheme.Name})";

    private static string MatchThemeDescription(SoundScheme scheme) =>
        $"Follows the theme, which sounds like this: {scheme.Description}";

    private TypingSoundOption OptionFor(string choice) =>
        TypingSoundOptions.FirstOrDefault(o => o.Id == SoundSettings.NormalizeTypingSound(choice)) ?? MatchThemeOption;

    /// <summary>Plays the preview, cutting off one still playing so quick changes don't overlap.</summary>
    private async Task PreviewAsync()
    {
        if (_player is null)
            return;

        _preview?.Cancel();
        var preview = _preview = new CancellationTokenSource();
        try
        {
            await _player.PreviewAsync(preview.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer preview took over.
        }
    }
}
