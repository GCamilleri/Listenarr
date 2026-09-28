/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Listenarr.Application.Common.Naming;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;
namespace Listenarr.Application.Common
{
    public partial class FileNamingService
    {
        /// <summary>
        /// Remove invalid characters from path components.
        /// </summary>
        private string SanitizePathComponent(string pathComponent)
        {
            if (string.IsNullOrWhiteSpace(pathComponent))
            {
                return "Unknown";
            }

            var sanitized = new StringBuilder();
            foreach (var c in pathComponent)
            {
                if (char.IsControl(c))
                {
                    continue;
                }

                if (c == ':' || c == '/' || c == '\\')
                {
                    sanitized.Append(" - ");
                }
                else if (PortableInvalidFileNameChars.Contains(c))
                {
                    sanitized.Append('_');
                }
                else
                {
                    sanitized.Append(c);
                }
            }

            var result = sanitized.ToString();
            result = Regex.Replace(result, @"\s+", " ");
            result = Regex.Replace(result, @"(?:\s*-\s*){2,}", " - ");
            result = Regex.Replace(result, @"_+", "_");
            result = result.Trim();
            result = result.TrimEnd('.', ' ');
            result = Regex.Replace(result, @"^\s*[-_]+\s*", string.Empty);
            result = Regex.Replace(result, @"\s*[-_]+\s*$", string.Empty);

            if (string.IsNullOrWhiteSpace(result))
            {
                return "Unknown";
            }

            var extensionSeparator = result.IndexOf('.');
            var deviceNameStem = extensionSeparator >= 0 ? result[..extensionSeparator] : result;
            if (ReservedWindowsDeviceNames.Contains(deviceNameStem))
            {
                result = extensionSeparator >= 0
                    ? deviceNameStem + "_" + result[extensionSeparator..]
                    : result + "_";
            }

            return result;
        }

        // Both overloads defer to the one shared builder so every naming flow emits the
        // same token set with the same rules. See NamingVariableBuilder for those rules.
        private static Dictionary<string, object> BuildVariables(AudioMetadata metadata)
            => NamingVariableBuilder.FromAudioMetadata(metadata);

        private static Dictionary<string, object> BuildVariables(AudibleBookMetadata metadata)
            => NamingVariableBuilder.FromAudibleMetadata(metadata);

        private static HashSet<char> BuildPortableInvalidFileNameChars()
        {
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());

            foreach (var c in "<>:\"/\\|?*")
            {
                invalidChars.Add(c);
            }

            for (int i = 0; i < 32; i++)
            {
                invalidChars.Add((char)i);
            }

            return invalidChars;
        }

        /// <summary>
        /// Windows MAX_PATH limit (260 chars including null terminator).
        /// We use 259 as the effective usable limit.
        /// </summary>
        private const int WindowsMaxPath = 259;

        /// <summary>
        /// Maximum length for a single path component (file or folder name) on NTFS / most filesystems.
        /// </summary>
        private const int MaxComponentLength = 255;

        /// <summary>
        /// Ensure the generated path does not exceed platform limits.
        /// On Windows: total path ≤ 259 chars, each component ≤ 255 chars.
        /// Truncates the longest non-root components first while preserving the file extension.
        /// </summary>
        public string EnsurePathWithinLimits(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return fullPath;

            // Only enforce strict limits on Windows; other platforms support much longer paths
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return fullPath;

            var originalPath = fullPath;

            // Split into root (e.g. "D:\") and component parts
            var root = Path.GetPathRoot(fullPath) ?? string.Empty;
            var withoutRoot = fullPath.Substring(root.Length);
            var parts = withoutRoot.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (parts.Count == 0)
                return fullPath;

            // Preserve the file extension on the last component
            var extension = Path.GetExtension(parts.Last());

            // --- Step 1: Enforce per-component limit (255 chars) ---
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Length <= MaxComponentLength)
                    continue;

                // Last component (filename): keep extension
                parts[i] = i == parts.Count - 1 && !string.IsNullOrEmpty(extension)
                    ? parts[i].Substring(0, MaxComponentLength - extension.Length) + extension
                    : parts[i].Substring(0, MaxComponentLength);
            }

            // --- Step 2: Enforce total path length ---
            // Iteratively shorten the longest non-root component until within limit
            const int maxIterations = 50; // safety valve
            for (int iter = 0; iter < maxIterations; iter++)
            {
                var currentPath = root + string.Join(Path.DirectorySeparatorChar.ToString(), parts);
                if (currentPath.Length <= WindowsMaxPath)
                    break;

                var excess = currentPath.Length - WindowsMaxPath;

                // Find the longest component (prefer earlier components for ties, but skip tiny ones)
                int longestIdx = -1;
                int longestLen = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    var effectiveLen = (i == parts.Count - 1 && !string.IsNullOrEmpty(extension))
                        ? parts[i].Length - extension.Length
                        : parts[i].Length;

                    if (effectiveLen > longestLen)
                    {
                        longestLen = effectiveLen;
                        longestIdx = i;
                    }
                }

                if (longestIdx < 0 || longestLen <= 1)
                {
                    // Nothing left to truncate
                    _logger.LogWarning("Cannot shorten path below Windows MAX_PATH limit ({Limit} chars). Path length: {Length}. Path: {Path}",
                        WindowsMaxPath, currentPath.Length, currentPath);
                    break;
                }

                var part = parts[longestIdx];
                bool isFilename = longestIdx == parts.Count - 1 && !string.IsNullOrEmpty(extension);
                var nameWithoutExt = isFilename ? part.Substring(0, part.Length - extension.Length) : part;

                var newLen = Math.Max(1, nameWithoutExt.Length - excess);
                parts[longestIdx] = isFilename
                    ? nameWithoutExt.Substring(0, newLen).TrimEnd() + extension
                    : nameWithoutExt.Substring(0, newLen).TrimEnd();
            }

            var result = root + string.Join(Path.DirectorySeparatorChar.ToString(), parts);

            if (result != originalPath)
            {
                _logger.LogWarning("Path truncated to fit Windows MAX_PATH limit ({Limit} chars). Original length: {OriginalLength}, New length: {NewLength}. Truncated path: {Path}",
                    WindowsMaxPath, originalPath.Length, result.Length, result);
            }

            return result;
        }

        private static string CombineWithOptionalBase(string? basePath, string candidatePath)
        {
            // Keep all naming-path composition on the shared helper so filesystem roots
            // and Unix/Docker whitespace semantics stay consistent with import/move flows.
            return FileUtils.CombineWithOptionalBase(basePath, candidatePath);
        }
    }
}
