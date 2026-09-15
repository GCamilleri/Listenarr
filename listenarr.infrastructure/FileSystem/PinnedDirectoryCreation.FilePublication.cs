using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal sealed partial class PinnedDirectoryAnchor
    {
    }

    private static int TryRenameRelativeEntryNoReplaceLinux(
        SafeFileHandle sourceDirectoryHandle,
        string sourceName,
        SafeFileHandle destinationDirectoryHandle,
        string finalName)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The non-throwing no-replace rename probe is Linux-specific.");
        }

        return RenameRelativeEntryNoReplaceLinuxCore(
            sourceDirectoryHandle.DangerousGetHandle().ToInt32(),
            sourceName,
            destinationDirectoryHandle.DangerousGetHandle().ToInt32(),
            finalName);
    }

    /// <summary>
    /// Publishes <paramref name="sourceName"/> as <paramref name="finalName"/> without ever
    /// replacing an existing entry, returning the errno rather than throwing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fast path is renameat2 with RENAME_NOREPLACE, which is atomic. That flag needs support
    /// from the underlying filesystem: rename(2) lists ext2/ext4, btrfs, tmpfs, cifs, xfs, minix,
    /// reiserfs, jfs, vfat and bpf, and a filesystem outside that set answers EINVAL. FUSE mounts
    /// in particular (mergerfs, rclone, unRAID's user shares, older fuse-overlayfs) and other
    /// unsupported backings therefore failed every organize with errno 22, so libraries on that
    /// storage could never move a single file.
    /// </para>
    /// <para>
    /// The fallback is linkat plus unlinkat, chosen because it keeps both properties the
    /// no-replace rename was there for. linkat fails with EEXIST if the destination is taken, so
    /// the guarantee stays atomic rather than degrading to a check-then-rename race, and a hard
    /// link is the same inode, so the physical object identity this type exists to preserve is
    /// preserved. It is only attempted when renameat2 reports the flag itself is unsupported;
    /// every other errno is returned untouched.
    /// </para>
    /// </remarks>
    private static int RenameRelativeEntryNoReplaceLinuxCore(
        int sourceDirectoryFileDescriptor,
        string sourceName,
        int destinationDirectoryFileDescriptor,
        string finalName)
    {
        if (RenameAtNoReplaceLinux(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName,
                RenameNoReplace) == 0)
        {
            return 0;
        }

        var renameError = Marshal.GetLastWin32Error();
        if (!IsRenameFlagUnsupported(renameError))
        {
            return renameError;
        }

        var linkError = PublishRelativeEntryByHardLink(
            sourceDirectoryFileDescriptor,
            sourceName,
            destinationDirectoryFileDescriptor,
            finalName);

        // Report the original refusal when the filesystem supports neither mechanism, so the
        // failure still reads as "this storage cannot publish safely" rather than as a
        // hard-link quirk.
        return linkError is ErrnoOperationNotPermitted
            or ErrnoOperationNotSupported
            or ErrnoFunctionNotImplemented
            ? renameError
            : linkError;
    }

    /// <summary>
    /// Moves a directory-relative entry by hard-linking it under the new name and dropping the
    /// old one. Returns 0 on success, otherwise the errno, including EEXIST when the destination
    /// name is already taken.
    /// </summary>
    internal static int PublishRelativeEntryByHardLink(
        int sourceDirectoryFileDescriptor,
        string sourceName,
        int destinationDirectoryFileDescriptor,
        string finalName)
    {
        if (LinkAt(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName,
                0) != 0)
        {
            return Marshal.GetLastWin32Error();
        }

        if (UnlinkAt(sourceDirectoryFileDescriptor, sourceName, 0) == 0)
        {
            return 0;
        }

        // The link landed but the old name survives, which would leave the file visible twice.
        // Drop the new name so the caller sees a clean failure instead of a duplicate.
        var unlinkError = Marshal.GetLastWin32Error();
        UnlinkAt(destinationDirectoryFileDescriptor, finalName, 0);
        return unlinkError;
    }

    private static bool IsRenameFlagUnsupported(int errorCode) =>
        errorCode is ErrnoInvalidArgument
            or ErrnoOperationNotSupported
            or ErrnoFunctionNotImplemented;

    private static void RenameRelativeEntry(
        SafeFileHandle sourceDirectoryHandle,
        SafeFileHandle entryHandle,
        string sourceName,
        SafeFileHandle destinationDirectoryHandle,
        string finalName,
        bool replaceExisting = false)
    {
        if (OperatingSystem.IsWindows())
        {
            RenameRelativeEntryWindows(
                destinationDirectoryHandle,
                entryHandle,
                finalName,
                replaceExisting);
            return;
        }

        var sourceDirectoryFileDescriptor = sourceDirectoryHandle
            .DangerousGetHandle()
            .ToInt32();
        var destinationDirectoryFileDescriptor = destinationDirectoryHandle
            .DangerousGetHandle()
            .ToInt32();
        var result = replaceExisting
            ? RenameAtUnix(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName)
            : OperatingSystem.IsMacOS()
            ? RenameAtExclusiveMac(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName,
                RenameExclusiveMac)
            : RenameRelativeEntryNoReplaceLinuxCore(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName);
        if (result != 0)
        {
            // The Linux no-replace path already resolves its own errno, because it may fall back
            // to linkat and the last syscall is then not the one that decided the outcome.
            var errorCode = !replaceExisting && OperatingSystem.IsLinux()
                ? result
                : Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                "Could not publish a pinned filesystem entry relative to its owned directory.");
        }
    }

    private static void RenameRelativeEntryWindows(
        SafeFileHandle directoryHandle,
        SafeFileHandle entryHandle,
        string finalName,
        bool replaceExisting)
    {
        var fileNameBytes = Encoding.Unicode.GetBytes(finalName);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(uint);
        var bufferSize = checked(fileNameOffset + fileNameBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0; index < bufferSize; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            const int fileRenameInformation = 10;
            const int fileRenameInformationEx = 65;
            const int fileRenameReplaceIfExists = 0x00000001;
            const int fileRenamePosixSemantics = 0x00000002;
            if (replaceExisting)
            {
                Marshal.WriteInt32(
                    buffer,
                    0,
                    fileRenameReplaceIfExists | fileRenamePosixSemantics);
            }
            else
            {
                Marshal.WriteByte(buffer, 0, 0);
            }
            Marshal.WriteIntPtr(
                buffer,
                rootDirectoryOffset,
                directoryHandle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, fileNameLengthOffset, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, buffer + fileNameOffset, fileNameBytes.Length);
            var status = NtSetInformationFile(
                entryHandle,
                out _,
                buffer,
                checked((uint)bufferSize),
                replaceExisting
                    ? fileRenameInformationEx
                    : fileRenameInformation);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                throw new Win32Exception(
                    error,
                    $"Could not publish a pinned filesystem entry relative to its owned directory (Windows error {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

}
