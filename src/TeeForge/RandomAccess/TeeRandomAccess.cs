using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;
using TeeForge.RandomAccess.Internal;

namespace TeeForge.RandomAccess;

/// <summary>Discovers native position-independent I/O capabilities for streams.</summary>
public static class TeeRandomAccess
{
    /// <summary>Attempts to obtain a safe explicit-offset capability for a stream.</summary>
    /// <remarks>
    /// Arbitrary seekable streams are not adapted because save/seek/restore cannot coordinate
    /// with unrelated users. Wrappers that exclusively own a seekable stream may provide their
    /// own serialized fallback.
    /// </remarks>
    public static bool TryGet(
        Stream stream,
        [NotNullWhen(true)] out ITeeRandomAccessStream? randomAccess)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream is ITeeRandomAccessStream implemented &&
            (implemented.CanReadAt || implemented.CanWriteAt))
        {
            randomAccess = implemented;
            return true;
        }

        if (stream is FileStream fileStream && fileStream.CanSeek)
        {
            var adapter = new FileStreamRandomAccess(fileStream);
            if (adapter.CanReadAt || adapter.CanWriteAt)
            {
                randomAccess = adapter;
                return true;
            }
        }

        randomAccess = null;
        return false;
    }

    internal static ITeeRangeReadSource? TryGetRangeReadSource(Stream stream)
    {
        if (stream is ITeeRangeReadSource rangeReadSource)
        {
            return rangeReadSource;
        }

        return stream is FileStream fileStream && fileStream.CanRead && fileStream.CanSeek
            ? new FileStreamRandomAccess(fileStream)
            : null;
    }

    private sealed class FileStreamRandomAccess : ITeeRandomAccessStream, ITeeRangeReadSource
    {
        private readonly FileStream _stream;
        private readonly SafeFileHandle _handle;

        internal FileStreamRandomAccess(FileStream stream)
        {
            _stream = stream;
            _handle = stream.SafeFileHandle;
        }

        public bool CanReadAt => _stream.CanRead;

        public bool CanWriteAt => _stream.CanWrite;

        public int ReadAt(Span<byte> buffer, long offset)
        {
            EnsureCanRead();
            return System.IO.RandomAccess.Read(_handle, buffer, offset);
        }

        public ValueTask<int> ReadAtAsync(
            Memory<byte> buffer,
            long offset,
            CancellationToken cancellationToken = default)
        {
            EnsureCanRead();
            return System.IO.RandomAccess.ReadAsync(_handle, buffer, offset, cancellationToken);
        }

        public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
        {
            EnsureCanWrite();
            System.IO.RandomAccess.Write(_handle, buffer, offset);
        }

        public ValueTask WriteAtAsync(
            ReadOnlyMemory<byte> buffer,
            long offset,
            CancellationToken cancellationToken = default)
        {
            EnsureCanWrite();
            return System.IO.RandomAccess.WriteAsync(_handle, buffer, offset, cancellationToken);
        }

        public ValueTask<Stream> OpenReadRangeAsync(
            long offset,
            long length,
            CancellationToken cancellationToken = default)
        {
            EnsureCanRead();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            cancellationToken.ThrowIfCancellationRequested();

            long sourceLength = System.IO.RandomAccess.GetLength(_handle);
            long boundedLength = offset >= sourceLength
                ? 0
                : Math.Min(length, sourceLength - offset);

            return ValueTask.FromResult<Stream>(
                new BoundedRandomAccessReadStream(this, offset, boundedLength));
        }

        private void EnsureCanRead()
        {
            if (!CanReadAt)
            {
                throw new NotSupportedException("The stream does not support random-access reads.");
            }
        }

        private void EnsureCanWrite()
        {
            if (!CanWriteAt)
            {
                throw new NotSupportedException("The stream does not support random-access writes.");
            }
        }
    }
}
