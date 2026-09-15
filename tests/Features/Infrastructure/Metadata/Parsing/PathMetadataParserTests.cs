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

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Parsing
{
    public class PathMetadataParserTests
    {
        [Fact]
        public void Parse_UsesResolvedCaseSensitivityForContainment()
        {
            var root = Path.Join(Path.GetTempPath(), $"MetadataRoot-{Guid.NewGuid():N}");
            var differentlyCasedRoot = root.ToUpperInvariant();
            var file = Path.Join(root, "Author", "2020 - Title", "book.m4b");
            var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;

            var sensitive = PathMetadataParser.Parse(
                file,
                differentlyCasedRoot,
                new FileSystemPathSemantics(syntax, FileSystemCaseSensitivity.Sensitive));
            var insensitive = PathMetadataParser.Parse(
                file,
                differentlyCasedRoot,
                new FileSystemPathSemantics(syntax, FileSystemCaseSensitivity.Insensitive));

            Assert.Null(sensitive.Title);
            Assert.Equal("Title", insensitive.Title);
            Assert.Equal("Author", insensitive.Author);
        }

        [Fact]
        public void Parse_PlainAuthorTitleLayout_FallsBackToFolderNames()
        {
            var root = Path.Join(Path.GetTempPath(), $"MetadataRoot-{Guid.NewGuid():N}");
            var titleFolder = Path.Join(root, "Brandon Sanderson", "Elantris");
            var file = Path.Join(titleFolder, "book.m4b");

            var parsed = PathMetadataParser.ParsePathOnly(
                file,
                root,
                FileSystemPathSemantics.CurrentHostDefault);

            Assert.Equal("Elantris", parsed.Title);
            Assert.Equal("Brandon Sanderson", parsed.Author);
            Assert.False(parsed.ParsedFromPattern);
            Assert.Equal(titleFolder, parsed.BookFolderPath);
        }

        [Fact]
        public void Parse_PlainLayoutInsideDiscFolder_UsesBookFolderAboveTheDisc()
        {
            var root = Path.Join(Path.GetTempPath(), $"MetadataRoot-{Guid.NewGuid():N}");
            var titleFolder = Path.Join(root, "Brandon Sanderson", "Elantris");
            var file = Path.Join(titleFolder, "CD2", "01.mp3");

            var parsed = PathMetadataParser.ParsePathOnly(
                file,
                root,
                FileSystemPathSemantics.CurrentHostDefault);

            Assert.Equal("Elantris", parsed.Title);
            Assert.Equal("Brandon Sanderson", parsed.Author);
            Assert.Equal(titleFolder, parsed.BookFolderPath);
        }

        [Fact]
        public void Parse_YearTitleFolder_StillPrefersTheStrictPattern()
        {
            var root = Path.Join(Path.GetTempPath(), $"MetadataRoot-{Guid.NewGuid():N}");
            var file = Path.Join(root, "Author", "2020 - Title", "book.m4b");

            var parsed = PathMetadataParser.ParsePathOnly(
                file,
                root,
                FileSystemPathSemantics.CurrentHostDefault);

            Assert.True(parsed.ParsedFromPattern);
            Assert.Equal("Title", parsed.Title);
            Assert.Equal("Author", parsed.Author);
            Assert.Equal("2020", parsed.Year);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ReadsUppercaseAlbumAndArtistTags()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "ALBUM": "Alchemised",
                  "ARTIST": "SenLinYu"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("Alchemised", result.Title);
            Assert.Equal("SenLinYu", result.Author);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_PrefersAlbumArtistOverArtist()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "album_artist": "SenLinYu",
                  "artist": "Narrator Person"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("SenLinYu", result.Author);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ReadsFormatDurationIntoDurationSeconds()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "duration": "3671.250000",
                "tags": {
                  "album": "Alchemised"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.NotNull(result.DurationSeconds);
            Assert.Equal(3671.25, result.DurationSeconds!.Value, 3);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ReadsDurationWhenThereAreNoTags()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "duration": "120.5"
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Null(result.Title);
            Assert.Equal(120.5, result.DurationSeconds!.Value, 3);
        }

        [Fact]
        public async Task ReadEmbeddedTagsAsync_MissingFfprobeBinary_ReportsProbeFailure()
        {
            var result = await PathMetadataParser.ReadEmbeddedTagsAsync(
                "book.m4b",
                Path.Join(Path.GetTempPath(), $"ffprobe-does-not-exist-{Guid.NewGuid():N}"),
                CancellationToken.None);

            Assert.True(result.ProbeFailed);
            Assert.NotNull(result.FailureSummary);
            Assert.Null(result.Metadata.Title);
        }

        [Fact]
        public async Task ReadEmbeddedTagsAsync_NonZeroExit_ReportsExitCodeAndStderr()
        {
            var directory = Path.Join(Path.GetTempPath(), $"ffprobe-failure-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var script = Path.Join(directory, "failing-ffprobe.sh");
                await File.WriteAllTextAsync(
                    script,
                    "#!/bin/sh\necho 'Invalid data found when processing input' 1>&2\nexit 3\n");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        script,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                var result = await PathMetadataParser.ReadEmbeddedTagsAsync(
                    "book.m4b",
                    script,
                    CancellationToken.None);

                Assert.True(result.ProbeFailed);
                Assert.Equal(3, result.ExitCode);
                Assert.Contains(
                    "Invalid data found",
                    result.FailureSummary!,
                    StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public async Task ReadEmbeddedTagsAsync_CanceledToken_PropagatesCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PathMetadataParser.ReadEmbeddedTagsAsync(
                    "book.m4b",
                    "ffprobe-does-not-need-to-exist-for-canceled-work",
                    cancellation.Token));
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesStandardAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "album_artist": "SenLinYu",
                  "ASIN": "B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("Alchemised", result.Title);
            Assert.Equal("SenLinYu", result.Author);
            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesMp3UserTextAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "TXXX:ASIN": "B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesAppleFreeformAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "----:com.apple.iTunes:ASIN": "amazon://asin/B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesColonSuffixedAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "ASIN:": "B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesCdekTagContainingAsin()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "CDEK:": "amazon://asin/B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }
    }
}
