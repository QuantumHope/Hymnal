namespace Hymnal.Core.Models;

/// <summary>One collection of stories in an anthology workspace.</summary>
/// <param name="Slug">Its folder name under <c>collections/</c>; stable across title changes.</param>
/// <param name="Title">The collection's title (the book's title when published).</param>
/// <param name="IsActive">True for the collection open in Hymnal, whose list is the workspace's Book.txt.</param>
/// <param name="EntryCount">Lines its Book.txt lists (stories, scenes and story dividers).</param>
public sealed record CollectionInfo(string Slug, string Title, bool IsActive, int EntryCount);

/// <summary>One line of a collection's Book.txt as LeanPub would read it.</summary>
public sealed record PublishEntry(string RelativePath, string Title, bool IsPart, bool Exists);

/// <summary>What publishing a collection to LeanPub would produce, and anything that stops it.</summary>
/// <param name="Problems">Things LeanPub would trip over (missing files); publishing still copies the rest.</param>
/// <param name="BlockedReason">Why it can't be published from here at all; null when it can.</param>
public sealed record PublishPreview(
    string CollectionTitle,
    IReadOnlyList<PublishEntry> Entries,
    IReadOnlyList<string> Problems,
    string? BlockedReason)
{
    public bool CanPublish => BlockedReason == null && Entries.Any(e => e.Exists);
}

/// <summary>The result of copying a collection into <c>manuscript/</c>.</summary>
public sealed record PublishResult(int FilesCopied, int FilesRemoved, string ManuscriptFolder);
