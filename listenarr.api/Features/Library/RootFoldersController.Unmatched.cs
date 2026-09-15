/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public partial class RootFoldersController
    {
        /// <summary>
        /// Enqueues a background scan of a root folder to find audio files not in the library.
        /// Returns a jobId; subscribe to the realtime "UnmatchedScanComplete" event for completion notification.
        /// </summary>
        [HttpPost("{id}/scan-unmatched")]
        public async Task<IActionResult> ScanUnmatched(int id)
        {
            _filesystemMutationGate.EnsureReady();

            var folder = await _service.GetByIdAsync(id);
            if (folder == null) return NotFound(new { message = "Root folder not found" });

            var storage = await _storageHealthResolver.ResolveAsync(folder);
            if (!storage.CanScanFilesystem)
            {
                return Conflict(new
                {
                    message = storage.Message
                        ?? "The root folder cannot be scanned in its current storage state.",
                    code = "root_folder_scan_unavailable"
                });
            }

            var jobId = await _unmatchedQueue.EnqueueAsync(folder.Path);
            return Ok(new { jobId = jobId.ToString() });
        }

        /// <summary>
        /// Returns the status and results of a previously enqueued unmatched scan job.
        /// </summary>
        [HttpGet("unmatched-results/{jobId}")]
        public async Task<IActionResult> GetUnmatchedResults(Guid jobId)
        {
            if (!_unmatchedQueue.TryGetJob(jobId, out var job) || job == null)
                return NotFound(new { message = "Scan job not found" });

            // Results are held in memory for an hour, so files can be imported between the
            // scan finishing and this call. Without the same filter the saved endpoint
            // applies, those files are offered for import a second time.
            var items = await FilterTrackedFilesAsync(
                job.Results,
                job.RootFolderPath);

            return Ok(new
            {
                jobId = job.Id.ToString(),
                status = job.Status,
                error = UnmatchedScanPublicError.FromInternal(job.Error),
                diagnostics = job.Diagnostics,
                items
            });
        }

        /// <summary>
        /// Returns the cached results from the last completed unmatched scan for a root folder.
        /// Returns an empty list if no scan has been run yet this session.
        /// </summary>
        [HttpGet("{id}/unmatched")]
        public async Task<IActionResult> GetSavedUnmatched(int id)
        {
            var folder = await _service.GetByIdAsync(id);
            if (folder == null) return NotFound(new { message = "Root folder not found" });

            if (_unmatchedQueue.TryGetLastJobForPath(folder.Path, out var job) && job != null)
            {
                var trackedPathSemantics = await ResolveFolderSemanticsAsync(folder);
                var filtered = FilterTrackedFiles(
                    job.Results,
                    await LoadTrackedPathsAsync(trackedPathSemantics),
                    trackedPathSemantics);

                return Ok(new
                {
                    lastScannedAt = job.CompletedAt,
                    diagnostics = job.Diagnostics,
                    items = filtered
                });
            }

            return Ok(new
            {
                lastScannedAt = (DateTime?)null,
                diagnostics = (UnmatchedScanDiagnostics?)null,
                items = new List<UnmatchedFileResult>()
            });
        }

        private async Task<List<UnmatchedFileResult>> FilterTrackedFilesAsync(
            List<UnmatchedFileResult>? results,
            string rootFolderPath)
        {
            // The job carries its root path; resolve that root's semantics the same way
            // the saved-results endpoint does, so both apply an identical tracked filter.
            var folder = (await _service.GetAllAsync())
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Path,
                    rootFolderPath,
                    StringComparison.Ordinal));
            var semantics = folder != null
                ? await ResolveFolderSemanticsAsync(folder)
                : await ResolvePathSemanticsAsync(
                    rootFolderPath,
                    FileSystemCaseSensitivityMode.Auto);

            return FilterTrackedFiles(
                results,
                await LoadTrackedPathsAsync(semantics),
                semantics);
        }

        private async Task<HashSet<string>> LoadTrackedPathsAsync(
            FileSystemPathSemantics semantics)
        {
            var trackedFromFiles = await _fileRepository.GetAllFilePathsAsync(semantics);
            var trackedFromAudiobooks = (await _audiobookRepository.GetAllAsync())
                .Where(a => a.FilePath != null)
                .Select(a => a.FilePath!)
                .ToList();

            return trackedFromFiles
                .Concat(trackedFromAudiobooks)
                .Select(path => TryCanonicalizePathForComparison(path, semantics))
                .Where(path => path != null)
                .Select(path => path!)
                .ToHashSet(semantics.Comparer);
        }

        /// <summary>
        /// Drops files that are already in the library from each row and recomputes the
        /// row's counts. Filtering on the representative file alone hid rows whose
        /// representative had been imported and re-offered files behind rows whose other
        /// files had been.
        /// </summary>
        private List<UnmatchedFileResult> FilterTrackedFiles(
            List<UnmatchedFileResult>? results,
            HashSet<string> tracked,
            FileSystemPathSemantics semantics)
        {
            var filtered = new List<UnmatchedFileResult>();
            foreach (var result in results ?? new List<UnmatchedFileResult>())
            {
                var sourceFiles = result.SourceFiles.Count > 0
                    ? result.SourceFiles
                    : new List<string> { result.FullPath };

                var remaining = sourceFiles
                    .Where(path =>
                    {
                        var canonicalPath = TryCanonicalizePathForComparison(path, semantics);
                        return canonicalPath != null
                            && !tracked.Contains(canonicalPath)
                            && _fileSystem.FileExists(path);
                    })
                    .ToList();

                if (remaining.Count == 0)
                {
                    continue;
                }

                if (remaining.Count == sourceFiles.Count
                    && remaining.Contains(result.FullPath, semantics.Comparer))
                {
                    filtered.Add(result);
                    continue;
                }

                filtered.Add(CloneWithRemainingFiles(result, remaining, semantics));
            }

            return filtered;
        }

        private static UnmatchedFileResult CloneWithRemainingFiles(
            UnmatchedFileResult result,
            List<string> remaining,
            FileSystemPathSemantics semantics)
        {
            var fullPath = remaining.Contains(result.FullPath, semantics.Comparer)
                ? result.FullPath
                : remaining[0];
            var removed = result.SourceFiles.Count - remaining.Count;
            var size = result.FileCount > 0 && removed > 0
                ? result.Size * remaining.Count / result.FileCount
                : result.Size;

            return new UnmatchedFileResult
            {
                FullPath = fullPath,
                SourceFiles = remaining,
                RelativePath = result.RelativePath,
                BookFolder = result.BookFolder,
                Size = size,
                FileCount = remaining.Count,
                Title = result.Title,
                Author = result.Author,
                Series = result.Series,
                SeriesNumber = result.SeriesNumber,
                Year = result.Year,
                Narrator = result.Narrator,
                Description = result.Description,
                CoverPath = result.CoverPath,
                Asin = result.Asin,
                Format = result.Format,
                DurationSeconds = result.DurationSeconds
            };
        }
    }
}
