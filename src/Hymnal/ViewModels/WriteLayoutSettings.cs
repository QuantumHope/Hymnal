namespace Hymnal.ViewModels;

/// <summary>
/// Serializable snapshot of Write-tab geometry.  Stored under the "writeLayout" key
/// in IAppSettingsStore.  All dimensions are pixel values; null means "use default".
/// </summary>
public sealed class WriteLayoutSettings
{
    // ── Outer sidebar widths ──────────────────────────────────────────────────

    /// <summary>Width of column 0 (left rail) when at least one left pane is expanded.</summary>
    public double LeftSidebarWidth { get; set; } = DefaultLeftSidebarWidth;

    /// <summary>Width of column 4 (right rail) when at least one right pane is expanded.</summary>
    public double RightSidebarWidth { get; set; } = DefaultRightSidebarWidth;

    // ── Left pane horizontal-split ratio ─────────────────────────────────────

    /// <summary>Star weight for the Chapters section row (Row 0 of LeftPaneGrid).</summary>
    public double LeftPaneTopStar { get; set; } = 1.0;

    /// <summary>Star weight for the Docs section row (Row 2 of LeftPaneGrid).</summary>
    public double LeftPaneBottomStar { get; set; } = 1.0;

    // ── Right pane vertical-split ratio ────────────────────────────────────────

    /// <summary>Star weight for the Chapter Info content row (Row 0 of RightPaneGrid).</summary>
    public double RightPaneTopStar { get; set; } = 1.0;

    /// <summary>Star weight for the Beats content row (Row 2 of RightPaneGrid; story/anthology only).</summary>
    public double RightPaneBeatsStar { get; set; } = 1.0;

    /// <summary>Star weight for the Notes content row (Row 4 of RightPaneGrid).</summary>
    public double RightPaneBottomStar { get; set; } = 1.0;

    /// <summary>Star weight for the AI Chat content row (Row 6 of RightPaneGrid).</summary>
    public double RightPaneChatStar { get; set; } = 1.0;

    // ── Defaults ─────────────────────────────────────────────────────────────

    public const double DefaultLeftSidebarWidth  = 220.0;
    public const double DefaultRightSidebarWidth = 280.0;
    public const double MinSidebarWidth          = 160.0;
    public const double MaxSidebarWidth          = 600.0;

    /// <summary>Returns a new instance with all fields set to their out-of-the-box defaults.</summary>
    public static WriteLayoutSettings CreateDefault() => new();
}
