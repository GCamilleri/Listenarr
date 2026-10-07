using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Area", "LibraryApi")]
[Trait("Name", "LibraryPreviewPathWorkflowTests")]
[Trait("Category", "LibraryController")]
public sealed class LibraryPreviewPathWorkflowTests : BaseTests
{
    [Fact]
    public async Task PreviewAsync_NoExplicitRoot_UsesManagedDefaultRootInsteadOfLegacyOutputPath()
    {
        var managedRoot = FileService.GetTempDirectory(
            "preview-managed-default-root");
        var legacyOutput = FileService.GetTempDirectory(
            "preview-legacy-output-root");
        var root = await AddAuthorizedRootAsync(managedRoot);
        root.IsDefault = true;
        await _rootFolderRepository.UpdateAsync(root);
        var settings = await _applicationSettingsRepository.GetAsync()
            ?? await _applicationSettingsRepository.InitializeIfMissingAsync(
                new ApplicationSettingsBuilder().Build());
        settings.OutputPath = legacyOutput;
        settings.FolderNamingPattern = "{Author}/{Title}";
        settings.FileNamingPattern = "{Title}";
        await _applicationSettingsRepository.SaveAsync(settings);
        var request = new LibraryController.PreviewPathRequest
        {
            Metadata = new AudibleBookMetadata
            {
                Title = "The Title",
                Authors = ["The Author"]
            }
        };

        var result = await _provider
            .GetRequiredService<LibraryPreviewPathWorkflow>()
            .PreviewAsync(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var payload = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(ok.Value));
        Assert.Equal(
            managedRoot,
            payload.RootElement.GetProperty("root").GetString());
        Assert.Equal(
            Path.Join(managedRoot, "The Author", "The Title"),
            payload.RootElement.GetProperty("fullPath").GetString());
    }

    [Fact]
    public async Task PreviewAsync_MatchesTheBasePathTheAddActuallyStores()
    {
        // Given a configured default root and a folder pattern with every token the two
        // planners used to disagree about
        var destinationGuard = new Mock<ILibraryDestinationMutationGuard>();
        destinationGuard
            .Setup(guard => guard.GetBlockingReasonAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        Init(services => services.WithSingleton(destinationGuard.Object));

        var managedRoot = FileService.GetTempDirectory("preview-matches-add-root");
        var root = await AddAuthorizedRootAsync(managedRoot);
        root.IsDefault = true;
        await _rootFolderRepository.UpdateAsync(root);
        var settings = await _applicationSettingsRepository.GetAsync()
            ?? await _applicationSettingsRepository.InitializeIfMissingAsync(
                new ApplicationSettingsBuilder().Build());
        settings.OutputPath = managedRoot;
        settings.FolderNamingPattern = "{Author}/{Series}/{SeriesNumber} - {Title}";
        settings.FileNamingPattern = "{Title}";
        await _applicationSettingsRepository.SaveAsync(settings);

        var metadata = new AudibleBookMetadata
        {
            Title = "The Final Empire",
            Subtitle = "Mistborn Book 1",
            Authors = ["Brandon Sanderson", "Robert Jordan"],
            Series = "Mistborn",
            SeriesNumber = "1",
            Asin = "PREVIEW-MATCHES-ADD"
        };

        // When the path is previewed and then the same book is added
        var previewResult = await _provider
            .GetRequiredService<LibraryPreviewPathWorkflow>()
            .PreviewAsync(new LibraryController.PreviewPathRequest { Metadata = metadata });
        var addResult = await _provider
            .GetRequiredService<ILibraryAddService>()
            .AddToLibraryAsync(
                new LibraryAddOperationRequest { Metadata = metadata, Monitored = true },
                CancellationToken.None);

        // Then the preview described exactly the folder the add committed
        var ok = Assert.IsType<OkObjectResult>(previewResult);
        using var payload = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(ok.Value));
        var previewedPath = payload.RootElement.GetProperty("fullPath").GetString();

        Assert.False(addResult.ValidationFailed, addResult.ValidationMessage ?? addResult.Message);
        var stored = Assert.Single(await _audiobookRepository.GetAllAsync());
        Assert.Equal(
            Path.GetFullPath(Path.Join(managedRoot, "Brandon Sanderson", "Mistborn", "1 - The Final Empire")),
            stored.BasePath);
        Assert.Equal(Path.GetFullPath(previewedPath!), stored.BasePath);
    }

    [WindowsFact]
    public async Task PreviewAsync_UnavailableDestinationFilesystem_ReturnsGeneratedRelativePathWithoutStorageProbe()
    {
        var unavailableDrive = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(letter => $"{(char)letter}:\\")
            .FirstOrDefault(path => !Directory.Exists(path));
        Assert.False(string.IsNullOrWhiteSpace(unavailableDrive));
        var destinationRoot = Path.Join(
            unavailableDrive!,
            "listenarr-preview-unavailable-root");
        var settings = new ApplicationSettingsBuilder()
            .WithFolderNamingPattern("{Author}/{Title}")
            .Build();
        var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
        configurationService
            .Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(settings);
        var workflow = new LibraryPreviewPathWorkflow(
            configurationService.Object,
            Mock.Of<IRootFolderService>(),
            _provider.GetRequiredService<IFileNamingService>(),
            Mock.Of<ILogger<LibraryPreviewPathWorkflow>>());
        var request = new LibraryController.PreviewPathRequest
        {
            DestinationRoot = destinationRoot,
            Metadata = new AudibleBookMetadata
            {
                Title = "The Title",
                Authors = ["The Author"]
            }
        };

        var result = await workflow.PreviewAsync(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var payload = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(ok.Value));
        Assert.Equal(
            Path.Join(destinationRoot, "The Author", "The Title"),
            payload.RootElement.GetProperty("fullPath").GetString());
        Assert.Equal(
            Path.Join("The Author", "The Title"),
            payload.RootElement.GetProperty("relativePath").GetString());
        configurationService.VerifyAll();
    }
}
