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
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    /// <summary>
    /// Per-batch, lazily populated library state the destination guards need. Both queries
    /// are whole-table reads, so neither runs unless a guard actually reaches for it.
    /// </summary>
    private sealed class ManualImportGuardContext
    {
        private readonly Dictionary<int, IReadOnlyList<string?>> _trackedFilePathsByAudiobook = [];
        private IReadOnlyCollection<string?>? _otherAudiobookManagedPaths;
        private int? _otherManagedPathsAudiobookId;

        public async Task<IReadOnlyList<string?>> GetTrackedFilePathsAsync(
            IAudiobookFileRepository repository,
            int audiobookId,
            CancellationToken cancellationToken)
        {
            if (_trackedFilePathsByAudiobook.TryGetValue(audiobookId, out var cached))
            {
                return cached;
            }

            var files = await repository.GetByAudiobookIdAsync(audiobookId, cancellationToken);
            var paths = files
                .Select(file => string.IsNullOrWhiteSpace(file.CanonicalPath)
                    ? file.Path
                    : file.CanonicalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
            _trackedFilePathsByAudiobook[audiobookId] = paths;
            return paths;
        }

        public async Task<IReadOnlyCollection<string?>> GetOtherAudiobookManagedPathsAsync(
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository fileRepository,
            int audiobookId,
            CancellationToken cancellationToken)
        {
            if (_otherAudiobookManagedPaths != null
                && _otherManagedPathsAudiobookId == audiobookId)
            {
                return _otherAudiobookManagedPaths;
            }

            var audiobooks = await audiobookRepository.GetAllAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var files = await fileRepository.GetAllAsync(cancellationToken);
            var paths = audiobooks
                .Where(candidate => candidate.Id != audiobookId)
                .Select(candidate => candidate.BasePath)
                .Concat(files
                    .Where(file => file.AudiobookId != audiobookId)
                    .Select(file => string.IsNullOrWhiteSpace(file.CanonicalPath)
                        ? file.Path
                        : file.CanonicalPath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
            _otherAudiobookManagedPaths = paths;
            _otherManagedPathsAudiobookId = audiobookId;
            return paths;
        }
    }

    private ManualImportResultDto ToPlanRefusalResult(
        ManualImportPathPlan pathPlan,
        Audiobook audiobook,
        string? sourcePath)
    {
        _logger.LogWarning(
            "Blocked manual import for audiobook {AudiobookId}: {Reason}",
            audiobook.Id,
            LogRedaction.SanitizeText(pathPlan.RefusalMessage));
        return ManualImportResultDto.RefusalResult(
            pathPlan.RefusalCode!,
            pathPlan.RefusalMessage!,
            sourcePath,
            audiobook);
    }

    /// <summary>
    /// The scan refuses to widen a BasePath or to repoint one that already has tracked files
    /// somewhere unrelated. Manual import used to overwrite it with whatever the planner
    /// produced, so the same rule applies here.
    /// </summary>
    private async Task<(string BasePath, string? WarningCode)> ApplyBasePathMonotonicityAsync(
        ManualImportGuardContext guardContext,
        Audiobook audiobook,
        string plannedBasePath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var trackedFilePaths = await guardContext.GetTrackedFilePathsAsync(
            _audiobookFileRepository,
            audiobook.Id,
            cancellationToken);
        var selection = AudiobookBasePathSelector.Select(
            audiobook.BasePath,
            plannedBasePath,
            trackedFilePaths.Count,
            semantics);
        if (!selection.PreservedExisting
            || string.IsNullOrWhiteSpace(selection.SelectedBasePath))
        {
            return (plannedBasePath, null);
        }

        _logger.LogWarning(
            "Manual import kept audiobook {AudiobookId} BasePath {BasePath} instead of the planned {PlannedBasePath}: {Outcome}",
            audiobook.Id,
            LogRedaction.SanitizeFilePath(selection.SelectedBasePath),
            LogRedaction.SanitizeFilePath(plannedBasePath),
            selection.Outcome);
        return (selection.SelectedBasePath!, "base_path_preserved");
    }

    private async Task<ManualImportResultDto?> EvaluateMergeGuardAsync(
        ManualImportGuardContext guardContext,
        Audiobook audiobook,
        string plannedBasePath,
        FileSystemPathSemantics semantics,
        bool allowMerge,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        var trackedPaths = await guardContext.GetTrackedFilePathsAsync(
            _audiobookFileRepository,
            audiobook.Id,
            cancellationToken);
        var decision = ManualImportMergeGuard.Evaluate(
            trackedPaths,
            plannedBasePath,
            semantics);
        if (decision == ManualImportMergeDecision.Allowed)
        {
            return null;
        }

        if (allowMerge)
        {
            _logger.LogInformation(
                "Manual import is merging into audiobook {AudiobookId}, which already tracks {FileCount} file(s) outside {BasePath}, because the request opted in",
                audiobook.Id,
                trackedPaths.Count,
                LogRedaction.SanitizeFilePath(plannedBasePath));
            return null;
        }

        _logger.LogWarning(
            "Blocked manual import into audiobook {AudiobookId}: it already tracks {FileCount} file(s) unrelated to {BasePath}",
            audiobook.Id,
            trackedPaths.Count,
            LogRedaction.SanitizeFilePath(plannedBasePath));
        return ManualImportResultDto.RefusalResult(
            ManualImportMergeGuard.RefusalWarningCode,
            ManualImportMergeGuard.RefusalMessage,
            sourcePath,
            audiobook);
    }
}
