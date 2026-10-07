/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Name", "LibraryUpdateWorkflowTests")]
[Trait("Area", "LibraryApi")]
[Trait("Category", "LibraryController")]
public sealed class LibraryUpdateWorkflowTests : BaseTests
{
    private static IRootFolderService CreateEmptyRootFolderService()
    {
        var rootFolderService = new Mock<IRootFolderService>();
        rootFolderService.Setup(service => service.GetAllAsync()).ReturnsAsync([]);
        return rootFolderService.Object;
    }

    private static IAudiobookFileRepository CreateEmptyAudiobookFileRepository()
    {
        var fileRepository = new Mock<IAudiobookFileRepository>();
        fileRepository
            .Setup(repository => repository.GetByAudiobookIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return fileRepository.Object;
    }

    [WindowsFact]
    public async Task UpdateAsync_ForeignPersistedBasePathAlias_RoutesThroughAuthoritativeRewrite()
    {
        var id = 41;
        var nativeTarget = Path.Join(
            Path.GetPathRoot(Environment.CurrentDirectory)!,
            "listenarr-update-foreign-alias",
            Guid.NewGuid().ToString("N"));
        var foreignSource = TempFileService
            .GetWindowsRootRelativeForeignAlias(nativeTarget);
        var before = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = foreignSource
        };
        var after = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = nativeTarget
        };

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        repository.Setup(candidate => candidate.GetByIdAsync(id))
            .ReturnsAsync(after);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        rewriteService
            .Setup(candidate => candidate.RewriteDestinationAsync(
                id,
                nativeTarget,
                foreignSource,
                It.IsAny<CancellationToken>(),
                true))
            .ReturnsAsync(new AudiobookDestinationRewriteResult(
                id,
                nativeTarget,
                foreignSource));

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            CreateEmptyRootFolderService(),
            CreateEmptyAudiobookFileRepository(),
            new LocalFileSystem(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(
            id,
            new AudiobookUpdateRequest { BasePath = nativeTarget });

        Assert.IsType<OkObjectResult>(result);
        rewriteService.Verify(candidate => candidate.RewriteDestinationAsync(
            id,
            nativeTarget,
            foreignSource,
            It.IsAny<CancellationToken>(),
            true), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_DestinationOnlyRewrite_DoesNotIssueMetadataWrite()
    {
        var id = 42;
        var source = Path.Join(Path.GetTempPath(), $"listenarr-update-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"listenarr-update-target-{Guid.NewGuid():N}");
        var before = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = source,
            Explicit = true,
            Abridged = true,
            Monitored = false
        };
        var after = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = target,
            Explicit = true,
            Abridged = true,
            Monitored = false
        };

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        repository.Setup(candidate => candidate.GetByIdAsync(id))
            .ReturnsAsync(after);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        rewriteService
            .Setup(candidate => candidate.RewriteDestinationAsync(
                id,
                target,
                source,
                It.IsAny<CancellationToken>(),
                true))
            .ReturnsAsync(new AudiobookDestinationRewriteResult(id, target, source));

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            CreateEmptyRootFolderService(),
            CreateEmptyAudiobookFileRepository(),
            new LocalFileSystem(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(id, new AudiobookUpdateRequest { BasePath = target });

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(candidate => candidate.UpdateAsync(It.IsAny<Audiobook>()), Times.Never);
        Assert.True(after.Explicit);
        Assert.True(after.Abridged);
        Assert.False(after.Monitored);
    }

    [Fact]
    public async Task UpdateAsync_CancelledAfterDestinationCommit_CompletesRequestedMetadataFinalization()
    {
        var id = 44;
        var source = Path.Join(Path.GetTempPath(), $"listenarr-update-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"listenarr-update-target-{Guid.NewGuid():N}");
        var before = new Audiobook
        {
            Id = id,
            Title = "Original",
            BasePath = source
        };
        var after = new Audiobook
        {
            Id = id,
            Title = "Original",
            BasePath = target
        };
        using var cancellation = new CancellationTokenSource();
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        repository.Setup(candidate => candidate.GetByIdAsync(id))
            .ReturnsAsync(after);
        repository.Setup(candidate => candidate.UpdateAsync(after)).ReturnsAsync(true);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        rewriteService
            .Setup(candidate => candidate.RewriteDestinationAsync(
                id,
                target,
                source,
                It.IsAny<CancellationToken>(),
                true))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult(
                    new AudiobookDestinationRewriteResult(id, target, source));
            });

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            CreateEmptyRootFolderService(),
            CreateEmptyAudiobookFileRepository(),
            new LocalFileSystem(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(
            id,
            new AudiobookUpdateRequest
            {
                BasePath = target,
                Title = "Edited"
            },
            cancellation.Token);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Edited", after.Title);
        repository.Verify(candidate => candidate.UpdateAsync(after), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_DestinationAndMetadataUpdate_PreservesOmittedBooleans()
    {
        var id = 43;
        var source = Path.Join(Path.GetTempPath(), $"listenarr-update-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"listenarr-update-target-{Guid.NewGuid():N}");
        var before = new Audiobook
        {
            Id = id,
            Title = "Original",
            BasePath = source,
            Explicit = true,
            Abridged = true,
            Monitored = false
        };
        var after = new Audiobook
        {
            Id = id,
            Title = "Original",
            BasePath = target,
            Explicit = true,
            Abridged = true,
            Monitored = false
        };

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        repository.Setup(candidate => candidate.GetByIdAsync(id))
            .ReturnsAsync(after);
        repository.Setup(candidate => candidate.UpdateAsync(after)).ReturnsAsync(true);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        rewriteService
            .Setup(candidate => candidate.RewriteDestinationAsync(
                id,
                target,
                source,
                It.IsAny<CancellationToken>(),
                true))
            .ReturnsAsync(new AudiobookDestinationRewriteResult(id, target, source));

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            CreateEmptyRootFolderService(),
            CreateEmptyAudiobookFileRepository(),
            new LocalFileSystem(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(
            id,
            new AudiobookUpdateRequest
            {
                BasePath = target,
                Title = "Edited"
            });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Edited", after.Title);
        Assert.True(after.Explicit);
        Assert.True(after.Abridged);
        Assert.False(after.Monitored);
        repository.Verify(candidate => candidate.UpdateAsync(after), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_BasePathEqualToConfiguredRoot_IsRejected()
    {
        var id = 45;
        var root = Path.Join(Path.GetTempPath(), $"listenarr-update-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var before = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = Path.Join(root, "Author", "Title")
        };

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        var rootFolderService = new Mock<IRootFolderService>();
        rootFolderService.Setup(service => service.GetAllAsync()).ReturnsAsync(
            [new RootFolder { Id = 1, Name = "Library", Path = root }]);

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            rootFolderService.Object,
            CreateEmptyAudiobookFileRepository(),
            new LocalFileSystem(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(
            id,
            new AudiobookUpdateRequest { BasePath = root });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(
            "destination_base_path_is_root",
            System.Text.Json.JsonSerializer.Serialize(badRequest.Value),
            StringComparison.Ordinal);
        rewriteService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateAsync_BasePathWithoutTheRegisteredFiles_IsRejected()
    {
        var id = 46;
        var root = Path.Join(Path.GetTempPath(), $"listenarr-update-missing-{Guid.NewGuid():N}");
        var source = Path.Join(root, "Author", "Original");
        var target = Path.Join(root, "Author", "Empty Target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var registeredFile = Path.Join(source, "book.m4b");
        await File.WriteAllTextAsync(registeredFile, "book");
        var before = new Audiobook
        {
            Id = id,
            Title = "Book",
            BasePath = source
        };

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        var rootFolderService = new Mock<IRootFolderService>();
        rootFolderService.Setup(service => service.GetAllAsync()).ReturnsAsync(
            [new RootFolder { Id = 1, Name = "Library", Path = root }]);
        var fileRepository = new Mock<IAudiobookFileRepository>();
        fileRepository
            .Setup(candidate => candidate.GetByAudiobookIdAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AudiobookFile { Id = 1, AudiobookId = id, Path = registeredFile }]);

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            rootFolderService.Object,
            fileRepository.Object,
            new LocalFileSystem(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(
            id,
            new AudiobookUpdateRequest { BasePath = target });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(
            "destination_base_path_missing_files",
            System.Text.Json.JsonSerializer.Serialize(badRequest.Value),
            StringComparison.Ordinal);
        Assert.True(File.Exists(registeredFile));
        rewriteService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateAsync_BasePathWhereTheRegisteredFilesAlreadyAre_IsAllowed()
    {
        var id = 47;
        var root = Path.Join(Path.GetTempPath(), $"listenarr-update-present-{Guid.NewGuid():N}");
        var source = Path.Join(root, "Author", "Original");
        var target = Path.Join(root, "Author", "Populated Target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var registeredFile = Path.Join(source, "book.m4b");
        await File.WriteAllTextAsync(registeredFile, "book");
        await File.WriteAllTextAsync(Path.Join(target, "book.m4b"), "book");
        var before = new Audiobook { Id = id, Title = "Book", BasePath = source };
        var after = new Audiobook { Id = id, Title = "Book", BasePath = target };

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetForUpdateSnapshotAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(before);
        repository.Setup(candidate => candidate.GetByIdAsync(id)).ReturnsAsync(after);
        var rewriteService = new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Strict);
        rewriteService
            .Setup(candidate => candidate.RewriteDestinationAsync(
                id,
                target,
                source,
                It.IsAny<CancellationToken>(),
                true))
            .ReturnsAsync(new AudiobookDestinationRewriteResult(id, target, source));
        var rootFolderService = new Mock<IRootFolderService>();
        rootFolderService.Setup(service => service.GetAllAsync()).ReturnsAsync(
            [new RootFolder { Id = 1, Name = "Library", Path = root }]);
        var fileRepository = new Mock<IAudiobookFileRepository>();
        fileRepository
            .Setup(candidate => candidate.GetByAudiobookIdAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AudiobookFile { Id = 1, AudiobookId = id, Path = registeredFile }]);

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        using var provider = services.BuildServiceProvider();
        using var operationCoordinator = new AudiobookOperationCoordinator();
        var workflow = new LibraryUpdateWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            rewriteService.Object,
            operationCoordinator,
            new FileSystemSemanticsResolver(),
            rootFolderService.Object,
            fileRepository.Object,
            new LocalFileSystem(),
            NullLogger<LibraryUpdateWorkflow>.Instance);

        var result = await workflow.UpdateAsync(
            id,
            new AudiobookUpdateRequest { BasePath = target });

        Assert.IsType<OkObjectResult>(result);
        rewriteService.Verify(candidate => candidate.RewriteDestinationAsync(
            id,
            target,
            source,
            It.IsAny<CancellationToken>(),
            true), Times.Once);
    }
}
