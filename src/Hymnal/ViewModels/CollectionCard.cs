using System.Collections.Generic;

namespace Hymnal.ViewModels;

/// <summary>One collection on Plan's collections shelf.</summary>
/// <param name="Slug">Its folder under <c>collections/</c>; null for an open collection not yet named.</param>
/// <param name="IsActive">True for the collection open in Hymnal (the one the corkboard below shows).</param>
/// <param name="IsNamed">False for an open collection with no title (it must be named before another is opened).</param>
/// <param name="Stories">Its first story titles, in order.</param>
/// <param name="MoreCount">How many stories <paramref name="Stories"/> leaves out.</param>
/// <param name="CountText">"1 story", "4 stories".</param>
public sealed record CollectionCard(
    string? Slug,
    string Title,
    bool IsActive,
    bool IsNamed,
    IReadOnlyList<string> Stories,
    int MoreCount,
    string CountText)
{
    public bool HasMore => MoreCount > 0;
    public string MoreText => $"+ {MoreCount} more";
    public bool IsEmpty => Stories.Count == 0;
    public bool CanOpen => !IsActive && Slug != null;
}
