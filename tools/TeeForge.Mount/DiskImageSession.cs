using TeeForge.RandomAccess;
using TeeForge.Experimental.Storage.Sparse;

namespace TeeForge.Mount;

internal sealed class DiskImageSession(Stream logicalStream, string imagePath) : IDisposable, IAsyncDisposable
{
    internal Stream LogicalStream { get; } = logicalStream;

    internal ITeeRandomAccessStream RandomAccess { get; } =
        logicalStream as ITeeRandomAccessStream ??
        throw new InvalidOperationException("The mounted image does not expose position-independent I/O.");

    internal IVirtualDiskStream VirtualDisk { get; } =
        logicalStream as IVirtualDiskStream ??
        throw new InvalidOperationException("The mounted image does not expose virtual-disk geometry.");

    internal string ImagePath { get; } = imagePath;

    public ValueTask DisposeAsync() => LogicalStream.DisposeAsync();

    public void Dispose() => LogicalStream.Dispose();

    internal static DiskImageSession Open(string imagePath, bool readOnly, string? explicitParentPath = null) =>
        OpenCore(
            imagePath,
            readOnly,
            explicitParentPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static DiskImageSession OpenCore(
        string imagePath,
        bool readOnly,
        string? explicitParentPath,
        HashSet<string> chainPaths)
    {
        string fullPath = Path.GetFullPath(imagePath);
        if (!chainPaths.Add(fullPath))
        {
            throw new InvalidDataException($"The differencing parent chain contains a cycle at '{fullPath}'.");
        }

        string extension = Path.GetExtension(fullPath);
        if (extension.Equals(".tfdisk", StringComparison.OrdinalIgnoreCase))
        {
            FileAccess access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
            FileShare share = readOnly ? FileShare.Read : FileShare.None;
            var storage = new FileStream(fullPath, FileMode.Open, access, share, 4096, FileOptions.RandomAccess);
            try
            {
                SparseDiskImage disk = SparseDiskImage.Open(
                    storage,
                    new SparseDiskImageOptions(
                        readOnly: readOnly,
                        freeBlockQueueCapacity: readOnly ? 0 : 4096,
                        freeBlockQueueLowWatermark: readOnly ? 0 : 1024));
                return new DiskImageSession(disk, fullPath);
            }
            catch
            {
                storage.Dispose();
                throw;
            }
        }

        if (!extension.Equals(".tfdiff", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Mount accepts only .tfdisk and .tfdiff images.");
        }

        DifferencingDiskImageLocator locator = ReadDifferencingLocator(fullPath);
        string? parentPath = explicitParentPath;
        if (parentPath is null && locator.ParentPathHint is not null)
        {
            parentPath = Path.GetFullPath(locator.ParentPathHint, Path.GetDirectoryName(fullPath)!);
        }

        parentPath ??= MountCatalog.TryResolve(locator.BaseId);
        if (parentPath is null)
        {
            throw new FileNotFoundException(
                $"Could not resolve base {locator.BaseId}. Supply --parent or add the base to the mount catalog.",
                fullPath);
        }

        DiskImageSession baseSession = OpenCore(parentPath, readOnly: true, explicitParentPath: null, chainPaths);
        FileAccess differenceAccess = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        FileShare differenceShare = readOnly ? FileShare.Read : FileShare.None;
        var differenceStorage = new FileStream(
            fullPath,
            FileMode.Open,
            differenceAccess,
            differenceShare,
            4096,
            FileOptions.RandomAccess);
        try
        {
            DifferencingDiskImage child = DifferencingDiskImage.Open(
                baseSession.LogicalStream,
                differenceStorage,
                new DifferencingDiskImageOptions(readOnly: readOnly));
            return new DiskImageSession(child, fullPath);
        }
        catch
        {
            differenceStorage.Dispose();
            baseSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    internal static DifferencingDiskImageLocator ReadDifferencingLocator(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return DifferencingDiskImage.ReadLocator(stream);
    }
}
