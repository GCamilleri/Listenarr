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

namespace Listenarr.Tests.Features.Api.Services
{
    public class UnmatchedScanBackgroundServiceTests
    {
        [Fact]
        public void BuildGroupedFilesForFolder_MergesForewordIntoSingleBookGroup()
        {
            var folder = @"D:\test\Jack of Shadows - Roger Zelazny (narrated by Eric Jason Martin)";
            var files = new[]
            {
                Path.Join(folder, "(Foreword by Joe Haldeman).mp3"),
                Path.Join(folder, "Chapter 01.mp3"),
                Path.Join(folder, "Chapter 02.mp3")
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            var group = Assert.Single(groups);
            Assert.Equal(3, group.Count);
            Assert.Contains(Path.Join(folder, "(Foreword by Joe Haldeman).mp3"), group);
            Assert.Contains(Path.Join(folder, "Chapter 01.mp3"), group);
            Assert.Contains(Path.Join(folder, "Chapter 02.mp3"), group);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_KeepsDistinctTitlesSeparated()
        {
            var folder = @"D:\test\Roger Zelazny";
            var files = new[]
            {
                Path.Join(folder, "Jack of Shadows.mp3"),
                Path.Join(folder, "Lord of Light.mp3")
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Single() == Path.Join(folder, "Jack of Shadows.mp3"));
            Assert.Contains(groups, group => group.Single() == Path.Join(folder, "Lord of Light.mp3"));
        }

        [Fact]
        public void BuildGroupedFilesForFolder_UsesEmbeddedTitleAndAuthorToMergeMixedFolderTracks()
        {
            var folder = @"D:\test\test-import";
            var foreword = Path.Join(folder, "(Foreword by Joe Haldeman).mp3");
            var chapter1 = Path.Join(folder, "Chapter 01.mp3");
            var alchemised = Path.Join(folder, "Alchemised (Spanish Edition)_ No queda nadie a quien salvar.m4b");
            var files = new[]
            {
                foreword,
                chapter1,
                alchemised
            };

            var embeddedTags = new Dictionary<string, PathParsedMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [foreword] = new() { Title = "Jack of Shadows", Author = "Roger Zelazny" },
                [chapter1] = new() { Title = "Jack of Shadows", Author = "Roger Zelazny" },
                [alchemised] = new() { Title = "Alchemised (Spanish Edition)", Author = "SenLinYu" }
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault,
                embeddedTags);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Count == 2 && group.Contains(foreword) && group.Contains(chapter1));
            Assert.Contains(groups, group => group.Count == 1 && group.Contains(alchemised));
        }

        [Fact]
        public void BuildGroupedFilesForFolder_NumberedChapterFilesAreOneBook()
        {
            var folder = @"D:\test\The Hobbit";
            var files = Enumerable.Range(1, 30)
                .Select(i => Path.Join(folder, $"The Hobbit - {i:D2}.mp3"))
                .ToArray();

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            var group = Assert.Single(groups);
            Assert.Equal(30, group.Count);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_ChapterTitledFilesWithLeadingNumbersAreOneBook()
        {
            var folder = @"D:\test\The Hobbit";
            var files = new[]
            {
                Path.Join(folder, "01 - An Unexpected Party.mp3"),
                Path.Join(folder, "02 - Roast Mutton.mp3"),
                Path.Join(folder, "03 - A Short Rest.mp3")
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            var group = Assert.Single(groups);
            Assert.Equal(3, group.Count);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_DistinctContainerTitlesUnderAuthorFolderStaySeparate()
        {
            var folder = @"D:\test\Brandon Sanderson";
            var elantris = Path.Join(folder, "Elantris.m4b");
            var warbreaker = Path.Join(folder, "Warbreaker.m4b");

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                new[] { elantris, warbreaker },
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Single() == elantris);
            Assert.Contains(groups, group => group.Single() == warbreaker);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_NumberedContainerFilesUnderAuthorFolderStaySeparate()
        {
            // A numbered run of single-file containers is a series in one folder, not one
            // book in parts, so the trailing-number rule must not merge them.
            var folder = @"D:\test\Brandon Sanderson";
            var files = new[]
            {
                Path.Join(folder, "Mistborn 1.m4b"),
                Path.Join(folder, "Mistborn 2.m4b"),
                Path.Join(folder, "Mistborn 3.m4b")
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            Assert.Equal(3, groups.Count);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_SeriesNumberedAlbumTagsAreNotMergedBySubstring()
        {
            var folder = @"D:\test\Robert Jordan";
            var first = Path.Join(folder, "wot01.m4b");
            var eleventh = Path.Join(folder, "wot11.m4b");
            var embeddedTags = new Dictionary<string, PathParsedMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [first] = new() { Title = "Wheel of Time 1", Author = "Robert Jordan" },
                [eleventh] = new() { Title = "Wheel of Time 11", Author = "Robert Jordan" }
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                new[] { first, eleventh },
                folder,
                FileSystemPathSemantics.CurrentHostDefault,
                embeddedTags);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Single() == first);
            Assert.Contains(groups, group => group.Single() == eleventh);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_ParenthesisedEditionSuffixStillMergesWithBareTitle()
        {
            var folder = @"D:\test\bounded-tolerance";
            var bare = Path.Join(folder, "alpha.m4b");
            var suffixed = Path.Join(folder, "beta.m4b");
            var embeddedTags = new Dictionary<string, PathParsedMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [bare] = new() { Title = "Elantris", Author = "Brandon Sanderson" },
                [suffixed] = new() { Title = "Elantris (Unabridged)", Author = "Brandon Sanderson" }
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                new[] { bare, suffixed },
                folder,
                FileSystemPathSemantics.CurrentHostDefault,
                embeddedTags);

            var group = Assert.Single(groups);
            Assert.Equal(2, group.Count);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_UntaggedDistinctFileDoesNotAttachToTaggedGroup()
        {
            var folder = @"D:\test\half-tagged";
            var tagged = Path.Join(folder, "Elantris.m4b");
            var untagged = Path.Join(folder, "Warbreaker.m4b");
            var embeddedTags = new Dictionary<string, PathParsedMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [tagged] = new() { Title = "Elantris", Author = "Brandon Sanderson" },
                [untagged] = new()
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                new[] { tagged, untagged },
                folder,
                FileSystemPathSemantics.CurrentHostDefault,
                embeddedTags);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Single() == tagged);
            Assert.Contains(groups, group => group.Single() == untagged);
        }

        [Fact]
        public void ResolveBookFolder_CollapsesDiscSubfoldersIntoTheBookFolder()
        {
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "unmatched-disc-root"));
            var book = Path.Join(root, "Author", "Book Title");

            var first = UnmatchedScanProcessor.ResolveBookFolder(
                Path.Join(book, "CD1", "01.mp3"),
                root,
                semantics);
            var second = UnmatchedScanProcessor.ResolveBookFolder(
                Path.Join(book, "CD2", "01.mp3"),
                root,
                semantics);

            Assert.Equal(book, first);
            Assert.Equal(book, second);
        }

