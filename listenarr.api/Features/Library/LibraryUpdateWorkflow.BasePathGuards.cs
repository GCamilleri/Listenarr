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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryUpdateWorkflow
    {
        /// <summary>
        /// The deprecated BasePath update relabels every stored path reference without moving
        /// a single file. Two rewrites are never recoverable: adopting a configured root as a
        /// book folder, and rebasing onto a folder that does not actually hold the files.
        /// </summary>
        private async Task<IActionResult?> ValidateBasePathRewriteAsync(
            int audiobookId,
            string requestedBasePath,
            string? currentBasePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(requestedBasePath))
            {
                return null;
            }

            var rootFolders = await _rootFolderService.GetAllAsync();
            cancellationToken.ThrowIfCancellationRequested();

            // Exact canonical spelling is safe to compare without consulting live storage.
            if (MatchesConfiguredRootSpelling(rootFolders, requestedBasePath))
            {
                return RejectRoot(audiobookId, requestedBasePath);
            }

            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(
                audiobookId,
                cancellationToken);
            if (files.Count == 0 || rootFolders.Count == 0)
            {
                return null;
            }

            // The requested folder may not exist yet, which is exactly the case this guard is
            // for, so semantics come from the configured root that would contain it.
            var containment = await ResolveContainingRootSemanticsAsync(
                rootFolders,
                requestedBasePath,
                cancellationToken);
            if (containment == null)
            {
                // Outside every configured root, or no root whose identity can be resolved.
                // The rewrite service rejects out-of-root destinations with a more specific
                // message than anything this guard could produce, so let it.
                return null;
            }

            var (semantics, containingRootPath) = containment.Value;
            try
            {
                if (FileSystemPathIdentity.AreEquivalent(
                        containingRootPath,
                        requestedBasePath,
                        semantics))
                {
                    return RejectRoot(audiobookId, requestedBasePath);
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                // Broken root metadata is not evidence that this path is that root.
            }

            foreach (var file in files)
            {
                var storedPath = string.IsNullOrWhiteSpace(file.CanonicalPath)
                    ? file.Path
                    : file.CanonicalPath;
                if (string.IsNullOrWhiteSpace(storedPath))
                {
                    continue;
                }

                // Only absolute references under the current base get rebased. Relative and
                // out-of-base entries are left alone, so the rewrite cannot make them wrong.
                if (!TryRebaseFilePath(
                        storedPath,
                        currentBasePath,
                        requestedBasePath,
                        semantics,
                        out var rebasedPath))
                {
                    continue;
                }

                if (!_fileSystem.FileExists(rebasedPath))
                {
                    _logger.LogWarning(
                        "Rejected BasePath update for audiobook {AudiobookId}: a registered file would not exist under {BasePath}",
                        audiobookId,
                        LogRedaction.SanitizeFilePath(requestedBasePath));
                    return BasePathRejection(
                        "destination_base_path_missing_files",
                        "The registered files for this audiobook are not present under the requested library folder. Use the move endpoint to relocate them.");
                }
            }

            return null;
        }

        private IActionResult RejectRoot(int audiobookId, string requestedBasePath)
        {
            _logger.LogWarning(
                "Rejected BasePath update for audiobook {AudiobookId}: {BasePath} is a configured root folder",
                audiobookId,
                LogRedaction.SanitizeFilePath(requestedBasePath));
            return BasePathRejection(
                "destination_base_path_is_root",
                "A configured root folder cannot become an audiobook library folder.");
        }

        private static bool MatchesConfiguredRootSpelling(
            IEnumerable<RootFolder> rootFolders,
            string requestedBasePath)
        {
            var canonicalRequested = CanonicalizeOrSelf(requestedBasePath);
            return rootFolders
                .Where(root => !string.IsNullOrWhiteSpace(root.Path))
                .Any(root => string.Equals(
                    CanonicalizeOrSelf(root.Path),
                    canonicalRequested,
                    StringComparison.Ordinal));
        }

        private static string CanonicalizeOrSelf(string path) =>
            FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var normalized,
                out _)
                ? normalized
                : path;

        private async Task<(FileSystemPathSemantics Semantics, string RootPath)?>
            ResolveContainingRootSemanticsAsync(
                IReadOnlyCollection<RootFolder> rootFolders,
                string requestedBasePath,
                CancellationToken cancellationToken)
        {
            if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                    requestedBasePath,
                    out var requestedSyntax))
            {
                return null;
            }

            (FileSystemPathSemantics Semantics, string RootPath)? best = null;
            var bestLength = -1;
            foreach (var root in rootFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(root.Path)
                    || !FileSystemPathIdentity
                        .TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                            root.Path,
                            out var canonicalRoot,
                            out _)
                    || !FileSystemPathIdentity.StoredBoundaryMayContainPath(
                        canonicalRoot,
                        requestedBasePath,
                        requestedSyntax,
                        root.CaseSensitivityMode)
                    || canonicalRoot.Length <= bestLength)
                {
                    continue;
                }

                FileSystemSemanticsResolution resolution;
                try
                {
                    resolution = await _fileSystemSemanticsResolver.ResolveAsync(
                        canonicalRoot,
                        root.CaseSensitivityMode,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is
                    IOException or UnauthorizedAccessException or ArgumentException or
                    InvalidOperationException or NotSupportedException or PathTooLongException or
                    System.ComponentModel.Win32Exception or
                    System.Security.SecurityException)
                {
                    continue;
                }

                if (resolution.State != PathIdentityState.Valid
                    || resolution.Semantics.CaseSensitivity
                        == FileSystemCaseSensitivity.Unknown)
                {
                    continue;
                }

                best = (resolution.Semantics, canonicalRoot);
                bestLength = canonicalRoot.Length;
            }

            return best;
        }

        private static bool TryRebaseFilePath(
            string storedPath,
            string? currentBasePath,
            string requestedBasePath,
            FileSystemPathSemantics semantics,
            out string rebasedPath)
        {
            rebasedPath = string.Empty;
            try
            {
                if (FileSystemPathIdentity.IsSameOrInside(
                        storedPath,
                        requestedBasePath,
                        semantics))
                {
                    rebasedPath = storedPath;
                    return true;
                }

                if (string.IsNullOrWhiteSpace(currentBasePath)
                    || !FileSystemPathIdentity.IsSameOrInside(
                        storedPath,
                        currentBasePath,
                        semantics))
                {
                    return false;
                }

                var relative = Path.GetRelativePath(currentBasePath, storedPath);
                if (string.IsNullOrWhiteSpace(relative)
                    || Path.IsPathRooted(relative)
                    || relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return false;
                }

                rebasedPath = Path.Join(requestedBasePath, relative);
                return true;
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                return false;
            }
        }

        private static BadRequestObjectResult BasePathRejection(
            string code,
            string message) =>
            new(new { message, code });
    }
}
