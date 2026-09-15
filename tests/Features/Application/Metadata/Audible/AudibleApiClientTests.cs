/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Net;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Metadata.Audible
{
    [Trait("Name", "AudibleApiClientTests")]
    [Trait("Category", "AudibleApiClient")]
    [Trait("Third-Party", "Audible")]
    public class AudibleApiClientTests : BaseTests
    {
        [Fact]
        [Trait("Method", "SearchBooksAsync")]
        [Trait("Scenario", "RateLimitIsNotAnEmptySuccessfulResult")]
        public async Task SearchBooksAsync_AudibleAnswers429_ReportsRateLimitedRatherThanZeroResults()
        {
            // Given Audible answers 429 with a Retry-After
            using var httpClient = new HttpClient(new StubHandler(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
                return response;
            }));
            var service = new AudibleService(httpClient, NullLogger<AudibleService>.Instance);

            // When
            var response = await service.SearchBooksAsync("Mistborn", 1, 50, "us", null);

            // Then the caller can tell an outage from "this book does not exist"
            Assert.NotNull(response);
            Assert.True(response!.Failed);
            Assert.True(response.RateLimited);
            Assert.Equal(TimeSpan.FromSeconds(45), response.RetryAfter);
            Assert.Empty(response.Results ?? new List<AudibleSearchResult>());
        }

        [Fact]
        [Trait("Method", "SearchBooksAsync")]
        [Trait("Scenario", "TransportFailureIsNotAnEmptySuccessfulResult")]
        public async Task SearchBooksAsync_TransportFailure_ReportsFailedWithoutClaimingRateLimit()
        {
            // Given
            using var httpClient = new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection reset")));
            var service = new AudibleService(httpClient, NullLogger<AudibleService>.Instance);

            // When
            var response = await service.SearchBooksAsync("Mistborn", 1, 50, "us", null);

            // Then
            Assert.NotNull(response);
            Assert.True(response!.Failed);
            Assert.False(response.RateLimited);
        }

        [Fact]
        [Trait("Method", "SearchBooksAsync")]
        [Trait("Scenario", "GenuineZeroResultsAreNotReportedAsFailure")]
        public async Task SearchBooksAsync_EmptyCatalogueResponse_IsNotReportedAsAFailure()
        {
            // Given
            using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"products\":[],\"total_results\":0}")
            }));
            var service = new AudibleService(httpClient, NullLogger<AudibleService>.Instance);

            // When
            var response = await service.SearchBooksAsync("a book that does not exist", 1, 50, "us", null);

            // Then
            Assert.NotNull(response);
            Assert.False(response!.Failed);
            Assert.False(response.RateLimited);
            Assert.Empty(response.Results ?? new List<AudibleSearchResult>());
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_respond(request));
            }
        }
    }
}
