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
using System.Globalization;
using System.Text;

namespace Listenarr.Domain.Common
{
    /// <summary>
    /// Recognizes a disc or part subfolder such as "CD1", "Disc 02" or "Part_3".
    /// Callers collapse those folders into the book folder above them so a book
    /// split across discs stays one book.
    /// </summary>
    public static class DiscFolderRules
    {
        private static readonly string[] DiscPrefixes = { "cd", "disc", "disk", "part" };

        public static bool IsDiscDirectory(string? segment)
        {
            var normalized = NormalizeSegment(segment);
            foreach (var prefix in DiscPrefixes)
            {
                if (normalized.Length > prefix.Length
                    && normalized.StartsWith(prefix, StringComparison.Ordinal)
                    && normalized[prefix.Length..].All(char.IsDigit))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeSegment(string? segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return string.Empty;
            }

            var decomposed = segment.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }

            return builder.ToString();
        }
    }
}
