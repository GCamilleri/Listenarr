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

namespace Listenarr.Api.Features.Downloads;

/// <summary>
/// A custom BasePath that already contains another audiobook's folder or tracked file is
/// a shared parent, not a book folder. Committing it would make every later scan, move,
/// rename and delete treat the whole subtree as one book.
/// </summary>
public static class ManualImportSharedFolderGuard
{
    public const string RefusalWarningCode = "base_path_is_shared_parent";

    public const string RefusalMessage =
        "The audiobook library folder contains other audiobooks. "
        + "Importing into it would claim a shared folder as this book's folder.";

    public static bool ContainsOtherAudiobookPaths(
        string basePath,
        IEnumerable<string?> otherAudiobookManagedPaths,
        FileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(otherAudiobookManagedPaths);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        foreach (var candidate in otherAudiobookManagedPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                if (FileSystemPathIdentity.AreEquivalent(candidate, basePath, semantics))
                {
                    // An exactly equal claim is the in-place multi-book case that
                    // FileAction.None supports. Only a strict descendant proves the
                    // base is a parent folder holding separate books.
                    continue;
                }

                if (FileSystemPathIdentity.IsSameOrInside(candidate, basePath, semantics))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                // Broken stored path metadata is not evidence of a shared parent.
            }
        }

        return false;
    }
}
