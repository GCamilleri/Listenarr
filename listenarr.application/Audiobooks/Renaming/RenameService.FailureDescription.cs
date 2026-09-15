/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Common.Exceptions;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    /// <summary>
    /// Turns an organize failure into a code and a message that is safe to return to the caller.
    /// </summary>
    /// <remarks>
    /// Every organize failure used to surface as the single string "File organize operation
    /// failed.", with the real reason only in the server log. A user whose library never moves
    /// could not tell a platform restriction from a permission problem from a missing file, and
    /// neither could anyone reading a bug report. These messages never carry a path or raw
    /// exception text; the full exception is still logged.
    /// </remarks>
    internal static (string Code, string Message) DescribeOrganizeFailure(Exception exception) =>
        exception switch
        {
            // Codes and safe details are already curated at the throw site.
            ListenarrApplicationException application =>
                (application.Code, application.SafeDetail),

            // The published release matrix excludes this host, so no amount of retrying helps.
            PlatformNotSupportedException =>
                ("platform-unsupported",
                    "This Listenarr build cannot perform filesystem operations on this platform."),

            UnauthorizedAccessException =>
                ("permission-denied",
                    "Listenarr was not allowed to write to the destination folder. Check the "
                    + "permissions and ownership of the library root."),

            FileNotFoundException =>
                ("source-missing",
                    "The file is no longer at the location Listenarr has recorded for it. "
                    + "Rescan the library and try again."),

            DirectoryNotFoundException =>
                ("destination-missing",
                    "The destination folder could not be created or reached."),

            // Covers the storage-level refusals: read-only mounts, a full disk, a network share
            // that dropped, and a destination that already exists.
            IOException =>
                ("io-error",
                    "The filesystem refused the operation. The destination may be read-only, "
                    + "out of space, or temporarily unavailable."),

            _ => ("organize-failed", "File organize operation failed."),
        };
}
