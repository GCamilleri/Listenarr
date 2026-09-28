/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Application.Common.Naming;

/// <summary>
/// Rename, manual import, library add, library preview and download import each used to
/// build their own naming variables. The same book and the same pattern produced five
/// different folders, so every import planted a path the next organize run moved. These
/// tests drive all five real code paths and require one answer.
/// </summary>
[Trait("Name", "UnifiedNamingPathTests")]
[Trait("Area", "Naming")]
[Trait("Category", "NamingVariables")]
public sealed class UnifiedNamingPathTests : BaseTests
{
    private const string FolderPattern = "{Author}/{Series}/{SeriesNumber} - {Title}";
    private const string FilePattern = "{Title}";

    private static AudibleBookMetadata BookMetadata() => new()
    {
        Title = "The Final Empire",
        Subtitle = "Mistborn Book 1",
        Authors = ["Brandon Sanderson", "Robert Jordan"],
        Narrators = ["Michael Kramer", "Kate Reading"],
        Series = "Mistborn",
        SeriesNumber = "1",
        PublishYear = "2006",
        Asin = "UNIFIED-NAMING-1"
    };

    private const string ExpectedRelativeFolder = "Brandon Sanderson/Mistborn/1 - The Final Empire";

    [Fact]
    public async Task AllFiveNamingFlows_PlanTheSameFolderForTheSameBookAndPattern()
    {
        // Given one configured root, one folder pattern and one book
        var destinationGuard = new Mock<ILibraryDestinationMutationGuard>();
        destinationGuard
            .Setup(guard => guard.GetBlockingReasonAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        Init(services => services.WithSingleton(destinationGuard.Object));

        var managedRoot = FileService.GetTempDirectory("unified-naming-root");
        var root = await AddAuthorizedRootAsync(managedRoot);
        root.IsDefault = true;
        await _rootFolderRepository.UpdateAsync(root);
        var settings = await _applicationSettingsRepository.GetAsync()
            ?? await _applicationSettingsRepository.InitializeIfMissingAsync(
                new ApplicationSettingsBuilder().Build());
        settings.OutputPath = managedRoot;
        settings.FolderNamingPattern = FolderPattern;
        settings.FileNamingPattern = FilePattern;
        await _applicationSettingsRepository.SaveAsync(settings);

        var metadata = BookMetadata();
        var expectedFolder = Path.GetFullPath(
            Path.Join(managedRoot, ExpectedRelativeFolder.Replace('/', Path.DirectorySeparatorChar)));
        var fileNamingService = _provider.GetRequiredService<IFileNamingService>();

        // When the book is added, which is the path all the others have to agree with
        var addResult = await _provider
            .GetRequiredService<ILibraryAddService>()
            .AddToLibraryAsync(
                new LibraryAddOperationRequest { Metadata = metadata, Monitored = true },
                CancellationToken.None);
        Assert.False(addResult.ValidationFailed, addResult.ValidationMessage ?? addResult.Message);
        var stored = Assert.Single(await _audiobookRepository.GetAllAsync());
        Assert.Equal(expectedFolder, stored.BasePath);
        Assert.False(stored.BasePathIsUserPinned);

        // ... and previewed through the preview endpoint
        var previewResult = await _provider
            .GetRequiredService<LibraryPreviewPathWorkflow>()
            .PreviewAsync(new LibraryController.PreviewPathRequest { Metadata = metadata });
        var ok = Assert.IsType<OkObjectResult>(previewResult);
        using var payload = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(ok.Value));
        var previewFolder = Path.GetFullPath(
            payload.RootElement.GetProperty("fullPath").GetString()!);

        // ... and planned by manual import for the stored, unpinned book
        var manualImportFolder = await PlanManualImportFolderAsync(
            stored,
            managedRoot,
            root,
            settings,
            fileNamingService);

        // ... and planned by organize
        var organizeFolder = await PlanOrganizeFolderAsync(stored, managedRoot);

        // ... and planned by the download import
        var downloadFolder = PlanDownloadImportFolder(
            stored,
            managedRoot,
            settings,
            fileNamingService);

        // Then all five name the same folder
        Assert.Equal(expectedFolder, previewFolder);
        Assert.Equal(expectedFolder, manualImportFolder);
        Assert.Equal(expectedFolder, organizeFolder);
        Assert.Equal(expectedFolder, downloadFolder);
    }

