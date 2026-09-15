/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Globalization;
using System.Text.RegularExpressions;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Search.Scoring
{
    /// <summary>
    /// What the caller knows about the book it is trying to identify. Every field is optional;
    /// a term that cannot be evaluated is dropped from the weighting rather than scored zero.
    /// </summary>
    public sealed record MatchScoreRequest(
        string? Title = null,
        string? Author = null,
        int? DurationSeconds = null,
        string? Series = null,
        string? SeriesPosition = null,
        string? Language = null);

    /// <summary>
    /// A candidate from a metadata provider, flattened so both response shapes can be scored.
    /// </summary>
    public sealed record MatchScoreCandidate(
        string? Title = null,
        string? Subtitle = null,
        IReadOnlyList<string>? Authors = null,
        string? SeriesName = null,
        string? SeriesPosition = null,
        int? RuntimeMinutes = null,
        string? Language = null,
        string? FormatType = null);

    public sealed record MatchScoreBreakdown(double Score, IReadOnlyList<string> Reasons);

    /// <summary>
    /// Confidence scoring for "is this candidate the book the caller is holding".
    /// Deliberately separate from <see cref="SearchResultScorerService"/>, whose weights assume a
    /// free-text query and reward title containment of the whole query string.
    /// </summary>
    public static class LibraryMatchScorer
    {
        private const double AuthorWeight = 0.35;
        private const double TitleWeight = 0.40;
        private const double RuntimeWeight = 0.15;
        private const double SeriesWeight = 0.05;
        private const double LanguageWeight = 0.05;

        private const double AbridgedPenalty = 0.10;
        private const double SuspiciouslyShortPenalty = 0.15;
        private const int SuspiciouslyShortMinutes = 60;

        private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "the", "of", "book", "novel", "audiobook", "unabridged", "abridged", "vol", "volume"
        };

        public static MatchScoreBreakdown Score(MatchScoreRequest request, MatchScoreCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(candidate);

            var reasons = new List<string>();
            var weighted = 0.0;
            var available = 0.0;

            var author = ScoreAuthor(request.Author, candidate.Authors, reasons);
            if (author.HasValue)
            {
                weighted += AuthorWeight * author.Value;
                available += AuthorWeight;
            }

            var title = ScoreTitle(request.Title, candidate, reasons);
            if (title.HasValue)
            {
                weighted += TitleWeight * title.Value;
                available += TitleWeight;
            }

            var runtime = ScoreRuntime(request.DurationSeconds, candidate.RuntimeMinutes, reasons);
            if (runtime.HasValue)
            {
                weighted += RuntimeWeight * runtime.Value;
                available += RuntimeWeight;
            }

            var series = ScoreSeries(request, candidate, reasons);
            if (series.HasValue)
            {
                weighted += SeriesWeight * series.Value;
                available += SeriesWeight;
            }

            var language = ScoreLanguage(request.Language, candidate.Language, reasons);
            if (language.HasValue)
            {
                weighted += LanguageWeight * language.Value;
                available += LanguageWeight;
            }

            if (available <= 0)
            {
                return new MatchScoreBreakdown(0.0, new[] { "nothing to compare" });
            }

            var score = weighted / available;

            if (IsAbridged(candidate))
            {
                score -= AbridgedPenalty;
                reasons.Add("abridged edition");
            }

            if (!request.DurationSeconds.HasValue &&
                candidate.RuntimeMinutes.HasValue &&
                candidate.RuntimeMinutes.Value > 0 &&
                candidate.RuntimeMinutes.Value < SuspiciouslyShortMinutes)
            {
                score -= SuspiciouslyShortPenalty;
                reasons.Add($"only {candidate.RuntimeMinutes.Value} minutes long");
            }

            return new MatchScoreBreakdown(Math.Clamp(score, 0.0, 1.0), reasons);
        }

        /// <summary>
        /// Token overlap between two titles, ignoring punctuation, case and stopwords.
        /// Lifted from AudiobookMatchingService.CalculateTitleSimilarity, which plan 05 deletes.
        /// </summary>
        public static double TitleSimilarity(string? left, string? right)
        {
            var normalizedLeft = Normalize(left);
            var normalizedRight = Normalize(right);

            if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            {
                return 0.0;
            }

            if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal))
            {
                return 1.0;
            }

            if (normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal) ||
                normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal))
            {
                return 0.8;
            }

            var leftTokens = Tokenize(left);
            var rightTokens = Tokenize(right);
            var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
            if (union == 0)
            {
                return 0.0;
            }

            return (double)leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count() / union;
        }

        /// <summary>
        /// 1.0 within 10 percent, 0.5 within 25 percent, otherwise 0.
        /// </summary>
        public static double RuntimeAgreement(int expectedMinutes, int candidateMinutes)
        {
            if (expectedMinutes <= 0 || candidateMinutes <= 0)
            {
                return 0.0;
            }

            var drift = Math.Abs(expectedMinutes - candidateMinutes) / (double)expectedMinutes;
            if (drift <= 0.10) return 1.0;
            if (drift <= 0.25) return 0.5;
            return 0.0;
        }

        /// <summary>
        /// True when the detected title is not obviously a different book. Tolerant on purpose:
        /// its job is to drop summaries and unrelated titles, not to demand the detected text.
        /// </summary>
        public static bool TitlesPlausiblyMatch(string? detectedTitle, MatchScoreCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(detectedTitle))
            {
                return true;
            }

            var score = ScoreTitle(detectedTitle, candidate, new List<string>());
            return score.GetValueOrDefault() > 0.0;
        }

        public static HashSet<string> Tokenize(string? value)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(value))
            {
                return tokens;
            }

            foreach (var token in Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Stopwords.Contains(token))
                {
                    tokens.Add(token);
                }
            }

            return tokens;
        }

        private static double? ScoreAuthor(string? requestAuthor, IReadOnlyList<string>? candidateAuthors, List<string> reasons)
        {
            if (string.IsNullOrWhiteSpace(requestAuthor))
            {
                return null;
            }

            var names = (candidateAuthors ?? Array.Empty<string>())
                .SelectMany(SplitAuthorNames)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(Normalize)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            // A result with authors: [{ asin, name: undefined }] tells us nothing. Do not call
            // that a disagreement; drop the term instead.
            if (names.Count == 0)
            {
                return null;
            }

            var needle = Normalize(requestAuthor);
            if (names.Any(name => string.Equals(name, needle, StringComparison.Ordinal)))
            {
                reasons.Add("author matches");
                return 1.0;
            }

            if (names.Any(name => name.Contains(needle, StringComparison.Ordinal) || needle.Contains(name, StringComparison.Ordinal)))
            {
                reasons.Add("author partially matches");
                return 0.6;
            }

            reasons.Add($"author disagrees (found {string.Join(", ", names.Take(2))})");
            return 0.0;
        }

        private static double? ScoreTitle(string? requestTitle, MatchScoreCandidate candidate, List<string> reasons)
        {
            if (string.IsNullOrWhiteSpace(requestTitle))
            {
                return null;
            }

            var candidateTitle = candidate.Title ?? string.Empty;
            var combined = string.Join(
                " ",
                new[] { candidate.Title, candidate.Subtitle, candidate.SeriesName }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

            if (string.IsNullOrWhiteSpace(combined))
            {
                return null;
            }

            var detectedTokens = Tokenize(requestTitle);
            var combinedTokens = Tokenize(combined);
            var titleTokens = Tokenize(candidateTitle);

            if (detectedTokens.Count > 0 && detectedTokens.IsSubsetOf(combinedTokens))
            {
                reasons.Add("title matches");
                return 1.0;
            }

            // "Mistborn" is the canonical title behind the detected alias "Mistborn, The Final
            // Empire": every word of the candidate title is in what we detected.
            if (titleTokens.Count > 0 && titleTokens.IsSubsetOf(detectedTokens))
            {
                reasons.Add("title matches");
                return 0.9;
            }

            if (TitleUtils.AreTitlesSimilar(requestTitle, candidateTitle) ||
                TitleUtils.AreTitlesSimilar(requestTitle, candidate.Subtitle ?? string.Empty) ||
                TitleUtils.AreTitlesSimilar(requestTitle, combined))
            {
                reasons.Add("title is similar");
                return 0.8;
            }

            var overlap = TitleSimilarity(requestTitle, combined);
            reasons.Add(overlap > 0 ? "title partially matches" : "title disagrees");
            return overlap;
        }

        private static double? ScoreRuntime(int? requestDurationSeconds, int? candidateMinutes, List<string> reasons)
        {
            if (!requestDurationSeconds.HasValue || requestDurationSeconds.Value <= 0)
            {
                return null;
            }

            if (!candidateMinutes.HasValue || candidateMinutes.Value <= 0)
            {
                return null;
            }

            var expectedMinutes = (int)Math.Round(requestDurationSeconds.Value / 60.0);
            var agreement = RuntimeAgreement(expectedMinutes, candidateMinutes.Value);
            reasons.Add(agreement switch
            {
                >= 1.0 => "runtime matches",
                >= 0.5 => "runtime is close",
                _ => $"runtime disagrees ({candidateMinutes.Value} min vs {expectedMinutes} min)"
            });
            return agreement;
        }

        private static double? ScoreSeries(MatchScoreRequest request, MatchScoreCandidate candidate, List<string> reasons)
        {
            var requestPosition = ParsePosition(request.SeriesPosition) ?? ParsePositionFromTitle(request.Title);
            var candidatePosition = ParsePosition(candidate.SeriesPosition) ?? ParsePositionFromTitle(candidate.Subtitle);
            if (requestPosition == null || candidatePosition == null)
            {
                return null;
            }

            if (requestPosition == candidatePosition)
            {
                reasons.Add($"series position {requestPosition} matches");
                return 1.0;
            }

            reasons.Add($"series position disagrees ({candidatePosition} vs {requestPosition})");
            return 0.0;
        }

        private static double? ScoreLanguage(string? requestLanguage, string? candidateLanguage, List<string> reasons)
        {
            if (string.IsNullOrWhiteSpace(requestLanguage) || string.IsNullOrWhiteSpace(candidateLanguage))
            {
                return null;
            }

            if (string.Equals(requestLanguage.Trim(), candidateLanguage.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return 1.0;
            }

            reasons.Add($"language disagrees ({candidateLanguage})");
            return 0.0;
        }

        private static bool IsAbridged(MatchScoreCandidate candidate)
        {
            var haystack = string.Join(
                " ",
                new[] { candidate.FormatType, candidate.Title, candidate.Subtitle }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            return haystack.Contains("abridged", StringComparison.OrdinalIgnoreCase) &&
                   !haystack.Contains("unabridged", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SplitAuthorNames(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static decimal? ParsePosition(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static decimal? ParsePositionFromTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = Regex.Match(value, @"\b(?:book|vol|volume|part)\s*#?\s*(\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase);
            return match.Success && decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = TitleUtils.NormalizeTitle(value);
            normalized = Regex.Replace(normalized, @"[^\p{L}\p{Nd}\s]", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized.Trim().ToLowerInvariant();
        }
    }
}
