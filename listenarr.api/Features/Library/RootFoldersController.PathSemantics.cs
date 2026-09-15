using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Library;

public partial class RootFoldersController
{
    private Task<FileSystemPathSemantics> ResolveFolderSemanticsAsync(
        RootFolder folder) =>
        ResolvePathSemanticsAsync(folder.Path, folder.CaseSensitivityMode);

    private async Task<FileSystemPathSemantics> ResolvePathSemanticsAsync(
        string path,
        FileSystemCaseSensitivityMode caseSensitivityMode)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var resolution = await _semanticsResolver.ResolveAsync(
            canonicalPath,
            caseSensitivityMode);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                resolution.Reason
                    ?? "Root folder filesystem identity could not be resolved.");
        }

        return resolution.Semantics;
    }

    private static string? TryCanonicalizePathForComparison(
        string? path,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return FileSystemPathIdentity.Canonicalize(
                path,
                semantics.Syntax);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
