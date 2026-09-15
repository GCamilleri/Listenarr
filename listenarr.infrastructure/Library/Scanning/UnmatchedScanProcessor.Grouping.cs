/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Text.RegularExpressions;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public partial class UnmatchedScanProcessor
    {
        // "01 - ", "Track 01 - ", "1. "
        private static readonly Regex LeadingTrackNumberPattern = new(
            @"^(track\s*)?\d+[\s\-_\.]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "- Part 1", "CD2", "Disc 2", "pt00"
        private static readonly Regex TrailingPartKeywordPattern = new(
            @"[\s\-_]*(part|cd|disc|chapter|pt)\s*\d+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "The Hobbit - 01", "The Hobbit 30"
        private static readonly Regex TrailingBareNumberPattern = new(
            @"^(?<core>.*?)[\s\-_\.]+(?<number>\d+)$",
            RegexOptions.Compiled);

        private static readonly string[] SingleBookContainerExtensions = { ".m4b" };

        internal static List<List<string>> BuildGroupedFilesForFolder(
            IEnumerable<string> files,
            string folderPath,
            FileSystemPathSemantics semantics,
            IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile = null)
        {
            var allFiles = files
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(semantics.Comparer)
                .ToList();

            if (allFiles.Count <= 1)
            {
                return new List<List<string>> { allFiles };
            }

            var directoryIsSingleRun = IsSinglePartedBookDirectory(allFiles);

            if (embeddedTagsByFile != null)
            {
                var metadataAwareGroups = BuildMetadataAwareGroups(
                    allFiles,
                    folderPath,
                    semantics,
                    embeddedTagsByFile,
                    directoryIsSingleRun);
                if (metadataAwareGroups.Count > 0)
                {
                    return metadataAwareGroups;
                }
            }

            return BuildStemGroups(allFiles, folderPath);
        }

        private static List<List<string>> BuildMetadataAwareGroups(
            IReadOnlyCollection<string> files,
            string folderPath,
            FileSystemPathSemantics semantics,
            IReadOnlyDictionary<string, PathParsedMetadata> embeddedTagsByFile,
            bool directoryIsSingleRun)
        {
            var folderKey = NormalizeGroupKey(Path.GetFileName(folderPath));
            var candidates = files
                .Select(file => CreateGroupCandidate(file, folderPath, embeddedTagsByFile))
                .ToList();

            if (!candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate.TitleKey)))
            {
                return new List<List<string>>();
            }

            var metadataGroups = new List<List<GroupCandidate>>();
            foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate.TitleKey)))
            {
                var matchingGroup = metadataGroups.FirstOrDefault(group => group.Any(existing => TitlesAndAuthorsMatch(existing, candidate)));
                if (matchingGroup != null)
                {
                    matchingGroup.Add(candidate);
                }
                else
                {
                    metadataGroups.Add(new List<GroupCandidate> { candidate });
                }
            }

            if (metadataGroups.Count == 0)
            {
                return new List<List<string>>();
            }

            var attachedFiles = new HashSet<string>(
                metadataGroups.SelectMany(group => group).Select(candidate => candidate.FilePath),
                semantics.Comparer);

            foreach (var candidate in candidates.Where(candidate => !attachedFiles.Contains(candidate.FilePath)))
            {
                // Every candidate reaching this loop is untagged: the loop above attached
                // all candidates that carried a title. A blank author used to make such a
                // file "compatible" with every group, which silently glued an unrelated
                // file onto whichever book happened to be the only one in the folder.
                // Require a positive signal that the file belongs to that book.
                var hasPositiveSignal = candidate.IsAncillary
                    || directoryIsSingleRun
                    || string.IsNullOrWhiteSpace(candidate.Stem)
                    || string.Equals(candidate.Stem, folderKey, StringComparison.OrdinalIgnoreCase);
                if (!hasPositiveSignal)
                {
                    continue;
                }

                var compatibleGroups = metadataGroups
                    .Where(group => group.All(existing =>
                        CompareAuthors(existing.AuthorKey, candidate.AuthorKey) != AuthorCompatibility.Conflict))
                    .ToList();

                if (compatibleGroups.Count == 1)
                {
                    compatibleGroups[0].Add(candidate);
                    attachedFiles.Add(candidate.FilePath);
                }
            }

            var leftovers = candidates
                .Where(candidate => !attachedFiles.Contains(candidate.FilePath))
                .Select(candidate => candidate.FilePath)
                .ToList();

            var grouped = metadataGroups
                .Select(group => group.Select(candidate => candidate.FilePath).Distinct(semantics.Comparer).ToList())
                .ToList();

            if (leftovers.Count > 0)
            {
                grouped.AddRange(BuildStemGroups(leftovers, folderPath));
            }

            return grouped;
        }

        private static List<List<string>> BuildStemGroups(IReadOnlyCollection<string> allFiles, string folderPath)
        {
            // A directory whose filenames read as one ordered run of parts is one book,
            // however many files it holds. Without this, "The Hobbit - 01.mp3" through
            // "- 30.mp3" produced thirty stems and therefore thirty separate books.
            if (IsSinglePartedBookDirectory(allFiles))
            {
                return new List<List<string>> { allFiles.ToList() };
            }

            var byTitle = allFiles
                .GroupBy(f => ExtractTitleStem(f, folderPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => new StemGroup(g.Key, g.ToList()))
                .ToList();

            if (byTitle.Count <= 1)
            {
                return new List<List<string>> { allFiles.ToList() };
            }

            var primaryGroups = byTitle
                .Where(g => !IsAncillaryStem(g.Stem))
                .ToList();

            if (primaryGroups.Count == 1)
            {
                return new List<List<string>>
                {
                    byTitle.SelectMany(g => g.Files).ToList()
                };
            }

            return byTitle.Select(g => g.Files).ToList();
        }

        private static GroupCandidate CreateGroupCandidate(
            string filePath,
            string folderPath,
            IReadOnlyDictionary<string, PathParsedMetadata> embeddedTagsByFile)
        {
            embeddedTagsByFile.TryGetValue(filePath, out var tags);
            var stem = ExtractTitleStem(filePath, folderPath);
            return new GroupCandidate(
                filePath,
                stem,
                IsAncillaryStem(stem),
                FileUtils.NormalizeComparisonValue(tags?.Title),
                StripParentheticalSuffix(tags?.Title),
                FileUtils.NormalizeComparisonValue(tags?.Author));
        }

        /// <summary>
        /// Normalized form of a title with one trailing parenthesised qualifier removed,
        /// so "Elantris (Unabridged)" can still meet "Elantris". Returns empty when there
        /// is no such suffix, which keeps the tolerance bounded to that one shape.
        /// </summary>
        private static string StripParentheticalSuffix(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var stripped = Regex.Replace(title, @"\s*\([^()]*\)\s*$", string.Empty);
            if (string.Equals(stripped, title, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return FileUtils.NormalizeComparisonValue(stripped);
        }

        private static bool TitlesAndAuthorsMatch(GroupCandidate left, GroupCandidate right)
        {
            return TitleKeysMatch(left, right)
                && CompareAuthors(left.AuthorKey, right.AuthorKey) != AuthorCompatibility.Conflict;
        }

        /// <summary>
        /// Exact normalized equality, plus the single bounded tolerance of a trailing
        /// parenthesised qualifier. Substring containment used to be the test, which
        /// merged "Wheel of Time 1" into "Wheel of Time 11".
        /// </summary>
        private static bool TitleKeysMatch(GroupCandidate left, GroupCandidate right)
        {
            if (string.IsNullOrWhiteSpace(left.TitleKey) || string.IsNullOrWhiteSpace(right.TitleKey))
            {
                return false;
            }

            if (string.Equals(left.TitleKey, right.TitleKey, StringComparison.Ordinal))
            {
                return true;
            }

            return KeysEqual(left.TitleKeyWithoutSuffix, right.TitleKey)
                || KeysEqual(left.TitleKey, right.TitleKeyWithoutSuffix)
                || KeysEqual(left.TitleKeyWithoutSuffix, right.TitleKeyWithoutSuffix);
        }

        private static bool KeysEqual(string left, string right) =>
            !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left, right, StringComparison.Ordinal);

        private enum AuthorCompatibility
        {
            Match,
            Unknown,
            Conflict
        }

        /// <summary>
        /// A blank author is unknown, not compatible. Callers that used to read "true"
        /// here treated "no information" as "same author".
        /// </summary>
        private static AuthorCompatibility CompareAuthors(string? leftAuthor, string? rightAuthor)
        {
            if (string.IsNullOrWhiteSpace(leftAuthor) || string.IsNullOrWhiteSpace(rightAuthor))
            {
                return AuthorCompatibility.Unknown;
            }

            return FileUtils.ValuesOverlap(leftAuthor, rightAuthor)
                ? AuthorCompatibility.Match
                : AuthorCompatibility.Conflict;
        }

        /// <summary>
        /// True when every filename in the directory reads as one part of a single
        /// ordered work: either they all carry a leading track number, or they all share
        /// one stem and differ only by a trailing number. Single-file container formats
        /// are excluded from the second rule, because "Mistborn 1.m4b" beside
        /// "Mistborn 2.m4b" is a series sitting in one folder, not one book in two parts.
        /// </summary>
        private static bool IsSinglePartedBookDirectory(IReadOnlyCollection<string> files)
        {
            if (files.Count < 2)
            {
                return false;
            }

            var names = files
                .Select(file => Path.GetFileNameWithoutExtension(file) ?? string.Empty)
                .ToList();

            if (names.All(name => LeadingTrackNumberPattern.IsMatch(name)))
            {
                return true;
            }

            if (files.All(file => SingleBookContainerExtensions.Contains(
                    Path.GetExtension(file),
                    StringComparer.OrdinalIgnoreCase)))
            {
                return false;
            }

            string? sharedCore = null;
            var numbers = new List<int>(names.Count);
            foreach (var name in names)
            {
                var withoutLeadingTrack = LeadingTrackNumberPattern.Replace(name, string.Empty);
                var match = TrailingBareNumberPattern.Match(withoutLeadingTrack);
                if (!match.Success)
                {
                    return false;
                }

                var core = NormalizeGroupKey(match.Groups["core"].Value);
                if (core.Length == 0
                    || !int.TryParse(match.Groups["number"].Value, out var number))
                {
                    return false;
                }

                sharedCore ??= core;
                if (!string.Equals(sharedCore, core, StringComparison.Ordinal))
                {
                    return false;
                }

                numbers.Add(number);
            }

            return numbers.Distinct().Count() == numbers.Count;
        }

        private static void ApplyEmbeddedTags(PathParsedMetadata target, PathParsedMetadata tags)
        {
            if (!string.IsNullOrEmpty(tags.Title)) target.Title = tags.Title;
            if (!string.IsNullOrEmpty(tags.Author)) target.Author = tags.Author;
            if (!string.IsNullOrEmpty(tags.Narrator)) target.Narrator = tags.Narrator;
            if (!string.IsNullOrEmpty(tags.Series)) target.Series = tags.Series;
            if (!string.IsNullOrEmpty(tags.SeriesNumber)) target.SeriesNumber = tags.SeriesNumber;
            if (!string.IsNullOrEmpty(tags.Year)) target.Year = tags.Year;
            if (!string.IsNullOrEmpty(tags.Description)) target.Description = tags.Description;
            if (!string.IsNullOrEmpty(tags.Asin)) target.Asin = tags.Asin;
        }

        /// <summary>
        /// Extracts a normalized title stem from a filename for grouping purposes.
        /// Strips leading track numbers, trailing Part/CD/Disc numbers, year and
        /// series decorations in brackets. Files that resolve to the same stem are
        /// treated as parts of the same audiobook. Returns the folder name as fallback
        /// when the stem would otherwise be empty (for example purely numeric filenames).
        /// </summary>
        private static string ExtractTitleStem(string filePath, string folderPath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);

            // Strip leading track/disc number prefix: "01 - ", "Track 01 - ", "1. "
            name = LeadingTrackNumberPattern.Replace(name, "");
            // Strip trailing Part/CD/Disc/Chapter number: "- Part 1", "CD2", "Disc 2", "pt00"
            name = TrailingPartKeywordPattern.Replace(name, "");
            // Strip 4-digit years in parens: (2020), (2021)
            name = Regex.Replace(name, @"\s*\(\d{4}\)", "");
            // Strip square bracket content: [Series 3], [Chaos Seeds 1]
            name = Regex.Replace(name, @"\s*\[.*?\]", "");
            // Plain numeric parens like (1), (2) are intentionally kept because they
            // distinguish separate books in a series.
            name = NormalizeGroupKey(name);

            // Purely numeric or empty after stripping: use the folder name so numbered-part
            // files like 1.m4b and 2.m4b still group together under their folder.
            if (string.IsNullOrEmpty(name) || Regex.IsMatch(name, @"^\d+$"))
                return NormalizeGroupKey(Path.GetFileName(folderPath));

            return name;
        }

        private static bool IsAncillaryStem(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem))
            {
                return false;
            }

            var normalized = stem.Trim();
            normalized = Regex.Replace(normalized, @"^[\s\(\[\{""'`_-]+|[\s\)\]\}""'`_-]+$", "");
            normalized = NormalizeGroupKey(normalized);

            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            return Regex.IsMatch(
                    normalized,
                    @"^(?:foreword|afterword|preface|prologue|epilogue|appendix|dedication|interlude)(?:\b.*)?$",
                    RegexOptions.IgnoreCase)
                || Regex.IsMatch(
                    normalized,
                    @"^(?:introduction|intro)(?:$|\s+by\b.*)",
                    RegexOptions.IgnoreCase)
                || Regex.IsMatch(
                    normalized,
                    @"^(?:acknowledg(?:e)?ments?|credits|opening credits|closing credits|author'?s note|note from the author|about the author|bonus chapter|bonus material|preview|sample)(?:\b.*)?$",
                    RegexOptions.IgnoreCase);
        }

        private static string NormalizeGroupKey(string value) =>
            Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim().ToLowerInvariant();

        private static string NormalizePath(string path, FileSystemPathSyntax syntax) =>
            FileSystemPathIdentity.Canonicalize(Path.GetFullPath(path), syntax);
    }
}
