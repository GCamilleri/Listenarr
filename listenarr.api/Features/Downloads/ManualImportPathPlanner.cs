/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Application.Common.Naming;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public sealed record ManualImportPathPlan(
    string DestinationPath,
    string AudiobookBasePath,
    string? RefusalCode = null,
    string? RefusalMessage = null)
{
    public bool IsRefused => RefusalCode != null;

    public static ManualImportPathPlan Refused(string code, string message) =>
        new(string.Empty, string.Empty, code, message);
}

public sealed class ManualImportPathPlanner
{
    private readonly IFileNamingService _fileNamingService;

    public ManualImportPathPlanner(IFileNamingService fileNamingService)
    {
        _fileNamingService = fileNamingService;
    }

    public static string? DetermineScanPath(IReadOnlyList<string> destinationPaths)
    {
        return FileUtils.GetCommonDirectory(destinationPaths);
    }

    public async Task<ManualImportPathPlan> GeneratePathAsync(
        Audiobook audiobook,
        AudioMetadata metadata,
        ManualImportItemDto item,
        string destinationBasePath,
        List<RootFolder> rootFolders,
        ApplicationSettings settings,
        FileSystemPathSemantics destinationSemantics,
        bool isMultiFile = false,
        Func<Task<IReadOnlyCollection<string?>>>? otherAudiobookManagedPathsProvider = null)
    {
        await Task.CompletedTask;

        var sourceFilePath = item.FullPath ?? string.Empty;
        var folderPattern = settings.FolderNamingPattern;
        var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;

        var requestedBasePath = string.IsNullOrWhiteSpace(destinationBasePath)
            ? string.Empty
            : FileUtils.NormalizeStoredPath(destinationBasePath);

        // One predicate decides this for import and for organize alike. A pattern-managed
        // destination is re-planned from the root that contains it, so both flows produce
        // the same folder; a user-pinned one is committed verbatim.
        var classification = LibraryBasePathPolicy.Classify(
            requestedBasePath,
            audiobook.BasePathIsUserPinned,
            settings.OutputPath,
            rootFolders,
            destinationSemantics);
        var isCustomBasePath = classification.IsUserPinned;
        var basePath = string.IsNullOrWhiteSpace(classification.PatternRoot)
            ? requestedBasePath
            : classification.PatternRoot;

        // A custom base is committed verbatim as the audiobook folder, so it must not be a
        // folder that already holds other books. Anything else there would be attributed to
        // this one book by every later scan, move, rename and delete.
        if (isCustomBasePath && otherAudiobookManagedPathsProvider != null)
        {
            var otherAudiobookManagedPaths = await otherAudiobookManagedPathsProvider();
            if (otherAudiobookManagedPaths.Count > 0
                && ManualImportSharedFolderGuard.ContainsOtherAudiobookPaths(
                    basePath,
                    otherAudiobookManagedPaths,
                    destinationSemantics))
            {
                return ManualImportPathPlan.Refused(
                    ManualImportSharedFolderGuard.RefusalWarningCode,
                    ManualImportSharedFolderGuard.RefusalMessage);
            }
        }

        var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".m4b";
        }

        var variables = BuildNamingVariables(audiobook, metadata, item, isMultiFile, out var stableSuffixNumber);

        string relativePath;
        string? plannedFolderRelative = null;
        var patternHasNumberTokens = !string.IsNullOrWhiteSpace(filePattern)
            && (filePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || filePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0);

