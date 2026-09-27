using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public enum ScanPathAuthorizationFailure
{
    None,
    InvalidPath,
    ConfigurationUnavailable,
    NoConfiguredRoots,
    OutsideConfiguredRoots,
    IdentityUnavailable,
    NoAudiobookPath
}

public enum ScanPathPhysicalProofKind
{
    DurableGeneration,
    PinnedPathOnly
}

public readonly record struct ScanPathPhysicalIdentity
{
    public ScanPathPhysicalIdentity(
        string boundaryObjectIdentity,
        string scanRootObjectIdentity)
        : this(
            ScanPathPhysicalProofKind.DurableGeneration,
            boundaryObjectIdentity,
            scanRootObjectIdentity)
    {
    }

    private ScanPathPhysicalIdentity(
        ScanPathPhysicalProofKind proofKind,
        string? boundaryObjectIdentity,
        string? scanRootObjectIdentity)
    {
        ProofKind = proofKind;
        BoundaryObjectIdentity = boundaryObjectIdentity;
        ScanRootObjectIdentity = scanRootObjectIdentity;
    }

    public ScanPathPhysicalProofKind ProofKind { get; }
    public string? BoundaryObjectIdentity { get; init; }
    public string? ScanRootObjectIdentity { get; init; }
    public bool HasDurableGenerationProof =>
        ProofKind == ScanPathPhysicalProofKind.DurableGeneration
        && !string.IsNullOrWhiteSpace(BoundaryObjectIdentity)
        && !string.IsNullOrWhiteSpace(ScanRootObjectIdentity);

    public static ScanPathPhysicalIdentity PinnedPathOnly() =>
        new(ScanPathPhysicalProofKind.PinnedPathOnly, null, null);
}

public sealed record ScanPathAuthorizationResult(
    string? Path,
    PathIdentitySnapshot? Identity,
    ScanPathPhysicalIdentity? PhysicalIdentity,
    ScanPathAuthorizationFailure Failure,
    string? Error)
{
    public bool IsAuthorized =>
        !string.IsNullOrWhiteSpace(Path)
        && Identity.HasValue
        && PhysicalIdentity.HasValue
        && (PhysicalIdentity.Value.HasDurableGenerationProof
            || PhysicalIdentity.Value.ProofKind
                == ScanPathPhysicalProofKind.PinnedPathOnly)
        && Failure == ScanPathAuthorizationFailure.None
        && string.IsNullOrWhiteSpace(Error);

    public static ScanPathAuthorizationResult Authorized(
        string path,
        PathIdentitySnapshot identity,
        ScanPathPhysicalIdentity physicalIdentity) =>
        new(
            path,
            identity,
            physicalIdentity,
            ScanPathAuthorizationFailure.None,
            null);

    public static ScanPathAuthorizationResult Rejected(
        ScanPathAuthorizationFailure failure,
        string error) =>
        new(null, null, null, failure, error);
}

public interface IScanPathAuthorizationService
{
    Task<ScanPathAuthorizationResult> AuthorizeAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorizes a scan that acts on behalf of a single audiobook, using that audiobook's stored
    /// library folder. A blank folder is refused: there is no safe substitute, and falling back to a
    /// configured root would let one audiobook claim every unowned file beneath it.
    /// </summary>
    Task<ScanPathAuthorizationResult> ResolveAudiobookScopedAsync(
        string? audiobookBasePath,
        CancellationToken cancellationToken = default);
}
