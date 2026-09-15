/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public class UnmatchedScanBackgroundService(
        IUnmatchedScanQueueService queue,
        IUnmatchedScanProcessor processor,
        ILibraryFilesystemReadiness filesystemReadiness,
        ILogger<UnmatchedScanBackgroundService> logger,
        IHubContext<SettingsHub> hubContext,
        IAppMetricsService metrics) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("UnmatchedScanBackgroundService waiting for library filesystem initialization");
            await filesystemReadiness.WaitUntilReadyAsync(stoppingToken);
            logger.LogInformation("UnmatchedScanBackgroundService started");
            try
            {
                await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        metrics.Increment("worker.unmatchedscanbackgroundservice.job.started");
                        await processor.ProcessJobAsync(job, stoppingToken);
                        metrics.Increment("worker.unmatchedscanbackgroundservice.job.completed");
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        metrics.Increment("worker.unmatchedscanbackgroundservice.job.skipped");
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                    catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("UnmatchedScanBackgroundService stopping due to host shutdown");
            }
        }

        private async Task HandleJobFailureAsync(Guid jobId, Exception ex, CancellationToken stoppingToken)
        {
            if (TryGetTerminalJobStatus(jobId, out var terminalStatus)
                && string.Equals(terminalStatus, "Completed", StringComparison.Ordinal))
            {
                // The processor commits results before publishing its SignalR notification.
                // A post-completion notification failure must never downgrade a successful scan.
                metrics.Increment("worker.unmatchedscanbackgroundservice.job.completed");
                logger.LogWarning(
                    ex,
                    "Unmatched scan job {JobId} completed, but a post-completion side effect failed",
                    jobId);
                return;
            }

            metrics.Increment("worker.unmatchedscanbackgroundservice.job.failed");
            logger.LogError(ex, "Unmatched scan job {JobId} failed", jobId);
            if (!string.Equals(terminalStatus, "Failed", StringComparison.Ordinal))
            {
                try
                {
                    queue.UpdateJob(jobId, "Failed", error: ex.Message);
                }
                catch (OperationCanceledException statusException) when (
                    !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        statusException,
                        "Unmatched scan job {JobId} failed, but its failed status update was canceled internally",
                        jobId);
                }
                catch (Exception statusException) when (
                    WorkerExceptionClassifier.IsNonFatal(statusException))
                {
                    logger.LogWarning(
                        statusException,
                        "Unmatched scan job {JobId} failed, but its failed status could not be recorded",
                        jobId);
                }
            }

            try
            {
                await hubContext.Clients.All.SendAsync(
                    "UnmatchedScanComplete",
                    new
                    {
                        jobId = jobId.ToString(),
                        count = 0,
                        error = UnmatchedScanPublicError.FromInternal(ex.Message)
                    },
                    stoppingToken);
            }
            catch (OperationCanceledException notificationException) when (
                !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    notificationException,
                    "Unmatched scan job {JobId} failed, but its completion notification was canceled internally",
                    jobId);
            }
            catch (Exception notificationException) when (
                WorkerExceptionClassifier.IsNonFatal(notificationException))
            {
                logger.LogWarning(
                    notificationException,
                    "Unmatched scan job {JobId} failed, but its completion notification could not be published",
                    jobId);
            }
        }

        private bool TryGetTerminalJobStatus(Guid jobId, out string? terminalStatus)
        {
            terminalStatus = null;
            try
            {
                if (!queue.TryGetJob(jobId, out var current)
                    || current?.Status is not ("Completed" or "Failed"))
                {
                    return false;
                }

                terminalStatus = current.Status;
                return true;
            }
            catch (Exception statusException) when (
                WorkerExceptionClassifier.IsNonFatal(statusException))
            {
                logger.LogWarning(
                    statusException,
                    "Could not inspect terminal state for unmatched scan job {JobId}",
                    jobId);
                return false;
            }
        }

        internal static List<List<string>> BuildGroupedFilesForFolder(
            IEnumerable<string> files,
            string folderPath,
            FileSystemPathSemantics semantics,
            IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile = null) =>
            UnmatchedScanProcessor.BuildGroupedFilesForFolder(
                files,
                folderPath,
                semantics,
                embeddedTagsByFile);
    }
}
