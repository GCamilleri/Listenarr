/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

/// <summary>
/// Why a planned audiobook BasePath was or was not adopted. The scan maps these onto
/// its diagnostics; the manual import path maps them onto a result warning code.
/// </summary>
public enum AudiobookBasePathSelectionOutcome
{
    /// <summary>The audiobook had no base path yet, or the planned base is a narrowing.</summary>
    Adopted,

    /// <summary>The planned base already identifies the existing base.</summary>
    Unchanged,

    /// <summary>A move-owned operation cannot replace its durable move target.</summary>
    MoveOwnedPreserved,

    /// <summary>A non-authoritative scope cannot redefine the complete audiobook root.</summary>
    NonAuthoritativeScopePreserved,

    /// <summary>The planned base is an ancestor of the existing base.</summary>
    WideningRejected,

    /// <summary>The planned base is unrelated to files the audiobook already tracks.</summary>
    ConflictPreserved
}

public sealed record AudiobookBasePathSelection(
    string? SelectedBasePath,
    AudiobookBasePathSelectionOutcome Outcome)
{
    public bool PreservedExisting => Outcome
        is AudiobookBasePathSelectionOutcome.MoveOwnedPreserved
        or AudiobookBasePathSelectionOutcome.NonAuthoritativeScopePreserved
        or AudiobookBasePathSelectionOutcome.WideningRejected
        or AudiobookBasePathSelectionOutcome.ConflictPreserved;
}

/// <summary>
/// The single rule deciding whether a newly planned BasePath may replace the one an
/// audiobook already carries. The scan has always applied it; manual import used to
/// overwrite BasePath unconditionally, so it lives here where both layers can reach it.
/// </summary>
public static class AudiobookBasePathSelector
{
    public static AudiobookBasePathSelection Select(
        string? existingBasePath,
        string plannedBasePath,
        int existingFileCount,
        FileSystemPathSemantics semantics,
        bool moveOwned = false,
        bool isAuthoritativeScope = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plannedBasePath);

        if (string.IsNullOrWhiteSpace(existingBasePath))
        {
            return new AudiobookBasePathSelection(
                plannedBasePath,
                AudiobookBasePathSelectionOutcome.Adopted);
        }

        if (FileSystemPathIdentity.AreEquivalent(
                existingBasePath,
                plannedBasePath,
                semantics))
        {
            return new AudiobookBasePathSelection(
                existingBasePath,
                AudiobookBasePathSelectionOutcome.Unchanged);
        }

        if (moveOwned)
        {
            return new AudiobookBasePathSelection(
                existingBasePath,
                AudiobookBasePathSelectionOutcome.MoveOwnedPreserved);
        }

        if (!isAuthoritativeScope)
        {
            return new AudiobookBasePathSelection(
                existingBasePath,
                AudiobookBasePathSelectionOutcome.NonAuthoritativeScopePreserved);
        }

        if (FileSystemPathIdentity.IsSameOrInside(
                plannedBasePath,
                existingBasePath,
                semantics))
        {
            return new AudiobookBasePathSelection(
                plannedBasePath,
                AudiobookBasePathSelectionOutcome.Adopted);
        }

        if (FileSystemPathIdentity.IsSameOrInside(
                existingBasePath,
                plannedBasePath,
                semantics))
        {
            return new AudiobookBasePathSelection(
                existingBasePath,
                AudiobookBasePathSelectionOutcome.WideningRejected);
        }

        return existingFileCount == 0
            ? new AudiobookBasePathSelection(
                plannedBasePath,
                AudiobookBasePathSelectionOutcome.Adopted)
            : new AudiobookBasePathSelection(
                existingBasePath,
                AudiobookBasePathSelectionOutcome.ConflictPreserved);
    }
}
