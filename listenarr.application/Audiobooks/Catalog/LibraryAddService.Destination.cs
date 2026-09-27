using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Catalog;

public partial class LibraryAddService
{
    private async Task<LibraryAddOperationResult?> ResolveAndValidateDestinationAsync(
        Audiobook audiobook,
        AudibleBookMetadata metadata,
        LibraryAddOperationRequest request,
        CancellationToken cancellationToken)
    {
        var configuredRootFolders = await _rootFolderService.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();

        // An explicit managed destination must not depend on the legacy settings row, so
        // settings are read only when a folder pattern is actually needed.
        ApplicationSettings? settings = null;
        async Task<ApplicationSettings> GetSettingsAsync()
        {
            settings ??= await _configurationService.GetApplicationSettingsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return settings;
        }

        IReadOnlyCollection<string> allowedDestinationRoots;
        if (configuredRootFolders.Count > 0)
        {
            allowedDestinationRoots = FileUtils.GetValidMutationRootsForCurrentOs(
                configuredRootFolders.Select(root => root.Path));
        }
        else
        {
            allowedDestinationRoots = FileUtils.GetValidMutationRootsForCurrentOs(
                [(await GetSettingsAsync()).OutputPath]);
        }

        var requestedBaseDirectory = request.DestinationPath;
        var destinationWasSupplied = !string.IsNullOrWhiteSpace(requestedBaseDirectory);
        var destinationIsConfiguredRoot = false;
        if (destinationWasSupplied)
        {
            if (FileUtils.HasLeadingWhitespaceBeforeRootedPath(requestedBaseDirectory!))
            {
                return ValidationFailure(
                    "destination_path_invalid",
                    "DestinationPath is invalid: leading whitespace before an absolute path is not allowed.",
                    requestedBaseDirectory);
            }

            if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                requestedBaseDirectory!,
                out var normalizedRequestedBaseDirectory,
                out var validationReason,
                rejectParentTraversal: true))
            {
                return ValidationFailure(
                    "destination_path_invalid",
                    $"DestinationPath is invalid: {validationReason}",
                    requestedBaseDirectory);
            }

            if (allowedDestinationRoots.Count == 0
                || !_fileSystem.TryValidateMutationTarget(
                    normalizedRequestedBaseDirectory,
                    allowedDestinationRoots,
                    out normalizedRequestedBaseDirectory,
                    out _))
            {
                return ValidationFailure(
                    "destination_path_outside_roots",
                    "DestinationPath must be inside a configured root folder or output path",
                    normalizedRequestedBaseDirectory);
            }

