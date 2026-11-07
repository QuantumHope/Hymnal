using System.Reflection;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Models.Ai;

namespace Hymnal.Core.Services.Ai;

/// <summary>
/// Loads embedded prompt resources and provides system prompts and role availability rules per spec §3.
/// </summary>
public sealed class RolePromptProvider : IRolePromptProvider
{
    private static readonly Assembly _asm = typeof(RolePromptProvider).Assembly;
    private readonly string _commonPreamble;
    private readonly Dictionary<AiRole, string> _roleBodies;
    private readonly string _shortStoryAddendum;
    private readonly string _anthologyAddendum;

    // spec §3.2 — view-to-role mappings
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<AiRole>> _viewRoleMap =
        new Dictionary<string, IReadOnlyList<AiRole>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Write"]    = new[] { AiRole.WritingCoach, AiRole.Proofreader, AiRole.LineEditor, AiRole.DevelopmentalEditor, AiRole.BetaReader },
            ["Research"] = new[] { AiRole.WritingCoach, AiRole.Proofreader, AiRole.LineEditor, AiRole.DevelopmentalEditor, AiRole.BetaReader },
            ["Manage"]   = new[] { AiRole.WritingCoach, AiRole.DevelopmentalEditor, AiRole.BetaReader },
            ["Plan"]     = new[] { AiRole.DevelopmentalEditor, AiRole.WritingCoach, AiRole.BetaReader },
        };

    private static readonly IReadOnlyDictionary<string, AiRole> _defaultRoleMap =
        new Dictionary<string, AiRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["Write"]    = AiRole.WritingCoach,
            ["Research"] = AiRole.WritingCoach,
            ["Manage"]   = AiRole.WritingCoach,
            ["Plan"]     = AiRole.DevelopmentalEditor,
        };

    public RolePromptProvider()
    {
        _commonPreamble = LoadResource("common");
        _shortStoryAddendum = LoadResource("short-story").Trim();
        _anthologyAddendum = LoadResource("anthology").Trim();
        _roleBodies = new Dictionary<AiRole, string>
        {
            [AiRole.WritingCoach]        = LoadResource("writing-coach"),
            [AiRole.DevelopmentalEditor] = LoadResource("developmental-editor"),
            [AiRole.Proofreader]         = LoadResource("proofreader"),
            [AiRole.LineEditor]          = LoadResource("line-editor"),
            [AiRole.BetaReader]          = LoadResource("beta-reader"),
        };
    }

    public string GetSystemPrompt(AiRole role)
    {
        var body = _roleBodies.TryGetValue(role, out var b) ? b : string.Empty;
        return _commonPreamble.TrimEnd() + "\n\n" + body.TrimEnd();
    }

    public string GetSystemPrompt(AiRole role, ProjectKind kind)
    {
        var basePrompt = GetSystemPrompt(role);
        return kind switch
        {
            ProjectKind.Story => basePrompt + "\n\n" + _shortStoryAddendum,
            ProjectKind.Anthology => basePrompt + "\n\n" + _anthologyAddendum,
            _ => basePrompt,
        };
    }

    public IReadOnlyList<AiRole> GetAvailableRoles(string shellModeName)
    {
        if (_viewRoleMap.TryGetValue(shellModeName, out var roles))
            return roles;
        // Fallback: all roles
        return new[] { AiRole.WritingCoach, AiRole.DevelopmentalEditor, AiRole.Proofreader, AiRole.LineEditor, AiRole.BetaReader };
    }

    public AiRole GetDefaultRole(string shellModeName)
    {
        if (_defaultRoleMap.TryGetValue(shellModeName, out var role))
            return role;
        return AiRole.WritingCoach;
    }

    private static string LoadResource(string name)
    {
        var resourceName = $"Hymnal.Core.Resources.Prompts.{name}.txt";
        using var stream = _asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
