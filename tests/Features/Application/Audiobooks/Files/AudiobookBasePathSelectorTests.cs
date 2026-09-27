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

[Trait("Name", "AudiobookBasePathSelectorTests")]
[Trait("Area", "AudiobookFiles")]
[Trait("Category", "AudiobookBasePath")]
public sealed class AudiobookBasePathSelectorTests : BaseTests
{
    private static readonly FileSystemPathSemantics Semantics = new(
        FileSystemPathSyntax.Unix,
        FileSystemCaseSensitivity.Sensitive);

    [Fact]
    public void Select_NoExistingBasePath_AdoptsThePlannedOne()
    {
        var selection = AudiobookBasePathSelector.Select(
            existingBasePath: null,
            plannedBasePath: "/library/Author/Book",
            existingFileCount: 0,
            Semantics);

        Assert.Equal("/library/Author/Book", selection.SelectedBasePath);
        Assert.Equal(AudiobookBasePathSelectionOutcome.Adopted, selection.Outcome);
        Assert.False(selection.PreservedExisting);
    }

    [Fact]
    public void Select_PlannedInsideExisting_NarrowsToThePlannedOne()
    {
        var selection = AudiobookBasePathSelector.Select(
            "/library/Author",
            "/library/Author/Book",
            existingFileCount: 3,
            Semantics);

        Assert.Equal("/library/Author/Book", selection.SelectedBasePath);
        Assert.Equal(AudiobookBasePathSelectionOutcome.Adopted, selection.Outcome);
    }

    [Fact]
    public void Select_PlannedIsAncestorOfExisting_RefusesToWiden()
    {
        var selection = AudiobookBasePathSelector.Select(
            "/library/Author/Book",
            "/library/Author",
            existingFileCount: 3,
            Semantics);

        Assert.Equal("/library/Author/Book", selection.SelectedBasePath);
        Assert.Equal(
            AudiobookBasePathSelectionOutcome.WideningRejected,
            selection.Outcome);
        Assert.True(selection.PreservedExisting);
    }

    [Fact]
    public void Select_UnrelatedPlannedBaseWithTrackedFiles_PreservesTheExistingOne()
    {
        var selection = AudiobookBasePathSelector.Select(
            "/library/AuthorA/Book A",
            "/library/AuthorB/Book B",
            existingFileCount: 1,
            Semantics);

        Assert.Equal("/library/AuthorA/Book A", selection.SelectedBasePath);
        Assert.Equal(
            AudiobookBasePathSelectionOutcome.ConflictPreserved,
            selection.Outcome);
        Assert.True(selection.PreservedExisting);
    }

    [Fact]
    public void Select_UnrelatedPlannedBaseWithoutTrackedFiles_AdoptsThePlannedOne()
    {
        var selection = AudiobookBasePathSelector.Select(
            "/library/AuthorA/Book A",
            "/library/AuthorB/Book B",
            existingFileCount: 0,
            Semantics);

        Assert.Equal("/library/AuthorB/Book B", selection.SelectedBasePath);
        Assert.Equal(AudiobookBasePathSelectionOutcome.Adopted, selection.Outcome);
    }

    [Fact]
    public void Select_MoveOwned_KeepsTheDurableMoveTarget()
    {
        var selection = AudiobookBasePathSelector.Select(
            "/library/AuthorA/Book A",
            "/library/AuthorA/Book A/Disc 1",
            existingFileCount: 1,
            Semantics,
            moveOwned: true);

        Assert.Equal("/library/AuthorA/Book A", selection.SelectedBasePath);
        Assert.Equal(
            AudiobookBasePathSelectionOutcome.MoveOwnedPreserved,
            selection.Outcome);
    }

    [Fact]
    public void Select_NonAuthoritativeScope_CannotRedefineTheRoot()
    {
        var selection = AudiobookBasePathSelector.Select(
            "/library/AuthorA/Book A",
            "/library/AuthorA/Book A/Disc 1",
            existingFileCount: 1,
            Semantics,
            isAuthoritativeScope: false);

        Assert.Equal("/library/AuthorA/Book A", selection.SelectedBasePath);
        Assert.Equal(
            AudiobookBasePathSelectionOutcome.NonAuthoritativeScopePreserved,
            selection.Outcome);
    }

    [Fact]
    public void Select_SameBasePath_IsUnchanged()
    {
        var selection = AudiobookBasePathSelector.Select(
            "/library/Author/Book",
            "/library/Author/Book",
            existingFileCount: 2,
            Semantics);

        Assert.Equal("/library/Author/Book", selection.SelectedBasePath);
        Assert.Equal(AudiobookBasePathSelectionOutcome.Unchanged, selection.Outcome);
        Assert.False(selection.PreservedExisting);
    }
}
