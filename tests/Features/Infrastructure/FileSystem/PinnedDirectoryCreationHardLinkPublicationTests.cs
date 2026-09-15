using System.Runtime.InteropServices;
using Listenarr.Tests.Common;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

/// <summary>
/// Covers the publication fallback used when a filesystem does not implement renameat2's
/// RENAME_NOREPLACE flag.
/// </summary>
/// <remarks>
/// rename(2) lists RENAME_NOREPLACE support for ext2/ext4, btrfs, tmpfs, cifs, xfs, minix,
/// reiserfs, jfs, vfat and bpf only; anything else answers EINVAL. FUSE mounts such as mergerfs,
/// rclone and unRAID user shares sit outside that set, and before this fallback existed every
/// file publication on that storage failed with errno 22. That broke organize, and it also left
/// startup rename recovery unable to finish, which disables filesystem mutation for the whole
/// process. The EINVAL trigger is kernel behaviour, so these tests exercise the fallback
/// mechanism itself rather than trying to manufacture an unsupported filesystem.
/// </remarks>
[Trait("Name", "PinnedDirectoryCreationHardLinkPublicationTests")]
[Trait("Category", "Infrastructure")]
public sealed class PinnedDirectoryCreationHardLinkPublicationTests : BaseTests
{
    private const int ErrnoFileExists = 17;

    [LinuxFact]
    public void PublishByHardLink_MovesTheEntryAndKeepsTheSameInode()
    {
        // Given a file with a second hard link, so the inode can be observed after the move
        // without reading struct stat.
        var (sourceDirectory, destinationDirectory) = CreateSourceAndDestination();
        var sourceFile = Path.Combine(sourceDirectory, "book.m4b");
        var witness = Path.Combine(sourceDirectory, "witness.m4b");
        File.WriteAllText(sourceFile, "audio");
        if (Link(sourceFile, witness) != 0)
        {
            throw new IOException(
                $"Could not hard-link the witness: errno {Marshal.GetLastWin32Error()}.");
        }

        using var sourceHandle = OpenDirectory(sourceDirectory);
        using var destinationHandle = OpenDirectory(destinationDirectory);

        // When it is published under the destination directory.
        var result = PinnedDirectoryCreation.PublishRelativeEntryByHardLink(
            sourceHandle.DangerousGetHandle().ToInt32(),
            "book.m4b",
            destinationHandle.DangerousGetHandle().ToInt32(),
            "book.m4b");

        // Then the entry moved.
        var destinationFile = Path.Combine(destinationDirectory, "book.m4b");
        Assert.Equal(0, result);
        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(destinationFile));
        Assert.Equal("audio", File.ReadAllText(destinationFile));

        // And it is the same inode, not a copy: writing through the published name is visible
        // through the witness link. Physical object identity is what the pinned path exists to
        // preserve, so a copying fallback would be unacceptable here.
        File.WriteAllText(destinationFile, "rewritten");
        Assert.Equal("rewritten", File.ReadAllText(witness));
    }

    [LinuxFact]
    public void PublishByHardLink_RefusesToReplaceAnExistingDestination()
    {
        // Given a destination name that is already taken.
        var (sourceDirectory, destinationDirectory) = CreateSourceAndDestination();
        File.WriteAllText(Path.Combine(sourceDirectory, "book.m4b"), "incoming");
        File.WriteAllText(Path.Combine(destinationDirectory, "book.m4b"), "already here");

        using var sourceHandle = OpenDirectory(sourceDirectory);
        using var destinationHandle = OpenDirectory(destinationDirectory);

        // When the publication is attempted.
        var result = PinnedDirectoryCreation.PublishRelativeEntryByHardLink(
            sourceHandle.DangerousGetHandle().ToInt32(),
            "book.m4b",
            destinationHandle.DangerousGetHandle().ToInt32(),
            "book.m4b");

        // Then it reports EEXIST and touches neither side. linkat decides this atomically, so
        // the no-clobber guarantee does not degrade into a check-then-rename race on the
        // filesystems that take this path.
        Assert.Equal(ErrnoFileExists, result);
        Assert.Equal(
            "already here",
            File.ReadAllText(Path.Combine(destinationDirectory, "book.m4b")));
        Assert.Equal(
            "incoming",
            File.ReadAllText(Path.Combine(sourceDirectory, "book.m4b")));
    }

    private static (string Source, string Destination) CreateSourceAndDestination()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"listenarr-hardlink-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        return (source, destination);
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        // O_DIRECTORY is architecture-specific (0x10000 on x86-64, 0x4000 on arm64), so take the
        // flags from the production helper rather than hardcoding one architecture's value.
        var descriptor = Open(path, UnixOpenFlags.Directory(noFollow: false));
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not open '{path}': errno {Marshal.GetLastWin32Error()}.");
        }

        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
