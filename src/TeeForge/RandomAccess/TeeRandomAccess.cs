using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using TeeForge.Mirroring;
using TeeForge.RandomAccess.Internal;

namespace TeeForge.RandomAccess;

/// <summary>Discovers native position-independent I/O capabilities for streams.</summary>
public static class TeeRandomAccess
{
    private static readonly ConditionalWeakTable<FileStream, FileStreamRandomAccess> FileAdapters = new();

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
            var adapter = FileAdapters.GetValue(fileStream, static file => new FileStreamRandomAccess(file));
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
        // Mirrored ranges read every member through its positional capability.
        if (stream is TeeStream { CanReadAt: false } or TeeBufferedStream { CanReadAt: false })
        {
            return null;
        }

        if (stream is ITeeRangeReadSource rangeReadSource)
        {
            return rangeReadSource;
        }

        return stream is FileStream fileStream && fileStream.CanRead && fileStream.CanSeek
            ? FileAdapters.GetValue(fileStream, static file => new FileStreamRandomAccess(file))
            : null;
    }

    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "Adapters share the FileStream lifetime; the semaphore never creates a wait handle.")]
    private sealed class FileStreamRandomAccess : ITeeRandomAccessStream, ITeeRangeReadSource
    {
        private readonly FileStream _stream;
        private SafeFileHandle? _handle;
        // FileStream owns one read/write buffer, shared by all adapters for this file.
        private readonly SemaphoreSlim _flushGate = new(1, 1);

        internal FileStreamRandomAccess(FileStream stream)
        {
            _stream = stream;
        }

        public bool CanReadAt => _stream.CanRead;

        public bool CanWriteAt => _stream.CanWrite;

        public int ReadAt(Span<byte> buffer, long offset)
        {
            EnsureCanRead();
            SafeFileHandle handle = PrepareHandle();
            return System.IO.RandomAccess.Read(handle, buffer, offset);
        }

        public async ValueTask<int> ReadAtAsync(
            Memory<byte> buffer,
            long offset,
            CancellationToken cancellationToken = default)
        {
            EnsureCanRead();
            SafeFileHandle handle = await PrepareHandleAsync(cancellationToken).ConfigureAwait(false);
            return await System.IO.RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false);
        }

        public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
        {
            EnsureCanWrite();
            SafeFileHandle handle = PrepareHandle();
            System.IO.RandomAccess.Write(handle, buffer, offset);
        }

        public async ValueTask WriteAtAsync(
            ReadOnlyMemory<byte> buffer,
            long offset,
            CancellationToken cancellationToken = default)
        {
            EnsureCanWrite();
            SafeFileHandle handle = await PrepareHandleAsync(cancellationToken).ConfigureAwait(false);
            await System.IO.RandomAccess.WriteAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<Stream> OpenReadRangeAsync(
            long offset,
            long length,
            CancellationToken cancellationToken = default)
        {
            EnsureCanRead();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            cancellationToken.ThrowIfCancellationRequested();

            SafeFileHandle handle = await PrepareHandleAsync(cancellationToken).ConfigureAwait(false);
            long sourceLength = System.IO.RandomAccess.GetLength(handle);
            long boundedLength = offset >= sourceLength
                ? 0
                : Math.Min(length, sourceLength - offset);

            return new BoundedRandomAccessReadStream(this, offset, boundedLength);
        }

        private SafeFileHandle PrepareHandle()
        {
            _flushGate.Wait();
            try
            {
                if (_stream.CanWrite)
                {
                    _stream.Flush();
                }

                return _handle ??= _stream.SafeFileHandle;
            }
            finally
            {
                _flushGate.Release();
            }
        }

        private async ValueTask<SafeFileHandle> PrepareHandleAsync(CancellationToken cancellationToken)
        {
            await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_stream.CanWrite)
                {
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                return _handle ??= _stream.SafeFileHandle;
            }
            finally
            {
                _flushGate.Release();
            }
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
