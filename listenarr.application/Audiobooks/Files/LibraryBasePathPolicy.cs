/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

/// <summary>
/// How a BasePath should be treated by anything that plans a path for it.
/// </summary>
/// <param name="IsPatternManaged">
/// True when the folder pattern owns this book's folder, so a planner re-derives it as
/// <c>PatternRoot / folderPattern</c>. False when the user pinned the folder, so the
/// path is committed verbatim and only filenames inside it are planned.
/// </param>
/// <param name="PatternRoot">
/// The base a pattern-managed plan hangs off: the deepest configured root containing the
/// path, or the configured output path. For a user-pinned book it is the pinned folder.
/// Empty only when no configured destination exists at all.
/// </param>
public sealed record LibraryBasePathClassification(
    bool IsPatternManaged,
    string PatternRoot)
{
    public bool IsUserPinned => !IsPatternManaged;
}

/// <summary>
/// The single answer to "is this BasePath user-pinned or pattern-managed".
/// </summary>
/// <remarks>
/// Rename used to call anything inside a root pattern-managed; manual import called only
/// a path exactly equal to a root or to OutputPath pattern-managed. Neither could be
/// right, because the path alone does not record whether the user chose the folder. The
/// <see cref="Audiobook.BasePathIsUserPinned"/> flag records it, and this policy is the
/// only place that reads it.
/// </remarks>
public static class LibraryBasePathPolicy
{
    public static LibraryBasePathClassification Classify(
        string? basePath,
        bool basePathIsUserPinned,
        string? configuredOutputPath,
        IReadOnlyCollection<RootFolder> rootFolders,
        FileSystemPathSemantics semantics,
        bool includePinned = false)
    {
        ArgumentNullException.ThrowIfNull(rootFolders);

        try
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                // Nothing to pin. Plan from the configured destination.
                return new LibraryBasePathClassification(
                    true,
                    ResolveDefaultRoot(configuredOutputPath, rootFolders));
            }

            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    basePath,
                    out var canonicalBasePath,
                    out _))
            {
                // Fail closed. An unreadable path cannot be proven to be pattern-managed,
                // and treating it as such would relocate it on the next organize run.
                return new LibraryBasePathClassification(false, basePath);
            }

            if (basePathIsUserPinned && !includePinned)
            {
                return new LibraryBasePathClassification(false, canonicalBasePath);
            }

            var containingRoot = rootFolders
                .Select(root => TryCanonicalize(root.Path))
                .Where(root => root != null
                    && FileSystemPathIdentity.IsSameOrInside(canonicalBasePath, root, semantics))
                .OrderByDescending(root => root!.Length)
                .FirstOrDefault();
            if (containingRoot != null)
            {
                return new LibraryBasePathClassification(true, containingRoot);
            }

            if (rootFolders.Count == 0)
            {
                var outputPath = TryCanonicalize(configuredOutputPath);
                if (outputPath != null
                    && FileSystemPathIdentity.IsSameOrInside(canonicalBasePath, outputPath, semantics))
                {
                    return new LibraryBasePathClassification(true, outputPath);
                }
            }

            // Outside every configured root: the user put it there deliberately.
            return new LibraryBasePathClassification(false, canonicalBasePath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            // Fail closed, as above: an incomplete comparison never authorizes a move.
            return new LibraryBasePathClassification(false, basePath ?? string.Empty);
        }
    }

    private static string ResolveDefaultRoot(
        string? configuredOutputPath,
        IReadOnlyCollection<RootFolder> rootFolders)
    {
        if (rootFolders.Count > 0)
        {
            var configuredRoot = rootFolders.FirstOrDefault(root => root.IsDefault)?.Path
                ?? rootFolders.First().Path;
            return TryCanonicalize(configuredRoot) ?? string.Empty;
        }

        return TryCanonicalize(configuredOutputPath) ?? string.Empty;
    }

    private static string? TryCanonicalize(string? path) =>
        !string.IsNullOrWhiteSpace(path)
            && FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var canonical,
                out _)
            ? canonical
            : null;
}
