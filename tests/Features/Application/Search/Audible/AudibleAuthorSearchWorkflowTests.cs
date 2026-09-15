/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.Audible
{
    [Trait("Area", "Search")]
    [Trait("Name", "AudibleAuthorSearchWorkflowTests")]
    [Trait("Category", "AudibleAuthorSearchWorkflow")]
    [Trait("Third-Party", "Audible")]
    public class AudibleAuthorSearchWorkflowTests : BaseTests
    {
        private const string Author = "Brandon Sanderson";
        private const string DetectedTitle = "Mistborn, The Final Empire";

        [Fact]
        [Trait("Method", "TrySearchAsync")]
        [Trait("Scenario", "KeywordSearchFindsTheAliasAndTheAuthorFilterDropsTheSummary")]
        public async Task TrySearchAsync_AuthorAndTitle_KeepsBothSandersonTitlesAndDropsTheSummary()
        {
            // Given the four products Audible really returns for these keywords
            var audible = CreateAudibleMock();
            audible
                .Setup(service => service.SearchBooksAsync($"{Author} {DetectedTitle}", 1, 50, "us", null))
                .ReturnsAsync(AudibleCatalogFixture.LoadResponse(AudibleCatalogFixture.KeywordsMistbornTheFinalEmpire));

            var workflow = CreateWorkflow(audible);

            // When
            var results = await workflow.TrySearchAsync("AUTHOR_TITLE", Author, DetectedTitle, isbn: null, candidateLimit: 5, region: "us", language: null);

            // Then the Briefly Summaries product is gone and the real Sanderson titles survive
            Assert.NotNull(results);
            var asins = results!.Select(result => result.Asin).ToList();
            Assert.Contains("B004SOK2SE", asins);
            Assert.Contains("B07F88TSBT", asins);
            Assert.DoesNotContain("B0GFB7JV5P", asins);
        }

        [Fact]
        [Trait("Method", "TrySearchAsync")]
        [Trait("Scenario", "KeywordSearchAvoidsTheCatalogueWalk")]
        public async Task TrySearchAsync_AuthorAndTitle_DoesNotWalkTheAuthorCatalogue()
        {
            // Given
            var audible = CreateAudibleMock();
            audible
                .Setup(service => service.SearchBooksAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(AudibleCatalogFixture.LoadResponse(AudibleCatalogFixture.KeywordsSandersonMistbornTheFinalEmpire));

            var workflow = CreateWorkflow(audible);

            // When
            var results = await workflow.TrySearchAsync("AUTHOR_TITLE", Author, DetectedTitle, isbn: null, candidateLimit: 5, region: "us", language: null);

            // Then the 126-request author catalogue walk never happens
            Assert.Equal(2, results?.Count);
            audible.Verify(
                service => service.SearchBooksAsync($"{Author} {DetectedTitle}", 1, 50, "us", null),
                Times.Once);
            audible.Verify(
                service => service.SearchByAuthorAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never);
            audible.Verify(
                service => service.GetAllBooksByAuthorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never);
        }

        [Fact]
        [Trait("Method", "TrySearchAsync")]
        [Trait("Scenario", "CatalogueWalkRunsOnceWhenTheKeywordSearchFindsNothing")]
        public async Task TrySearchAsync_KeywordSearchEmpty_WalksTheCatalogueExactlyOnce()
        {
            // Given both keyword attempts come back empty, as the separate-field search does
            var audible = CreateAudibleMock();
            audible
                .Setup(service => service.SearchBooksAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(AudibleCatalogFixture.LoadResponse(AudibleCatalogFixture.AuthorTitleFields));
            audible
                .Setup(service => service.LookupAuthorAsync(Author, "us"))
                .ReturnsAsync(new AuthorLookupItem { Asin = "B001IGFHW6", Name = Author });
            audible
                .Setup(service => service.GetAllBooksByAuthorAsync(Author, "B001IGFHW6", It.IsAny<int>(), "us", null))
                .ReturnsAsync(AudibleCatalogFixture.LoadResponse(AudibleCatalogFixture.AuthorSandersonPage2));

            var workflow = CreateWorkflow(audible);

            // When the same author is searched twice within one scoped request
            await workflow.TrySearchAsync("AUTHOR_TITLE", Author, DetectedTitle, isbn: null, candidateLimit: 5, region: "us", language: null);
            await workflow.TrySearchAsync("AUTHOR_TITLE", Author, DetectedTitle, isbn: null, candidateLimit: 5, region: "us", language: null);

            // Then the catalogue is fetched once, in one call, not paged ten at a time
            audible.Verify(
                service => service.GetAllBooksByAuthorAsync(Author, "B001IGFHW6", It.IsAny<int>(), "us", null),
                Times.Once);
            audible.Verify(
                service => service.SearchByAuthorAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never);
        }

        [Fact]
        [Trait("Method", "TrySearchAsync")]
        [Trait("Scenario", "TolerantTitleFilterKeepsTheCanonicalRecord")]
        public async Task TrySearchAsync_CatalogueWalk_KeepsMistbornDespiteTheAliasTitle()
        {
            // Given the canonical record, whose title and subtitle contain neither "Final" nor
            // "Empire". The old substring filter discarded exactly this book.
            var audible = CreateAudibleMock();
            audible
                .Setup(service => service.SearchBooksAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse { Results = new List<AudibleSearchResult>(), TotalResults = 0 });
            audible
                .Setup(service => service.LookupAuthorAsync(Author, "us"))
                .ReturnsAsync(new AuthorLookupItem { Asin = "B001IGFHW6", Name = Author });

            var canonical = AudibleCatalogFixture
                .LoadResults(AudibleCatalogFixture.KeywordsMistbornTheFinalEmpire)
                .Single(result => result.Asin == "B004SOK2SE");
            Assert.Equal("Mistborn", canonical.Title);
            Assert.Equal("Mistborn, Book 1", canonical.Subtitle);

            audible
                .Setup(service => service.GetAllBooksByAuthorAsync(Author, "B001IGFHW6", It.IsAny<int>(), "us", null))
                .ReturnsAsync(new AudibleSearchResponse { Results = new List<AudibleSearchResult> { canonical }, TotalResults = 1 });

            var workflow = CreateWorkflow(audible);

            // When
            var results = await workflow.TrySearchAsync("AUTHOR_TITLE", Author, DetectedTitle, isbn: null, candidateLimit: 5, region: "us", language: null);

            // Then
            var result = Assert.Single(results ?? new List<MetadataSearchResult>());
            Assert.Equal("B004SOK2SE", result.Asin);
        }

        [Fact]
        [Trait("Method", "TrySearchAsync")]
        [Trait("Scenario", "RateLimitedKeywordSearchIsNotAnEmptyResult")]
        public async Task TrySearchAsync_KeywordSearchRateLimited_ThrowsRatherThanReturningNothing()
        {
            // Given
            var audible = CreateAudibleMock();
            audible
                .Setup(service => service.SearchBooksAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>(),
                    TotalResults = 0,
                    Failed = true,
                    RateLimited = true,
                    RetryAfter = TimeSpan.FromSeconds(30)
                });

            var workflow = CreateWorkflow(audible);

            // When / Then
            var exception = await Assert.ThrowsAsync<MetadataSearchUnavailableException>(
                () => workflow.TrySearchAsync("AUTHOR_TITLE", Author, DetectedTitle, isbn: null, candidateLimit: 5, region: "us", language: null));
            Assert.True(exception.RateLimited);
            Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
        }

        private static Mock<AudibleService> CreateAudibleMock()
        {
            var httpClient = new HttpClient();
            return new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance) { CallBase = false };
        }

        private static AudibleAuthorSearchWorkflow CreateWorkflow(Mock<AudibleService> audible)
        {
            return new AudibleAuthorSearchWorkflow(
                audible.Object,
                new AudibleAuthorPageCollector(audible.Object, NullLogger<AudibleAuthorPageCollector>.Instance),
                new MetadataConverters(null, NullLogger<MetadataConverters>.Instance),
                NullLogger<AudibleAuthorSearchWorkflow>.Instance);
        }
    }
}
