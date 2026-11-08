using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public sealed class BookTxtStructureService : IBookTxtStructureService
{
    private readonly IMetadataStore _metadataStore;
    private readonly IExclusionManifestService _exclusionManifestService;
    private readonly ChapterRegistryService _chapterRegistryService;
    private readonly StoryFileWriter _storyWriter;

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public BookTxtStructureService(IMetadataStore metadataStore)
        : this(metadataStore, new ExclusionManifestService(metadataStore), new ChapterRegistryService(metadataStore))
    {
    }

    public BookTxtStructureService(IMetadataStore metadataStore, IExclusionManifestService exclusionManifestService)
        : this(metadataStore, exclusionManifestService, new ChapterRegistryService(metadataStore))
    {
    }

    public BookTxtStructureService(
        IMetadataStore metadataStore,
        IExclusionManifestService exclusionManifestService,
        ChapterRegistryService chapterRegistryService)
        : this(metadataStore, exclusionManifestService, chapterRegistryService, new StoryFileWriter(metadataStore))
    {
    }

    /// <param name="storyWriter">
    /// Rewrites a renamed file's first heading, keeping its metadata block; the app passes the editor's writer.
    /// </param>
    public BookTxtStructureService(
        IMetadataStore metadataStore,
        IExclusionManifestService exclusionManifestService,
        ChapterRegistryService chapterRegistryService,
        StoryFileWriter storyWriter)
    {
        _metadataStore = metadataStore;
        _exclusionManifestService = exclusionManifestService;
        _chapterRegistryService = chapterRegistryService;
        _storyWriter = storyWriter;
    }

    public async Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<IReadOnlyList<string>>.Fail(document.Error!);

        return Result<IReadOnlyList<string>>.Ok(document.Value!.Entries.AsReadOnly());
    }

    public async Task<Result<Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        if (doc.Entries.Count == 0)
            return Result<Unit>.Fail($"Book.txt at '{doc.BookTxtPath}' does not contain any entries to reorder.");

        var normalized = NormalizeStructurePath(doc.ManuscriptRoot, chapterPath, nameof(chapterPath));
        if (!normalized.IsSuccess)
            return Result<Unit>.Fail(normalized.Error!);

        var sourceIndex = FindEntryIndex(doc.Entries, normalized.Value!.RelativePath);
        if (sourceIndex < 0)
            return Result<Unit>.Fail($"Entry '{normalized.Value.RelativePath}' was not found in '{doc.BookTxtPath}'.");

        var sourcePartValidation = ValidatePartDivider(doc.ManuscriptRoot, normalized.Value.RelativePath);
        if (sourcePartValidation.IsSuccess)
            return await ReorderPartBlockAsync(doc, normalized.Value.RelativePath, sourceIndex, newIndex).ConfigureAwait(false);

        if (newIndex < 0 || newIndex >= doc.Entries.Count)
            return Result<Unit>.Fail($"Requested reorder index {newIndex} is out of range for '{doc.BookTxtPath}'.");

        if (sourceIndex == newIndex)
            return Result<Unit>.Ok(Unit.Default);

        var lines = doc.RawLines.ToList();
        var sourceRawIndex = doc.EntryLineIndexes[sourceIndex];
        var movingLine = lines[sourceRawIndex];
        lines.RemoveAt(sourceRawIndex);

        var insertionIndex = FindRawInsertionIndex(lines, newIndex);
        lines.Insert(insertionIndex, movingLine);

        var write = await WriteBookTxtAsync(doc.BookTxtPath, lines).ConfigureAwait(false);
        return write.IsSuccess ? Result<Unit>.Ok(Unit.Default) : Result<Unit>.Fail(write.Error!);
    }

    private async Task<Result<Unit>> ReorderPartBlockAsync(BookDocument doc, string sourcePartPath, int sourceIndex, int newIndex)
    {
        if (newIndex < 0 || newIndex >= doc.Entries.Count)
        {
            return Result<Unit>.Fail(
                $"Part reorder operation failed for '{sourcePartPath}' to index {newIndex} during Book.txt validation: requested reorder index {newIndex} is out of range for '{doc.BookTxtPath}'.");
        }

        var blockEndIndex = FindPartBlockEndEntryIndex(doc, sourceIndex);
        if (newIndex >= sourceIndex && newIndex < blockEndIndex)
            return Result<Unit>.Ok(Unit.Default);

        var targetPartValidation = ValidatePartDivider(doc.ManuscriptRoot, doc.Entries[newIndex]);
        if (!targetPartValidation.IsSuccess)
        {
            return Result<Unit>.Fail(
                $"Part reorder operation failed for '{sourcePartPath}' to index {newIndex} during Book.txt validation: requested reorder index {newIndex} targets non-Part entry '{doc.Entries[newIndex]}'.");
        }

        var lines = doc.RawLines.ToList();
        var sourceRawStart = doc.EntryLineIndexes[sourceIndex];
        var sourceRawEnd = blockEndIndex < doc.EntryLineIndexes.Count
            ? doc.EntryLineIndexes[blockEndIndex]
            : doc.RawLines.Count;
        var movingLines = lines.GetRange(sourceRawStart, sourceRawEnd - sourceRawStart);
        lines.RemoveRange(sourceRawStart, sourceRawEnd - sourceRawStart);

        var targetEntryIndexAfterRemoval = CountEntriesBeforeIndexOutsideRange(doc, newIndex, sourceIndex, blockEndIndex);
        var insertionIndex = FindRawInsertionIndex(lines, targetEntryIndexAfterRemoval);
        lines.InsertRange(insertionIndex, movingLines);

        var write = await WriteBookTxtAsync(doc.BookTxtPath, lines).ConfigureAwait(false);
        return write.IsSuccess
            ? Result<Unit>.Ok(Unit.Default)
            : Result<Unit>.Fail($"Part reorder operation failed for '{sourcePartPath}' to index {newIndex} during Book.txt write: {write.Error}");
    }

    private static int FindPartBlockEndEntryIndex(BookDocument doc, int sourceIndex)
    {
        for (var i = sourceIndex + 1; i < doc.Entries.Count; i++)
        {
            if (ValidatePartDivider(doc.ManuscriptRoot, doc.Entries[i]).IsSuccess)
                return i;
        }

        return doc.Entries.Count;
    }

    private static int CountEntriesBeforeIndexOutsideRange(BookDocument doc, int targetIndex, int removedStartIndex, int removedEndIndex)
    {
        var count = 0;
        for (var i = 0; i < targetIndex; i++)
        {
            if (i < removedStartIndex || i >= removedEndIndex)
                count++;
        }

        return count;
    }

    public async Task<Result<Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var sourcePath = NormalizeStructurePath(doc.ManuscriptRoot, existingPath, nameof(existingPath));
        if (!sourcePath.IsSuccess)
            return Result<Unit>.Fail($"Rename operation failed for '{existingPath}' to '{replacementPath}' during path validation: {sourcePath.Error}");

        var replacement = NormalizeStructurePath(doc.ManuscriptRoot, replacementPath, nameof(replacementPath));
        if (!replacement.IsSuccess)
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.Value!.RelativePath}' to '{replacementPath}' during path validation: {replacement.Error}");

        if (string.Equals(sourcePath.Value!.RelativePath, replacement.Value!.RelativePath, StringComparison.Ordinal))
            return Result<Unit>.Ok(Unit.Default);

        if (string.Equals(sourcePath.Value.RelativePath, replacement.Value.RelativePath, StringComparison.OrdinalIgnoreCase))
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during path validation: case-only path renames are not supported.");

        var sourceIndex = FindEntryIndex(doc.Entries, sourcePath.Value.RelativePath);
        if (sourceIndex < 0)
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during Book.txt validation: source entry was not found in '{doc.BookTxtPath}'.");

        if (!File.Exists(sourcePath.Value.AbsolutePath))
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during file move validation: source file '{sourcePath.Value.AbsolutePath}' does not exist.");

        var partValidation = ValidatePartDivider(doc.ManuscriptRoot, sourcePath.Value.RelativePath);
        return partValidation.IsSuccess
            ? await RenamePartFolderAsync(doc, sourceIndex, sourcePath.Value, replacement.Value).ConfigureAwait(false)
            : await RenameChapterFileAsync(doc, sourceIndex, sourcePath.Value, replacement.Value).ConfigureAwait(false);
    }

    private async Task<Result<Unit>> RenameChapterFileAsync(
        BookDocument doc,
        int sourceIndex,
        (string RelativePath, string AbsolutePath) sourcePath,
        (string RelativePath, string AbsolutePath) replacementPath)
    {
        if (FindEntryIndex(doc.Entries, replacementPath.RelativePath) >= 0)
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during conflict validation: target entry already exists in '{doc.BookTxtPath}'.");

        if (File.Exists(replacementPath.AbsolutePath) || Directory.Exists(replacementPath.AbsolutePath))
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during conflict validation: target path '{replacementPath.AbsolutePath}' already exists.");

        var registryValidation = await BuildRegistryMoveAsync(
            doc.ManuscriptRoot,
            sourcePath.RelativePath,
            replacementPath.RelativePath).ConfigureAwait(false);
        if (!registryValidation.IsSuccess)
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during registry validation: {registryValidation.Error}");

        var originalLines = doc.RawLines.ToList();
        var updatedLines = doc.RawLines.ToList();
        updatedLines[doc.EntryLineIndexes[sourceIndex]] = replacementPath.RelativePath;

        var originalContent = await ReadTextForRollbackAsync(sourcePath.AbsolutePath, sourcePath.RelativePath, replacementPath.RelativePath).ConfigureAwait(false);
        if (!originalContent.IsSuccess)
            return Result<Unit>.Fail(originalContent.Error!);

        var moved = TryMoveFileForRename(sourcePath, replacementPath);
        if (!moved.IsSuccess)
            return Result<Unit>.Fail(moved.Error!);

        var headingWrite = await RewriteFirstHeadingAsync(replacementPath.AbsolutePath, DisplayTitleFromChapterPath(replacementPath.RelativePath)).ConfigureAwait(false);
        if (!headingWrite.IsSuccess)
            return await RollBackRenameFileAsync(
                doc.BookTxtPath,
                sourcePath,
                replacementPath,
                restoreBookTxtLines: null,
                restoreSourceContent: originalContent.Value,
                failurePhase: "display heading update",
                failureDetail: headingWrite.Error!).ConfigureAwait(false);

        var bookWrite = await WriteBookTxtAsync(doc.BookTxtPath, updatedLines).ConfigureAwait(false);
        if (!bookWrite.IsSuccess)
            return await RollBackRenameFileAsync(
                doc.BookTxtPath,
                sourcePath,
                replacementPath,
                restoreBookTxtLines: null,
                restoreSourceContent: originalContent.Value,
                failurePhase: "Book.txt write",
                failureDetail: bookWrite.Error!).ConfigureAwait(false);

        try
        {
            await _chapterRegistryService.SaveAsync(doc.ManuscriptRoot, registryValidation.Value!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await RollBackRenameFileAsync(
                doc.BookTxtPath,
                sourcePath,
                replacementPath,
                originalLines,
                originalContent.Value,
                failurePhase: "registry update",
                failureDetail: ex.Message).ConfigureAwait(false);
        }

        var include = await _exclusionManifestService.IncludeAsync(doc.ManuscriptRoot, replacementPath.RelativePath).ConfigureAwait(false);
        if (!include.IsSuccess)
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during manifest save after file move, Book.txt write, and registry update: {include.Error}");

        return Result<Unit>.Ok(Unit.Default);
    }

    private async Task<Result<Unit>> RenamePartFolderAsync(
        BookDocument doc,
        int sourceIndex,
        (string RelativePath, string AbsolutePath) sourcePath,
        (string RelativePath, string AbsolutePath) replacementPath)
    {
        var sourceFolderRelative = Path.GetDirectoryName(sourcePath.RelativePath)?.Replace('\\', '/');
        var replacementFolderRelative = Path.GetDirectoryName(replacementPath.RelativePath)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(sourceFolderRelative) || string.IsNullOrWhiteSpace(replacementFolderRelative))
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during path validation: Part renames must keep the Part file inside a containing folder.");

        if (!string.Equals(Path.GetFileName(sourcePath.RelativePath), Path.GetFileName(replacementPath.RelativePath), StringComparison.OrdinalIgnoreCase))
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during path validation: Part folder renames must keep the Part file name unchanged.");

        var sourcePrefix = sourceFolderRelative + "/";
        var replacementPrefix = replacementFolderRelative + "/";
        var movedEntryIndexes = doc.Entries
            .Select((entry, index) => (entry, index))
            .Where(pair => pair.entry.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (movedEntryIndexes.Count == 0 || movedEntryIndexes.All(pair => pair.index != sourceIndex))
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during Book.txt validation: no entries were found under source folder '{sourceFolderRelative}'.");

        if (doc.Entries.Any(entry => entry.StartsWith(replacementPrefix, StringComparison.OrdinalIgnoreCase)))
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during conflict validation: target folder prefix '{replacementFolderRelative}' already exists in '{doc.BookTxtPath}'.");

        var sourceFolderAbsolute = Path.Combine(doc.ManuscriptRoot, sourceFolderRelative.Replace('/', Path.DirectorySeparatorChar));
        var replacementFolderAbsolute = Path.Combine(doc.ManuscriptRoot, replacementFolderRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(sourceFolderAbsolute))
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during file move validation: source folder '{sourceFolderAbsolute}' does not exist.");

        if (File.Exists(replacementFolderAbsolute) || Directory.Exists(replacementFolderAbsolute))
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during conflict validation: target folder '{replacementFolderAbsolute}' already exists.");

        var pathMap = movedEntryIndexes.ToDictionary(
            pair => pair.entry,
            pair => replacementPrefix + pair.entry[sourcePrefix.Length..],
            StringComparer.OrdinalIgnoreCase);
        pathMap[sourcePath.RelativePath] = replacementPath.RelativePath;

        var registryValidation = await BuildRegistryPrefixRenameAsync(doc.ManuscriptRoot, pathMap).ConfigureAwait(false);
        if (!registryValidation.IsSuccess)
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during registry validation: {registryValidation.Error}");

        var originalLines = doc.RawLines.ToList();
        var updatedLines = doc.RawLines.ToList();
        foreach (var (_, index) in movedEntryIndexes)
        {
            var rawIndex = doc.EntryLineIndexes[index];
            updatedLines[rawIndex] = pathMap[doc.Entries[index]];
        }

        var originalPartContent = await ReadTextForRollbackAsync(sourcePath.AbsolutePath, sourcePath.RelativePath, replacementPath.RelativePath).ConfigureAwait(false);
        if (!originalPartContent.IsSuccess)
            return Result<Unit>.Fail(originalPartContent.Error!);

        try
        {
            var parentDirectory = Path.GetDirectoryName(replacementFolderAbsolute);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
                Directory.CreateDirectory(parentDirectory);

            Directory.Move(sourceFolderAbsolute, replacementFolderAbsolute);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during file move from '{sourceFolderAbsolute}' to '{replacementFolderAbsolute}': {ex.Message}; rollback not attempted because no mutation was confirmed.");
        }

        var headingWrite = await RewriteFirstHeadingAsync(replacementPath.AbsolutePath, DisplayTitleFromPartPath(replacementPath.RelativePath)).ConfigureAwait(false);
        if (!headingWrite.IsSuccess)
            return await RollBackRenameFolderAsync(
                doc.BookTxtPath,
                sourcePath,
                replacementPath,
                sourceFolderAbsolute,
                replacementFolderAbsolute,
                restoreBookTxtLines: null,
                restorePartContent: originalPartContent.Value,
                failurePhase: "display heading update",
                failureDetail: headingWrite.Error!).ConfigureAwait(false);

        var bookWrite = await WriteBookTxtAsync(doc.BookTxtPath, updatedLines).ConfigureAwait(false);
        if (!bookWrite.IsSuccess)
            return await RollBackRenameFolderAsync(
                doc.BookTxtPath,
                sourcePath,
                replacementPath,
                sourceFolderAbsolute,
                replacementFolderAbsolute,
                restoreBookTxtLines: null,
                restorePartContent: originalPartContent.Value,
                failurePhase: "Book.txt write",
                failureDetail: bookWrite.Error!).ConfigureAwait(false);

        try
        {
            await _chapterRegistryService.SaveAsync(doc.ManuscriptRoot, registryValidation.Value!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await RollBackRenameFolderAsync(
                doc.BookTxtPath,
                sourcePath,
                replacementPath,
                sourceFolderAbsolute,
                replacementFolderAbsolute,
                originalLines,
                originalPartContent.Value,
                failurePhase: "registry update",
                failureDetail: ex.Message).ConfigureAwait(false);
        }

        foreach (var newPath in pathMap.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var include = await _exclusionManifestService.IncludeAsync(doc.ManuscriptRoot, newPath).ConfigureAwait(false);
            if (!include.IsSuccess)
                return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during manifest save after file move, Book.txt write, and registry update: {include.Error}");
        }

        return Result<Unit>.Ok(Unit.Default);
    }

    public async Task<Result<Unit>> MoveEntryAsync(string bookTxtPath, string existingPath, string replacementPath, int newIndex)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var sourcePath = NormalizeStructurePath(doc.ManuscriptRoot, existingPath, nameof(existingPath));
        if (!sourcePath.IsSuccess)
            return Result<Unit>.Fail(sourcePath.Error!);

        var replacement = NormalizeStructurePath(doc.ManuscriptRoot, replacementPath, nameof(replacementPath));
        if (!replacement.IsSuccess)
            return Result<Unit>.Fail(replacement.Error!);

        if (string.Equals(sourcePath.Value!.RelativePath, replacement.Value!.RelativePath, StringComparison.Ordinal))
            return Result<Unit>.Ok(Unit.Default);

        if (string.Equals(sourcePath.Value.RelativePath, replacement.Value.RelativePath, StringComparison.OrdinalIgnoreCase))
            return Result<Unit>.Fail($"Move operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during path validation: case-only path moves are not supported.");

        var sourceIndex = FindEntryIndex(doc.Entries, sourcePath.Value.RelativePath);
        if (sourceIndex < 0)
            return Result<Unit>.Fail($"Move operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during Book.txt validation: source entry was not found in '{doc.BookTxtPath}'.");

        if (FindEntryIndex(doc.Entries, replacement.Value.RelativePath) >= 0)
            return Result<Unit>.Fail($"Move operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during Book.txt validation: target entry already exists in '{doc.BookTxtPath}'.");

        if (newIndex < 0 || newIndex >= doc.Entries.Count)
            return Result<Unit>.Fail($"Move operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during Book.txt validation: requested move index {newIndex} is out of range for '{doc.BookTxtPath}'.");

        if (!File.Exists(sourcePath.Value.AbsolutePath))
            return Result<Unit>.Fail($"Move operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during file move validation: source file '{sourcePath.Value.AbsolutePath}' does not exist.");

        if (File.Exists(replacement.Value.AbsolutePath))
            return Result<Unit>.Fail($"Move operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during file move validation: target file '{replacement.Value.AbsolutePath}' already exists.");

        var registryValidation = await BuildRegistryMoveAsync(
            doc.ManuscriptRoot,
            sourcePath.Value.RelativePath,
            replacement.Value.RelativePath).ConfigureAwait(false);
        if (!registryValidation.IsSuccess)
            return Result<Unit>.Fail($"Move operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during registry validation: {registryValidation.Error}");

        var originalLines = doc.RawLines.ToList();
        var updatedLines = BuildMoveLines(doc, sourceIndex, replacement.Value.RelativePath, newIndex);

        try
        {
            var targetDirectory = Path.GetDirectoryName(replacement.Value.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            File.Move(sourcePath.Value.AbsolutePath, replacement.Value.AbsolutePath);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"Move operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during file move from '{sourcePath.Value.AbsolutePath}' to '{replacement.Value.AbsolutePath}': {ex.Message}");
        }

        var bookWrite = await WriteBookTxtAsync(doc.BookTxtPath, updatedLines).ConfigureAwait(false);
        if (!bookWrite.IsSuccess)
            return await RollBackMoveAsync(
                doc.BookTxtPath,
                sourcePath.Value,
                replacement.Value,
                restoreBookTxtLines: null,
                failurePhase: "Book.txt write",
                failureDetail: bookWrite.Error!).ConfigureAwait(false);

        try
        {
            await _chapterRegistryService.SaveAsync(doc.ManuscriptRoot, registryValidation.Value!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await RollBackMoveAsync(
                doc.BookTxtPath,
                sourcePath.Value,
                replacement.Value,
                originalLines,
                failurePhase: "registry update",
                failureDetail: ex.Message).ConfigureAwait(false);
        }

        var include = await _exclusionManifestService.IncludeAsync(doc.ManuscriptRoot, replacement.Value.RelativePath).ConfigureAwait(false);
        if (!include.IsSuccess)
            return Result<Unit>.Fail($"Move operation failed for '{sourcePath.Value.RelativePath}' to '{replacement.Value.RelativePath}' during manifest save after file move, Book.txt write, and registry update: {include.Error}");

        return Result<Unit>.Ok(Unit.Default);
    }

    public async Task<Result<Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var normalized = NormalizeStructurePath(doc.ManuscriptRoot, chapterPath, nameof(chapterPath));
        if (!normalized.IsSuccess)
            return Result<Unit>.Fail(normalized.Error!);

        if (FindEntryIndex(doc.Entries, normalized.Value!.RelativePath) >= 0)
            return Result<Unit>.Fail($"Entry '{normalized.Value.RelativePath}' already exists in '{doc.BookTxtPath}'.");

        if (!File.Exists(normalized.Value.AbsolutePath))
            return Result<Unit>.Fail($"Chapter file '{normalized.Value.AbsolutePath}' does not exist.");

        if (index < 0 || index > doc.Entries.Count)
            return Result<Unit>.Fail($"Requested insert index {index} is out of range for '{doc.BookTxtPath}'.");

        var lines = doc.RawLines.ToList();
        var insertionIndex = index == doc.Entries.Count ? lines.Count : doc.EntryLineIndexes[index];
        lines.Insert(insertionIndex, normalized.Value.RelativePath);

        var write = await WriteBookTxtAsync(doc.BookTxtPath, lines).ConfigureAwait(false);
        return write.IsSuccess ? Result<Unit>.Ok(Unit.Default) : Result<Unit>.Fail(write.Error!);
    }

    public async Task<Result<Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var normalizedChapter = NormalizeStructurePath(doc.ManuscriptRoot, chapterPath, nameof(chapterPath));
        if (!normalizedChapter.IsSuccess)
            return Result<Unit>.Fail(normalizedChapter.Error!);

        if (FindEntryIndex(doc.Entries, normalizedChapter.Value!.RelativePath) >= 0)
            return Result<Unit>.Fail($"Entry '{normalizedChapter.Value.RelativePath}' already exists in '{doc.BookTxtPath}'.");

        if (!File.Exists(normalizedChapter.Value.AbsolutePath))
            return Result<Unit>.Fail($"Chapter file '{normalizedChapter.Value.AbsolutePath}' does not exist.");

        var normalizedPart = NormalizeStructurePath(doc.ManuscriptRoot, partPath, nameof(partPath));
        if (!normalizedPart.IsSuccess)
            return Result<Unit>.Fail(normalizedPart.Error!);

        var partIndex = FindEntryIndex(doc.Entries, normalizedPart.Value!.RelativePath);
        if (partIndex < 0)
            return Result<Unit>.Fail($"Part entry '{normalizedPart.Value.RelativePath}' was not found in '{doc.BookTxtPath}'.");

        var partValidation = ValidatePartDivider(doc.ManuscriptRoot, normalizedPart.Value.RelativePath);
        if (!partValidation.IsSuccess)
            return Result<Unit>.Fail(partValidation.Error!);

        var lines = doc.RawLines.ToList();
        var insertionIndex = doc.EntryLineIndexes[partIndex] + 1;
        while (insertionIndex < lines.Count && string.IsNullOrWhiteSpace(lines[insertionIndex]))
            insertionIndex++;

        lines.Insert(insertionIndex, normalizedChapter.Value.RelativePath);

        var write = await WriteBookTxtAsync(doc.BookTxtPath, lines).ConfigureAwait(false);
        return write.IsSuccess ? Result<Unit>.Ok(Unit.Default) : Result<Unit>.Fail(write.Error!);
    }

    public async Task<Result<Unit>> IncludeExistingEntryAsync(string bookTxtPath, string chapterPath, int index)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var normalized = NormalizeStructurePath(doc.ManuscriptRoot, chapterPath, nameof(chapterPath));
        if (!normalized.IsSuccess)
            return Result<Unit>.Fail(normalized.Error!);

        var add = await AddExistingEntryAsync(bookTxtPath, normalized.Value!.RelativePath, index).ConfigureAwait(false);
        if (!add.IsSuccess)
            return Result<Unit>.Fail($"Include operation failed for '{normalized.Value.RelativePath}' during Book.txt write or validation: {add.Error}");

        var include = await _exclusionManifestService.IncludeAsync(doc.ManuscriptRoot, normalized.Value.RelativePath).ConfigureAwait(false);
        if (!include.IsSuccess)
            return Result<Unit>.Fail($"Include operation failed for '{normalized.Value.RelativePath}' during manifest save after Book.txt write: {include.Error}");

        return Result<Unit>.Ok(Unit.Default);
    }

    public async Task<Result<Unit>> IncludeExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var normalized = NormalizeStructurePath(doc.ManuscriptRoot, chapterPath, nameof(chapterPath));
        if (!normalized.IsSuccess)
            return Result<Unit>.Fail(normalized.Error!);

        var add = await AddExistingEntryAfterPartAsync(bookTxtPath, normalized.Value!.RelativePath, partPath).ConfigureAwait(false);
        if (!add.IsSuccess)
            return Result<Unit>.Fail($"Include operation failed for '{normalized.Value.RelativePath}' during Book.txt write or validation: {add.Error}");

        var include = await _exclusionManifestService.IncludeAsync(doc.ManuscriptRoot, normalized.Value.RelativePath).ConfigureAwait(false);
        if (!include.IsSuccess)
            return Result<Unit>.Fail($"Include operation failed for '{normalized.Value.RelativePath}' during manifest save after Book.txt write: {include.Error}");

        return Result<Unit>.Ok(Unit.Default);
    }

    public async Task<Result<Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var normalized = NormalizeStructurePath(doc.ManuscriptRoot, chapterPath, nameof(chapterPath));
        if (!normalized.IsSuccess)
            return Result<Unit>.Fail(normalized.Error!);

        if (FindEntryIndex(doc.Entries, normalized.Value!.RelativePath) >= 0)
            return Result<Unit>.Fail($"Entry '{normalized.Value.RelativePath}' already exists in '{doc.BookTxtPath}'.");

        if (File.Exists(normalized.Value.AbsolutePath))
            return Result<Unit>.Fail($"Chapter file '{normalized.Value.AbsolutePath}' already exists.");

        if (index < 0 || index > doc.Entries.Count)
            return Result<Unit>.Fail($"Requested insert index {index} is out of range for '{doc.BookTxtPath}'.");

        try
        {
            await _metadataStore.WriteTextAtomicAsync(normalized.Value.AbsolutePath, content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"Failed to create chapter file '{normalized.Value.AbsolutePath}': {ex.Message}");
        }

        var lines = doc.RawLines.ToList();
        var insertionIndex = index == doc.Entries.Count ? lines.Count : doc.EntryLineIndexes[index];
        lines.Insert(insertionIndex, normalized.Value.RelativePath);

        var write = await WriteBookTxtAsync(doc.BookTxtPath, lines).ConfigureAwait(false);
        if (write.IsSuccess)
            return Result<Unit>.Ok(Unit.Default);

        TryDeleteFile(normalized.Value.AbsolutePath);
        return Result<Unit>.Fail(write.Error!);
    }

    public async Task<Result<Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Result<Unit>.Fail("Part title is required.");

        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var normalized = NormalizeStructurePath(doc.ManuscriptRoot, partPath, nameof(partPath));
        if (!normalized.IsSuccess)
            return Result<Unit>.Fail(normalized.Error!);

        if (FindEntryIndex(doc.Entries, normalized.Value!.RelativePath) >= 0)
            return Result<Unit>.Fail($"Entry '{normalized.Value.RelativePath}' already exists in '{doc.BookTxtPath}'.");

        if (File.Exists(normalized.Value.AbsolutePath))
            return Result<Unit>.Fail($"Part file '{normalized.Value.AbsolutePath}' already exists.");

        if (index < 0 || index > doc.Entries.Count)
            return Result<Unit>.Fail($"Requested insert index {index} is out of range for '{doc.BookTxtPath}'.");

        var content = $"{{class: part}}\n\n# {title.Trim()}\n";
        try
        {
            var directory = Path.GetDirectoryName(normalized.Value.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await _metadataStore.WriteTextAtomicAsync(normalized.Value.AbsolutePath, content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"Failed to create part file '{normalized.Value.AbsolutePath}': {ex.Message}");
        }

        var lines = doc.RawLines.ToList();
        var insertionIndex = index == doc.Entries.Count ? lines.Count : doc.EntryLineIndexes[index];
        lines.Insert(insertionIndex, normalized.Value.RelativePath);

        var write = await WriteBookTxtAsync(doc.BookTxtPath, lines).ConfigureAwait(false);
        if (write.IsSuccess)
            return Result<Unit>.Ok(Unit.Default);

        TryDeleteFile(normalized.Value.AbsolutePath);
        return Result<Unit>.Fail(write.Error!);
    }

    public async Task<Result<Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var normalized = NormalizeStructurePath(doc.ManuscriptRoot, chapterPath, nameof(chapterPath));
        if (!normalized.IsSuccess)
            return Result<Unit>.Fail(normalized.Error!);

        var entryIndex = FindEntryIndex(doc.Entries, normalized.Value!.RelativePath);
        if (entryIndex < 0)
            return Result<Unit>.Fail($"Entry '{normalized.Value.RelativePath}' was not found in '{doc.BookTxtPath}'.");

        var lines = doc.RawLines.ToList();
        lines.RemoveAt(doc.EntryLineIndexes[entryIndex]);

        var write = await WriteBookTxtAsync(doc.BookTxtPath, lines).ConfigureAwait(false);
        return write.IsSuccess ? Result<Unit>.Ok(Unit.Default) : Result<Unit>.Fail(write.Error!);
    }

    public async Task<Result<Unit>> ExcludeEntryAsync(string bookTxtPath, string chapterPath)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var normalized = NormalizeStructurePath(doc.ManuscriptRoot, chapterPath, nameof(chapterPath));
        if (!normalized.IsSuccess)
            return Result<Unit>.Fail(normalized.Error!);

        var remove = await RemoveEntryAsync(bookTxtPath, normalized.Value!.RelativePath).ConfigureAwait(false);
        if (!remove.IsSuccess)
            return Result<Unit>.Fail($"Exclude operation failed for '{normalized.Value.RelativePath}' during Book.txt write or validation: {remove.Error}");

        var exclude = await _exclusionManifestService.ExcludeAsync(doc.ManuscriptRoot, normalized.Value.RelativePath).ConfigureAwait(false);
        if (!exclude.IsSuccess)
            return Result<Unit>.Fail($"Exclude operation failed for '{normalized.Value.RelativePath}' during manifest save after Book.txt write: {exclude.Error}");

        return Result<Unit>.Ok(Unit.Default);
    }

    public async Task<Result<Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath)
    {
        var document = await LoadDocumentAsync(bookTxtPath).ConfigureAwait(false);
        if (!document.IsSuccess)
            return Result<Unit>.Fail(document.Error!);

        var doc = document.Value!;
        var normalized = NormalizeStructurePath(doc.ManuscriptRoot, chapterPath, nameof(chapterPath));
        if (!normalized.IsSuccess)
            return Result<Unit>.Fail(normalized.Error!);

        var entryIndex = FindEntryIndex(doc.Entries, normalized.Value!.RelativePath);
        if (entryIndex < 0)
            return Result<Unit>.Fail($"Entry '{normalized.Value.RelativePath}' was not found in '{doc.BookTxtPath}'.");

        if (!File.Exists(normalized.Value.AbsolutePath))
            return Result<Unit>.Fail($"Chapter file '{normalized.Value.AbsolutePath}' does not exist.");

        var originalLines = doc.RawLines.ToList();
        var updatedLines = doc.RawLines.ToList();
        updatedLines.RemoveAt(doc.EntryLineIndexes[entryIndex]);

        var write = await WriteBookTxtAsync(doc.BookTxtPath, updatedLines).ConfigureAwait(false);
        if (!write.IsSuccess)
            return Result<Unit>.Fail(write.Error!);

        try
        {
            File.Delete(normalized.Value.AbsolutePath);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex)
        {
            var restore = await WriteBookTxtAsync(doc.BookTxtPath, originalLines).ConfigureAwait(false);
            if (!restore.IsSuccess)
                return Result<Unit>.Fail($"Failed to delete chapter file '{normalized.Value.AbsolutePath}' and failed to restore Book.txt at '{doc.BookTxtPath}': {ex.Message}; restore error: {restore.Error}");

            return Result<Unit>.Fail($"Failed to delete chapter file '{normalized.Value.AbsolutePath}': {ex.Message}");
        }
    }

    private async Task<Result<Dictionary<string, ChapterRegistryEntry>>> BuildRegistryPrefixRenameAsync(
        string manuscriptRoot,
        IReadOnlyDictionary<string, string> pathMap)
    {
        Dictionary<string, ChapterRegistryEntry> registry;
        try
        {
            registry = await _chapterRegistryService.LoadAsync(manuscriptRoot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result<Dictionary<string, ChapterRegistryEntry>>.Fail($"failed to load chapter registry: {ex.Message}");
        }

        var updated = new Dictionary<string, ChapterRegistryEntry>(registry);
        foreach (var (sourcePath, replacementPath) in pathMap)
        {
            var sourceMatches = registry
                .Where(pair => string.Equals(pair.Value.CurrentPath, sourcePath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sourceMatches.Count == 0)
                return Result<Dictionary<string, ChapterRegistryEntry>>.Fail($"no registry entry exists for source path '{sourcePath}'; refusing to rename because UUID continuity cannot be proven.");

            if (sourceMatches.Count > 1)
                return Result<Dictionary<string, ChapterRegistryEntry>>.Fail($"multiple registry entries exist for source path '{sourcePath}'; refusing to rename because UUID continuity is ambiguous.");

            var sourceUuid = sourceMatches[0].Key;
            var targetConflict = registry.Any(pair =>
                !string.Equals(pair.Key, sourceUuid, StringComparison.Ordinal) &&
                string.Equals(pair.Value.CurrentPath, replacementPath, StringComparison.OrdinalIgnoreCase));
            if (targetConflict)
                return Result<Dictionary<string, ChapterRegistryEntry>>.Fail($"another registry entry already targets replacement path '{replacementPath}'.");

            var entry = sourceMatches[0].Value;
            updated[sourceUuid] = new ChapterRegistryEntry
            {
                Uuid = entry.Uuid,
                CurrentPath = replacementPath,
                Orphaned = entry.Orphaned,
                Title = entry.Title
            };
        }

        return Result<Dictionary<string, ChapterRegistryEntry>>.Ok(updated);
    }

    private async Task<Result<string>> ReadTextForRollbackAsync(
        string absolutePath,
        string sourceRelativePath,
        string replacementRelativePath)
    {
        try
        {
            // A UTF-8 file is decoded with its byte order mark kept, so a restore writes back the same bytes.
            var bytes = await File.ReadAllBytesAsync(absolutePath).ConfigureAwait(false);
            try
            {
                return Result<string>.Ok(StrictUtf8.GetString(bytes));
            }
            catch (System.Text.DecoderFallbackException)
            {
                return Result<string>.Ok(await File.ReadAllTextAsync(absolutePath).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Rename operation failed for '{sourceRelativePath}' to '{replacementRelativePath}' during file move validation: source content at '{absolutePath}' could not be read for rollback: {ex.Message}");
        }
    }

    private Result<Unit> TryMoveFileForRename(
        (string RelativePath, string AbsolutePath) sourcePath,
        (string RelativePath, string AbsolutePath) replacementPath)
    {
        try
        {
            var targetDirectory = Path.GetDirectoryName(replacementPath.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            File.Move(sourcePath.AbsolutePath, replacementPath.AbsolutePath);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"Rename operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during file move from '{sourcePath.AbsolutePath}' to '{replacementPath.AbsolutePath}': {ex.Message}; rollback not attempted because no mutation was confirmed.");
        }
    }

    /// <summary>
    /// Sets the file's first <c># </c> heading to <paramref name="title"/> (inserting one at the top when there is
    /// none) through the story file writer, so a trailing metadata block, a UTF-8 byte order mark, the line
    /// endings and a trailing newline are kept. A file that isn't UTF-8 is rewritten as UTF-8 text, as the
    /// editor saves it.
    /// </summary>
    private async Task<Result<Unit>> RewriteFirstHeadingAsync(string absolutePath, string title)
    {
        try
        {
            var written = await _storyWriter.RewriteProseAsync(absolutePath, prose => WithFirstHeading(prose, title))
                .ConfigureAwait(false);
            if (written.IsSuccess)
                return written;
            if (!(await _storyWriter.TryReadFileAsync(absolutePath).ConfigureAwait(false)).NotUtf8)
                return Result<Unit>.Fail($"failed to update first '# ' heading at '{absolutePath}': {written.Error}");

            var text = await File.ReadAllTextAsync(absolutePath).ConfigureAwait(false);
            await _metadataStore.WriteTextAtomicAsync(absolutePath, WithFirstHeading(text, title)).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"failed to update first '# ' heading at '{absolutePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// <paramref name="text"/> with its first line starting (after indentation) with <c># </c> replaced by
    /// <c># </c><paramref name="title"/>, or with that heading inserted as the first line. Every other character,
    /// a leading byte order mark and each line's own line break are kept.
    /// </summary>
    private static string WithFirstHeading(string text, string title)
    {
        const char byteOrderMark = '\uFEFF';
        var bom = text.Length > 0 && text[0] == byteOrderMark ? 1 : 0;
        var heading = "# " + title;

        var lineStart = bom;
        while (lineStart <= text.Length)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? text.Length : newline;
            if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
                lineEnd--;

            if (text.AsSpan(lineStart, lineEnd - lineStart).TrimStart().StartsWith("# ", StringComparison.Ordinal))
                return string.Concat(text.AsSpan(0, lineStart), heading, text.AsSpan(lineEnd));

            if (newline < 0)
                break;
            lineStart = newline + 1;
        }

        var firstNewline = text.IndexOf('\n', bom);
        var lineEnding = firstNewline > 0 && text[firstNewline - 1] == '\r' ? "\r\n" : firstNewline >= 0 ? "\n" : Environment.NewLine;
        return string.Concat(text.AsSpan(0, bom), heading, text.Length > bom ? lineEnding : "", text.AsSpan(bom));
    }

    private async Task<Result<Unit>> RollBackRenameFileAsync(
        string bookTxtPath,
        (string RelativePath, string AbsolutePath) sourcePath,
        (string RelativePath, string AbsolutePath) replacementPath,
        IReadOnlyList<string>? restoreBookTxtLines,
        string? restoreSourceContent,
        string failurePhase,
        string failureDetail)
    {
        var rollbackFailures = new List<string>();

        try
        {
            if (File.Exists(replacementPath.AbsolutePath))
                File.Move(replacementPath.AbsolutePath, sourcePath.AbsolutePath);
            else
                rollbackFailures.Add($"target file '{replacementPath.AbsolutePath}' was missing during rollback");
        }
        catch (Exception ex)
        {
            rollbackFailures.Add($"file rollback from '{replacementPath.AbsolutePath}' to '{sourcePath.AbsolutePath}' failed: {ex.Message}");
        }

        await RestoreRenameRollbackStateAsync(bookTxtPath, restoreBookTxtLines, sourcePath.AbsolutePath, restoreSourceContent, rollbackFailures).ConfigureAwait(false);
        return BuildRenameRollbackFailure(sourcePath.RelativePath, replacementPath.RelativePath, failurePhase, failureDetail, rollbackFailures);
    }

    private async Task<Result<Unit>> RollBackRenameFolderAsync(
        string bookTxtPath,
        (string RelativePath, string AbsolutePath) sourcePath,
        (string RelativePath, string AbsolutePath) replacementPath,
        string sourceFolderAbsolute,
        string replacementFolderAbsolute,
        IReadOnlyList<string>? restoreBookTxtLines,
        string? restorePartContent,
        string failurePhase,
        string failureDetail)
    {
        var rollbackFailures = new List<string>();

        try
        {
            if (Directory.Exists(replacementFolderAbsolute))
                Directory.Move(replacementFolderAbsolute, sourceFolderAbsolute);
            else
                rollbackFailures.Add($"target folder '{replacementFolderAbsolute}' was missing during rollback");
        }
        catch (Exception ex)
        {
            rollbackFailures.Add($"folder rollback from '{replacementFolderAbsolute}' to '{sourceFolderAbsolute}' failed: {ex.Message}");
        }

        await RestoreRenameRollbackStateAsync(bookTxtPath, restoreBookTxtLines, sourcePath.AbsolutePath, restorePartContent, rollbackFailures).ConfigureAwait(false);
        return BuildRenameRollbackFailure(sourcePath.RelativePath, replacementPath.RelativePath, failurePhase, failureDetail, rollbackFailures);
    }

    private async Task RestoreRenameRollbackStateAsync(
        string bookTxtPath,
        IReadOnlyList<string>? restoreBookTxtLines,
        string sourceAbsolutePath,
        string? restoreSourceContent,
        List<string> rollbackFailures)
    {
        if (restoreBookTxtLines is not null)
        {
            var restoreBook = await WriteBookTxtAsync(bookTxtPath, restoreBookTxtLines).ConfigureAwait(false);
            if (!restoreBook.IsSuccess)
                rollbackFailures.Add($"Book.txt rollback failed: {restoreBook.Error}");
        }

        if (restoreSourceContent is not null)
        {
            try
            {
                await _metadataStore.WriteTextAtomicAsync(sourceAbsolutePath, restoreSourceContent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                rollbackFailures.Add($"source heading rollback at '{sourceAbsolutePath}' failed: {ex.Message}");
            }
        }
    }

    private static Result<Unit> BuildRenameRollbackFailure(
        string sourceRelativePath,
        string replacementRelativePath,
        string failurePhase,
        string failureDetail,
        IReadOnlyList<string> rollbackFailures)
    {
        if (rollbackFailures.Count > 0)
        {
            return Result<Unit>.Fail(
                $"Rename operation failed for '{sourceRelativePath}' to '{replacementRelativePath}' during {failurePhase}: {failureDetail}; rollback attempted but rollback failed: {string.Join("; ", rollbackFailures)}.");
        }

        return Result<Unit>.Fail(
            $"Rename operation failed for '{sourceRelativePath}' to '{replacementRelativePath}' during {failurePhase}: {failureDetail}; rollback attempted and manuscript state was restored.");
    }

    private static string DisplayTitleFromChapterPath(string relativePath)
        => TitleCaseToken(Path.GetFileNameWithoutExtension(relativePath));

    private static string DisplayTitleFromPartPath(string relativePath)
    {
        var folder = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
        return TitleCaseToken(string.IsNullOrWhiteSpace(folder) ? Path.GetFileNameWithoutExtension(relativePath) : Path.GetFileName(folder));
    }

    private static string TitleCaseToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var words = token.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return token.Trim();

        return string.Join(" ", words.Select(word => char.ToUpperInvariant(word[0]) + (word.Length == 1 ? string.Empty : word[1..].ToLowerInvariant())));
    }

    private async Task<Result<Dictionary<string, ChapterRegistryEntry>>> BuildRegistryMoveAsync(
        string manuscriptRoot,
        string sourceRelativePath,
        string replacementRelativePath)
    {
        Dictionary<string, ChapterRegistryEntry> registry;
        try
        {
            registry = await _chapterRegistryService.LoadAsync(manuscriptRoot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result<Dictionary<string, ChapterRegistryEntry>>.Fail($"failed to load chapter registry: {ex.Message}");
        }

        var sourceMatches = registry
            .Where(pair => string.Equals(pair.Value.CurrentPath, sourceRelativePath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sourceMatches.Count == 0)
            return Result<Dictionary<string, ChapterRegistryEntry>>.Fail($"no registry entry exists for source path '{sourceRelativePath}'; refusing to move because UUID continuity cannot be proven.");

        if (sourceMatches.Count > 1)
            return Result<Dictionary<string, ChapterRegistryEntry>>.Fail($"multiple registry entries exist for source path '{sourceRelativePath}'; refusing to move because UUID continuity is ambiguous.");

        var sourceUuid = sourceMatches[0].Key;
        var targetConflict = registry.Any(pair =>
            !string.Equals(pair.Key, sourceUuid, StringComparison.Ordinal) &&
            string.Equals(pair.Value.CurrentPath, replacementRelativePath, StringComparison.OrdinalIgnoreCase));
        if (targetConflict)
            return Result<Dictionary<string, ChapterRegistryEntry>>.Fail($"another registry entry already targets replacement path '{replacementRelativePath}'.");

        var updated = _chapterRegistryService.ReconcileRename(registry, sourceRelativePath, replacementRelativePath);
        return Result<Dictionary<string, ChapterRegistryEntry>>.Ok(updated);
    }

    private static List<string> BuildMoveLines(BookDocument doc, int sourceIndex, string replacementRelativePath, int newIndex)
    {
        var lines = doc.RawLines.ToList();
        var sourceRawIndex = doc.EntryLineIndexes[sourceIndex];
        lines[sourceRawIndex] = replacementRelativePath;

        if (sourceIndex == newIndex)
            return lines;

        var movingLine = lines[sourceRawIndex];
        lines.RemoveAt(sourceRawIndex);
        var insertionIndex = FindRawInsertionIndex(lines, newIndex);
        lines.Insert(insertionIndex, movingLine);
        return lines;
    }

    private async Task<Result<Unit>> RollBackMoveAsync(
        string bookTxtPath,
        (string RelativePath, string AbsolutePath) sourcePath,
        (string RelativePath, string AbsolutePath) replacementPath,
        IReadOnlyList<string>? restoreBookTxtLines,
        string failurePhase,
        string failureDetail)
    {
        var rollbackFailures = new List<string>();

        try
        {
            if (File.Exists(replacementPath.AbsolutePath))
                File.Move(replacementPath.AbsolutePath, sourcePath.AbsolutePath);
            else
                rollbackFailures.Add($"target file '{replacementPath.AbsolutePath}' was missing during rollback");
        }
        catch (Exception ex)
        {
            rollbackFailures.Add($"file rollback from '{replacementPath.AbsolutePath}' to '{sourcePath.AbsolutePath}' failed: {ex.Message}");
        }

        if (restoreBookTxtLines is not null)
        {
            var restoreBook = await WriteBookTxtAsync(bookTxtPath, restoreBookTxtLines).ConfigureAwait(false);
            if (!restoreBook.IsSuccess)
                rollbackFailures.Add($"Book.txt rollback failed: {restoreBook.Error}");
        }

        if (rollbackFailures.Count > 0)
        {
            return Result<Unit>.Fail(
                $"Move operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during {failurePhase}: {failureDetail}; rollback attempted but rollback failed: {string.Join("; ", rollbackFailures)}.");
        }

        return Result<Unit>.Fail(
            $"Move operation failed for '{sourcePath.RelativePath}' to '{replacementPath.RelativePath}' during {failurePhase}: {failureDetail}; rollback attempted and manuscript state was restored.");
    }

    private async Task<Result<BookDocument>> LoadDocumentAsync(string bookTxtPath)
    {
        var fullBookTxtPathResult = NormalizeAbsolutePath(bookTxtPath, nameof(bookTxtPath));
        if (!fullBookTxtPathResult.IsSuccess)
            return Result<BookDocument>.Fail(fullBookTxtPathResult.Error!);

        var fullBookTxtPath = fullBookTxtPathResult.Value!;
        if (!File.Exists(fullBookTxtPath))
            return Result<BookDocument>.Fail($"Book.txt not found: '{fullBookTxtPath}'.");

        var manuscriptRoot = Path.GetDirectoryName(fullBookTxtPath);
        if (string.IsNullOrWhiteSpace(manuscriptRoot))
            return Result<BookDocument>.Fail($"Could not determine manuscript root for '{fullBookTxtPath}'.");

        try
        {
            var rawLines = (await File.ReadAllLinesAsync(fullBookTxtPath).ConfigureAwait(false)).ToList();
            var entries = new List<string>();
            var entryLineIndexes = new List<int>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < rawLines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(rawLines[i]))
                    continue;

                var normalizedEntry = NormalizeStructurePath(manuscriptRoot, rawLines[i], $"Book.txt entry line {i + 1}");
                if (!normalizedEntry.IsSuccess)
                    return Result<BookDocument>.Fail(normalizedEntry.Error!);

                if (!seen.Add(normalizedEntry.Value!.RelativePath))
                    return Result<BookDocument>.Fail($"Duplicate Book.txt entry '{normalizedEntry.Value.RelativePath}' found in '{fullBookTxtPath}'.");

                entries.Add(normalizedEntry.Value.RelativePath);
                entryLineIndexes.Add(i);
            }

            return Result<BookDocument>.Ok(new BookDocument(fullBookTxtPath, manuscriptRoot, rawLines, entryLineIndexes, entries));
        }
        catch (Exception ex)
        {
            return Result<BookDocument>.Fail($"Failed to read Book.txt at '{fullBookTxtPath}': {ex.Message}");
        }
    }

    private static Result<string> NormalizeAbsolutePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result<string>.Fail($"{label} is required.");

        try
        {
            return Result<string>.Ok(Path.GetFullPath(path.Trim().Replace('\\', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Invalid {label} '{path}': {ex.Message}");
        }
    }

    private Result<(string RelativePath, string AbsolutePath)> NormalizeStructurePath(string manuscriptRoot, string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result<(string RelativePath, string AbsolutePath)>.Fail($"{label} is required.");

        var cleaned = path.Trim().Replace('\\', Path.DirectorySeparatorChar);
        string absolutePath;

        try
        {
            absolutePath = Path.IsPathRooted(cleaned)
                ? Path.GetFullPath(cleaned)
                : Path.GetFullPath(Path.Combine(manuscriptRoot, cleaned));
        }
        catch (Exception ex)
        {
            return Result<(string RelativePath, string AbsolutePath)>.Fail($"Invalid {label} '{path}': {ex.Message}");
        }

        var manuscriptRootFull = EnsureTrailingSeparator(Path.GetFullPath(manuscriptRoot));
        var absolutePathFull = Path.GetFullPath(absolutePath);
        if (!absolutePathFull.StartsWith(manuscriptRootFull, StringComparison.OrdinalIgnoreCase))
            return Result<(string RelativePath, string AbsolutePath)>.Fail($"{label} '{path}' is outside the manuscript root '{manuscriptRoot}'.");

        var relativePath = Path.GetRelativePath(manuscriptRoot, absolutePathFull).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "." || relativePath.StartsWith("..", StringComparison.Ordinal))
            return Result<(string RelativePath, string AbsolutePath)>.Fail($"{label} '{path}' is outside the manuscript root '{manuscriptRoot}'.");

        if (string.Equals(Path.GetFileName(relativePath), "Book.txt", StringComparison.OrdinalIgnoreCase))
            return Result<(string RelativePath, string AbsolutePath)>.Fail($"{label} '{path}' cannot target Book.txt.");

        return Result<(string RelativePath, string AbsolutePath)>.Ok((relativePath, absolutePathFull));
    }

    private static int FindEntryIndex(IReadOnlyList<string> entries, string path)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i], path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static int FindRawInsertionIndex(IReadOnlyList<string> lines, int entryIndex)
    {
        var currentEntryIndex = 0;
        for (var rawIndex = 0; rawIndex < lines.Count; rawIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[rawIndex]))
                continue;

            if (currentEntryIndex == entryIndex)
                return rawIndex;

            currentEntryIndex++;
        }

        return lines.Count;
    }

    private static Result<Unit> ValidatePartDivider(string manuscriptRoot, string partRelativePath)
    {
        var partAbsolutePath = Path.Combine(manuscriptRoot, partRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(partAbsolutePath))
            return Result<Unit>.Fail($"Part file '{partAbsolutePath}' does not exist.");

        try
        {
            foreach (var line in File.ReadLines(partAbsolutePath))
            {
                if (!string.IsNullOrWhiteSpace(line) && line.Trim() == "{class: part}")
                    return Result<Unit>.Ok(Unit.Default);
            }
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"Failed to inspect part file '{partAbsolutePath}': {ex.Message}");
        }

        return Result<Unit>.Fail($"Entry '{partRelativePath}' is not a Part divider.");
    }

    private async Task<Result<Unit>> WriteBookTxtAsync(string bookTxtPath, IReadOnlyList<string> lines)
    {
        try
        {
            var content = string.Join(Environment.NewLine, lines);
            await _metadataStore.WriteTextAtomicAsync(bookTxtPath, content).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"Failed to write Book.txt at '{bookTxtPath}': {ex.Message}");
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }

    private static void TryDeleteFile(string absolutePath)
    {
        try
        {
            if (File.Exists(absolutePath))
                File.Delete(absolutePath);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private sealed record BookDocument(
        string BookTxtPath,
        string ManuscriptRoot,
        List<string> RawLines,
        List<int> EntryLineIndexes,
        List<string> Entries);
}