    [Fact]
    public async Task AllFiveNamingFlows_AgreeOnALowercasePattern()
    {
        // Given the same setup with a lowercase pattern. The token regex has always been
        // case-insensitive, but four of the five variable tables were not, so a lowercase
        // pattern resolved to nothing and the book landed flat in the root.
        var destinationGuard = new Mock<ILibraryDestinationMutationGuard>();
        destinationGuard
            .Setup(guard => guard.GetBlockingReasonAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        Init(services => services.WithSingleton(destinationGuard.Object));

        var managedRoot = FileService.GetTempDirectory("unified-naming-lowercase-root");
        var root = await AddAuthorizedRootAsync(managedRoot);
        root.IsDefault = true;
        await _rootFolderRepository.UpdateAsync(root);
        var settings = await _applicationSettingsRepository.GetAsync()
            ?? await _applicationSettingsRepository.InitializeIfMissingAsync(
                new ApplicationSettingsBuilder().Build());
        settings.OutputPath = managedRoot;
        settings.FolderNamingPattern = "{author}/{series}/{seriesnumber} - {title}";
        settings.FileNamingPattern = "{title}";
        await _applicationSettingsRepository.SaveAsync(settings);

        var metadata = BookMetadata();
        var expectedFolder = Path.GetFullPath(
            Path.Join(managedRoot, ExpectedRelativeFolder.Replace('/', Path.DirectorySeparatorChar)));
        var fileNamingService = _provider.GetRequiredService<IFileNamingService>();

        // When every flow plans the folder
        var addResult = await _provider
            .GetRequiredService<ILibraryAddService>()
            .AddToLibraryAsync(
                new LibraryAddOperationRequest { Metadata = metadata, Monitored = true },
                CancellationToken.None);
        Assert.False(addResult.ValidationFailed, addResult.ValidationMessage ?? addResult.Message);
        var stored = Assert.Single(await _audiobookRepository.GetAllAsync());

        var previewResult = await _provider
            .GetRequiredService<LibraryPreviewPathWorkflow>()
            .PreviewAsync(new LibraryController.PreviewPathRequest { Metadata = metadata });
        var ok = Assert.IsType<OkObjectResult>(previewResult);
        using var payload = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(ok.Value));

        // Then every one of them resolves the lowercase tokens to the same folder
        Assert.Equal(expectedFolder, stored.BasePath);
        Assert.Equal(
            expectedFolder,
            Path.GetFullPath(payload.RootElement.GetProperty("fullPath").GetString()!));
        Assert.Equal(
            expectedFolder,
            await PlanManualImportFolderAsync(stored, managedRoot, root, settings, fileNamingService));
        Assert.Equal(expectedFolder, await PlanOrganizeFolderAsync(stored, managedRoot));
        Assert.Equal(
            expectedFolder,
            PlanDownloadImportFolder(stored, managedRoot, settings, fileNamingService));
    }

    private static async Task<string> PlanManualImportFolderAsync(
        Audiobook audiobook,
        string managedRoot,
        RootFolder root,
        ApplicationSettings settings,
        IFileNamingService fileNamingService)
    {
        // A manual import into a book with no committed folder yet resolves its managed
        // destination to the configured root, exactly as ManualImportController does.
        var planner = new ManualImportPathPlanner(fileNamingService);
        var plan = await planner.GeneratePathAsync(
            audiobook,
            audiobook.CreateBasicAudioMetadata(),
            new ManualImportItemDto
            {
                FullPath = Path.Join(managedRoot, "incoming.m4b"),
                MatchedAudiobookId = audiobook.Id
            },
            managedRoot,
            [root],
            settings,
            FileSystemPathSemantics.CurrentHostDefault);

        Assert.False(plan.IsRefused);
        return Path.GetFullPath(plan.AudiobookBasePath);
    }

    private async Task<string> PlanOrganizeFolderAsync(Audiobook audiobook, string managedRoot)
    {
        // Organize plans from where the files actually are, so give the book one.
        var bookFolder = Path.Join(managedRoot, "Loose Drop");
        Directory.CreateDirectory(bookFolder);
        var filePath = Path.Join(bookFolder, "whatever-the-release-called-it.m4b");
        await File.WriteAllTextAsync(filePath, "audio");

        audiobook.BasePath = bookFolder;
        audiobook.BasePathIsUserPinned = false;
        await _audiobookRepository.UpdateAsync(audiobook);
        await _audiobookFileRepository.AddAsync(new AudiobookFile
        {
            AudiobookId = audiobook.Id,
            Path = filePath,
            Format = "m4b"
        });

        var preview = Assert.Single(
            await _provider.GetRequiredService<IRenameService>()
                .PreviewRenameAsync([audiobook.Id]));
        return Path.GetFullPath(preview.NewFolderPath!);
    }

    private static string PlanDownloadImportFolder(
        Audiobook audiobook,
        string managedRoot,
        ApplicationSettings settings,
        IFileNamingService fileNamingService)
    {
        var namingMetadata = DownloadImportService.BuildNamingMetadata(
            audiobook,
            extractedMetadata: null,
            fallbackTitle: "release-name");
        var variables = DownloadImportService.BuildDownloadNamingVariables(
            namingMetadata,
            "release-name",
            diskNumber: null,
            chapterNumber: null);
        var folderRelative = fileNamingService.ApplyNamingPattern(
            settings.FolderNamingPattern,
            variables,
            false);
        return Path.GetFullPath(Path.Join(managedRoot, folderRelative));
    }
}
