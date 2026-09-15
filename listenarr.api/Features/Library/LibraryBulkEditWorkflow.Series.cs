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

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryBulkEditWorkflow
    {
        internal const string SeriesUpdateKey = "series";

        private static readonly JsonSerializerOptions SeriesUpdateSerializerOptions =
            new(JsonSerializerDefaults.Web);

        /// <summary>
        /// The shape of the <c>series</c> key in a bulk update. Values arrive as
        /// <see cref="JsonElement"/> on the wire, so they are deserialised into this record rather
        /// than inspected property by property.
        /// </summary>
        internal sealed record BulkSeriesUpdate
        {
            public string? Mode { get; init; }
            public string? SeriesName { get; init; }
            public string? SeriesAsin { get; init; }
            public string? MatchName { get; init; }
            public string? Numbering { get; init; }
            public string? SeriesNumber { get; init; }
            public bool ReplaceOthers { get; init; }
        }

        /// <summary>
        /// The result of one series update. <c>Changed</c> is true when the audiobook's memberships
        /// actually differ afterwards; a non-null <c>Error</c> means the update was rejected and
        /// nothing was written.
        /// </summary>
        internal sealed record SeriesUpdateOutcome(
            bool Changed,
            string? Error,
            string? HistoryMessage);

        /// <summary>
        /// Applies a <c>series</c> bulk update to <paramref name="audiobook"/>. Metadata only: this
        /// never touches the filesystem, so the caller offers the Organize preview afterwards.
        /// </summary>
        internal static SeriesUpdateOutcome ApplySeriesUpdate(Audiobook audiobook, object? value)
        {
            BulkSeriesUpdate? update;
            try
            {
                update = ParseSeriesUpdate(value);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return new SeriesUpdateOutcome(false, "Invalid series update value", null);
            }

            if (update == null)
            {
                return new SeriesUpdateOutcome(false, "Invalid series update value", null);
            }

            var mode = Trimmed(update.Mode);
            var seriesName = Trimmed(update.SeriesName);
            var seriesAsin = Trimmed(update.SeriesAsin);
            var matchName = Trimmed(update.MatchName) ?? seriesName;
            var numbering = Trimmed(update.Numbering) ?? "keep";
            if (!IsKnownNumbering(numbering))
            {
                return new SeriesUpdateOutcome(
                    false,
                    $"Unknown series numbering mode '{numbering}'",
                    null);
            }

            var before = Snapshot(audiobook);
            var working = before.Select(Clone).ToList();
            var previousPrimary = Describe(
                AudiobookSeriesMembershipHelper.GetPrimaryMembership(working));
            string historyMessage;

            switch (mode?.ToLowerInvariant())
            {
                case "setprimary":
                    {
                        if (seriesName == null)
                        {
                            return new SeriesUpdateOutcome(false, "A series name is required", null);
                        }

                        var target = FindByName(working, seriesName);
                        if (target == null)
                        {
                            target = new AudiobookSeriesMembership
                            {
                                SeriesName = seriesName,
                                SeriesAsin = seriesAsin
                            };
                            working.Add(target);
                        }
                        else if (seriesAsin != null)
                        {
                            target.SeriesAsin = seriesAsin;
                        }

                        ApplyNumbering(target, numbering, update.SeriesNumber);
                        if (update.ReplaceOthers)
                        {
                            working.RemoveAll(membership => !ReferenceEquals(membership, target));
                        }

                        foreach (var membership in working)
                        {
                            membership.IsPrimary = ReferenceEquals(membership, target);
                        }

                        historyMessage =
                            $"Primary series set to {Describe(target)} (was {previousPrimary})";
                        break;
                    }

                case "addmembership":
                    {
                        if (seriesName == null)
                        {
                            return new SeriesUpdateOutcome(false, "A series name is required", null);
                        }

                        var target = FindByName(working, seriesName);
                        if (target == null)
                        {
                            target = new AudiobookSeriesMembership
                            {
                                SeriesName = seriesName,
                                SeriesAsin = seriesAsin,
                                IsPrimary = working.Count == 0
                            };
                            working.Add(target);
                        }
                        else if (seriesAsin != null)
                        {
                            target.SeriesAsin = seriesAsin;
                        }

                        ApplyNumbering(target, numbering, update.SeriesNumber);
                        historyMessage = $"Added to series {Describe(target)}";
                        break;
                    }

                case "removemembership":
                    {
                        if (matchName == null)
                        {
                            return new SeriesUpdateOutcome(
                                false,
                                "A series name to remove is required",
                                null);
                        }

                        var removed = working.RemoveAll(membership => NameMatches(membership, matchName));
                        if (removed == 0)
                        {
                            return new SeriesUpdateOutcome(
                                false,
                                $"This audiobook is not in the series '{matchName}'",
                                null);
                        }

                        historyMessage = $"Removed from series {matchName}";
                        break;
                    }

                case "renamemembership":
                    {
                        if (seriesName == null)
                        {
                            return new SeriesUpdateOutcome(false, "A series name is required", null);
                        }

                        if (matchName == null)
                        {
                            return new SeriesUpdateOutcome(
                                false,
                                "A series name to rename is required",
                                null);
                        }

                        var target = FindByName(working, matchName);
                        if (target == null)
                        {
                            return new SeriesUpdateOutcome(
                                false,
                                $"This audiobook is not in the series '{matchName}'",
                                null);
                        }

                        target.SeriesName = seriesName;
                        if (seriesAsin != null)
                        {
                            target.SeriesAsin = seriesAsin;
                        }

                        ApplyNumbering(target, numbering, update.SeriesNumber);
                        MergeDuplicateSeriesName(working, target);
                        historyMessage = $"Series {matchName} renamed to {Describe(target)}";
                        break;
                    }

                default:
                    return new SeriesUpdateOutcome(
                        false,
                        $"Unknown series update mode '{update.Mode}'",
                        null);
            }

            var normalized = AudiobookSeriesMembershipHelper.Normalize(working);
            audiobook.SeriesMemberships ??= [];
            audiobook.SeriesMemberships.Clear();
            foreach (var membership in normalized)
            {
                audiobook.SeriesMemberships.Add(membership);
            }

            AudiobookSeriesMembershipHelper.ApplyPrimarySeriesFields(audiobook);
            var changed = !SameMemberships(before, normalized);
            return new SeriesUpdateOutcome(changed, null, changed ? historyMessage : null);
        }

        /// <summary>
        /// Renaming a membership onto a name another membership already carries would leave the book
        /// in the same series twice, because <c>Normalize</c> only dedupes on name plus number plus
        /// ASIN. Fold the pair into one membership: the renamed one keeps its position (that is the
        /// number the user is preserving), the other only fills in what the renamed one lacks.
        /// </summary>
        private static void MergeDuplicateSeriesName(
            List<AudiobookSeriesMembership> memberships,
            AudiobookSeriesMembership target)
        {
            var duplicates = memberships
                .Where(membership => !ReferenceEquals(membership, target)
                    && NameMatches(membership, target.SeriesName))
                .ToList();
            if (duplicates.Count == 0)
            {
                return;
            }

            foreach (var duplicate in duplicates)
            {
                target.SeriesNumber ??= Trimmed(duplicate.SeriesNumber);
                target.SeriesAsin ??= Trimmed(duplicate.SeriesAsin);
                target.IsPrimary = target.IsPrimary || duplicate.IsPrimary;
                memberships.Remove(duplicate);
            }
        }

        private static BulkSeriesUpdate? ParseSeriesUpdate(object? value)
        {
            return value switch
            {
                null => null,
                BulkSeriesUpdate typed => typed,
                JsonElement { ValueKind: JsonValueKind.Object } element =>
                    element.Deserialize<BulkSeriesUpdate>(SeriesUpdateSerializerOptions),
                JsonElement => null,
                _ => JsonSerializer.SerializeToElement(value, SeriesUpdateSerializerOptions)
                    .Deserialize<BulkSeriesUpdate>(SeriesUpdateSerializerOptions)
            };
        }

        private static List<AudiobookSeriesMembership> Snapshot(Audiobook audiobook)
        {
            var memberships = (audiobook.SeriesMemberships ?? [])
                .OrderBy(membership => membership.SortOrder)
                .Where(membership => !string.IsNullOrWhiteSpace(membership.SeriesName))
                .Select(Clone)
                .ToList();
            if (memberships.Count > 0 || string.IsNullOrWhiteSpace(audiobook.Series))
            {
                return memberships;
            }

            // A book that predates the memberships table carries only the legacy columns; seed from
            // them so remove and rename can find the series the user can see.
            memberships.Add(new AudiobookSeriesMembership
            {
                SeriesName = audiobook.Series!.Trim(),
                SeriesNumber = Trimmed(audiobook.SeriesNumber),
                IsPrimary = true
            });
            return memberships;
        }

        private static AudiobookSeriesMembership Clone(AudiobookSeriesMembership membership) =>
            new()
            {
                SeriesName = Trimmed(membership.SeriesName),
                SeriesNumber = Trimmed(membership.SeriesNumber),
                SeriesAsin = Trimmed(membership.SeriesAsin),
                IsPrimary = membership.IsPrimary,
                SortOrder = membership.SortOrder
            };

        private static AudiobookSeriesMembership? FindByName(
            List<AudiobookSeriesMembership> memberships,
            string? name) =>
            memberships.FirstOrDefault(membership => NameMatches(membership, name));

        private static bool NameMatches(AudiobookSeriesMembership membership, string? name) =>
            name != null
            && string.Equals(
                Trimmed(membership.SeriesName),
                name,
                StringComparison.OrdinalIgnoreCase);

        private static void ApplyNumbering(
            AudiobookSeriesMembership membership,
            string numbering,
            string? seriesNumber)
        {
            switch (numbering.ToLowerInvariant())
            {
                case "clear":
                    membership.SeriesNumber = null;
                    break;
                case "explicit":
                    membership.SeriesNumber = Trimmed(seriesNumber);
                    break;
                default:
                    break;
            }
        }

        private static bool IsKnownNumbering(string numbering) =>
            numbering.Equals("keep", StringComparison.OrdinalIgnoreCase)
            || numbering.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || numbering.Equals("explicit", StringComparison.OrdinalIgnoreCase);

        private static bool SameMemberships(
            List<AudiobookSeriesMembership> left,
            List<AudiobookSeriesMembership> right)
        {
            var normalizedLeft = AudiobookSeriesMembershipHelper.Normalize(left);
            if (normalizedLeft.Count != right.Count)
            {
                return false;
            }

            return !normalizedLeft
                .Where((membership, index) => !IdentityMatches(membership, right[index]))
                .Any();
        }

        private static bool IdentityMatches(
            AudiobookSeriesMembership left,
            AudiobookSeriesMembership right) =>
            string.Equals(left.SeriesName, right.SeriesName, StringComparison.Ordinal)
            && string.Equals(left.SeriesNumber, right.SeriesNumber, StringComparison.Ordinal)
            && string.Equals(left.SeriesAsin, right.SeriesAsin, StringComparison.Ordinal)
            && left.IsPrimary == right.IsPrimary;

        private static string Describe(AudiobookSeriesMembership? membership)
        {
            if (membership == null || string.IsNullOrWhiteSpace(membership.SeriesName))
            {
                return "no series";
            }

            return string.IsNullOrWhiteSpace(membership.SeriesNumber)
                ? membership.SeriesName!
                : $"{membership.SeriesName} #{membership.SeriesNumber}";
        }

        private static string? Trimmed(string? value)
        {
            var trimmed = value?.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }
}
