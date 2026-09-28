using Listenarr.Application.Common.Naming;

namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    /// <summary>
    /// The naming variables the download import plans one file with. It is the same
    /// shared builder rename, manual import, library add and library preview use; this
    /// dictionary used to be assembled inline at the call site with the default ordinal
    /// comparer, so a lowercase <c>{author}</c> resolved to nothing and the file landed
    /// flat in the root.
    /// </summary>
    internal static Dictionary<string, object> BuildDownloadNamingVariables(
        AudioMetadata namingMetadata,
        string fallbackTitle,
        int? diskNumber,
        int? chapterNumber)
    {
        if (string.IsNullOrWhiteSpace(namingMetadata.Title))
        {
            namingMetadata.Title = fallbackTitle;
        }

        namingMetadata.DiscNumber = diskNumber;
        namingMetadata.TrackNumber = chapterNumber;
        return NamingVariableBuilder.FromAudioMetadata(namingMetadata);
    }

    internal static AudioMetadata BuildNamingMetadata(
        Audiobook? audiobook,
        AudioMetadata? extractedMetadata,
        string fallbackTitle)
    {
        if (audiobook != null)
        {
            // Every other planner uses the first author for {Author}. Joining them here
            // put a co-authored download in "Sanderson, Jordan/" and the next organize run
            // moved it to "Sanderson/". The list travels alongside so {Authors} still works.
            var namedAuthors = (audiobook.Authors ?? [])
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Select(candidate => candidate.Trim())
                .ToList();
            if (namedAuthors.Count == 0)
            {
                var fallbackAuthor = FirstNonEmpty(
                    NamingVariableBuilder.ChooseAuthor(extractedMetadata),
                    NamingVariableBuilder.UnknownAuthor);
                namedAuthors.Add(fallbackAuthor);
            }

            var author = namedAuthors[0];

            return new AudioMetadata
            {
                Authors = namedAuthors,
                Title = FirstNonEmpty(
                    audiobook.Title,
                    extractedMetadata?.Title,
                    fallbackTitle,
                    "Unknown Title"),
                Subtitle = FirstNonEmpty(
                    audiobook.Subtitle,
                    extractedMetadata?.Subtitle),
                Edition = FirstNonEmpty(
                    audiobook.Edition,
                    extractedMetadata?.Edition),
                Artist = author,
                AlbumArtist = author,
                Album = FirstNonEmpty(
                    extractedMetadata?.Album,
                    audiobook.Title,
                    fallbackTitle),
                Narrator = audiobook.Narrators is { Count: > 0 }
                    ? string.Join(", ", audiobook.Narrators.Where(
                        narrator => !string.IsNullOrWhiteSpace(narrator)))
                    : extractedMetadata?.Narrator,
                Publisher = FirstNonEmpty(
                    audiobook.Publisher,
                    extractedMetadata?.Publisher),
                Language = FirstNonEmpty(
                    audiobook.Language,
                    extractedMetadata?.Language),
                Asin = FirstNonEmpty(
                    audiobook.Asin,
                    extractedMetadata?.Asin),
                Series = FirstNonEmpty(
                    audiobook.Series,
                    extractedMetadata?.Series),
                SeriesPosition = !string.IsNullOrWhiteSpace(audiobook.SeriesNumber)
                    && decimal.TryParse(audiobook.SeriesNumber, out var seriesPosition)
                        ? seriesPosition
                        : extractedMetadata?.SeriesPosition,
                Year = !string.IsNullOrWhiteSpace(audiobook.PublishYear)
                    && int.TryParse(audiobook.PublishYear, out var year)
                        ? year
                        : extractedMetadata?.Year,
                TrackNumber = extractedMetadata?.TrackNumber,
                DiscNumber = extractedMetadata?.DiscNumber,
                BitRate = extractedMetadata?.BitRate,
                Format = extractedMetadata?.Format
            };
        }

        if (extractedMetadata != null)
        {
            if (string.IsNullOrWhiteSpace(extractedMetadata.Title))
            {
                extractedMetadata.Title = fallbackTitle;
            }

            if (string.IsNullOrWhiteSpace(extractedMetadata.Artist))
            {
                extractedMetadata.Artist = FirstNonEmpty(
                    NamingVariableBuilder.ChooseAuthor(extractedMetadata),
                    NamingVariableBuilder.UnknownAuthor);
            }

            if (string.IsNullOrWhiteSpace(extractedMetadata.AlbumArtist))
            {
                extractedMetadata.AlbumArtist = extractedMetadata.Artist;
            }

            return extractedMetadata;
        }

        return new AudioMetadata
        {
            Title = fallbackTitle,
            Artist = NamingVariableBuilder.UnknownAuthor,
            AlbumArtist = NamingVariableBuilder.UnknownAuthor
        };
    }

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))
        ?? string.Empty;
}
