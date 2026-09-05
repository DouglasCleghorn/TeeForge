using System.IO.MemoryMappedFiles;
using TeeForge.Experimental.Storage.Sparse;

namespace TeeForge.Mount.Tests;

public class ImDiskProxyServerTests
{
    private const int HeaderSize = 4096;
    private const int BlockSize = 64 * 1024;

    [Fact]
    public async Task SharedMemoryProtocolExposes4KGeometryAndTranslatesUnmapAndZeroToTrim()
    {
        using var storage = new MemoryStream();
        using SparseDiskImage disk = SparseDiskImage.Create(
            storage,
            4L * BlockSize,
            BlockSize,
            new SparseDiskImageOptions(
                leaveOpen: true,
                freeBlockQueueCapacity: 0,
                freeBlockQueueLowWatermark: 0));
        using var image = new DiskImageSession(disk, "test.tfdisk");
        string name = "TeeForge_Test_" + Guid.NewGuid().ToString("N");
        using var server = new ImDiskProxyServer(name, image, readOnly: false, useGlobalNamespace: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task serverTask = Task.Run(() => server.RunAsync(cancellationToken), cancellationToken);
        using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(name);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        using EventWaitHandle request = EventWaitHandle.OpenExisting(name + "_Request");
        using EventWaitHandle response = EventWaitHandle.OpenExisting(name + "_Response");

        SendRequest(view, request, response, requestCode: 1);
        Assert.Equal(4L * BlockSize, view.ReadInt64(0));
        Assert.Equal(4096, view.ReadInt64(8));
        Assert.Equal(6, view.ReadInt64(16));

        byte[] payload = Enumerable.Repeat((byte)73, 2 * BlockSize).ToArray();
        view.Write(0, 3UL);
        view.Write(8, 0UL);
        view.Write(16, (ulong)payload.Length);
        view.WriteArray(HeaderSize, payload, 0, payload.Length);
        Signal(request, response);
        Assert.Equal(0, view.ReadInt64(0));
        Assert.Equal(payload.Length, view.ReadInt64(8));

        view.Write(0, 6UL);
        view.Write(8, 32UL);
        view.Write(HeaderSize, 0L);
        view.Write(HeaderSize + 8, (long)BlockSize);
        view.Write(HeaderSize + 16, 4L * BlockSize);
        view.Write(HeaderSize + 24, 4096L);
        Signal(request, response);
        Assert.Equal(22, view.ReadInt64(0));
        Assert.All(ReadAt(disk, 0, 4096), static value => Assert.Equal(73, value));

        SendRangeRequest(view, request, response, requestCode: 6, offset: 0, length: BlockSize);
        Assert.Equal(0, view.ReadInt64(0));
        Assert.All(ReadAt(disk, 0, BlockSize), static value => Assert.Equal(0, value));
        Assert.All(ReadAt(disk, BlockSize, 4096), static value => Assert.Equal(73, value));

        SendRangeRequest(view, request, response, requestCode: 7, offset: BlockSize, length: 4096);
        Assert.Equal(0, view.ReadInt64(0));
        Assert.All(ReadAt(disk, BlockSize, 4096), static value => Assert.Equal(0, value));
        Assert.Equal(73, ReadAt(disk, BlockSize + 4096, 1)[0]);

        view.Write(0, 5UL);
        request.Set();
        await serverTask;
    }

    [Fact]
    public async Task ProxyUnmapOnDifferenceMasksBaseWithoutWritingUpstream()
    {
        using var baseStorage = new MemoryStream();
        using SparseDiskImage baseDisk = SparseDiskImage.Create(
            baseStorage,
            4L * BlockSize,
            BlockSize,
            new SparseDiskImageOptions(
                leaveOpen: true,
                freeBlockQueueCapacity: 0,
                freeBlockQueueLowWatermark: 0));
        baseDisk.Write(Enumerable.Repeat((byte)91, 2 * BlockSize).ToArray());
        baseDisk.Flush();
        Guid baseDataWriteId = baseDisk.DataWriteId;
        using var differenceStorage = new MemoryStream();
        using DifferencingDiskImage difference = DifferencingDiskImage.Create(
            baseDisk,
            differenceStorage,
            new DifferencingDiskImageOptions(
                leaveBaseOpen: true,
                leaveDifferenceOpen: true));
        using var image = new DiskImageSession(difference, "test.tfdiff");
        string name = "TeeForge_Test_" + Guid.NewGuid().ToString("N");
        using var server = new ImDiskProxyServer(name, image, readOnly: false, useGlobalNamespace: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task serverTask = Task.Run(() => server.RunAsync(cancellationToken), cancellationToken);
        using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(name);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        using EventWaitHandle request = EventWaitHandle.OpenExisting(name + "_Request");
        using EventWaitHandle response = EventWaitHandle.OpenExisting(name + "_Response");

        SendRangeRequest(view, request, response, requestCode: 6, offset: 0, length: BlockSize);
        SendRangeRequest(view, request, response, requestCode: 7, offset: BlockSize, length: 4096);

        Assert.All(ReadAt(difference, 0, BlockSize), static value => Assert.Equal(0, value));
        Assert.All(ReadAt(difference, BlockSize, 4096), static value => Assert.Equal(0, value));
        Assert.Equal(91, ReadAt(difference, BlockSize + 4096, 1)[0]);
        Assert.All(ReadAt(baseDisk, 0, BlockSize + 4096), static value => Assert.Equal(91, value));
        Assert.Equal(baseDataWriteId, baseDisk.DataWriteId);

        view.Write(0, 5UL);
        request.Set();
        await serverTask;
    }

    private static void SendRequest(
        MemoryMappedViewAccessor view,
        EventWaitHandle request,
        EventWaitHandle response,
        ulong requestCode)
    {
        view.Write(0, requestCode);
        Signal(request, response);
    }

    private static void SendRangeRequest(
        MemoryMappedViewAccessor view,
        EventWaitHandle request,
        EventWaitHandle response,
        ulong requestCode,
        long offset,
        long length)
    {
        view.Write(0, requestCode);
        view.Write(8, 16UL);
        view.Write(HeaderSize, offset);
        view.Write(HeaderSize + 8, length);
        Signal(request, response);
    }

    private static void Signal(EventWaitHandle request, EventWaitHandle response)
    {
        request.Set();
        Assert.True(response.WaitOne(TimeSpan.FromSeconds(10)), "The proxy did not respond in time.");
    }

    private static byte[] ReadAt(SparseDiskImage stream, long offset, int count)
    {
        byte[] buffer = new byte[count];
        Assert.Equal(count, stream.ReadAt(buffer, offset));
        return buffer;
    }

    private static byte[] ReadAt(DifferencingDiskImage stream, long offset, int count)
    {
        byte[] buffer = new byte[count];
        Assert.Equal(count, stream.ReadAt(buffer, offset));
        return buffer;
    }
}