        [Fact]
        public void ResolveBookFolder_LeavesNonDiscFolderAlone()
        {
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "unmatched-plain-root"));
            var book = Path.Join(root, "Author", "Book Title");

            var resolved = UnmatchedScanProcessor.ResolveBookFolder(
                Path.Join(book, "01.mp3"),
                root,
                semantics);

            Assert.Equal(book, resolved);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_DiscSubfolderFilesFormOneGroup()
        {
            var book = @"D:\test\Author\Book Title";
            var files = new[]
            {
                Path.Join(book, "CD1", "01.mp3"),
                Path.Join(book, "CD1", "02.mp3"),
                Path.Join(book, "CD2", "01.mp3"),
                Path.Join(book, "CD2", "02.mp3")
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                book,
                FileSystemPathSemantics.CurrentHostDefault);

            var group = Assert.Single(groups);
            Assert.Equal(4, group.Count);
        }

        [Fact]
        public void SelectRepresentative_PrefersLargestFileOverSortFirstFile()
        {
            var folder = @"D:\test\Jack of Shadows";
            var foreword = Path.Join(folder, "(Foreword by Joe Haldeman).mp3");
            var body = Path.Join(folder, "Chapter 01.mp3");
            var ordered = new List<string> { foreword, body };
            var lengths = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [foreword] = 1_000,
                [body] = 900_000
            };

            var representative = UnmatchedScanProcessor.SelectRepresentative(
                ordered,
                lengths,
                embeddedTagsByFile: null);

            Assert.Equal(body, representative);
        }

        [Fact]
        public void SelectRepresentative_WithoutLengthsPrefersFirstTaggedFile()
        {
            var folder = @"D:\test\Jack of Shadows";
            var foreword = Path.Join(folder, "(Foreword by Joe Haldeman).mp3");
            var body = Path.Join(folder, "Chapter 01.mp3");
            var ordered = new List<string> { foreword, body };
            var tags = new Dictionary<string, PathParsedMetadata>(StringComparer.Ordinal)
            {
                [foreword] = new(),
                [body] = new() { Title = "Jack of Shadows" }
            };

            var representative = UnmatchedScanProcessor.SelectRepresentative(
                ordered,
                new Dictionary<string, long>(StringComparer.Ordinal),
                tags);

            Assert.Equal(body, representative);
        }

        [Fact]
        public void SelectRepresentative_EqualLengthsIsDeterministic()
        {
            var folder = @"D:\test\stable";
            var a = Path.Join(folder, "a.mp3");
            var b = Path.Join(folder, "b.mp3");
            var lengths = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [a] = 5_000,
                [b] = 5_000
            };

            var forward = UnmatchedScanProcessor.SelectRepresentative(
                new List<string> { a, b },
                lengths,
                embeddedTagsByFile: null);
            var reversed = UnmatchedScanProcessor.SelectRepresentative(
                new List<string> { b, a },
                lengths,
                embeddedTagsByFile: null);

            Assert.Equal(forward, reversed);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_UsesAuthorToKeepSameTitleSeparated()
        {
            var folder = @"D:\test\same-title";
            var fileA = Path.Join(folder, "Book A.m4b");
            var fileB = Path.Join(folder, "Book B.m4b");
            var files = new[] { fileA, fileB };

            var embeddedTags = new Dictionary<string, PathParsedMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [fileA] = new() { Title = "Shared Title", Author = "Roger Zelazny" },
                [fileB] = new() { Title = "Shared Title", Author = "SenLinYu" }
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault,
                embeddedTags);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Single() == fileA);
            Assert.Contains(groups, group => group.Single() == fileB);
        }
    }
}
