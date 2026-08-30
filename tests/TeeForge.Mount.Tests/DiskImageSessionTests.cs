using TeeForge.Sparse;

namespace TeeForge.Mount.Tests;

public class DiskImageSessionTests
{
    private const int BlockSize = 64 * 1024;
    private const long Capacity = 4L * BlockSize;

    [Fact]
    public void DifferenceOpenResolvesRelativeParentAndKeepsTheParentReadOnly()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TeeForge-Mount-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string basePath = Path.Combine(directory, "base.tfdisk");
        string childPath = Path.Combine(directory, "child.tfdiff");
        try
        {
            Guid baseDataWriteId;
            using (var baseStorage = new FileStream(basePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (DynamicAllocationStream baseDisk = DynamicAllocationStream.Create(
                baseStorage,
                Capacity,
                BlockSize,
                new DynamicAllocationStreamOptions(
                    leaveOpen: true,
                    freeBlockQueueCapacity: 0,
                    freeBlockQueueLowWatermark: 0)))
            {
                baseDisk.Write(Enumerable.Repeat((byte)31, 2 * BlockSize).ToArray());
                baseDisk.Flush();
                baseDataWriteId = baseDisk.DataWriteId;
                using var differenceStorage = new FileStream(
                    childPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None);
                using DifferencingStream child = DifferencingStream.Create(
                    baseDisk,
                    differenceStorage,
                    new DifferencingStreamOptions(
                        leaveBaseOpen: true,
                        leaveDifferenceOpen: true),
                    "base.tfdisk");
                child.WriteAt([7], 100);
                child.Flush();
            }

            using (DiskImageSession session = DiskImageSession.Open(childPath, readOnly: false))
            {
                DifferencingStream child = Assert.IsType<DifferencingStream>(session.LogicalStream);
                Assert.Equal(basePath, Path.GetFullPath(child.ParentPathHint!, directory));
                Assert.Equal([31, 7, 31], ReadAt(child, 99, 3));
                child.WriteAt([8], BlockSize + 20);
                Assert.Equal([8], ReadAt(child, BlockSize + 20, 1));
            }

            using var reopenedStorage = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using DynamicAllocationStream reopenedBase = DynamicAllocationStream.Open(
                reopenedStorage,
                new DynamicAllocationStreamOptions(
                    leaveOpen: true,
                    readOnly: true,
                    freeBlockQueueCapacity: 0,
                    freeBlockQueueLowWatermark: 0));
            Assert.Equal(baseDataWriteId, reopenedBase.DataWriteId);
            Assert.Equal([31], ReadAt(reopenedBase, BlockSize + 20, 1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DifferenceOpenRejectsCyclicParentHint()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TeeForge-Mount-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string basePath = Path.Combine(directory, "base.tfdisk");
        string childPath = Path.Combine(directory, "child.tfdiff");
        try
        {
            {
                using var baseStorage = new FileStream(basePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                using DynamicAllocationStream baseDisk = DynamicAllocationStream.Create(
                    baseStorage,
                    Capacity,
                    BlockSize,
                    new DynamicAllocationStreamOptions(
                        leaveOpen: true,
                        freeBlockQueueCapacity: 0,
                        freeBlockQueueLowWatermark: 0));
                using var differenceStorage = new FileStream(
                    childPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None);
                using DifferencingStream child = DifferencingStream.Create(
                    baseDisk,
                    differenceStorage,
                    new DifferencingStreamOptions(
                        leaveBaseOpen: true,
                        leaveDifferenceOpen: true),
                    "child.tfdiff");
                child.Flush();
            }

            InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
                DiskImageSession.Open(childPath, readOnly: true));
            Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] ReadAt(TeeForge.RandomAccess.ITeeRandomAccessStream stream, long offset, int count)
    {
        byte[] buffer = new byte[count];
        Assert.Equal(count, stream.ReadAt(buffer, offset));
        return buffer;
    }
}
