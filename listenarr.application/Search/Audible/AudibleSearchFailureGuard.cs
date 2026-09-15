/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Search.Audible
{
    internal static class AudibleSearchFailureGuard
    {
        /// <summary>
        /// A failed Audible request that produced nothing must not fall through the rest of the
        /// pipeline as an empty successful search, because the caller then marks the row searched
        /// and never retries it.
        /// </summary>
        public static void ThrowIfUnavailable(AudibleSearchResponse? response, string context)
        {
            if (response == null)
            {
                return;
            }

            if (!response.Failed && !response.RateLimited)
            {
                return;
            }

            if (response.Results?.Count > 0)
            {
                return;
            }

            throw new MetadataSearchUnavailableException(
                response.RateLimited
                    ? $"Audible rate limited the search for '{context}'"
                    : $"The Audible search for '{context}' could not be completed",
                response.RateLimited,
                response.RetryAfter);
        }
    }
}
