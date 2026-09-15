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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Name", "LibraryBulkEditWorkflowSeriesTests")]
    [Trait("Category", "LibraryController")]
    [Trait("Area", "SeriesMemberships")]
    public sealed class LibraryBulkEditWorkflowSeriesTests : BaseTests
    {
        [Fact]
        public async Task SetPrimary_AddsTheSeriesDemotesTheRestAndMirrorsTheLegacyColumns()
        {
            // Given a book whose primary series is the misspelt duplicate.
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("The Final Empire")
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .WithSeriesMembership("Cosmere", "3")
                .Build());

            // When the bulk update sets the correctly spelt series as primary.
            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "setPrimary",
                    seriesName = "The Mistborn Saga",
                    seriesAsin = "B0SERIES1",
                    numbering = "explicit",
                    seriesNumber = "1"
                });

            // Then the new membership is primary, the others survive, and the legacy columns follow.
            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(stored);
            var memberships = OrderedMemberships(stored!);
            Assert.Equal(3, memberships.Count);
            var primary = Assert.Single(memberships, membership => membership.IsPrimary);
            Assert.Equal("The Mistborn Saga", primary.SeriesName);
            Assert.Equal("1", primary.SeriesNumber);
            Assert.Equal("B0SERIES1", primary.SeriesAsin);
            Assert.Contains(memberships, membership => membership.SeriesName == "Mistborn");
            Assert.Contains(memberships, membership => membership.SeriesName == "Cosmere");
            Assert.Equal("The Mistborn Saga", stored.Series);
            Assert.Equal("1", stored.SeriesNumber);
        }

        [Fact]
        public async Task SetPrimary_WithReplaceOthers_LeavesOnlyTheTargetSeries()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Replace Others")
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .WithSeriesMembership("Cosmere", "3")
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "setPrimary",
                    seriesName = "The Mistborn Saga",
                    numbering = "keep",
                    replaceOthers = true
                });

            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            var membership = Assert.Single(OrderedMemberships(stored!));
            Assert.Equal("The Mistborn Saga", membership.SeriesName);
            Assert.True(membership.IsPrimary);
        }

        [Fact]
        public async Task AddMembership_AddsTheSeriesWithoutTouchingThePrimary()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Secret History")
                .WithSeriesMembership("Mistborn", "3.5", isPrimary: true)
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "addMembership",
                    seriesName = "Cosmere",
                    numbering = "explicit",
                    seriesNumber = "7"
                });

            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            var memberships = OrderedMemberships(stored!);
            Assert.Equal(2, memberships.Count);
            var primary = Assert.Single(memberships, membership => membership.IsPrimary);
            Assert.Equal("Mistborn", primary.SeriesName);
            var added = Assert.Single(
                memberships,
                membership => membership.SeriesName == "Cosmere");
            Assert.Equal("7", added.SeriesNumber);
            Assert.False(added.IsPrimary);
            Assert.Equal("Mistborn", stored!.Series);
            Assert.Equal("3.5", stored.SeriesNumber);
        }

        [Fact]
        public async Task RemoveMembership_DropsTheSeriesAndPromotesTheNextOne()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Wrongly Filed")
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .WithSeriesMembership("Cosmere", "3")
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "removeMembership",
                    matchName = "mistborn"
                });

            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            var membership = Assert.Single(OrderedMemberships(stored!));
            Assert.Equal("Cosmere", membership.SeriesName);
            Assert.True(membership.IsPrimary);
            Assert.Equal("Cosmere", stored!.Series);
            Assert.Equal("3", stored.SeriesNumber);
        }

        [Fact]
        public async Task RemoveMembership_SeriesNotOnTheBook_IsAPerBookErrorNotASilentSuccess()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Untouched")
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "removeMembership",
                    matchName = "Stormlight Archive"
                });

            var item = SingleItem(result);
            Assert.False(item.GetProperty("success").GetBoolean());
            Assert.Contains(
                item.GetProperty("errors").EnumerateArray(),
                error => error.GetString()?.Contains(
                    "not in the series 'Stormlight Archive'",
                    StringComparison.Ordinal) == true);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            var membership = Assert.Single(OrderedMemberships(stored!));
            Assert.Equal("Mistborn", membership.SeriesName);
        }

        [Fact]
        public async Task RenameMembership_KeepsTheNumberAndThePrimaryFlag()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("The Well of Ascension")
                .WithSeriesMembership("Mistborn", "2", isPrimary: true)
                .WithSeriesMembership("Cosmere", "4")
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "renameMembership",
                    matchName = "Mistborn",
                    seriesName = "The Mistborn Saga",
                    seriesAsin = "B0SERIES1",
                    numbering = "keep"
                });

            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            var memberships = OrderedMemberships(stored!);
            Assert.Equal(2, memberships.Count);
            var renamed = Assert.Single(
                memberships,
                membership => membership.SeriesName == "The Mistborn Saga");
            Assert.Equal("2", renamed.SeriesNumber);
            Assert.Equal("B0SERIES1", renamed.SeriesAsin);
            Assert.True(renamed.IsPrimary);
            Assert.Equal("The Mistborn Saga", stored!.Series);
            Assert.Equal("2", stored.SeriesNumber);
        }

        [Fact]
        public async Task RenameMembership_OntoANameTheBookAlreadyCarries_MergesToOneMembership()
        {
            // Given a book that is in both spellings at different positions. Normalize dedupes on
            // name plus number plus ASIN, so a plain rename would leave two Mistborn Saga rows.
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Double Filed")
                .WithSeriesMembership("The Mistborn Saga", isPrimary: true, seriesAsin: "B0SERIES1")
                .WithSeriesMembership("Mistborn", "2")
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "renameMembership",
                    matchName = "Mistborn",
                    seriesName = "The Mistborn Saga",
                    numbering = "keep"
                });

            // Then one membership remains: the renamed position, the surviving ASIN, and primary
            // because the membership it merged into was primary.
            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            var membership = Assert.Single(OrderedMemberships(stored!));
            Assert.Equal("The Mistborn Saga", membership.SeriesName);
            Assert.Equal("2", membership.SeriesNumber);
            Assert.Equal("B0SERIES1", membership.SeriesAsin);
            Assert.True(membership.IsPrimary);
            Assert.Equal("The Mistborn Saga", stored!.Series);
            Assert.Equal("2", stored.SeriesNumber);
        }

        [Fact]
        public async Task RenameMembership_SeriesNotOnTheBook_IsAPerBookError()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Not In That Series")
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "renameMembership",
                    matchName = "Stormlight Archive",
                    seriesName = "The Stormlight Archive"
                });

            var item = SingleItem(result);
            Assert.False(item.GetProperty("success").GetBoolean());
            Assert.Contains(
                item.GetProperty("errors").EnumerateArray(),
                error => error.GetString()?.Contains(
                    "not in the series 'Stormlight Archive'",
                    StringComparison.Ordinal) == true);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.Equal("Mistborn", Assert.Single(OrderedMemberships(stored!)).SeriesName);
        }

        [Fact]
        public async Task Numbering_Clear_BlanksTheNumberOnTheTouchedMembershipOnly()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Cleared Position")
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .WithSeriesMembership("Cosmere", "3")
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "setPrimary",
                    seriesName = "Mistborn",
                    numbering = "clear"
                });

            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            var memberships = OrderedMemberships(stored!);
            Assert.Null(
                Assert.Single(memberships, membership => membership.SeriesName == "Mistborn")
                    .SeriesNumber);
            Assert.Equal(
                "3",
                Assert.Single(memberships, membership => membership.SeriesName == "Cosmere")
                    .SeriesNumber);
            Assert.Null(stored!.SeriesNumber);
        }

        [Fact]
        public async Task Numbering_Keep_LeavesTheExistingNumberUntouched()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Kept Position")
                .WithSeriesMembership("Mistborn", "2")
                .WithSeriesMembership("Cosmere", "3", isPrimary: true)
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "setPrimary",
                    seriesName = "Mistborn",
                    numbering = "keep"
                });

            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            var primary = Assert.Single(
                OrderedMemberships(stored!),
                membership => membership.IsPrimary);
            Assert.Equal("Mistborn", primary.SeriesName);
            Assert.Equal("2", primary.SeriesNumber);
            Assert.Equal("2", stored!.SeriesNumber);
        }

        [Fact]
        public async Task PerIdOverrides_GiveEachBookItsOwnPositionInOneRequest()
        {
            Init();
            var first = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Book One")
                .Build());
            var second = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Book Two")
                .Build());
            var third = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Book Three")
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [first.Id, second.Id, third.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["series"] = SeriesElement(new
                        {
                            mode = "setPrimary",
                            seriesName = "The Mistborn Saga",
                            numbering = "explicit"
                        })
                    },
                    PerIdOverrides = new Dictionary<int, Dictionary<string, object>>
                    {
                        [first.Id] = SeriesOverride("The Mistborn Saga", "1"),
                        [second.Id] = SeriesOverride("The Mistborn Saga", "2"),
                        [third.Id] = SeriesOverride("The Mistborn Saga", "3")
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(result);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            Assert.All(
                document.RootElement.GetProperty("results").EnumerateArray(),
                item => Assert.True(item.GetProperty("success").GetBoolean()));
            Assert.Equal("1", (await GetFreshAudiobookAsync(first.Id))!.SeriesNumber);
            Assert.Equal("2", (await GetFreshAudiobookAsync(second.Id))!.SeriesNumber);
            Assert.Equal("3", (await GetFreshAudiobookAsync(third.Id))!.SeriesNumber);
            Assert.Equal(
                "The Mistborn Saga",
                (await GetFreshAudiobookAsync(third.Id))!.Series);
        }

        [Fact]
        public async Task LegacySeriesColumnsOnly_AreStillFoundByRename()
        {
            // Given a book that predates the memberships table.
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Legacy Only")
                .WithSeries("Mistborn")
                .WithSeriesNumber("1")
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "renameMembership",
                    matchName = "Mistborn",
                    seriesName = "The Mistborn Saga"
                });

            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            var membership = Assert.Single(OrderedMemberships(stored!));
            Assert.Equal("The Mistborn Saga", membership.SeriesName);
            Assert.Equal("1", membership.SeriesNumber);
            Assert.Equal("The Mistborn Saga", stored!.Series);
        }

        [Theory]
        [InlineData("setPrimary")]
        [InlineData("addMembership")]
        [InlineData("renameMembership")]
        public async Task BlankSeriesName_IsRejectedWithoutWritingAnything(string mode)
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Blank Name")
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode,
                    seriesName = "   ",
                    matchName = "Mistborn"
                });

            var item = SingleItem(result);
            Assert.False(item.GetProperty("success").GetBoolean());
            Assert.Contains(
                item.GetProperty("errors").EnumerateArray(),
                error => error.GetString() == "A series name is required");
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.Equal("Mistborn", Assert.Single(OrderedMemberships(stored!)).SeriesName);
        }

        [Fact]
        public async Task UnknownMode_IsRejectedAsAPerBookError()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Unknown Mode")
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "deleteEverything",
                    seriesName = "The Mistborn Saga"
                });

            var item = SingleItem(result);
            Assert.False(item.GetProperty("success").GetBoolean());
            Assert.Contains(
                item.GetProperty("errors").EnumerateArray(),
                error => error.GetString() == "Unknown series update mode 'deleteEverything'");
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.Equal("Mistborn", Assert.Single(OrderedMemberships(stored!)).SeriesName);
        }

        [Fact]
        public async Task SeriesUpdate_ReturnsTheNewMembershipsAndWritesHistory()
        {
            Init();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("History Book")
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "renameMembership",
                    matchName = "Mistborn",
                    seriesName = "The Mistborn Saga"
                });

            var item = SingleItem(result);
            var memberships = item.GetProperty("seriesMemberships");
            Assert.Equal(JsonValueKind.Array, memberships.ValueKind);
            Assert.Equal(1, memberships.GetArrayLength());
            var histories = await _historyRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Contains(
                histories,
                history => history.Message != null
                    && history.Message.Contains(
                        "renamed to The Mistborn Saga",
                        StringComparison.Ordinal));
        }

        [Fact]
        public async Task SeriesUpdate_DoesNotMoveFilesOrRewriteTheBasePath()
        {
            Init();
            var basePath = FileService.GetTempDirectory("series-bulk-update");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Stationary")
                .WithAuthor("Brandon Sanderson")
                .WithBasePath(basePath)
                .WithSeriesMembership("Mistborn", "1", isPrimary: true)
                .Build());

            var result = await BulkUpdateSeriesAsync(
                [audiobook.Id],
                new
                {
                    mode = "renameMembership",
                    matchName = "Mistborn",
                    seriesName = "The Mistborn Saga"
                });

            AssertItemSucceeded(result, audiobook.Id);
            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.Equal(basePath, stored!.BasePath);
            Assert.Equal("none", SingleItem(result).GetProperty("pathChangeOutcome").GetString());
        }

        private Task<IActionResult> BulkUpdateSeriesAsync(List<int> ids, object series) =>
            _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = ids,
                    Updates = new Dictionary<string, object>
                    {
                        ["series"] = SeriesElement(series)
                    }
                });

        private static Dictionary<string, object> SeriesOverride(
            string seriesName,
            string seriesNumber) =>
            new()
            {
                ["series"] = SeriesElement(new
                {
                    mode = "setPrimary",
                    seriesName,
                    numbering = "explicit",
                    seriesNumber
                })
            };

        // The controller receives the value as a JsonElement in production, so the tests do too.
        private static JsonElement SeriesElement(object series) =>
            JsonSerializer.SerializeToElement(series, new JsonSerializerOptions(
                JsonSerializerDefaults.Web));

        private static JsonElement SingleItem(IActionResult result)
        {
            var ok = Assert.IsType<OkObjectResult>(result);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            return Assert.Single(
                document.RootElement.GetProperty("results").EnumerateArray().ToList())
                .Clone();
        }

        private static void AssertItemSucceeded(IActionResult result, int id)
        {
            var item = SingleItem(result);
            Assert.Equal(id, item.GetProperty("id").GetInt32());
            Assert.True(
                item.GetProperty("success").GetBoolean(),
                JsonSerializer.Serialize(item.GetProperty("errors")));
            Assert.True(item.GetProperty("metadataUpdated").GetBoolean());
        }

        private static List<AudiobookSeriesMembership> OrderedMemberships(Audiobook audiobook) =>
            (audiobook.SeriesMemberships ?? [])
                .OrderBy(membership => membership.SortOrder)
                .ToList();

        private async Task<Audiobook?> GetFreshAudiobookAsync(int id)
        {
            using var scope = _provider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            return await repository.GetByIdAsync(id);
        }
    }
}
