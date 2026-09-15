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

namespace Listenarr.Tests.Features.Application.Search.Scoring
{
    [Trait("Area", "Search")]
    [Trait("Name", "LibraryMatchScorerTests")]
    [Trait("Category", "LibraryMatchScorer")]
    public class LibraryMatchScorerTests : BaseTests
    {
        private static readonly MatchScoreRequest MistbornRequest = new(
            Title: "Mistborn, The Final Empire",
            Author: "Brandon Sanderson",
            DurationSeconds: 1499 * 60);

        [Fact]
        [Trait("Method", "Score")]
        [Trait("Scenario", "RanksTheCanonicalRecordAboveEveryOtherRealCandidate")]
        public void Score_MistbornWorkedExample_RanksB004SOK2SEHighest()
        {
            // Given the four products Audible really returns for "Mistborn, The Final Empire"
            var candidates = AudibleCatalogFixture
                .LoadResults(AudibleCatalogFixture.KeywordsMistbornTheFinalEmpire)
                .ToDictionary(result => result.Asin!, ToCandidate, StringComparer.OrdinalIgnoreCase);

            // When
            var scores = candidates.ToDictionary(
                pair => pair.Key,
                pair => LibraryMatchScorer.Score(MistbornRequest, pair.Value).Score,
                StringComparer.OrdinalIgnoreCase);

            // Then the canonical record wins, and by more than the ambiguity margin
            var ranked = scores.OrderByDescending(pair => pair.Value).ToList();
            Assert.Equal("B004SOK2SE", ranked[0].Key);
            Assert.True(ranked[0].Value >= 0.75, $"expected at or above the auto-match threshold, got {ranked[0].Value}");
            Assert.True(
                ranked[0].Value - ranked[1].Value > 0.1,
                $"expected a clear winner, got {ranked[0].Value} vs {ranked[1].Value} ({ranked[1].Key})");
        }

        [Fact]
        [Trait("Method", "Score")]
        [Trait("Scenario", "RuntimeSeparatesTheNovelFromTheNovella")]
        public void Score_RuntimeHint_PutsMistbornAboveSecretHistory()
        {
            // Given two books by the same author whose titles both start with "Mistborn"
            var results = AudibleCatalogFixture.LoadResults(AudibleCatalogFixture.KeywordsSandersonMistbornTheFinalEmpire);
            var novel = ToCandidate(results.Single(result => result.Asin == "B004SOK2SE"));
            var novella = ToCandidate(results.Single(result => result.Asin == "B07F88TSBT"));
            Assert.Equal(1499, novel.RuntimeMinutes);
            Assert.Equal(329, novella.RuntimeMinutes);

            // When
            var novelScore = LibraryMatchScorer.Score(MistbornRequest, novel).Score;
            var novellaScore = LibraryMatchScorer.Score(MistbornRequest, novella).Score;

            // Then
            Assert.True(novelScore > novellaScore, $"{novelScore} should beat {novellaScore}");
        }

        [Fact]
        [Trait("Method", "Score")]
        [Trait("Scenario", "SummaryProductScoresBelowTheThreshold")]
        public void Score_SummaryByADifferentAuthor_ScoresBelowTheAutoMatchThreshold()
        {
            // Given the Briefly Summaries product, whose title contains every detected word
            var summary = ToCandidate(AudibleCatalogFixture
                .LoadResults(AudibleCatalogFixture.KeywordsMistbornTheFinalEmpire)
                .Single(result => result.Asin == "B0GFB7JV5P"));

            // When
            var breakdown = LibraryMatchScorer.Score(MistbornRequest, summary);

            // Then
            Assert.True(breakdown.Score < 0.75, $"expected below threshold, got {breakdown.Score}");
            Assert.Contains(breakdown.Reasons, reason => reason.StartsWith("author disagrees", StringComparison.Ordinal));
        }

        [Fact]
        [Trait("Method", "Score")]
        [Trait("Scenario", "MissingAuthorNameIsNotDisagreement")]
        public void Score_CandidateWithNoAuthorName_DoesNotCountAsAuthorDisagreement()
        {
            // Given a search result shaped like authors: [{ asin, name: undefined }]
            var candidate = new MatchScoreCandidate(
                Title: "Jack of Shadows",
                Authors: new[] { string.Empty },
                RuntimeMinutes: 300);

            // When
            var breakdown = LibraryMatchScorer.Score(
                new MatchScoreRequest(Title: "Jack of Shadows", Author: "Roger Zelazny"),
                candidate);

            // Then the title carries the whole score instead of a phantom author penalty
            Assert.Equal(1.0, breakdown.Score);
            Assert.DoesNotContain(breakdown.Reasons, reason => reason.StartsWith("author disagrees", StringComparison.Ordinal));
        }

        [Fact]
        [Trait("Method", "Score")]
        [Trait("Scenario", "UnrelatedResultScoresLow")]
        public void Score_UnrelatedCandidate_ScoresWellBelowTheThreshold()
        {
            // Given a row detected as "Chapter 1" by "Michael Kramer" (the narrator, not the author)
            var candidate = new MatchScoreCandidate(
                Title: "The Way of Kings",
                Subtitle: "The Stormlight Archive, Book 1",
                Authors: new[] { "Brandon Sanderson" },
                RuntimeMinutes: 2734);

            // When
            var breakdown = LibraryMatchScorer.Score(
                new MatchScoreRequest(Title: "Chapter 1", Author: "Michael Kramer"),
                candidate);

            // Then
            Assert.True(breakdown.Score < 0.75, $"expected below threshold, got {breakdown.Score}");
        }

        [Theory]
        [Trait("Method", "RuntimeAgreement")]
        [InlineData(1499, 1499, 1.0)]
        [InlineData(1499, 1400, 1.0)]
        [InlineData(1499, 1200, 0.5)]
        [InlineData(1499, 329, 0.0)]
        public void RuntimeAgreement_GradesDriftInTenAndTwentyFivePercentBands(int expected, int candidate, double agreement)
        {
            Assert.Equal(agreement, LibraryMatchScorer.RuntimeAgreement(expected, candidate));
        }

        private static MatchScoreCandidate ToCandidate(AudibleSearchResult result)
        {
            var series = result.Series?.FirstOrDefault();
            return new MatchScoreCandidate(
                Title: result.Title,
                Subtitle: result.Subtitle,
                Authors: (result.Authors ?? new List<AudibleAuthor>()).Select(author => author.Name ?? string.Empty).ToList(),
                SeriesName: series?.Name,
                SeriesPosition: series?.Position,
                RuntimeMinutes: result.RuntimeLengthMin ?? result.LengthMinutes ?? result.RuntimeMinutes,
                Language: result.Language,
                FormatType: result.BookFormat);
        }
    }
}
