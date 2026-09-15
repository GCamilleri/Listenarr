/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Audible
{
    public sealed class AudibleAuthorSearchWorkflow
    {
        private const int KeywordSearchLimit = 50;
        private const int AuthorCatalogueLimit = 500;

        private readonly AudibleService _audibleService;
        private readonly AudibleAuthorPageCollector _authorPageCollector;
        private readonly MetadataConverters _metadataConverters;
        private readonly ILogger<AudibleAuthorSearchWorkflow> _logger;

        // The workflow is registered scoped, so this only ever caches within one search request.
        private readonly Dictionary<string, List<AudibleSearchResult>> _authorCatalogueCache =
            new(StringComparer.OrdinalIgnoreCase);

        public AudibleAuthorSearchWorkflow(
            AudibleService audibleService,
            AudibleAuthorPageCollector authorPageCollector,
            MetadataConverters metadataConverters,
            ILogger<AudibleAuthorSearchWorkflow> logger)
        {
            _audibleService = audibleService;
            _authorPageCollector = authorPageCollector;
            _metadataConverters = metadataConverters;
            _logger = logger;
        }

        public async Task<List<MetadataSearchResult>?> TrySearchAsync(
            string? searchType,
            string? author,
            string? title,
            string? isbn,
            int candidateLimit,
            string region,
            string? language)
        {
            if (searchType == "AUTHOR" && !string.IsNullOrEmpty(author))
            {
                return await SearchByAuthorAsync(author, candidateLimit, region, language);
            }

            if (searchType == "AUTHOR_TITLE" && !string.IsNullOrEmpty(author))
            {
                return await SearchByAuthorAndTitleAsync(author, title, isbn, candidateLimit, region, language);
            }

            return null;
        }

        private async Task<List<MetadataSearchResult>?> SearchByAuthorAsync(
            string author,
            int candidateLimit,
            string region,
            string? language)
        {
            var aggregated = await _authorPageCollector.CollectAsync(
                author,
                candidateLimit,
                region,
                language,
                "author");

            if (!aggregated.Any())
            {
                return null;
            }

            var deduplicated = DeduplicateByAsin(aggregated);
            _logger.LogInformation(
                "Deduplicated author results for '{Author}': {OriginalCount} -> {DeduplicatedCount}",
                author,
                aggregated.Count,
                deduplicated.Count);

            var converted = new List<SearchResult>();
            var authorFiltered = ApplyStrictLanguageFilter(deduplicated, language);
            foreach (var book in authorFiltered.Where(book => !string.IsNullOrWhiteSpace(book.Asin)))
            {
                var bookResponse = new AudibleBookResponse
                {
                    Asin = book.Asin,
                    Title = book.Title,
                    Subtitle = book.Subtitle,
                    Authors = book.Authors,
                    ImageUrl = book.ImageUrl,
                    Language = book.Language,
                    BookFormat = book.BookFormat,
                    Genres = book.Genres,
                    Series = book.Series,
                    Publisher = book.Publisher,
                    Narrators = book.Narrators,
                    ReleaseDate = book.ReleaseDate,
                    Region = region
                };
                var metadata = _metadataConverters.ConvertAudibleToMetadata(bookResponse, book.Asin!, "Audible");
                var searchResult = await _metadataConverters.ConvertMetadataToSearchResultAsync(metadata, book.Asin!);
                searchResult.IsEnriched = true;
                searchResult.MetadataSource = "Audible";
                converted.Add(searchResult);
            }

            return converted.Any() ? SearchResultConverters.ToMetadataList(converted) : null;
        }

        private async Task<List<MetadataSearchResult>?> SearchByAuthorAndTitleAsync(
            string author,
            string? title,
            string? isbn,
            int candidateLimit,
            string region,
            string? language)
        {
            _logger.LogInformation("Entering AUTHOR_TITLE branch: author='{Author}', title='{Title}', isbn='{Isbn}'", author, title, isbn);

            // Audible's keyword index knows aliases the catalogue records do not: the words
            // "Final Empire" appear nowhere in B004SOK2SE, yet a keyword search for
            // "Brandon Sanderson Mistborn The Final Empire" returns it. One or two requests,
            // against the 100-plus the author catalogue walk costs.
            var aggregated = await SearchByKeywordsAsync(author, title, region, language);
            var usedKeywordSearch = aggregated.Count > 0;

            if (!usedKeywordSearch)
            {
                aggregated = await CollectAuthorCatalogueAsync(author, candidateLimit, region, language);
            }

            if (aggregated.Count == 0)
            {
                return null;
            }

            var deduplicated = DeduplicateByAsin(aggregated);
            _logger.LogInformation(
                "Deduplicated AUTHOR_TITLE results for '{Author}': {OriginalCount} -> {DeduplicatedCount} (keywordSearch={UsedKeywordSearch})",
                author,
                aggregated.Count,
                deduplicated.Count,
                usedKeywordSearch);

            var authorFiltered = ApplyStrictLanguageFilter(deduplicated, language);

            // The keyword search has already done the title work, and it knows aliases this
            // filter cannot. Only the catalogue walk, which returns everything the author wrote,
            // needs a title filter, and even then a tolerant one: its job is to drop obvious
            // non-matches, not to demand the detected text appear verbatim.
            if (!usedKeywordSearch && !string.IsNullOrEmpty(title))
            {
                authorFiltered = authorFiltered.Where(b => LibraryMatchScorer.TitlesPlausiblyMatch(title, ToScoreCandidate(b)));
            }

            var detailedMetaByAsin = new Dictionary<string, AudibleBookResponse>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(isbn))
            {
                authorFiltered = await FilterByIsbnAsync(aggregated, authorFiltered, isbn, candidateLimit, region, language, detailedMetaByAsin);
            }

            _logger.LogDebug("authorFiltered count after language/title/isbn filtering: {Count}", authorFiltered.Count());

            var converted = await AudibleSearchResultMapper.ConvertToSearchResultsAsync(
                authorFiltered,
                _metadataConverters,
                region,
                detailedMetaByAsin,
                _logger,
                continueOnConversionError: true);

            return converted.Any() ? SearchResultConverters.ToMetadataList(converted) : null;
        }

        /// <summary>
        /// One keyword search for author plus title, then title alone, keeping only candidates
        /// the requested author actually wrote.
        /// </summary>
        private async Task<List<AudibleSearchResult>> SearchByKeywordsAsync(
            string author,
            string? title,
            string region,
            string? language)
        {
            var queries = new List<string>();
            if (!string.IsNullOrWhiteSpace(title))
            {
                queries.Add($"{author} {title}".Trim());
                queries.Add(title.Trim());
            }
            else
            {
                queries.Add(author.Trim());
            }

            foreach (var query in queries.Where(q => !string.IsNullOrWhiteSpace(q)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var response = await _audibleService.SearchBooksAsync(query, 1, KeywordSearchLimit, region, language);
                AudibleSearchFailureGuard.ThrowIfUnavailable(response, query);
                var results = response?.Results;
                if (results == null || results.Count == 0)
                {
                    continue;
                }

                var byAuthor = results
                    .Where(result => AudibleAuthorCatalogMatcher.MatchesTarget(result, author, authorAsin: null))
                    .ToList();

                _logger.LogInformation(
                    "Audible keyword search '{Query}' returned {Total} result(s), {Kept} by '{Author}'",
                    query,
                    results.Count,
                    byAuthor.Count,
                    author);

                if (byAuthor.Count > 0)
                {
                    return byAuthor;
                }
            }

            return new List<AudibleSearchResult>();
        }

        /// <summary>
        /// Last resort: the author's whole catalogue. Fetched once per author for the lifetime of
        /// this scoped workflow, in a single call rather than paged ten at a time.
        /// </summary>
        private async Task<List<AudibleSearchResult>> CollectAuthorCatalogueAsync(
            string author,
            int candidateLimit,
            string region,
            string? language)
        {
            var cacheKey = $"{author}|{region}|{language}";
            if (_authorCatalogueCache.TryGetValue(cacheKey, out var cached))
            {
                _logger.LogDebug("Reusing cached Audible catalogue for '{Author}' ({Count} title(s))", author, cached.Count);
                return cached;
            }

            _logger.LogInformation("Keyword search found nothing for '{Author}'; walking the author catalogue", author);

            List<AudibleSearchResult> catalogue;
            var authorAsin = (await _audibleService.LookupAuthorAsync(author, region))?.Asin;
            if (!string.IsNullOrWhiteSpace(authorAsin))
            {
                var response = await _audibleService.GetAllBooksByAuthorAsync(author, authorAsin, AuthorCatalogueLimit, region, language);
                catalogue = response?.Results?.ToList() ?? new List<AudibleSearchResult>();
            }
            else
            {
                catalogue = await _authorPageCollector.CollectAsync(author, candidateLimit, region, language, "AUTHOR_TITLE");
            }

            _authorCatalogueCache[cacheKey] = catalogue;
            return catalogue;
        }

        private static MatchScoreCandidate ToScoreCandidate(AudibleSearchResult result)
        {
            var series = result.Series?.FirstOrDefault();
            return new MatchScoreCandidate(
                Title: result.Title,
                Subtitle: result.Subtitle,
                Authors: (result.Authors ?? new List<AudibleAuthor>())
                    .Select(author => author?.Name ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList(),
                SeriesName: series?.Name,
                SeriesPosition: series?.Position,
                RuntimeMinutes: result.RuntimeLengthMin ?? result.LengthMinutes ?? result.RuntimeMinutes,
                Language: result.Language,
                FormatType: result.BookFormat);
        }

        private async Task<IEnumerable<AudibleSearchResult>> FilterByIsbnAsync(
            IReadOnlyCollection<AudibleSearchResult> aggregated,
            IEnumerable<AudibleSearchResult> authorFiltered,
            string isbn,
            int candidateLimit,
            string region,
            string? language,
            IDictionary<string, AudibleBookResponse> detailedMetaByAsin)
        {
            var isbnScanLimit = Math.Min(200, Math.Max(50, candidateLimit));
            var scanCandidates = aggregated.Where(r => !string.IsNullOrWhiteSpace(r.Asin)).Take(isbnScanLimit).ToList();
            try { _logger.LogInformation("Scanning up to {Limit} author candidates for ISBN {Isbn}", scanCandidates.Count, isbn); }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            foreach (var candidate in scanCandidates.Where(c => !string.IsNullOrWhiteSpace(c.Asin)))
            {
                try
                {
                    var metadata = await _audibleService.GetBookMetadataAsync(candidate.Asin!, region, true, language);
                    if (metadata == null)
                    {
                        continue;
                    }

                    detailedMetaByAsin[candidate.Asin!] = metadata;
                    if (!string.IsNullOrWhiteSpace(metadata.Isbn) && string.Equals(metadata.Isbn.Trim(), isbn, StringComparison.OrdinalIgnoreCase))
                    {
                        return authorFiltered.Where(r => !string.IsNullOrWhiteSpace(r.Asin) && string.Equals(r.Asin, candidate.Asin, StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch (Exception exMeta) when (exMeta is not OperationCanceledException && exMeta is not OutOfMemoryException && exMeta is not StackOverflowException)
                {
                    _logger.LogDebug(exMeta, "Failed fetching audible metadata for ASIN {Asin} while scanning for ISBN", candidate.Asin);
                }
            }

            return authorFiltered;
        }

        private static List<AudibleSearchResult> DeduplicateByAsin(IEnumerable<AudibleSearchResult> books)
        {
            return books
                .Where(b => !string.IsNullOrWhiteSpace(b.Asin))
                .GroupBy(b => b.Asin, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static IEnumerable<AudibleSearchResult> ApplyStrictLanguageFilter(
            IEnumerable<AudibleSearchResult> books,
            string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return books;
            }

            return books.Where(b => !string.IsNullOrWhiteSpace(b.Language) && string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));
        }
    }
}