        if (string.IsNullOrWhiteSpace(folderPattern))
        {
            var legacyPattern = string.IsNullOrWhiteSpace(filePattern)
                ? "{Author}/{Title}/{Title}"
                : filePattern;

            relativePath = _fileNamingService.ApplyNamingPattern(legacyPattern, variables, treatAsFilename: false);
        }
        else if (isCustomBasePath)
        {
            var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
            var patternAllowsSubfolders = PatternAllowsSubfolders(effectiveFilePattern);

            relativePath = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !patternAllowsSubfolders);
        }
        else
        {
            var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
            var folderRelative = _fileNamingService.ApplyNamingPattern(folderPattern, variables, treatAsFilename: false);
            plannedFolderRelative = folderRelative;
            var patternAllowsSubfolders = PatternAllowsSubfolders(effectiveFilePattern);
            var fileRelative = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !patternAllowsSubfolders);

            if (isMultiFile && !patternHasNumberTokens && stableSuffixNumber.HasValue)
                fileRelative = FileUtils.AppendSequenceSuffix(fileRelative, stableSuffixNumber.Value);

            relativePath = string.IsNullOrWhiteSpace(folderRelative)
                ? fileRelative
                : CombineWithOptionalBase(folderRelative, fileRelative);
        }

        if ((string.IsNullOrWhiteSpace(folderPattern) || isCustomBasePath)
            && isMultiFile
            && !patternHasNumberTokens
            && stableSuffixNumber.HasValue)
        {
            relativePath = FileUtils.AppendSequenceSuffix(relativePath, stableSuffixNumber.Value);
        }

        if (!relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            relativePath += extension;
        }

        var destinationPath = string.IsNullOrWhiteSpace(basePath)
            ? relativePath
            : CombineWithOptionalBase(basePath, relativePath);
        var audiobookBasePath = ResolveAudiobookBasePath(
            basePath,
            relativePath,
            plannedFolderRelative,
            string.IsNullOrWhiteSpace(folderPattern),
            isCustomBasePath);
        return new ManualImportPathPlan(destinationPath, audiobookBasePath);
    }

    private static string ResolveAudiobookBasePath(
        string basePath,
        string relativePath,
        string? plannedFolderRelative,
        bool usesLegacyPattern,
        bool isCustomBasePath)
    {
        if (isCustomBasePath || string.IsNullOrWhiteSpace(basePath))
        {
            return basePath;
        }

        var relativeDirectory = usesLegacyPattern
            ? Path.GetDirectoryName(relativePath)
            : plannedFolderRelative;
        return string.IsNullOrWhiteSpace(relativeDirectory)
            ? basePath
            : CombineWithOptionalBase(basePath, relativeDirectory);
    }

    public static string CombineWithOptionalBase(string? basePath, string candidatePath)
    {
        return FileUtils.CombineWithOptionalBase(basePath, candidatePath);
    }

    public static List<ManualImportItemDto> BuildOrderedItems(
        IEnumerable<ManualImportItemDto> items,
        StringComparer sourcePathComparer)
    {
        var ordered = new List<ManualImportItemDto>();

        foreach (var validItems in items.GroupBy(i => i.MatchedAudiobookId).Select(g => g.Where(i => !string.IsNullOrWhiteSpace(i.FullPath)).ToList()))
        {
            if (validItems.Count == 0)
            {
                continue;
            }

            var plans = MultiFileImportPlanner.BuildPlans(
                validItems.Select(i => (i.FullPath!, string.IsNullOrWhiteSpace(i.RelativePath) ? null : i.RelativePath)),
                sourcePathComparer);
            var itemLookup = validItems.ToDictionary(i => i.FullPath!, sourcePathComparer);
            var diskNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plans, p => p.DiskNumberHint, sourcePathComparer);
            var chapterNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plans, p => p.ChapterNumberHint, sourcePathComparer);

            ordered.AddRange(plans
                .Select(plan =>
                {
                    if (!itemLookup.TryGetValue(plan.FullPath, out var item))
                    {
                        return null;
                    }

                    item.SequenceNumberHint = plan.SequenceNumber;
                    item.DiskNumberHint = diskNumbersForNaming.TryGetValue(plan.FullPath, out var diskNumber) ? diskNumber : plan.DiskNumberHint;
                    item.ChapterNumberHint = chapterNumbersForNaming.TryGetValue(plan.FullPath, out var chapterNumber) ? chapterNumber : plan.ChapterNumberHint;
                    return item;
                })
                .Where(item => item != null)!
                .Cast<ManualImportItemDto>());
        }

        foreach (var invalidItem in items.Where(i => string.IsNullOrWhiteSpace(i.FullPath)))
        {
            ordered.Add(invalidItem);
        }

        return ordered;
    }

    private static Dictionary<string, object> BuildNamingVariables(
        Audiobook audiobook,
        AudioMetadata metadata,
        ManualImportItemDto item,
        bool isMultiFile,
        out int? stableSuffixNumber)
    {
        var effectiveDiskNumber = item.DiskNumberHint
            ?? (metadata.DiscNumber.HasValue && metadata.DiscNumber.Value > 0 ? metadata.DiscNumber.Value : null);
        var effectiveChapterNumber = item.ChapterNumberHint
            ?? (metadata.TrackNumber.HasValue && metadata.TrackNumber.Value > 0 ? metadata.TrackNumber.Value : null);

        if (isMultiFile)
        {
            effectiveDiskNumber ??= effectiveChapterNumber;
            effectiveChapterNumber ??= effectiveDiskNumber;
        }

        stableSuffixNumber = effectiveChapterNumber ?? effectiveDiskNumber ?? item.SequenceNumberHint;
        return NamingVariableBuilder.FromAudiobook(
            audiobook,
            effectiveDiskNumber,
            effectiveChapterNumber);
    }

    private static bool PatternAllowsSubfolders(string effectiveFilePattern)
    {
        return effectiveFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
            || effectiveFilePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
            || effectiveFilePattern.IndexOf('/') >= 0
            || effectiveFilePattern.IndexOf('\\') >= 0;
    }
}
