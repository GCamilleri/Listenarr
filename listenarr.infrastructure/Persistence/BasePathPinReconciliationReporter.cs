/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Diagnostics;
using Listenarr.Application.Common.Naming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

/// <summary>
/// Counts, once at startup, the audiobooks whose BasePath is not what the current folder
/// pattern would produce.
/// </summary>
/// <remarks>
/// Those folders were placed deliberately, or by an older pattern, and there is no way to
/// tell which from the data. The migration therefore leaves every existing row unpinned
/// rather than guessing, and this reports the count so the user can decide: pin the ones
/// they chose through the library edit screen, or let the next organize run re-plan them.
/// It only reads.
/// </remarks>
internal sealed class BasePathPinReconciliationReporter(
    IServiceProvider provider,
    LibraryFilesystemReadiness filesystemReadiness,
    ILogger<BasePathPinReconciliationReporter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await filesystemReadiness.WaitUntilSettledAsync(stoppingToken);
            using var scope = provider.CreateScope();
            var configurationService = scope.ServiceProvider
                .GetRequiredService<IConfigurationService>();
            var settings = await configurationService.GetApplicationSettingsAsync();
            if (settings == null || string.IsNullOrWhiteSpace(settings.FolderNamingPattern))
            {
                return;
            }

            var fileNamingService = scope.ServiceProvider.GetRequiredService<IFileNamingService>();
            var audiobooks = await scope.ServiceProvider
                .GetRequiredService<IAudiobookRepository>()
                .GetAllAsync();

            var divergent = 0;
            foreach (var audiobook in audiobooks)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(audiobook.BasePath) || audiobook.BasePathIsUserPinned)
                {
                    continue;
                }

                var expectedRelative = fileNamingService.ApplyNamingPattern(
                    settings.FolderNamingPattern,
                    NamingVariableBuilder.FromAudiobook(audiobook),
                    false);
                if (string.IsNullOrWhiteSpace(expectedRelative))
                {
                    continue;
                }

                if (!audiobook.BasePath.EndsWith(expectedRelative, StringComparison.OrdinalIgnoreCase))
                {
                    divergent++;
                }
            }

            if (divergent > 0)
            {
                logger.LogWarning(
                    "{Count} audiobooks have a BasePath the current folder pattern would not produce. They are recorded as pattern-managed, so the next organize run will relocate them. Pin the ones you placed deliberately by saving their path on the audiobook before organizing.",
                    divergent);
            }
            else
            {
                logger.LogInformation(
                    "Every pattern-managed audiobook BasePath matches the configured folder pattern.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Debug.WriteLine("BasePathPinReconciliationReporter canceled during host shutdown.");
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            // A diagnostic count must never keep the host from starting.
            logger.LogDebug(exception, "Failed to report audiobook BasePath pin divergence.");
        }
    }
}
