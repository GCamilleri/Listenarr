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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal static partial class ScanFileDiscovery
{
    public static ScanDiscoveryResult Discover(
        IFileSystem fileSystem,
        string scanRoot,
        Audiobook audiobook,
        Guid jobId,
        ILogger logger,
        FileSystemPathSemantics semantics,
        IReadOnlyCollection<string>? ownedPaths = null,
        IReadOnlyDictionary<string, int>? ownershipByCanonicalPath = null,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? pinnedScanRoot = null,
        bool requireDurableGenerationProof = true)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanRoot);
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentNullException.ThrowIfNull(logger);

        var enumeration = CollectCandidates(
            fileSystem,
            scanRoot,
            jobId,
            logger,
            semantics,
            pinnedScanRoot,
            requireDurableGenerationProof);
        var issues = enumeration.Issues.ToList();
        var canonicalRoot = FileSystemPathIdentity.Canonicalize(
            scanRoot,
            semantics.Syntax);
        var owned = new HashSet<string>(
            (ownedPaths ?? [])
            .Select(path => FileSystemPathIdentity.Canonicalize(path, semantics.Syntax)),
            semantics.Comparer);
        var titleTokens = BuildExpectedTitleTokens(audiobook);
        var authorTokens = BuildExpectedAuthorTokens(audiobook);
        var identifierTokens = BuildExpectedIdentifierTokens(audiobook);
        var preliminary = new List<AttributionEvidence>();

        // A boundary found only because some ancestor directory is named like the audiobook is weak
        // evidence: a series folder, an author folder or a box-set folder can carry the same name.
        // Boundaries that come from the audiobook's own pinned folder, or from the folder an already
        // owned file lives in, are not guesses and keep the original reach.
        var pinnedBasePath = TryCanonicalize(audiobook.BasePath, semantics);
        var ownedDirectories = new HashSet<string>(
            owned
                .Select(Path.GetDirectoryName)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => directory!),
            semantics.Comparer);

        bool BoundaryIsProven(string boundary) =>
            (pinnedBasePath != null
                && FileSystemPathIdentity.IsSameOrInside(boundary, pinnedBasePath, semantics))
            || ownedDirectories.Contains(boundary);

        // Anything inside the audiobook's own folder belongs to it whatever shape the boundary has.
        bool SitsInsidePinnedBasePath(string path) =>
            pinnedBasePath != null
            && FileSystemPathIdentity.IsSameOrInside(path, pinnedBasePath, semantics);

        foreach (var candidate in enumeration.Candidates)
        {
            var canonicalCandidate = FileSystemPathIdentity.Canonicalize(
                candidate,
                semantics.Syntax);
            if (ownershipByCanonicalPath != null
                && ownershipByCanonicalPath.TryGetValue(canonicalCandidate, out var ownerId)
                && ownerId != audiobook.Id)
            {
                issues.Add(new ScanDiscoveryIssue(
                    ScanDiscoveryIssueKind.AttributionConflict,
                    candidate,
                    $"The file is already owned by audiobook {ownerId}."));
                continue;
            }

            if (owned.Contains(canonicalCandidate))
            {
                preliminary.Add(new AttributionEvidence(
                    candidate,
                    TryFindTitleBoundary(
                        candidate,
                        canonicalRoot,
                        titleTokens,
                        authorTokens,
                        semantics,
                        requireAuthorContext: false),
                    AttributionEvidenceKind.ExistingOwnership));
                continue;
            }

            var identifierBoundary = TryFindIdentifierBoundary(
                candidate,
                canonicalRoot,
                identifierTokens,
                semantics);
            if (identifierBoundary != null)
            {
                preliminary.Add(new AttributionEvidence(
                    candidate,
                    identifierBoundary,
                    AttributionEvidenceKind.StableIdentifier));
                continue;
            }

            var titleBoundary = TryFindTitleBoundary(
                candidate,
                canonicalRoot,
                titleTokens,
                authorTokens,
                semantics,
                requireAuthorContext: true);
            if (titleBoundary != null)
            {
                if (!BoundaryIsProven(titleBoundary)
                    && !SitsInsidePinnedBasePath(candidate)
                    && !FileSitsInBoundaryFolder(candidate, titleBoundary, semantics))
                {
                    issues.Add(new ScanDiscoveryIssue(
                        ScanDiscoveryIssueKind.OutsideStableIdentifierBoundary,
                        candidate,
                        "The file sits in a subfolder of a title-matching ancestor rather than in the book folder itself and was not attributed."));
                    continue;
                }

                preliminary.Add(new AttributionEvidence(
                    candidate,
                    titleBoundary,
                    AttributionEvidenceKind.BookBoundary));
                continue;
            }

            if (FileNameMatchesExpectedTitle(candidate, titleTokens)
                && HasAuthorContext(
                    Path.GetDirectoryName(candidate),
                    canonicalRoot,
                    authorTokens,
                    semantics))
            {
                preliminary.Add(new AttributionEvidence(
                    candidate,
                    Boundary: null,
                    AttributionEvidenceKind.ExactFileName));
            }
        }

        var strongBoundaries = preliminary
            .Where(evidence => evidence.Kind is
                AttributionEvidenceKind.StableIdentifier
                or AttributionEvidenceKind.BookBoundary)
            .Where(evidence => !string.IsNullOrWhiteSpace(evidence.Boundary))
            .Select(evidence => evidence.Boundary!)
            .Distinct(semantics.Comparer)
            .ToList();
        var identifierBoundaries = preliminary
            .Where(evidence => evidence.Kind == AttributionEvidenceKind.StableIdentifier)
            .Where(evidence => !string.IsNullOrWhiteSpace(evidence.Boundary))
            .Select(evidence => evidence.Boundary!)
            .Distinct(semantics.Comparer)
            .ToList();

        if (strongBoundaries.Count > 1 && identifierBoundaries.Count != 1)
        {
            issues.Add(new ScanDiscoveryIssue(
                ScanDiscoveryIssueKind.AttributionConflict,
                scanRoot,
                "Multiple book boundaries matched the same audiobook metadata."));
            preliminary.RemoveAll(evidence =>
                evidence.Kind != AttributionEvidenceKind.ExistingOwnership);
            strongBoundaries.Clear();
        }
        else if (identifierBoundaries.Count == 1)
        {
            var selectedBoundary = identifierBoundaries[0];
            strongBoundaries = [selectedBoundary];
        }

        var selectedStableIdentifierBoundary = identifierBoundaries.Count == 1
            ? identifierBoundaries[0]
            : null;
        if (selectedStableIdentifierBoundary != null)
        {
            var rejectedEvidence = preliminary
                .Where(evidence => evidence.Kind != AttributionEvidenceKind.ExistingOwnership)
                .Where(evidence => !CanClaimNewPath(
                    evidence.Path,
                    selectedStableIdentifierBoundary,
                    owned,
                    semantics))
                .ToList();
            foreach (var rejected in rejectedEvidence)
            {
                issues.Add(new ScanDiscoveryIssue(
                    ScanDiscoveryIssueKind.OutsideStableIdentifierBoundary,
                    rejected.Path,
                    "The candidate was outside the selected stable-identifier directory and was not attributed."));
            }

            preliminary.RemoveAll(evidence =>
                !CanClaimNewPath(
                    evidence.Path,
                    selectedStableIdentifierBoundary,
                    owned,
                    semantics));
        }

        var attributed = new HashSet<string>(semantics.Comparer);
        var boundaries = new Dictionary<string, string>(semantics.Comparer);
        foreach (var evidence in preliminary)
        {
            attributed.Add(evidence.Path);
            if (!string.IsNullOrWhiteSpace(evidence.Boundary))
            {
                if (selectedStableIdentifierBoundary == null
                    || FileSystemPathIdentity.IsSameOrInside(
                        evidence.Path,
                        selectedStableIdentifierBoundary,
                        semantics))
                {
                    boundaries[evidence.Path] = evidence.Boundary;
                }
            }
        }

        var identifierBoundarySet = new HashSet<string>(
            identifierBoundaries,
            semantics.Comparer);
        foreach (var boundary in strongBoundaries)
        {
            // Identifier boundaries carry the audiobook's own ASIN or OpenLibrary id, so everything
            // beneath them belongs to it. A title-derived boundary only speaks for the files in the
            // folder itself, otherwise one audiobook named after its series claims every book in it.
            var boundaryReachesSubfolders =
                identifierBoundarySet.Contains(boundary)
                || BoundaryIsProven(boundary);
            foreach (var candidate in enumeration.Candidates)
            {
                var canonicalCandidate = FileSystemPathIdentity.Canonicalize(
                    candidate,
                    semantics.Syntax);
                if (ownershipByCanonicalPath != null
                    && ownershipByCanonicalPath.TryGetValue(canonicalCandidate, out var ownerId)
                    && ownerId != audiobook.Id)
                {
                    continue;
                }

                if (!boundaryReachesSubfolders
                    && !owned.Contains(canonicalCandidate)
                    && !SitsInsidePinnedBasePath(candidate)
                    && !FileSitsInBoundaryFolder(candidate, boundary, semantics))
                {
                    continue;
                }

                if (CanClaimNewPath(
                        candidate,
                        selectedStableIdentifierBoundary,
                        owned,
                        semantics)
                    && FileSystemPathIdentity.IsSameOrInside(
                        candidate,
                        boundary,
                        semantics))
                {
                    attributed.Add(candidate);
                    boundaries[candidate] = boundary;
                }
            }
        }

        return new ScanDiscoveryResult(
            enumeration.Candidates,
            attributed.OrderBy(path => path, semantics.Comparer).ToList(),
            boundaries,
            enumeration.EnumeratedDirectories,
            enumeration.DirectoryObjectIdentities,
            enumeration.FileObjectIdentities,
            selectedStableIdentifierBoundary,
            identifierBoundaries.Count > 1,
            issues);
    }

    /// <summary>
    /// True when the file lives in the boundary directory itself, or in a disc subfolder of it.
    /// A file any deeper belongs to a folder of its own, which is another book far more often than
    /// it is a stray part of this one.
    /// </summary>
    internal static bool FileSitsInBoundaryFolder(
        string candidate,
        string boundary,
        FileSystemPathSemantics semantics)
    {
        var directory = Path.GetDirectoryName(candidate);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        if (FileSystemPathIdentity.AreEquivalent(directory, boundary, semantics))
        {
            return true;
        }

        var parent = Path.GetDirectoryName(directory);
        return !string.IsNullOrWhiteSpace(parent)
            && FileSystemPathIdentity.AreEquivalent(parent, boundary, semantics)
            && DiscFolderRules.IsDiscDirectory(Path.GetFileName(directory) ?? string.Empty);
    }

    private static string? TryCanonicalize(
        string? path,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return FileSystemPathIdentity.Canonicalize(path, semantics.Syntax);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static bool CanClaimNewPath(
        string path,
        string? selectedStableIdentifierBoundary,
        IReadOnlySet<string> ownedCanonicalPaths,
        FileSystemPathSemantics semantics)
    {
        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            path,
            semantics.Syntax);
        return ownedCanonicalPaths.Contains(canonicalPath)
            || string.IsNullOrWhiteSpace(selectedStableIdentifierBoundary)
            || FileSystemPathIdentity.IsSameOrInside(
                canonicalPath,
                selectedStableIdentifierBoundary,
                semantics);
    }

    private sealed record AttributionEvidence(
        string Path,
        string? Boundary,
        AttributionEvidenceKind Kind);

    private enum AttributionEvidenceKind
    {
        ExistingOwnership,
        StableIdentifier,
        BookBoundary,
        ExactFileName
    }
}
