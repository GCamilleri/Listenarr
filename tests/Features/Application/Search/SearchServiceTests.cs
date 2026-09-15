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

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search
{
    [Trait("Area", "Search")]
    [Trait("Name", "SearchServiceTests")]
    [Trait("Category", "SearchService")]
    public class SearchServiceTests : BaseTests
    {
        [Fact]
        [Trait("Method", "IntelligentSearchAsync")]
        [Trait("Scenario", "AudibleTitleResultUsesRequestedRegionForLinks")]
        public async Task IntelligentSearch_TitleAudibleResult_UsesRequestedRegionForProductLinks()
        {
            // Given
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance);
            var audibleResult = new AudibleSearchResultBuilder()
                .WithAsin("B0DUNE1234")
                .WithTitle("Dune")
                .WithAuthor("Frank Herbert")
                .WithLanguage("german")
                .Build();
            var audibleResponse = new AudibleSearchResponseBuilder()
                .WithResult(audibleResult)
                .WithTotalResults(1)
                .Build();

            audible
                .Setup(service => service.SearchByTitleAsync("Dune", 1, 50, "de", "german"))
                .ReturnsAsync(audibleResponse);

            Init(services => services.WithSingleton<AudibleService>(audible.Object));
            var searchService = _provider.GetRequiredService<ISearchService>();

            // When
            var results = await searchService.IntelligentSearchAsync("TITLE:Dune", region: "de", language: "german");

            // Then
            var result = Assert.Single(results);
            Assert.Equal("https://www.audible.de/pd/B0DUNE1234", result.ProductUrl);
            Assert.Equal("https://www.audible.de/pd/B0DUNE1234", result.SourceLink);
            Assert.Equal("Audible", result.MetadataSource);
            audible.Verify(service => service.SearchByTitleAsync("Dune", 1, 50, "de", "german"), Times.Once);
        }

        [Fact]
        [Trait("Method", "IntelligentSearchAsync")]
        [Trait("Scenario", "FallbackReceivesTheParsedTermsNotThePrefixedQuery")]
        public async Task IntelligentSearch_AuthorAndTitle_PassesTheParsedTermsToTheFallbackCollector()
        {
            // Given Audible finds nothing, so execution falls through to candidate collection
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance);
            audible
                .Setup(service => service.SearchBooksAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse { Results = new List<AudibleSearchResult>(), TotalResults = 0 });
            audible
                .Setup(service => service.LookupAuthorAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((AuthorLookupItem?)null);
            audible
                .Setup(service => service.SearchByAuthorAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse { Results = new List<AudibleSearchResult>(), TotalResults = 0 });

            var collector = new Mock<AsinCandidateCollector>(
                NullLogger<AsinCandidateCollector>.Instance,
                Mock.Of<IOpenLibraryService>(),
                new MetadataConverters(null, NullLogger<MetadataConverters>.Instance),
                new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance));
            var observedQueries = new List<string>();
            collector
                .Setup(candidateCollector => candidateCollector.CollectCandidatesAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<string, bool, CancellationToken>((query, _, _) => observedQueries.Add(query))
                .ReturnsAsync(new AsinCandidateCollection());

            Init(services => services
                .WithSingleton<AudibleService>(audible.Object)
                .WithSingleton<AsinCandidateCollector>(collector.Object));
            var searchService = _provider.GetRequiredService<ISearchService>();

            // When
            await searchService.IntelligentSearchAsync("AUTHOR:Brandon Sanderson TITLE:Mistborn, The Final Empire");

            // Then the collector sees words, not "AUTHOR:... TITLE:..."
            var observed = Assert.Single(observedQueries);
            Assert.DoesNotContain("AUTHOR:", observed, StringComparison.Ordinal);
            Assert.DoesNotContain("TITLE:", observed, StringComparison.Ordinal);
            Assert.Contains("Mistborn, The Final Empire", observed, StringComparison.Ordinal);
            Assert.Contains("Brandon Sanderson", observed, StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Method", "IntelligentSearchAsync")]
        [Trait("Scenario", "AudibleFirstResultsCarryAMatchScore")]
        public async Task IntelligentSearch_AuthorAndTitleWithDurationHint_ScoresTheCanonicalRecordHighest()
        {
            // Given the four products Audible really returns for these keywords
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance);
            audible
                .Setup(service => service.SearchBooksAsync("Brandon Sanderson Mistborn, The Final Empire", 1, 50, "us", null))
                .ReturnsAsync(AudibleCatalogFixture.LoadResponse(AudibleCatalogFixture.KeywordsMistbornTheFinalEmpire));

            Init(services => services.WithSingleton<AudibleService>(audible.Object));
            var searchService = _provider.GetRequiredService<ISearchService>();

            // When the row reports about 25 hours of audio
            var results = await searchService.IntelligentSearchAsync(
                "AUTHOR:Brandon Sanderson TITLE:Mistborn, The Final Empire",
                durationSeconds: 1499 * 60);

            // Then
            Assert.NotEmpty(results);
            Assert.Equal("B004SOK2SE", results[0].Asin);
            Assert.NotNull(results[0].MatchScore);
            Assert.True(results[0].MatchScore >= 0.75, $"expected an auto-matchable score, got {results[0].MatchScore}");
            Assert.NotEmpty(results[0].MatchReasons ?? new List<string>());
            audible.Verify(
                service => service.SearchByAuthorAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never);
        }

        [Fact]
        [Trait("Method", "IntelligentSearchAsync")]
        [Trait("Scenario", "RateLimitIsNotAnEmptySuccess")]
        public async Task IntelligentSearch_AudibleRateLimited_SurfacesTheFailureInsteadOfNoResults()
        {
            // Given Audible answers 429
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance);
            audible
                .Setup(service => service.SearchByTitleAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>(),
                    TotalResults = 0,
                    Failed = true,
                    RateLimited = true,
                    RetryAfter = TimeSpan.FromSeconds(12)
                });

            Init(services => services.WithSingleton<AudibleService>(audible.Object));
            var searchService = _provider.GetRequiredService<ISearchService>();

            // When / Then
            var exception = await Assert.ThrowsAsync<MetadataSearchUnavailableException>(
                () => searchService.IntelligentSearchAsync("TITLE:Dune"));
            Assert.True(exception.RateLimited);
            Assert.Equal(TimeSpan.FromSeconds(12), exception.RetryAfter);
        }
    }
}
