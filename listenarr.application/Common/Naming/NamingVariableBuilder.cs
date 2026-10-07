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

namespace Listenarr.Application.Common.Naming;

/// <summary>
/// The one place an audiobook, an Audible metadata record or a set of file tags turns
/// into naming-pattern variables. Rename, manual import, library add, library preview
/// and download import all call this, so the same book and the same pattern produce the
/// same path in all five flows.
/// </summary>
/// <remarks>
/// The rules, decided once:
/// <list type="bullet">
/// <item><c>{Author}</c> is the first non-blank author. <c>{Authors}</c> is the joined list.</item>
/// <item><c>{Title}</c> is the title and nothing else. <c>{Subtitle}</c> is the subtitle.
/// A pattern that wants both asks for them, either as two tokens or as the explicit
/// <c>{TitleWithSubtitle}</c>. Nothing is appended implicitly.</item>
/// <item><c>{SeriesNumber}</c> is emitted everywhere, empty when unknown.</item>
/// <item>Narrator names never become <c>{Author}</c>.</item>
/// <item>Values are raw. <see cref="IFileNamingService.ApplyNamingPattern(string, Dictionary{string, object}, bool)"/>
/// sanitizes each rendered value, so sanitizing here would only make the two rules
/// disagree.</item>
/// <item>The dictionary is <see cref="StringComparer.OrdinalIgnoreCase"/>, because the
/// token regex is case-insensitive. A lowercase <c>{author}</c> resolves.</item>
/// </list>
/// An unknown or blank value is emitted as <see cref="string.Empty"/> so the pattern
/// engine can strip the token together with its adjacent separators.
/// </remarks>
public static class NamingVariableBuilder
{
    public const string UnknownAuthor = "Unknown Author";
    public const string UnknownTitle = "Unknown Title";

    public static Dictionary<string, object> FromAudiobook(
        Audiobook audiobook,
        int? diskNumber = null,
        int? chapterNumber = null)
    {
        ArgumentNullException.ThrowIfNull(audiobook);

        return Build(
            audiobook.Authors,
            audiobook.Title,
            audiobook.Subtitle,
            audiobook.Edition,
            audiobook.Series,
            audiobook.SeriesNumber,
            audiobook.Narrators,
            audiobook.Publisher,
            audiobook.Language,
            audiobook.Asin,
            audiobook.PublishYear,
            audiobook.Quality,
            diskNumber,
            chapterNumber);
    }

    public static Dictionary<string, object> FromAudibleMetadata(AudibleBookMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var authors = metadata.Authors is { Count: > 0 }
            ? metadata.Authors
            : ToList(metadata.Author);
        var narrators = metadata.Narrators is { Count: > 0 }
            ? metadata.Narrators
            : ToList(metadata.Narrator);

        return Build(
            authors,
            metadata.Title,
            metadata.Subtitle,
            metadata.Edition,
            metadata.Series,
            metadata.SeriesNumber,
            narrators,
            metadata.Publisher,
            metadata.Language,
            metadata.Asin,
            metadata.PublishYear,
            quality: null,
            diskNumber: null,
            chapterNumber: null);
    }

    public static Dictionary<string, object> FromAudioMetadata(AudioMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // Tag-derived metadata carries no author list, so an explicitly supplied one
        // (the download path attaches the audiobook's) wins over the artist heuristic.
        var authors = metadata.Authors is { Count: > 0 }
            ? metadata.Authors
            : ToList(ChooseAuthor(metadata));
        var seriesNumber = metadata.SeriesPosition?.ToString(CultureInfo.InvariantCulture)
            ?? metadata.TrackNumber?.ToString(CultureInfo.InvariantCulture);
        var quality = metadata.BitRate.HasValue
            ? metadata.BitRate.Value.ToString(CultureInfo.InvariantCulture) + "kbps"
            : metadata.Format;

        return Build(
            authors,
            metadata.Title,
            metadata.Subtitle,
            metadata.Edition,
            metadata.Series,
            seriesNumber,
            ToList(metadata.Narrator),
            metadata.Publisher,
            metadata.Language,
            metadata.Asin,
            metadata.Year?.ToString(CultureInfo.InvariantCulture),
            quality,
            metadata.DiscNumber,
            metadata.TrackNumber);
    }

