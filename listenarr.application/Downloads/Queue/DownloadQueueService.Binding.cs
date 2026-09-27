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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Queue
{
    public partial class DownloadQueueService
    {
        private async Task PersistDiscoveredClientIdentifiersAsync(
            DownloadQueueMatch match,
            DownloadClientConfiguration client,
            string? originalClientId,
            HashSet<string> allKnownClientItemIds,
            HashSet<string> liveClientItemIds)
        {
            var matchedDownload = match.Download;
            if (matchedDownload == null ||
                string.IsNullOrWhiteSpace(originalClientId) ||
                string.Equals(originalClientId, matchedDownload.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            matchedDownload.Metadata ??= new Dictionary<string, object>();

            var existingClientDownloadId = DownloadQueueMetadataMatcher.GetMetadataString(matchedDownload.Metadata, "ClientDownloadId");
            var existingTorrentHash = DownloadQueueMetadataMatcher.GetMetadataString(matchedDownload.Metadata, "TorrentHash");
            var isTorrentClient =
                string.Equals(client.Type, "qbittorrent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(client.Type, "transmission", StringComparison.OrdinalIgnoreCase);

            if (!CanBindToClientItem(
                    match,
                    matchedDownload,
                    originalClientId,
                    existingClientDownloadId,
                    existingTorrentHash,
                    liveClientItemIds))
            {
                return;
            }

            if (!string.Equals(existingClientDownloadId, originalClientId, StringComparison.OrdinalIgnoreCase))
            {
                matchedDownload.Metadata["ClientDownloadId"] = originalClientId;
                await downloadRepository.UpdateMetadataAsync(matchedDownload.Id, "ClientDownloadId", originalClientId);
            }

            allKnownClientItemIds.Add(originalClientId);

            if (isTorrentClient && !string.Equals(existingTorrentHash, originalClientId, StringComparison.OrdinalIgnoreCase))
            {
                matchedDownload.Metadata["TorrentHash"] = originalClientId;
                await downloadRepository.UpdateMetadataAsync(matchedDownload.Id, "TorrentHash", originalClientId);
            }
        }

        /// <summary>
        /// Decides whether a queue poll may write the client item's identifier onto the matched
        /// download record. Binding an unbound record is the point of the match. Re-pointing a record
        /// that is already bound to a different client item is not: at completion the stored
        /// identifier is what the import resolver asks the client for, so a title-similar stranger
        /// would otherwise have its files imported into this book.
        /// </summary>
        private bool CanBindToClientItem(
            DownloadQueueMatch match,
            Download matchedDownload,
            string originalClientId,
            string? existingClientDownloadId,
            string? existingTorrentHash,
            HashSet<string> liveClientItemIds)
        {
            var conflictingIdentifiers = new[] { existingClientDownloadId, existingTorrentHash }
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Where(id => !string.Equals(id, originalClientId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (conflictingIdentifiers.Count == 0)
            {
                return true;
            }

            // The record still points at an item this client is reporting right now. Whatever the
            // score, that live binding is better evidence than anything this row can offer.
            var liveConflict = conflictingIdentifiers.FirstOrDefault(liveClientItemIds.Contains);
            if (liveConflict != null)
            {
                logger.LogDebug(
                    "Download {DownloadId} is already bound to live client item {BoundClientItemId}; ignoring client item {CandidateClientItemId} (match score {Score})",
                    matchedDownload.Id,
                    liveConflict,
                    originalClientId,
                    match.Score);
                return false;
            }

            if (!match.IsIdentityMatch)
            {
                // A title-similar row is not evidence that the existing binding is wrong, even when
                // the bound item has vanished from the snapshot: a stalled, paused or manually
                // removed torrent looks the same from here.
                logger.LogDebug(
                    "Download {DownloadId} is already bound to client item {BoundClientItemId}; ignoring title-similar client item {CandidateClientItemId} (match score {Score})",
                    matchedDownload.Id,
                    conflictingIdentifiers[0],
                    originalClientId,
                    match.Score);
                return false;
            }

            return true;
        }
    }
}
