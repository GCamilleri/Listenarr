/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Common.Naming;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Common.Naming;

[Trait("Name", "NamingVariableBuilderTests")]
[Trait("Area", "Naming")]
[Trait("Category", "NamingVariables")]
public sealed class NamingVariableBuilderTests : BaseTests
{
    private static Audiobook CoAuthoredBook() => new()
    {
        Title = "The Final Empire",
        Subtitle = "Mistborn Book 1",
        Authors = ["Brandon Sanderson", "Robert Jordan"],
        Narrators = ["Michael Kramer", "Kate Reading"],
        Series = "Mistborn",
        SeriesNumber = "1",
        PublishYear = "2006",
        Asin = "B002UZZ8EG",
        Publisher = "Macmillan Audio",
        Language = "English",
        Edition = "Unabridged",
        Quality = "64kbps"
    };

    [Fact]
    public void FromAudiobook_AuthorIsTheFirstAuthorAndAuthorsIsTheJoinedList()
    {
        Init();

        var variables = NamingVariableBuilder.FromAudiobook(CoAuthoredBook());

        Assert.Equal("Brandon Sanderson", variables["Author"]);
        Assert.Equal("Brandon Sanderson, Robert Jordan", variables["Authors"]);
    }

    [Fact]
    public void FromAudiobook_TitleNeverIncludesTheSubtitleUnlessThePatternAsksForIt()
    {
        Init();

        var variables = NamingVariableBuilder.FromAudiobook(CoAuthoredBook());

        Assert.Equal("The Final Empire", variables["Title"]);
        Assert.Equal("Mistborn Book 1", variables["Subtitle"]);
        Assert.Equal("The Final Empire: Mistborn Book 1", variables["TitleWithSubtitle"]);
    }

    [Fact]
    public void FromAudiobook_TitleWithSubtitle_DoesNotRepeatASubtitleAlreadyInTheTitle()
    {
        Init();

        var audiobook = CoAuthoredBook();
        audiobook.Title = "The Final Empire: Mistborn Book 1";

        var variables = NamingVariableBuilder.FromAudiobook(audiobook);

        Assert.Equal("The Final Empire: Mistborn Book 1", variables["TitleWithSubtitle"]);
    }

    [Fact]
    public void FromAudiobook_SeriesNumberResolves()
    {
        Init();

        var variables = NamingVariableBuilder.FromAudiobook(CoAuthoredBook());

        Assert.Equal("1", variables["SeriesNumber"]);
    }

    [Fact]
    public void FromAudiobook_UnknownValuesAreEmptySoThePatternCanStripTheirSeparators()
    {
        Init();

        var variables = NamingVariableBuilder.FromAudiobook(new Audiobook { Title = "Loose Book" });

        Assert.Equal(string.Empty, variables["SeriesNumber"]);
        Assert.Equal(string.Empty, variables["Series"]);
        Assert.Equal(string.Empty, variables["Year"]);
        Assert.Equal(string.Empty, variables["Quality"]);
        Assert.Equal(string.Empty, variables["DiskNumber"]);
        Assert.Equal(string.Empty, variables["ChapterNumber"]);
        Assert.Equal(NamingVariableBuilder.UnknownAuthor, variables["Author"]);
    }

    [Fact]
    public void FromAudiobook_LowercaseTokensResolveThroughTheSameDictionary()
    {
        Init();

        var variables = NamingVariableBuilder.FromAudiobook(CoAuthoredBook());

        Assert.Equal("Brandon Sanderson", variables["author"]);
        Assert.Equal("The Final Empire", variables["title"]);
        Assert.Equal("1", variables["seriesnumber"]);
        Assert.Equal("Mistborn", variables["SERIES"]);
    }

    [Fact]
    public void FromAudibleMetadata_MatchesTheAudiobookBuiltFromTheSameMetadata()
    {
        Init();

        var metadata = new AudibleBookMetadata
        {
            Title = "The Final Empire",
            Subtitle = "Mistborn Book 1",
            Authors = ["Brandon Sanderson", "Robert Jordan"],
            Narrators = ["Michael Kramer", "Kate Reading"],
            Series = "Mistborn",
            SeriesNumber = "1",
            PublishYear = "2006",
            Asin = "B002UZZ8EG",
            Publisher = "Macmillan Audio",
            Language = "English",
            Edition = "Unabridged"
        };

        var fromMetadata = NamingVariableBuilder.FromAudibleMetadata(metadata);
        var fromAudiobook = NamingVariableBuilder.FromAudiobook(metadata.ToAudiobook());

        foreach (var token in new[]
        {
            "Author", "Authors", "Title", "TitleWithSubtitle", "Subtitle",
            "Series", "SeriesNumber", "Narrator", "Publisher", "Language", "Asin", "Year"
        })
        {
            Assert.Equal(fromMetadata[token], fromAudiobook[token]);
        }
    }

    [Fact]
    public void FromAudioMetadata_NarratorNamesNeverBecomeTheAuthor()
    {
        Init();

        var metadata = new AudioMetadata
        {
            Title = "The Final Empire",
            Artist = "Michael Kramer, Kate Reading",
            AlbumArtist = "Brandon Sanderson",
            Narrator = "Michael Kramer, Kate Reading"
        };

        var variables = NamingVariableBuilder.FromAudioMetadata(metadata);

        Assert.Equal("Brandon Sanderson", variables["Author"]);
    }

    [Fact]
    public void FromAudioMetadata_NarratorOnlyArtistWithNoAlternative_FallsBackToUnknownAuthor()
    {
        Init();

        var metadata = new AudioMetadata
        {
            Title = "The Final Empire",
            Artist = "Kate Reading, Michael Kramer",
            Narrator = "Michael Kramer, Kate Reading"
        };

        var variables = NamingVariableBuilder.FromAudioMetadata(metadata);

        Assert.Equal(NamingVariableBuilder.UnknownAuthor, variables["Author"]);
    }

    [Fact]
    public void FromAudioMetadata_ExplicitAuthorListWins_SoAuthorIsTheFirstAuthor()
    {
        Init();

        var metadata = new AudioMetadata
        {
            Title = "The Final Empire",
            Artist = "Brandon Sanderson, Robert Jordan",
            AlbumArtist = "Brandon Sanderson, Robert Jordan",
            Authors = ["Brandon Sanderson", "Robert Jordan"]
        };

        var variables = NamingVariableBuilder.FromAudioMetadata(metadata);

        Assert.Equal("Brandon Sanderson", variables["Author"]);
        Assert.Equal("Brandon Sanderson, Robert Jordan", variables["Authors"]);
    }

    [Fact]
    public void FromAudioMetadata_SeriesNumberFallsBackToTheTrackNumber()
    {
        Init();

        var withPosition = NamingVariableBuilder.FromAudioMetadata(new AudioMetadata
        {
            Title = "The Final Empire",
            SeriesPosition = 1m
        });
        var withTrack = NamingVariableBuilder.FromAudioMetadata(new AudioMetadata
        {
            Title = "The Final Empire",
            TrackNumber = 4
        });

        Assert.Equal("1", withPosition["SeriesNumber"]);
        Assert.Equal("4", withTrack["SeriesNumber"]);
    }

    [Fact]
    public void FromAudiobook_DiskAndChapterNumbersAreNumbersSoZeroPaddedFormatsWork()
    {
        Init();

        var variables = NamingVariableBuilder.FromAudiobook(
            CoAuthoredBook(),
            diskNumber: 2,
            chapterNumber: 7);

        Assert.Equal(2, variables["DiskNumber"]);
        Assert.Equal(7, variables["ChapterNumber"]);
    }
}
