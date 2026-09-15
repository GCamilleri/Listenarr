/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Search.Core
{
    /// <summary>
    /// The metadata provider could not answer: a rate limit, a timeout or a transport error.
    /// Thrown rather than returned so no caller can mistake it for "this book does not exist" and
    /// mark the row searched.
    /// </summary>
    public sealed class MetadataSearchUnavailableException : Exception
    {
        public MetadataSearchUnavailableException(string message, bool rateLimited, TimeSpan? retryAfter = null)
            : base(message)
        {
            RateLimited = rateLimited;
            RetryAfter = retryAfter;
        }

        public bool RateLimited { get; }

        public TimeSpan? RetryAfter { get; }
    }
}
