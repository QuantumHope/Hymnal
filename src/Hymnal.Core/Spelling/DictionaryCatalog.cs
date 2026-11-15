using System.Globalization;
using System.Reflection;
using Hymnal.Core.Common;

namespace Hymnal.Core.Spelling;

/// <summary>Where a dictionary came from.</summary>
public enum DictionarySource
{
    /// <summary>Shipped inside Hymnal.</summary>
    Bundled,

    /// <summary>A Hunspell pair the user dropped into <see cref="DictionaryCatalog.UserFolder"/>.</summary>
    User,
}

/// <summary>An installed dictionary.</summary>
/// <param name="Id">The file name without extension, such as <c>en-US</c> or <c>de_DE</c>; what settings store.</param>
/// <param name="DisplayName">The language's name, such as "English (United States)".</param>
public sealed record DictionaryInfo(string Id, string DisplayName, DictionarySource Source)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// The Hunspell dictionaries Hymnal can check against: those bundled in Hymnal.Core (embedded resources named
/// <c>Hymnal.Dictionaries.{id}.dic</c> / <c>.aff</c>) and any <c>{id}.dic</c> + <c>{id}.aff</c> pair in the user's
/// dictionaries folder (<c>%AppData%/Hymnal/dictionaries</c> on Windows, <c>~/.config/Hymnal/dictionaries</c> on
/// Linux). A user pair with the same id as a bundled one replaces it.
/// </summary>
public sealed class DictionaryCatalog
{
    internal const string ResourcePrefix = "Hymnal.Dictionaries.";

    private readonly Assembly? _bundle;

    public DictionaryCatalog()
        : this(DefaultUserFolder(), typeof(DictionaryCatalog).Assembly)
    {
    }

    /// <summary>For tests: a custom user folder, and an assembly to read bundled dictionaries from (or none).</summary>
    public DictionaryCatalog(string? userFolder, Assembly? bundle)
    {
        UserFolder = userFolder;
        _bundle = bundle;
    }

    /// <summary>Where the user can add Hunspell dictionaries; null when there is none.</summary>
    public string? UserFolder { get; }

    public static string DefaultUserFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hymnal", "dictionaries");

    /// <summary>Every installed dictionary, sorted by name.</summary>
    public IReadOnlyList<DictionaryInfo> Available()
    {
        var found = new Dictionary<string, DictionaryInfo>(StringComparer.OrdinalIgnoreCase);

        if (_bundle is not null)
        {
            var names = _bundle.GetManifestResourceNames();
            foreach (var name in names)
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(".dic", StringComparison.Ordinal))
                    continue;
                var id = name[ResourcePrefix.Length..^4];
                if (names.Contains(ResourcePrefix + id + ".aff"))
                    found[id] = new DictionaryInfo(id, DisplayNameFor(id), DictionarySource.Bundled);
            }
        }

        if (UserFolder is not null && Directory.Exists(UserFolder))
        {
            try
            {
                foreach (var dic in Directory.EnumerateFiles(UserFolder, "*.dic"))
                {
                    var id = Path.GetFileNameWithoutExtension(dic);
                    if (File.Exists(Path.ChangeExtension(dic, ".aff")))
                        found[id] = new DictionaryInfo(id, DisplayNameFor(id), DictionarySource.User);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder just means no user dictionaries.
            }
        }

        return found.Values.OrderBy(d => d.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// The dictionary to use for <paramref name="preferredId"/>: that one if installed, else one for the same
    /// language (en-GB for a missing en-AU), else the default, else the first installed; null when there are none.
    /// </summary>
    public DictionaryInfo? Resolve(string? preferredId)
    {
        var all = Available();
        if (all.Count == 0)
            return null;

        static string Norm(string id) => id.Replace('_', '-');
        static string Lang(string id) => Norm(id).Split('-')[0];

        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var exact = all.FirstOrDefault(d => string.Equals(Norm(d.Id), Norm(preferredId), StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;
            var sameLanguage = all.FirstOrDefault(d => string.Equals(Lang(d.Id), Lang(preferredId), StringComparison.OrdinalIgnoreCase));
            if (sameLanguage is not null)
                return sameLanguage;
        }

        return all.FirstOrDefault(d => string.Equals(Norm(d.Id), SpellCheckSettings.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
               ?? all[0];
    }

    /// <summary>Reads a dictionary. Fails (rather than throws) when it is missing or damaged.</summary>
    public async Task<Result<IWordChecker>> LoadAsync(DictionaryInfo info, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() => Load(info), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<IWordChecker>.Fail($"The {info.DisplayName} dictionary could not be read: {ex.Message}");
        }
    }

    private Result<IWordChecker> Load(DictionaryInfo info)
    {
        if (info.Source == DictionarySource.User && UserFolder is not null)
        {
            var dic = Path.Combine(UserFolder, info.Id + ".dic");
            var aff = Path.Combine(UserFolder, info.Id + ".aff");
            if (!File.Exists(dic) || !File.Exists(aff))
                return Result<IWordChecker>.Fail($"The {info.DisplayName} dictionary is no longer in {UserFolder}.");
            using var dicStream = File.OpenRead(dic);
            using var affStream = File.OpenRead(aff);
            return Result<IWordChecker>.Ok(HunspellWordChecker.FromStreams(dicStream, affStream));
        }

        using var bundledDic = _bundle?.GetManifestResourceStream(ResourcePrefix + info.Id + ".dic");
        using var bundledAff = _bundle?.GetManifestResourceStream(ResourcePrefix + info.Id + ".aff");
        if (bundledDic is null || bundledAff is null)
            return Result<IWordChecker>.Fail($"The {info.DisplayName} dictionary is missing from this build of Hymnal.");
        return Result<IWordChecker>.Ok(HunspellWordChecker.FromStreams(bundledDic, bundledAff));
    }

    /// <summary>"English (United States)" for <c>en-US</c> or <c>en_US</c>; the id itself when it isn't a culture.</summary>
    public static string DisplayNameFor(string id)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(id.Replace('_', '-'));
            if (!string.IsNullOrEmpty(culture.Name) && culture.EnglishName.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) < 0)
                return culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
        }

        return id;
    }
}
