/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;

namespace Listenarr.Tests.Common
{
    /// <summary>
    /// Loads the Audible catalogue payloads captured under tests/Data/Audible and runs them
    /// through the production mapper, so the tests exercise the real response shape rather than a
    /// hand-written approximation of it.
    /// </summary>
    public static class AudibleCatalogFixture
    {
        public const string KeywordsMistbornTheFinalEmpire = "catalog-keywords-mistborn-the-final-empire.json";
        public const string KeywordsSandersonMistbornTheFinalEmpire = "catalog-keywords-brandon-sanderson-mistborn-the-final-empire.json";
        public const string AuthorTitleFields = "catalog-author-title-fields-brandon-sanderson-mistborn.json";
        public const string AuthorSandersonPage2 = "catalog-author-brandon-sanderson-page2.json";

        public static List<AudibleSearchResult> LoadResults(string fileName, string region = "us")
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ResolvePath(fileName)));
            if (!document.RootElement.TryGetProperty("products", out var products) ||
                products.ValueKind != JsonValueKind.Array)
            {
                return new List<AudibleSearchResult>();
            }

            return products
                .EnumerateArray()
                .Where(product => product.ValueKind == JsonValueKind.Object)
                .Select(product => AudibleProductMapper.MapProductToBookResponse(product, region))
                .Where(book => book != null)
                .Select(book => AudibleProductMapper.MapBookResponseToSearchResult(book!))
                .Where(result => result != null)
                .Cast<AudibleSearchResult>()
                .ToList();
        }

        public static AudibleSearchResponse LoadResponse(string fileName, string region = "us")
        {
            var results = LoadResults(fileName, region);
            return new AudibleSearchResponse { Results = results, TotalResults = results.Count };
        }

        private static string ResolvePath(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "Audible", fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Audible fixture '{fileName}' was not copied to the test output", path);
            }

            return path;
        }
    }
}
