/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public partial class UnmatchedScanProcessor : IUnmatchedScanProcessor
    {
        private static readonly string[] AudioExtensions = { ".m4b", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav" };
        private sealed record StemGroup(string Stem, List<string> Files);
        private sealed record GroupCandidate(
            string FilePath,
            string Stem,
            bool IsAncillary,
            string TitleKey,
            string TitleKeyWithoutSuffix,
            string AuthorKey);

        private readonly IUnmatchedScanQueueService _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<UnmatchedScanProcessor> _logger;
        private readonly IHubContext<SettingsHub> _hubContext;
        private readonly IFfmpegService _ffmpegService;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;

        public UnmatchedScanProcessor(
            IUnmatchedScanQueueService queue,
            IServiceScopeFactory scopeFactory,
            ILogger<UnmatchedScanProcessor> logger,
            IHubContext<SettingsHub> hubContext,
            IFfmpegService ffmpegService,
            IFileSystemSemanticsResolver semanticsResolver)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _hubContext = hubContext;
            _ffmpegService = ffmpegService;
            _semanticsResolver = semanticsResolver;
        }

        public async Task ProcessJobAsync(UnmatchedScanJob job, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing unmatched scan job {JobId} for {Path}", job.Id, job.RootFolderPath);
            _queue.UpdateJob(job.Id, "Processing");

            var outcome = await ScanAsync(job.RootFolderPath, cancellationToken);
            var results = outcome.Results;

            job.Diagnostics = outcome.Diagnostics;
            _queue.UpdateJob(job.Id, "Completed", results);
            _logger.LogInformation(
                "Unmatched scan job {JobId} completed: {Count} unmatched items, {Probed} files probed, {Failures} probe failures",
                job.Id,
                results.Count,
                outcome.Diagnostics.FilesProbed,
                outcome.Diagnostics.ProbeFailures);

            await _hubContext.Clients.All.SendAsync(
                "UnmatchedScanComplete",
                new { jobId = job.Id.ToString(), count = results.Count },
                cancellationToken);
        }

        internal sealed record UnmatchedScanOutcome(
            List<UnmatchedFileResult> Results,
            UnmatchedScanDiagnostics Diagnostics);

        private async Task<UnmatchedScanOutcome> ScanAsync(string rootFolderPath, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var scanAuthorizationService = scope.ServiceProvider
                .GetRequiredService<IScanPathAuthorizationService>();
            var fileSystem = scope.ServiceProvider.GetRequiredService<IFileSystem>();
            var appSettings = await configService.GetApplicationSettingsAsync();
            var concurrency = Math.Clamp(appSettings?.UnmatchedScanConcurrency ?? 2, 1, 8);
            var authorization = await scanAuthorizationService.AuthorizeAsync(
                rootFolderPath,
                ct);
            if (!authorization.IsAuthorized
                || authorization.Path == null
                || !authorization.Identity.HasValue
                || !authorization.PhysicalIdentity.HasValue)
            {
                throw new InvalidOperationException(
                    authorization.Error
                        ?? "The unmatched scan root could not be authorized safely.");
            }

            var canonicalRootFolderPath = authorization.Path;
            var semantics = authorization.Identity.Value.Semantics;
            var hasDurableGenerationProof =
                authorization.PhysicalIdentity.Value.HasDurableGenerationProof;

            // Load all tracked file paths (normalized) from DB.
            // Check BOTH AudiobookFiles (multi-file imports) AND Audiobook.FilePath (single-file imports)
            // so that files already in the library are not reported as unmatched.
            var trackedFromFiles = await fileRepository.GetAllFilePathsAsync(semantics, ct);

            var allAudiobooks = await audiobookRepository.GetAllAsync();
            var trackedFromAudiobooks = allAudiobooks
                .Where(a => a.FilePath != null)
                .Select(a => a.FilePath!)
                .ToList();

            var trackedNormalized = new HashSet<string>(
                trackedFromFiles.Concat(trackedFromAudiobooks)
                    .Select(path => NormalizePath(path, semantics.Syntax)),
                semantics.Comparer);

            // Walk the root folder tree through the same pinned/generation-aware
            // enumeration primitive used by authoritative audiobook scans.
            using var pinnedRoot = PinnedDirectoryCreation.OpenPinnedBoundary(
                canonicalRootFolderPath);
            if (!pinnedRoot.VisiblePathMatches()
                || (authorization.PhysicalIdentity.Value.HasDurableGenerationProof
                    && !pinnedRoot.MatchesDirectoryObjectIdentity(
                        authorization.PhysicalIdentity.Value.ScanRootObjectIdentity!)))
            {
                throw new InvalidOperationException(
                    "The unmatched scan root changed after authorization.");
            }
            var enumeration = ScanFileDiscovery.CollectCandidates(
                fileSystem,
                canonicalRootFolderPath,
                jobId: Guid.Empty,
                _logger,
                semantics,
                pinnedRoot,
                authorization.PhysicalIdentity.Value.HasDurableGenerationProof);
            if (enumeration.Issues.Any(issue => issue.Kind is
                    ScanDiscoveryIssueKind.DirectoryGenerationChanged))
            {
                throw new InvalidOperationException(
                    "The unmatched scan root changed during enumeration.");
            }

            // One unreadable directory used to abort the whole scan and leave the user
            // with "review the server logs". Report it instead and scan the rest.
            var enumerationFailures = enumeration.Issues
                .Where(issue => issue.Kind == ScanDiscoveryIssueKind.EnumerationFailure)
                .ToList();
            foreach (var failure in enumerationFailures)
            {
                _logger.LogWarning(
                    "Unmatched scan could not read {Path}: {Message}",
                    LogRedaction.SanitizeFilePath(failure.Path ?? rootFolderPath),
                    failure.Message);
            }
            var candidates = enumeration.Candidates.ToList();

            // Filter to untracked files
            var unmatched = candidates
                .Where(f => !trackedNormalized.Contains(NormalizePath(f, semantics.Syntax)))
                .ToList();

            // Two-level grouping:
            // 1. Group by book directory. That is the file's parent directory, except that
            //    disc subfolders (CD1, Disc 02, Part_3) collapse into the folder above them
            //    so a book split across discs stays one book.
            // 2. Within each directory that has multiple files, sub-group by embedded tags
            //    where they are readable and by a normalized filename stem otherwise.
            var folderGroups = unmatched
                .GroupBy(
                    f => ResolveBookFolder(f, canonicalRootFolderPath, semantics),
                    semantics.Comparer)
                .ToList();

            // Resolve ffprobe path once for the whole scan (null = not available).
            // Reading tags is read-only work behind a pinned lease, so it does not need
            // durable generation proof; requiring it left every SMB and NFS mount unread.
            var ffprobePath = OperatingSystem.IsWindows()
                || OperatingSystem.IsLinux()
                || OperatingSystem.IsMacOS()
                    ? await _ffmpegService.GetFfprobePathAsync()
                    : null;
            if (string.IsNullOrEmpty(ffprobePath))
            {
                _logger.LogWarning(
                    "Unmatched scan of {Path} is running without ffprobe; embedded tags will not be read",
                    LogRedaction.SanitizeFilePath(rootFolderPath));
            }

            var results = new System.Collections.Concurrent.ConcurrentBag<UnmatchedFileResult>();
            var filesProbed = 0;
            var probeFailures = 0;

            // Parallel.ForEachAsync only allocates active slots and avoids creating all tasks up front.
            await Parallel.ForEachAsync(folderGroups,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (folderGroup, token) =>
                {
                    var bookFolder = folderGroup.Key;
                    var folderFiles = folderGroup.ToList();
                    IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile = null;

                    if (!string.IsNullOrEmpty(ffprobePath))
                    {
                        var read = await ReadEmbeddedTagsForFilesAsync(
                            folderFiles,
                            ffprobePath,
                            semantics,
                            enumeration.FileObjectIdentities,
                            token);
                        embeddedTagsByFile = read.Tags;
                        Interlocked.Add(ref filesProbed, read.FilesProbed);
                        Interlocked.Add(ref probeFailures, read.ProbeFailures);
                    }

                    var groupedFiles = BuildGroupedFilesForFolder(
                        folderFiles,
                        bookFolder,
                        semantics,
                        embeddedTagsByFile);

                    foreach (var files in groupedFiles)
                    {
                        var plans = MultiFileImportPlanner.BuildPlans(
                            files.Select(f => (FullPath: f, RelativePath: (string?)Path.GetRelativePath(bookFolder, f))),
                            semantics.Comparer);
                        var orderedFiles = plans.Select(p => p.FullPath).ToList();
                        if (orderedFiles.Count == 0)
                        {
                            continue;
                        }

                        // The representative supplies the row's path metadata, its tags and
                        // its id. Taking the sort-first file handed that job to a
                        // "(Foreword by ...)" track; the largest file is both a better
                        // sample and stable across rescans of an unchanged folder.
                        var representative = SelectRepresentative(
                            orderedFiles,
                            enumeration.FileLengths,
                            embeddedTagsByFile);
                        var parsed = PathMetadataParser.ParsePathOnly(
                            representative,
                            rootFolderPath,
                            semantics);
                        await ApplyPinnedFolderMetadataAsync(
                            parsed,
                            parsed.BookFolderPath ?? string.Empty,
                            enumeration,
                            semantics,
                            hasDurableGenerationProof,
                            token);

                        if (embeddedTagsByFile != null
                            && embeddedTagsByFile.TryGetValue(representative, out var tags))
                        {
                            ApplyEmbeddedTags(parsed, tags);
                        }

                        var relativeFolder = bookFolder.Length > rootFolderPath.Length
                            ? bookFolder[(rootFolderPath.Length)..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            : bookFolder;

                        var totalSize = files.Sum(file =>
                            enumeration.FileLengths.TryGetValue(file, out var length)
                                ? length
                                : 0L);

                        results.Add(new UnmatchedFileResult
                        {
                            FullPath = representative,
                            SourceFiles = orderedFiles,
                            RelativePath = relativeFolder,
                            BookFolder = bookFolder,
                            Size = totalSize,
                            FileCount = orderedFiles.Count,
                            Title = parsed.Title,
                            Author = parsed.Author,
                            Series = parsed.Series,
                            SeriesNumber = parsed.SeriesNumber,
                            Year = parsed.Year,
                            Narrator = parsed.Narrator,
                            Description = parsed.Description,
                            CoverPath = parsed.CoverPath,
                            Asin = parsed.Asin,
                            Format = ResolveFormat(orderedFiles),
                            DurationSeconds = SumDurationSeconds(orderedFiles, embeddedTagsByFile)
                        });
                    }
                });

            var diagnostics = BuildDiagnostics(
                tagReadingAvailable: !string.IsNullOrEmpty(ffprobePath),
                filesProbed,
                probeFailures,
                enumerationFailures.Count);

            var ordered = results
                .OrderBy(r => r.Author)
                .ThenBy(r => r.Series)
                .ThenBy(r => r.Title)
                .ToList();
            return new UnmatchedScanOutcome(ordered, diagnostics);
        }

        private static UnmatchedScanDiagnostics BuildDiagnostics(
            bool tagReadingAvailable,
            int filesProbed,
            int probeFailures,
            int directoriesSkipped)
        {
            var messages = new List<string>();
            if (!tagReadingAvailable)
            {
                messages.Add(
                    "ffprobe is not available, so embedded tags were not read. Titles and authors came from the folder layout only.");
            }
            else if (probeFailures > 0)
            {
                messages.Add(
                    FormattableString.Invariant(
                        $"Tag reading failed for {probeFailures} of {filesProbed} files."));
            }

            if (directoriesSkipped > 0)
            {
                messages.Add(
                    FormattableString.Invariant(
                        $"{directoriesSkipped} location(s) could not be read and were skipped."));
            }

            return new UnmatchedScanDiagnostics
            {
                TagReadingAvailable = tagReadingAvailable,
                FilesProbed = filesProbed,
                ProbeFailures = probeFailures,
                DirectoriesSkipped = directoriesSkipped,
                Message = messages.Count == 0 ? null : string.Join(" ", messages)
            };
        }

        /// <summary>
        /// The book folder for a file: its parent directory, with any trailing disc
        /// subfolders (CD1, Disc 02, Part_3) collapsed away so the discs of one book
        /// group together instead of becoming one book each.
        /// </summary>
        internal static string ResolveBookFolder(
            string filePath,
            string canonicalRootFolderPath,
            FileSystemPathSemantics semantics)
        {
            var directory = Path.GetFullPath(
                Path.GetDirectoryName(filePath) ?? canonicalRootFolderPath);
            if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    canonicalRootFolderPath,
                    directory,
                    semantics,
                    out var relative)
                || string.IsNullOrWhiteSpace(relative))
            {
                return directory;
            }

            var separators = semantics.Syntax == FileSystemPathSyntax.Windows
                ? new[] { '\\', '/' }
                : new[] { '/' };
            var segments = relative
                .Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            while (segments.Count > 1 && DiscFolderRules.IsDiscDirectory(segments[^1]))
            {
                segments.RemoveAt(segments.Count - 1);
            }

            return segments.Count == 0
                ? canonicalRootFolderPath
                : Path.GetFullPath(Path.Join(canonicalRootFolderPath, Path.Join(segments.ToArray())));
        }

        /// <summary>
        /// Picks the file that best represents a group: the largest one, falling back to
        /// the first file whose tags carried a title, then to the first file. Every branch
        /// is a total order over the group, so the choice does not move between scans.
        /// </summary>
        internal static string SelectRepresentative(
            IReadOnlyList<string> orderedFiles,
            IReadOnlyDictionary<string, long> fileLengths,
            IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile)
        {
            var best = orderedFiles[0];
            var bestLength = -1L;
            foreach (var file in orderedFiles)
            {
                var length = fileLengths.TryGetValue(file, out var value) ? value : 0L;
                if (length > bestLength
                    || (length == bestLength && string.CompareOrdinal(file, best) < 0))
                {
                    best = file;
                    bestLength = length;
                }
            }

            if (bestLength > 0)
            {
                return best;
            }

            if (embeddedTagsByFile != null)
            {
                var firstTagged = orderedFiles.FirstOrDefault(file =>
                    embeddedTagsByFile.TryGetValue(file, out var tags)
                    && !string.IsNullOrWhiteSpace(tags.Title));
                if (firstTagged != null)
                {
                    return firstTagged;
                }
            }

            return orderedFiles[0];
        }

        private static string ResolveFormat(IEnumerable<string> files) =>
            files
                .Select(file => Path.GetExtension(file).TrimStart('.').ToUpperInvariant())
                .Where(extension => !string.IsNullOrEmpty(extension))
                .GroupBy(extension => extension, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .FirstOrDefault() ?? string.Empty;

        private static double? SumDurationSeconds(
            IEnumerable<string> files,
            IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile)
        {
            if (embeddedTagsByFile == null)
            {
                return null;
            }

            var total = 0d;
            var found = false;
            foreach (var file in files)
            {
                if (embeddedTagsByFile.TryGetValue(file, out var tags)
                    && tags.DurationSeconds is > 0)
                {
                    total += tags.DurationSeconds.Value;
                    found = true;
                }
            }

            return found ? total : null;
        }
    }
}
