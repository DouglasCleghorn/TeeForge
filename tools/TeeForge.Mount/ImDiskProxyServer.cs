using System.IO.MemoryMappedFiles;
using TeeForge.RandomAccess;
using TeeForge.Sparse;

namespace TeeForge.Mount;

internal sealed class ImDiskProxyServer : IDisposable
{
    private const int HeaderSize = 4096;
    private const int BufferSize = 8 * 1024 * 1024;
    private const ulong RequestInfo = 1;
    private const ulong RequestRead = 2;
    private const ulong RequestWrite = 3;
    private const ulong RequestClose = 5;
    private const ulong RequestUnmap = 6;
    private const ulong RequestZero = 7;
    private const ulong ReadOnlyFlag = 1;
    private const ulong SupportsUnmapFlag = 2;
    private const ulong SupportsZeroFlag = 4;
    private const ulong ErrorInvalidArgument = 22;
    private const ulong ErrorBadFile = 9;

    private readonly string _objectName;
    private readonly bool _readOnly;
    private readonly DiskImageSession _image;
    private readonly Mutex _serverMutex;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _requestEvent;
    private readonly EventWaitHandle _responseEvent;

    internal ImDiskProxyServer(
        string objectName,
        DiskImageSession image,
        bool readOnly,
        bool useGlobalNamespace = true)
    {
        _objectName = objectName;
        _image = image;
        _readOnly = readOnly;
        string prefix = useGlobalNamespace ? "Global\\" : string.Empty;
        _serverMutex = new Mutex(false, prefix + objectName + "_Server", out bool createdNew);
        if (!createdNew)
        {
            throw new InvalidOperationException($"An ImDisk proxy named '{objectName}' is already running.");
        }

        _mapping = MemoryMappedFile.CreateNew(prefix + objectName, HeaderSize + BufferSize);
        _view = _mapping.CreateViewAccessor(0, HeaderSize + BufferSize, MemoryMappedFileAccess.ReadWrite);
        _requestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + objectName + "_Request");
        _responseEvent = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + objectName + "_Response");
    }

    internal string ObjectName => _objectName;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                int signaled = WaitHandle.WaitAny([_requestEvent, cancellationToken.WaitHandle]);
                if (signaled != 0)
                {
                    break;
                }

                ulong request = _view.ReadUInt64(0);
                if (request == RequestClose)
                {
                    break;
                }

                try
                {
                    switch (request)
                    {
                        case RequestInfo:
                            WriteInfo();
                            break;
                        case RequestRead:
                            await ReadAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        case RequestWrite:
                            await WriteAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        case RequestUnmap:
                        case RequestZero:
                            await TrimAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        default:
                            WriteError(ErrorInvalidArgument);
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    WriteError(ErrorBadFile);
                }

                _responseEvent.Set();
            }
        }
        finally
        {
            await _image.LogicalStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void WriteInfo()
    {
        _view.Write(0, checked((ulong)_image.VirtualDisk.VirtualCapacity));
        _view.Write(8, 4096UL);
        ulong flags = _readOnly ? ReadOnlyFlag : SupportsUnmapFlag | SupportsZeroFlag;
        _view.Write(16, flags);
    }

    private async ValueTask ReadAsync(CancellationToken cancellationToken)
    {
        ulong offset = _view.ReadUInt64(8);
        ulong requested = _view.ReadUInt64(16);
        if (requested > BufferSize || offset > (ulong)_image.VirtualDisk.VirtualCapacity ||
            requested > (ulong)_image.VirtualDisk.VirtualCapacity - offset)
        {
            WriteError(ErrorInvalidArgument);
            return;
        }

        byte[] buffer = new byte[checked((int)requested)];
        int read = await _image.RandomAccess.ReadAtAsync(buffer, checked((long)offset), cancellationToken).ConfigureAwait(false);
        if (read < buffer.Length)
        {
            Array.Clear(buffer, read, buffer.Length - read);
        }

        _view.Write(0, 0UL);
        _view.Write(8, requested);
        _view.WriteArray(HeaderSize, buffer, 0, buffer.Length);
    }

    private async ValueTask WriteAsync(CancellationToken cancellationToken)
    {
        ulong offset = _view.ReadUInt64(8);
        ulong requested = _view.ReadUInt64(16);
        if (_readOnly)
        {
            WriteError(ErrorBadFile);
            return;
        }

        if (requested > BufferSize || offset > (ulong)_image.VirtualDisk.VirtualCapacity ||
            requested > (ulong)_image.VirtualDisk.VirtualCapacity - offset)
        {
            WriteError(ErrorInvalidArgument);
            return;
        }

        byte[] buffer = new byte[checked((int)requested)];
        _view.ReadArray(HeaderSize, buffer, 0, buffer.Length);
        await _image.RandomAccess.WriteAtAsync(buffer, checked((long)offset), cancellationToken).ConfigureAwait(false);
        await _image.LogicalStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _view.Write(0, 0UL);
        _view.Write(8, requested);
    }

    private async ValueTask TrimAsync(CancellationToken cancellationToken)
    {
        ulong byteLength = _view.ReadUInt64(8);
        if (_readOnly)
        {
            WriteError(ErrorBadFile);
            return;
        }

        if (byteLength > BufferSize || (byteLength & 15UL) != 0)
        {
            WriteError(ErrorInvalidArgument);
            return;
        }

        int rangeCount = checked((int)(byteLength / 16));
        var ranges = new (long Offset, long Length)[rangeCount];
        ulong capacity = checked((ulong)_image.VirtualDisk.VirtualCapacity);
        for (int index = 0; index < rangeCount; index++)
        {
            long offset = _view.ReadInt64(HeaderSize + (index * 16L));
            ulong length = _view.ReadUInt64(HeaderSize + (index * 16L) + 8);
            if (offset < 0 || length > long.MaxValue || (ulong)offset > capacity || length > capacity - (ulong)offset)
            {
                WriteError(ErrorInvalidArgument);
                return;
            }

            ranges[index] = (offset, checked((long)length));
        }

        foreach ((long offset, long length) in ranges)
        {
            long logicalLength = _image.LogicalStream.Length;
            if (length == 0 || offset >= logicalLength)
            {
                continue;
            }

            long boundedLength = Math.Min(length, logicalLength - offset);
            switch (_image.LogicalStream)
            {
                case DynamicAllocationStream dynamicStream:
                    await dynamicStream.TrimAsync(offset, boundedLength, cancellationToken).ConfigureAwait(false);
                    break;
                case DifferencingStream differencingStream:
                    await differencingStream.TrimAsync(offset, boundedLength, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("The mounted stream does not support trim.");
            }
        }

        await _image.LogicalStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _view.Write(0, 0UL);
    }

    private void WriteError(ulong error)
    {
        _view.Write(0, error);
        _view.Write(8, 0UL);
    }

    public void Dispose()
    {
        _responseEvent.Dispose();
        _requestEvent.Dispose();
        _view.Dispose();
        _mapping.Dispose();
        _serverMutex.Dispose();
    }
}
