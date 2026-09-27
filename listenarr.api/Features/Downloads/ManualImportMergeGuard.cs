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

public enum ManualImportMergeDecision
{
    /// <summary>The audiobook tracks no files yet, or its files live in the planned folder.</summary>
    Allowed,

    /// <summary>
    /// The audiobook already tracks files in a folder unrelated to the planned one.
    /// Registering here would fold two books into a single library entity.
    /// </summary>
    RefusedFilesElsewhere
}

/// <summary>
/// Decides whether registering an incoming file against an audiobook would merge two
/// distinct books into one entity. The scan path never merges because it attributes by
/// folder; manual import registers by explicit audiobook id and so needs this check.
/// </summary>
public static class ManualImportMergeGuard
{
    public const string RefusalWarningCode = "audiobook_already_has_files_elsewhere";

    public const string RefusalMessage =
        "The audiobook already has files in a different folder. "
        + "Importing here would merge two books into one library entry.";

    public static ManualImportMergeDecision Evaluate(
        IEnumerable<string?> existingTrackedFilePaths,
        string plannedBasePath,
        FileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(existingTrackedFilePaths);
        if (string.IsNullOrWhiteSpace(plannedBasePath))
        {
            return ManualImportMergeDecision.Allowed;
        }

        var sawTrackedFile = false;
        foreach (var filePath in existingTrackedFilePaths)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            string? directory;
            try
            {
                directory = Path.GetDirectoryName(filePath);
            }
            catch (Exception exception) when (exception is
                ArgumentException or PathTooLongException)
            {
                // Unreadable legacy path metadata is not evidence of a second book.
                continue;
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            sawTrackedFile = true;
            try
            {
                if (FileSystemPathIdentity.IsSameOrInside(
                        directory,
                        plannedBasePath,
                        semantics)
                    || FileSystemPathIdentity.IsSameOrInside(
                        plannedBasePath,
                        directory,
                        semantics))
                {
                    return ManualImportMergeDecision.Allowed;
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                // Comparison failure is not proof of relatedness. Fail closed by
                // leaving this file counted but unmatched.
            }
        }

        return sawTrackedFile
            ? ManualImportMergeDecision.RefusedFilesElsewhere
            : ManualImportMergeDecision.Allowed;
    }
}
