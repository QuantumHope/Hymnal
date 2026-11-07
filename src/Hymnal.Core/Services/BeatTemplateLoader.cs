using System.Reflection;
using System.Text.Json;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>Loads the built-in beat templates from embedded JSON resources.</summary>
public static class BeatTemplateLoader
{
    private static readonly string[] Keys =
        ["threeAct", "tryFail", "sevenPoint", "kishotenketsu", "freytag", "mice", "blank"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<IReadOnlyList<BeatTemplate>> Templates = new(Load);

    public static IReadOnlyList<BeatTemplate> All => Templates.Value;

    public static BeatTemplate? Find(string key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));

    private static IReadOnlyList<BeatTemplate> Load()
    {
        var asm = typeof(BeatTemplateLoader).Assembly;
        return Keys.Select(key => LoadResource(asm, key)).ToList();
    }

    private static BeatTemplate LoadResource(Assembly asm, string key)
    {
        var resourceName = $"Hymnal.Core.Resources.BeatTemplates.{key}.json";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        return JsonSerializer.Deserialize<BeatTemplate>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Embedded resource is empty: {resourceName}");
    }
}