    private static Dictionary<string, object> Build(
        IReadOnlyList<string>? authors,
        string? title,
        string? subtitle,
        string? edition,
        string? series,
        string? seriesNumber,
        IReadOnlyList<string>? narrators,
        string? publisher,
        string? language,
        string? asin,
        string? year,
        string? quality,
        int? diskNumber,
        int? chapterNumber)
    {
        var namedAuthors = (authors ?? [])
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Select(author => author.Trim())
            .ToList();
        var primaryAuthor = namedAuthors.Count > 0 ? namedAuthors[0] : UnknownAuthor;
        var joinedAuthors = namedAuthors.Count > 0
            ? string.Join(", ", namedAuthors)
            : UnknownAuthor;
        var joinedNarrators = string.Join(
            ", ",
            (narrators ?? [])
                .Where(narrator => !string.IsNullOrWhiteSpace(narrator))
                .Select(narrator => narrator.Trim()));
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? UnknownTitle : title.Trim();

        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["Author"] = primaryAuthor,
            ["Authors"] = joinedAuthors,
            ["Title"] = resolvedTitle,
            ["TitleWithSubtitle"] = CombineTitleAndSubtitle(resolvedTitle, subtitle),
            ["Subtitle"] = Text(subtitle),
            ["Edition"] = Text(edition),
            ["Series"] = Text(series),
            ["SeriesNumber"] = Text(seriesNumber),
            ["Narrator"] = joinedNarrators,
            ["Publisher"] = Text(publisher),
            ["Language"] = Text(language),
            ["Asin"] = Text(asin),
            ["Year"] = Text(year),
            ["Quality"] = Text(quality),
            ["DiskNumber"] = Number(diskNumber),
            ["ChapterNumber"] = Number(chapterNumber)
        };
    }

    private static string CombineTitleAndSubtitle(string title, string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle)
            || title.Contains(subtitle.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }

        return $"{title}: {subtitle.Trim()}";
    }

    private static object Number(int? value) =>
        value.HasValue && value.Value > 0 ? value.Value : string.Empty;

    private static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static List<string> ToList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

    /// <summary>
    /// Noisy tags sometimes put the title, the series or the narrator in the artist field.
    /// Prefer the album artist in those cases, and never let a narrator become the author.
    /// </summary>
    public static string ChooseAuthor(AudioMetadata? metadata)
    {
        if (metadata == null)
        {
            return string.Empty;
        }

        var primary = NonNarratorAuthorCandidate(metadata.Artist, metadata.Narrator);
        var alternate = NonNarratorAuthorCandidate(metadata.AlbumArtist, metadata.Narrator);

        if (string.IsNullOrWhiteSpace(primary))
        {
            return alternate;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Title)
            && (primary.Contains(metadata.Title, StringComparison.OrdinalIgnoreCase)
                || string.Equals(primary, metadata.Title, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(metadata.Series)
                    && string.Equals(primary, metadata.Series, StringComparison.OrdinalIgnoreCase))))
        {
            return string.IsNullOrWhiteSpace(alternate) ? primary : alternate;
        }

        return primary;
    }

    /// <summary>
    /// Rejects a candidate that only names narrators. Comparing the whole string missed
    /// the common case of a multi-narrator tag copied into the artist field, so the
    /// candidate is rejected when every name in it also appears in the narrator list.
    /// </summary>
    private static string NonNarratorAuthorCandidate(string? candidate, string? narrator)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var trimmedCandidate = candidate.Trim();
        if (string.IsNullOrWhiteSpace(narrator))
        {
            return trimmedCandidate;
        }

        var narratorNames = ToList(narrator);
        if (narratorNames.Count == 0)
        {
            return trimmedCandidate;
        }

        var candidateNames = ToList(trimmedCandidate);
        if (candidateNames.Count > 0
            && candidateNames.All(name => narratorNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        return trimmedCandidate;
    }
}
