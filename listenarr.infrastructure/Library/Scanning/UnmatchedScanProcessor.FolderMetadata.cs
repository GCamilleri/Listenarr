/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public partial class UnmatchedScanProcessor
    {
        /// <summary>
        /// Reads embedded tags for a set of files, counting probe outcomes so the job can
        /// report how much of the library it could actually read.
        /// </summary>
        private async Task<EmbeddedTagReadOutcome> ReadEmbeddedTagsForFilesAsync(
            IEnumerable<string> files,
            string ffprobePath,
            FileSystemPathSemantics semantics,
            IReadOnlyDictionary<string, string> fileObjectIdentities,
            CancellationToken ct)
        {
            var result = new Dictionary<string, PathParsedMetadata>(semantics.Comparer);
            var probed = 0;
            var failures = 0;
            foreach (var file in files)
            {
                var canonicalFile = FileSystemPathIdentity.Canonicalize(
                    file,
                    semantics.Syntax);
                if (!fileObjectIdentities.TryGetValue(
                        canonicalFile,
                        out var expectedPhysicalObjectIdentity))
                {
                    throw new InvalidOperationException(
                        "The unmatched metadata candidate lacks its enumerated physical generation.");
                }

                using var lease = PinnedAudiobookFileRegistrationLease.Open(
                    file,
                    ToExpectedPhysicalIdentity(expectedPhysicalObjectIdentity));
                var read = await PathMetadataParser.ReadEmbeddedTagsAsync(
                    lease.MetadataPath,
                    ffprobePath,
                    ct);
                probed++;
                if (read.ProbeFailed)
                {
                    failures++;
                    _logger.LogDebug(
                        "ffprobe could not read tags for {Path} (exit {ExitCode}): {Error}",
                        LogRedaction.SanitizeFilePath(file),
                        read.ExitCode,
                        read.FailureSummary);
                }

                result[file] = read.Metadata;
                if (!lease.MatchesCurrentPublication())
                {
                    throw new InvalidOperationException(
                        "The unmatched metadata candidate changed during embedded-tag extraction.");
                }
            }

            return new EmbeddedTagReadOutcome(result, probed, failures);
        }

        /// <summary>
        /// Enumeration records a sentinel instead of a real object identity when the
        /// filesystem cannot prove generations (SMB and NFS typically cannot). The
        /// sentinel is not an identity to verify against, so it becomes "no expectation";
        /// the pinned open and the visible-path check still apply.
        /// </summary>
        private static string? ToExpectedPhysicalIdentity(string enumeratedIdentity) =>
            string.Equals(
                enumeratedIdentity,
                ScanFileDiscovery.PinnedPathOnlyIdentity,
                StringComparison.Ordinal)
                ? null
                : enumeratedIdentity;

        internal sealed record EmbeddedTagReadOutcome(
            IReadOnlyDictionary<string, PathParsedMetadata> Tags,
            int FilesProbed,
            int ProbeFailures);

        internal static async Task ApplyPinnedFolderMetadataAsync(
            PathParsedMetadata target,
            string bookFolder,
            ScanFileDiscovery.EnumerationResult enumeration,
            FileSystemPathSemantics semantics,
            bool hasDurableGenerationProof,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (string.IsNullOrWhiteSpace(bookFolder))
            {
                return;
            }

            var canonicalFolder = FileSystemPathIdentity.Canonicalize(
                bookFolder,
                semantics.Syntax);
            if (!enumeration.DirectoryObjectIdentities.TryGetValue(
                    canonicalFolder,
                    out var enumeratedDirectoryIdentity))
            {
                throw new InvalidOperationException(
                    "The unmatched metadata folder lacks its authoritative enumeration proof.");
            }

            // Reading a sidecar and a cover is read-only work behind a pinned handle. It
            // does not need mutation-grade directory identity proof, and requiring it made
            // the scan skip covers, descriptions and narrators on every NAS mount.
            var expectedDirectoryIdentity = hasDurableGenerationProof
                ? enumeratedDirectoryIdentity
                : null;

            using var folder = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                bookFolder,
                createMissing: false);
            if (!folder.VisiblePathMatches()
                || (expectedDirectoryIdentity != null
                    && !folder.MatchesDirectoryObjectIdentity(expectedDirectoryIdentity)))
            {
                throw new InvalidOperationException(
                    "The unmatched metadata folder changed after filesystem enumeration.");
            }
            var expectedNamespaceChangeToken = hasDurableGenerationProof
                ? folder.GetNamespaceChangeToken()
                : null;
            EnsurePinnedFolderMatches(
                folder,
                expectedDirectoryIdentity,
                expectedNamespaceChangeToken);

            var description = await TryReadPinnedTextFileAsync(
                folder,
                "desc.txt",
                maxCharacters: 2000,
                ct);
            EnsurePinnedFolderMatches(
                folder,
                expectedDirectoryIdentity,
                expectedNamespaceChangeToken);
            if (!string.IsNullOrWhiteSpace(description))
            {
                target.Description = description.Trim();
            }

            var narrator = await TryReadPinnedTextFileAsync(
                folder,
                "reader.txt",
                maxCharacters: 512,
                ct);
            EnsurePinnedFolderMatches(
                folder,
                expectedDirectoryIdentity,
                expectedNamespaceChangeToken);
            if (!string.IsNullOrWhiteSpace(narrator))
            {
                target.Narrator = narrator.Trim();
            }

            string[] visibleFiles;
            try
            {
                visibleFiles = Directory.EnumerateFiles(folder.FullPath)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToArray();
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException(
                    "The unmatched metadata folder became unavailable while locating its cover image.",
                    exception);
            }
            EnsurePinnedFolderMatches(
                folder,
                expectedDirectoryIdentity,
                expectedNamespaceChangeToken);

            foreach (var fileName in visibleFiles)
            {
                if (!Path.GetFileNameWithoutExtension(fileName)
                        .Contains("cover", StringComparison.OrdinalIgnoreCase)
                    || !new[] { ".jpg", ".jpeg", ".png", ".webp" }
                        .Contains(
                            Path.GetExtension(fileName),
                            StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var cover = folder.TryOpenExistingFile(
                    fileName,
                    requireDeleteAccess: false);
                if (cover == null || !cover.IsRegularFile() || !cover.VisiblePathMatches())
                {
                    continue;
                }

                EnsurePinnedFolderMatches(
                    folder,
                    expectedDirectoryIdentity,
                    expectedNamespaceChangeToken);
                target.CoverPath = cover.FullPath;
                break;
            }
        }

        private static async Task<string?> TryReadPinnedTextFileAsync(
            PinnedDirectoryCreation.PinnedDirectoryAnchor folder,
            string fileName,
            int maxCharacters,
            CancellationToken ct)
        {
            using var file = folder.TryOpenExistingFile(
                fileName,
                requireDeleteAccess: false);
            if (file == null || !file.IsRegularFile() || !file.VisiblePathMatches())
            {
                return null;
            }

            await using var stream = file.OpenReadStream(
                bufferSize: 4096,
                asynchronous: false);
            using var reader = new StreamReader(
                stream,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            var buffer = new char[maxCharacters + 1];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await reader.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    ct);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }
            if (!file.VisiblePathMatches() || !folder.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The unmatched metadata sidecar changed while it was being read.");
            }

            return new string(buffer, 0, Math.Min(totalRead, maxCharacters));
        }

        private static void EnsurePinnedFolderMatches(
            PinnedDirectoryCreation.PinnedDirectoryAnchor folder,
            string? expectedDirectoryIdentity,
            string? expectedNamespaceChangeToken)
        {
            if (!folder.VisiblePathMatches()
                || (expectedDirectoryIdentity != null
                    && !folder.MatchesDirectoryObjectIdentity(expectedDirectoryIdentity))
                || (expectedNamespaceChangeToken != null
                    && !string.Equals(
                        folder.GetNamespaceChangeToken(),
                        expectedNamespaceChangeToken,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "The unmatched metadata folder changed after filesystem enumeration.");
            }
        }

    }
}
