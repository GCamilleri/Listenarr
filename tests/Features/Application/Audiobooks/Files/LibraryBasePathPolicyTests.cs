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

namespace Listenarr.Tests.Features.Application.Audiobooks.Files;

[Trait("Name", "LibraryBasePathPolicyTests")]
[Trait("Area", "AudiobookFiles")]
[Trait("Category", "AudiobookBasePath")]
public sealed class LibraryBasePathPolicyTests : BaseTests
{
    private static readonly FileSystemPathSemantics Semantics =
        FileSystemPathSemantics.CurrentHostDefault;

    private static string Root => OperatingSystem.IsWindows()
        ? "C:\\audiobooks"
        : "/audiobooks";

    private static IReadOnlyCollection<RootFolder> Roots =>
        [new RootFolder { Id = 1, Name = "Library", Path = Root, IsDefault = true }];

    private static string Under(params string[] segments) =>
        Path.Join([Root, .. segments]);

    [Fact]
    public void Classify_BlankBasePath_PlansFromTheDefaultRoot()
    {
        Init();

        var classification = LibraryBasePathPolicy.Classify(
            basePath: null,
            basePathIsUserPinned: false,
            configuredOutputPath: null,
            Roots,
            Semantics);

        Assert.True(classification.IsPatternManaged);
        Assert.Equal(Path.GetFullPath(Root), Path.GetFullPath(classification.PatternRoot));
    }

    [Fact]
    public void Classify_UnpinnedPathInsideARoot_IsPatternManagedFromThatRoot()
    {
        Init();

        var classification = LibraryBasePathPolicy.Classify(
            Under("Brandon Sanderson", "Mistborn", "The Final Empire"),
            basePathIsUserPinned: false,
            configuredOutputPath: null,
            Roots,
            Semantics);

        Assert.True(classification.IsPatternManaged);
        Assert.Equal(Path.GetFullPath(Root), Path.GetFullPath(classification.PatternRoot));
    }

    [Fact]
    public void Classify_PinnedPathInsideARoot_IsUserPinned()
    {
        Init();

        var pinned = Under("Brandon Sanderson", "Mistborn 1 - The Final Empire");

        var classification = LibraryBasePathPolicy.Classify(
            pinned,
            basePathIsUserPinned: true,
            configuredOutputPath: null,
            Roots,
            Semantics);

        Assert.True(classification.IsUserPinned);
        Assert.Equal(Path.GetFullPath(pinned), Path.GetFullPath(classification.PatternRoot));
    }

    [Fact]
    public void Classify_PinnedPathWithIncludePinned_IsPatternManagedAgain()
    {
        Init();

        var classification = LibraryBasePathPolicy.Classify(
            Under("Brandon Sanderson", "Mistborn 1 - The Final Empire"),
            basePathIsUserPinned: true,
            configuredOutputPath: null,
            Roots,
            Semantics,
            includePinned: true);

        Assert.True(classification.IsPatternManaged);
        Assert.Equal(Path.GetFullPath(Root), Path.GetFullPath(classification.PatternRoot));
    }

    [Fact]
    public void Classify_PathOutsideEveryRoot_IsUserPinnedEvenWithoutTheFlag()
    {
        Init();

        var outside = OperatingSystem.IsWindows()
            ? "C:\\elsewhere\\Book"
            : "/elsewhere/Book";

        var classification = LibraryBasePathPolicy.Classify(
            outside,
            basePathIsUserPinned: false,
            configuredOutputPath: null,
            Roots,
            Semantics);

        Assert.True(classification.IsUserPinned);
    }

    [Fact]
    public void Classify_NoRootsConfigured_FallsBackToTheConfiguredOutputPath()
    {
        Init();

        var classification = LibraryBasePathPolicy.Classify(
            Under("Author", "Book"),
            basePathIsUserPinned: false,
            configuredOutputPath: Root,
            [],
            Semantics);

        Assert.True(classification.IsPatternManaged);
        Assert.Equal(Path.GetFullPath(Root), Path.GetFullPath(classification.PatternRoot));
    }

    [Fact]
    public void Classify_UnparseableBasePath_FailsClosedAsUserPinned()
    {
        Init();

        var classification = LibraryBasePathPolicy.Classify(
            "not-an-absolute-path",
            basePathIsUserPinned: false,
            configuredOutputPath: Root,
            Roots,
            Semantics);

        Assert.True(classification.IsUserPinned);
    }

    [Fact]
    public void Classify_NestedRoots_PlansFromTheDeepestContainingRoot()
    {
        Init();

        var nestedRoot = Under("nested");
        var classification = LibraryBasePathPolicy.Classify(
            Path.Join(nestedRoot, "Author", "Book"),
            basePathIsUserPinned: false,
            configuredOutputPath: null,
            [
                new RootFolder { Id = 1, Name = "Library", Path = Root, IsDefault = true },
                new RootFolder { Id = 2, Name = "Nested", Path = nestedRoot }
            ],
            Semantics);

        Assert.True(classification.IsPatternManaged);
        Assert.Equal(Path.GetFullPath(nestedRoot), Path.GetFullPath(classification.PatternRoot));
    }
}