            // A configured root is a library, not a book folder. Storing it verbatim makes
            // the audiobook claim the whole root until a later import corrects it, and
            // blocks the next add into the same root as an already-assigned destination.
            var matchedRoot = await FindEquivalentConfiguredRootAsync(
                normalizedRequestedBaseDirectory,
                configuredRootFolders,
                cancellationToken);
            if (matchedRoot == null)
            {
                audiobook.BasePath = normalizedRequestedBaseDirectory;
            }
            else
            {
                destinationIsConfiguredRoot = true;
                var patternFailure = await ApplyFolderPatternUnderRootAsync(
                    audiobook,
                    metadata,
                    matchedRoot.Path,
                    allowedDestinationRoots,
                    GetSettingsAsync);
                if (patternFailure != null)
                {
                    return patternFailure;
                }
            }
        }
        else if (request.RootFolderId is int requestedRootFolderId)
        {
            var requestedRoot = configuredRootFolders
                .FirstOrDefault(root => root.Id == requestedRootFolderId);
            if (requestedRoot == null)
            {
                return ValidationFailure(
                    "root_folder_not_found",
                    "The requested root folder does not exist.");
            }

            var patternFailure = await ApplyFolderPatternUnderRootAsync(
                audiobook,
                metadata,
                requestedRoot.Path,
                allowedDestinationRoots,
                GetSettingsAsync);
            if (patternFailure != null)
            {
                return patternFailure;
            }
        }
        else
        {
            var rootFolder = await _rootFolderService.GetDefaultAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var baseDirectory = rootFolder != null
                ? rootFolder.Path
                : (await GetSettingsAsync()).OutputPath;
            var patternFailure = await ApplyFolderPatternUnderRootAsync(
                audiobook,
                metadata,
                baseDirectory,
                allowedDestinationRoots,
                GetSettingsAsync);
            if (patternFailure != null)
            {
                return patternFailure;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var existingDestinationOwner = await FindExistingDestinationOwnerAsync(
            audiobook.BasePath!,
            configuredRootFolders,
            cancellationToken);
        if (existingDestinationOwner != null)
        {
            if (RepresentsSameDestinationAudiobook(
                    existingDestinationOwner,
                    audiobook))
            {
                // Repeating the same add against its already-committed destination is
                // idempotent. No filesystem mutation will occur, so relocation/mutation
                // blockers do not need to authorize that existing library location.
                return AlreadyExists(existingDestinationOwner);
            }

            // An in-place add points at a folder that already holds this book's files, and
            // registration there is by file rather than by folder. A folder holding several
            // loose books is legitimate, so exclusive folder ownership is not required.
            if (!(destinationWasSupplied
                && !destinationIsConfiguredRoot
                && DestinationHoldsExistingFiles(audiobook.BasePath!)))
            {
                return ValidationFailure(
                    "destination_path_blocked",
                    "Destination is already assigned to another audiobook in the library.",
                    audiobook.BasePath);
            }

            _logger.LogInformation(
                "Allowing an in-place add to share library folder {BasePath} with audiobook {ExistingAudiobookId}",
                LogRedaction.SanitizeFilePath(audiobook.BasePath),
                existingDestinationOwner.Id);
        }

        var destinationBlockingReason = await _destinationMutationGuard.GetBlockingReasonAsync(
            audiobook.BasePath!,
            cancellationToken);
        return destinationBlockingReason == null
            ? null
            : ValidationFailure(
                "destination_path_blocked",
                destinationBlockingReason,
                audiobook.BasePath);
    }

    private async Task<LibraryAddOperationResult?> ApplyFolderPatternUnderRootAsync(
        Audiobook audiobook,
        AudibleBookMetadata metadata,
        string? rootPath,
        IReadOnlyCollection<string> allowedDestinationRoots,
        Func<Task<ApplicationSettings>> getSettingsAsync)
    {
        var resolvedSettings = await getSettingsAsync();
        var generatedBasePath = Path.Join(
            rootPath,
            _fileNamingService.ApplyNamingPattern(
                resolvedSettings.FolderNamingPattern,
                metadata));
        if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
            generatedBasePath,
            out var normalizedGeneratedBasePath,
            out var validationReason,
            rejectParentTraversal: true))
        {
            return ValidationFailure(
                "destination_path_invalid",
                $"Generated library destination is invalid: {validationReason}",
                generatedBasePath);
        }

        if (allowedDestinationRoots.Count == 0
            || !_fileSystem.TryValidateMutationTarget(
                normalizedGeneratedBasePath,
                allowedDestinationRoots,
                out normalizedGeneratedBasePath,
                out _))
        {
            return ValidationFailure(
                "destination_path_outside_roots",
                "Generated library destination must be inside a configured root folder or output path",
                normalizedGeneratedBasePath);
        }

        audiobook.BasePath = normalizedGeneratedBasePath;
        return null;
    }

    private async Task<RootFolder?> FindEquivalentConfiguredRootAsync(
        string destinationPath,
        IReadOnlyCollection<RootFolder> configuredRoots,
        CancellationToken cancellationToken)
    {
        if (configuredRoots.Count == 0)
        {
            return null;
        }

        var canonicalDestination = FileSystemPathIdentity
            .TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                destinationPath,
                out var normalizedDestination,
                out _)
            ? normalizedDestination
            : destinationPath;
        foreach (var root in configuredRoots)
        {
            var canonicalRoot = FileSystemPathIdentity
                .TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var normalizedRoot,
                    out _)
                ? normalizedRoot
                : root.Path;
            if (string.Equals(
                    canonicalRoot,
                    canonicalDestination,
                    StringComparison.Ordinal))
            {
                return root;
            }
        }

        var semantics = await ResolveLiveDestinationSemanticsAsync(
            destinationPath,
            configuredRoots,
            cancellationToken);
        if (!semantics.HasValue)
        {
            return null;
        }

        foreach (var root in configuredRoots)
        {
            if (string.IsNullOrWhiteSpace(root.Path))
            {
                continue;
            }

            try
            {
                if (FileSystemPathIdentity.AreEquivalent(
                        root.Path,
                        destinationPath,
                        semantics.Value))
                {
                    return root;
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                // Broken root metadata is not evidence that the destination is that root.
            }
        }

        return null;
    }

    private bool DestinationHoldsExistingFiles(string destinationPath)
    {
        try
        {
            return _fileSystem.DirectoryExists(destinationPath)
                && _fileSystem
                    .EnumerateFiles(destinationPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Any(FileUtils.IsAudioFile);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            // Fail closed: without proof of existing files this is not an in-place add.
            return false;
        }
    }
}
