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

using Listenarr.Application.Common.Naming;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Library
{
    internal static class LibraryPathPlanner
    {
        public static string ComputeAudiobookBaseDirectoryFromPattern(
            Audiobook audiobook,
            string rootPath,
            string fileNamingPattern,
            IFileNamingService fileNamingService)
        {
            var relative = ComputeAudiobookRelativeDirectoryFromPattern(
                audiobook,
                fileNamingPattern,
                fileNamingService);
            return ResolvePathWithOptionalBase(rootPath, relative);
        }

        /// <summary>
        /// Applies the configured folder pattern exactly as the library add service does.
        /// The private {Series} injection and the private sanitizer that used to live here
        /// made this planner disagree with add and with organize for the same book, which
        /// is what made each import plan one path and the next organize run plan another.
        /// The pattern engine already strips an empty {Series} with its separators.
        /// </summary>
        internal static string ComputeAudiobookRelativeDirectoryFromPattern(
            Audiobook audiobook,
            string fileNamingPattern,
            IFileNamingService fileNamingService)
        {
            var directoryPattern = string.IsNullOrWhiteSpace(fileNamingPattern)
                ? DefaultDirectoryPattern
                : fileNamingPattern;

            return fileNamingService.ApplyNamingPattern(
                directoryPattern,
                NamingVariableBuilder.FromAudiobook(audiobook),
                false);
        }

        /// <summary>
        /// Only reached when neither naming pattern is configured at all.
        /// </summary>
        private const string DefaultDirectoryPattern = "{Author}/{Series}/{Title}";

        private static string ResolvePathWithOptionalBase(string? basePath, string candidatePath)
        {
            return FileUtils.CombineWithOptionalBase(basePath, candidatePath);
        }
    }
}
