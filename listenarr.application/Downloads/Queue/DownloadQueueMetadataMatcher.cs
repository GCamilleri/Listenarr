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
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Queue
{
    /// <summary>
    /// Result of matching a client queue row to a tracked download. <see cref="Score"/> is the
    /// <see cref="QueueItem.GetMatchScore(Download)"/> value that produced the match: 3 or 4 mean the
    /// client identifiers already agree, 2 or lower mean the match rests on title similarity alone.
    /// </summary>
    internal readonly record struct DownloadQueueMatch(Download Download, int Score)
    {
        /// <summary>
        /// True when the match came from an identifier the client and the record already share,
        /// rather than from title similarity.
        /// </summary>
        public bool IsIdentityMatch => Score >= 3;
    }

    internal static class DownloadQueueMetadataMatcher
    {
        public static DownloadQueueMatch? FindBestMatchingDownload(
            QueueItem queueItem,
            DownloadClientConfiguration client,
            IEnumerable<Download> candidateDownloads,
            ILogger logger)
        {
            if (queueItem == null || client == null || candidateDownloads == null)
            {
                return null;
            }

            var matches = candidateDownloads
                .Where(download => download.DownloadClientId == client.Id)
                .Select(download => new
                {
                    Download = download,
                    Score = queueItem.GetMatchScore(download)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Download.StartedAt)
                .ToList();

            if (matches.Count == 0)
            {
                return null;
            }

            var bestMatch = matches[0];

            // A tie on any title-derived score is not evidence for either candidate. Identifier
            // matches (3 and 4) cannot tie meaningfully, so the guard is limited to title scores.
            if (bestMatch.Score <= 2 && matches.Skip(1).Any(x => x.Score == bestMatch.Score))
            {
                logger.LogDebug(
                    "Queue item {QueueId} '{QueueTitle}' had ambiguous title-only matches on client {ClientId}; leaving unmatched",
                    queueItem.Id,
                    queueItem.Title,
                    client.Id);
                return null;
            }

            return new DownloadQueueMatch(bestMatch.Download, bestMatch.Score);
        }

        public static IEnumerable<string> GetKnownClientItemIds(Dictionary<string, object>? metadata)
        {
            var clientDownloadId = GetMetadataString(metadata, "ClientDownloadId");
            if (!string.IsNullOrWhiteSpace(clientDownloadId))
            {
                yield return clientDownloadId;
            }

            var torrentHash = GetMetadataString(metadata, "TorrentHash");
            if (!string.IsNullOrWhiteSpace(torrentHash))
            {
                yield return torrentHash;
            }
        }

        public static string? GetMetadataString(Dictionary<string, object>? metadata, string key)
        {
            if (metadata == null || !metadata.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Undefined => null,
                    _ => element.ToString()
                };
            }

            return value.ToString();
        }
    }
}
