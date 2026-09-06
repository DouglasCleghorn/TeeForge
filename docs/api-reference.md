# TeeForge public API reference

Version 0.1.0 · **unreleased** · .NET 10.

Generated from the analyzer-checked public API baseline. This is signature notation, not copyable C#: ! marks a non-null reference, ? marks a nullable reference, and -> introduces the return type. Use the [recipes](recipes/index.md) for runnable C# and the [specification](specification.md) for behavior.

## TeeForge.Broadcasting

```text
static TeeForge.Broadcasting.BroadcastCopyOptions.Default.get -> TeeForge.Broadcasting.BroadcastCopyOptions!
static TeeForge.Broadcasting.BroadcastPipeOptions.Default.get -> TeeForge.Broadcasting.BroadcastPipeOptions!
static TeeForge.Broadcasting.BroadcastStreamOptions.Default.get -> TeeForge.Broadcasting.BroadcastStreamOptions!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, params System.IO.Stream![]! destinations) -> System.Threading.Tasks.Task!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, TeeForge.Broadcasting.BroadcastCopyOptions! options, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Collections.Generic.IEnumerable<System.Security.Cryptography.HashAlgorithmName>! algorithms, params System.IO.Stream![]! destinations) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Collections.Generic.IEnumerable<System.Security.Cryptography.HashAlgorithmName>! algorithms, System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, TeeForge.Broadcasting.BroadcastCopyOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Collections.Generic.IEnumerable<System.Security.Cryptography.HashAlgorithmName>! algorithms, System.IO.Stream! destination, TeeForge.Broadcasting.BroadcastCopyOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Collections.Generic.IEnumerable<TeeForge.Hashing.TeeHashAlgorithm>! algorithms, params System.IO.Stream![]! destinations) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Collections.Generic.IEnumerable<TeeForge.Hashing.TeeHashAlgorithm>! algorithms, System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, TeeForge.Broadcasting.BroadcastCopyOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Collections.Generic.IEnumerable<TeeForge.Hashing.TeeHashAlgorithm>! algorithms, System.IO.Stream! destination, TeeForge.Broadcasting.BroadcastCopyOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Security.Cryptography.HashAlgorithmName algorithm, params System.IO.Stream![]! destinations) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Security.Cryptography.HashAlgorithmName algorithm, System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, TeeForge.Broadcasting.BroadcastCopyOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, System.Security.Cryptography.HashAlgorithmName algorithm, System.IO.Stream! destination, TeeForge.Broadcasting.BroadcastCopyOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, TeeForge.Hashing.TeeHashAlgorithm algorithm, params System.IO.Stream![]! destinations) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, TeeForge.Hashing.TeeHashAlgorithm algorithm, System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, TeeForge.Broadcasting.BroadcastCopyOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
static TeeForge.Broadcasting.StreamCopyExtensions.CopyToAsync(this System.IO.Stream! source, TeeForge.Hashing.TeeHashAlgorithm algorithm, System.IO.Stream! destination, TeeForge.Broadcasting.BroadcastCopyOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TeeForge.Hashing.TeeHashResults!>!
TeeForge.Broadcasting.BroadcastCopyDestinationException
TeeForge.Broadcasting.BroadcastCopyDestinationException.BroadcastCopyDestinationException(int destinationIndex, System.Exception! innerException) -> void
TeeForge.Broadcasting.BroadcastCopyDestinationException.DestinationIndex.get -> int
TeeForge.Broadcasting.BroadcastCopyFailureBehavior
TeeForge.Broadcasting.BroadcastCopyFailureBehavior.Continue = 1 -> TeeForge.Broadcasting.BroadcastCopyFailureBehavior
TeeForge.Broadcasting.BroadcastCopyFailureBehavior.Stop = 0 -> TeeForge.Broadcasting.BroadcastCopyFailureBehavior
TeeForge.Broadcasting.BroadcastCopyOptions
TeeForge.Broadcasting.BroadcastCopyOptions.BroadcastCopyOptions(int bufferSize = 4096, long pauseWriterThreshold = 65536, long resumeWriterThreshold = 32768, TeeForge.Broadcasting.BroadcastCopyFailureBehavior failureBehavior = TeeForge.Broadcasting.BroadcastCopyFailureBehavior.Stop) -> void
TeeForge.Broadcasting.BroadcastCopyOptions.BufferSize.get -> int
TeeForge.Broadcasting.BroadcastCopyOptions.FailureBehavior.get -> TeeForge.Broadcasting.BroadcastCopyFailureBehavior
TeeForge.Broadcasting.BroadcastCopyOptions.PauseWriterThreshold.get -> long
TeeForge.Broadcasting.BroadcastCopyOptions.ResumeWriterThreshold.get -> long
TeeForge.Broadcasting.BroadcastPipe
TeeForge.Broadcasting.BroadcastPipe.BroadcastPipe(int readerCount, TeeForge.Broadcasting.BroadcastPipeOptions! options) -> void
TeeForge.Broadcasting.BroadcastPipe.BroadcastPipe(int readerCount) -> void
TeeForge.Broadcasting.BroadcastPipe.ReaderCompletions.get -> System.Collections.Generic.IReadOnlyList<System.Threading.Tasks.Task<System.Exception?>!>!
TeeForge.Broadcasting.BroadcastPipe.Readers.get -> System.Collections.Generic.IReadOnlyList<System.IO.Pipelines.PipeReader!>!
TeeForge.Broadcasting.BroadcastPipe.Reset() -> void
TeeForge.Broadcasting.BroadcastPipe.Writer.get -> System.IO.Pipelines.PipeWriter!
TeeForge.Broadcasting.BroadcastPipeOptions
TeeForge.Broadcasting.BroadcastPipeOptions.BroadcastPipeOptions(System.Buffers.MemoryPool<byte>? pool = null, System.IO.Pipelines.PipeScheduler? readerScheduler = null, System.IO.Pipelines.PipeScheduler? writerScheduler = null, long pauseWriterThreshold = -1, long resumeWriterThreshold = -1, int minimumSegmentSize = -1, bool useSynchronizationContext = true, TeeForge.Broadcasting.BroadcastPipeReaderFailureBehavior readerFailureBehavior = TeeForge.Broadcasting.BroadcastPipeReaderFailureBehavior.Continue) -> void
TeeForge.Broadcasting.BroadcastPipeOptions.MinimumSegmentSize.get -> int
TeeForge.Broadcasting.BroadcastPipeOptions.PauseWriterThreshold.get -> long
TeeForge.Broadcasting.BroadcastPipeOptions.Pool.get -> System.Buffers.MemoryPool<byte>!
TeeForge.Broadcasting.BroadcastPipeOptions.ReaderFailureBehavior.get -> TeeForge.Broadcasting.BroadcastPipeReaderFailureBehavior
TeeForge.Broadcasting.BroadcastPipeOptions.ReaderScheduler.get -> System.IO.Pipelines.PipeScheduler!
TeeForge.Broadcasting.BroadcastPipeOptions.ResumeWriterThreshold.get -> long
TeeForge.Broadcasting.BroadcastPipeOptions.UseSynchronizationContext.get -> bool
TeeForge.Broadcasting.BroadcastPipeOptions.WriterScheduler.get -> System.IO.Pipelines.PipeScheduler!
TeeForge.Broadcasting.BroadcastPipeReaderFailureBehavior
TeeForge.Broadcasting.BroadcastPipeReaderFailureBehavior.CompletePipe = 1 -> TeeForge.Broadcasting.BroadcastPipeReaderFailureBehavior
TeeForge.Broadcasting.BroadcastPipeReaderFailureBehavior.Continue = 0 -> TeeForge.Broadcasting.BroadcastPipeReaderFailureBehavior
TeeForge.Broadcasting.BroadcastStream
TeeForge.Broadcasting.BroadcastStream.BroadcastStream(System.IO.Stream! source, int readerCount, TeeForge.Broadcasting.BroadcastStreamOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> void
TeeForge.Broadcasting.BroadcastStream.BytesBroadcast.get -> long
TeeForge.Broadcasting.BroadcastStream.Completion.get -> System.Threading.Tasks.Task!
TeeForge.Broadcasting.BroadcastStream.Dispose() -> void
TeeForge.Broadcasting.BroadcastStream.Readers.get -> System.Collections.Generic.IReadOnlyList<System.IO.Stream!>!
TeeForge.Broadcasting.BroadcastStreamOptions
TeeForge.Broadcasting.BroadcastStreamOptions.BroadcastStreamOptions(int bufferSize = 4096, long pauseWriterThreshold = 65536, long resumeWriterThreshold = 32768, bool leaveOpen = false) -> void
TeeForge.Broadcasting.BroadcastStreamOptions.BufferSize.get -> int
TeeForge.Broadcasting.BroadcastStreamOptions.LeaveOpen.get -> bool
TeeForge.Broadcasting.BroadcastStreamOptions.PauseWriterThreshold.get -> long
TeeForge.Broadcasting.BroadcastStreamOptions.ResumeWriterThreshold.get -> long
TeeForge.Broadcasting.StreamCopyExtensions
virtual TeeForge.Broadcasting.BroadcastStream.Dispose(bool disposing) -> void
virtual TeeForge.Broadcasting.BroadcastStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
```

## TeeForge.Composition

```text
override TeeForge.Composition.HandoffStream.CanRead.get -> bool
override TeeForge.Composition.HandoffStream.CanSeek.get -> bool
override TeeForge.Composition.HandoffStream.CanTimeout.get -> bool
override TeeForge.Composition.HandoffStream.CanWrite.get -> bool
override TeeForge.Composition.HandoffStream.CopyTo(System.IO.Stream! destination, int bufferSize) -> void
override TeeForge.Composition.HandoffStream.CopyToAsync(System.IO.Stream! destination, int bufferSize, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Composition.HandoffStream.Dispose(bool disposing) -> void
override TeeForge.Composition.HandoffStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
override TeeForge.Composition.HandoffStream.Flush() -> void
override TeeForge.Composition.HandoffStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Composition.HandoffStream.Length.get -> long
override TeeForge.Composition.HandoffStream.Position.get -> long
override TeeForge.Composition.HandoffStream.Position.set -> void
override TeeForge.Composition.HandoffStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.Composition.HandoffStream.Read(System.Span<byte> buffer) -> int
override TeeForge.Composition.HandoffStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.Composition.HandoffStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.Composition.HandoffStream.ReadByte() -> int
override TeeForge.Composition.HandoffStream.ReadTimeout.get -> int
override TeeForge.Composition.HandoffStream.ReadTimeout.set -> void
override TeeForge.Composition.HandoffStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.Composition.HandoffStream.SetLength(long value) -> void
override TeeForge.Composition.HandoffStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.Composition.HandoffStream.Write(System.ReadOnlySpan<byte> buffer) -> void
override TeeForge.Composition.HandoffStream.WriteAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Composition.HandoffStream.WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
override TeeForge.Composition.HandoffStream.WriteByte(byte value) -> void
override TeeForge.Composition.HandoffStream.WriteTimeout.get -> int
override TeeForge.Composition.HandoffStream.WriteTimeout.set -> void
override TeeForge.Composition.MigratingStream.CanRead.get -> bool
override TeeForge.Composition.MigratingStream.CanSeek.get -> bool
override TeeForge.Composition.MigratingStream.CanWrite.get -> bool
override TeeForge.Composition.MigratingStream.CopyTo(System.IO.Stream! destination, int bufferSize) -> void
override TeeForge.Composition.MigratingStream.CopyToAsync(System.IO.Stream! destination, int bufferSize, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Composition.MigratingStream.Dispose(bool disposing) -> void
override TeeForge.Composition.MigratingStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
override TeeForge.Composition.MigratingStream.Flush() -> void
override TeeForge.Composition.MigratingStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Composition.MigratingStream.Length.get -> long
override TeeForge.Composition.MigratingStream.Position.get -> long
override TeeForge.Composition.MigratingStream.Position.set -> void
override TeeForge.Composition.MigratingStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.Composition.MigratingStream.Read(System.Span<byte> buffer) -> int
override TeeForge.Composition.MigratingStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.Composition.MigratingStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.Composition.MigratingStream.ReadByte() -> int
override TeeForge.Composition.MigratingStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.Composition.MigratingStream.SetLength(long value) -> void
override TeeForge.Composition.MigratingStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.Composition.MigratingStream.Write(System.ReadOnlySpan<byte> buffer) -> void
override TeeForge.Composition.MigratingStream.WriteAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Composition.MigratingStream.WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
override TeeForge.Composition.MigratingStream.WriteByte(byte value) -> void
static TeeForge.Composition.MigratingStreamOptions.Default.get -> TeeForge.Composition.MigratingStreamOptions!
TeeForge.Composition.HandoffStream
TeeForge.Composition.HandoffStream.CanReadAt.get -> bool
TeeForge.Composition.HandoffStream.CanWriteAt.get -> bool
TeeForge.Composition.HandoffStream.Handoff(System.IO.Stream! stream) -> void
TeeForge.Composition.HandoffStream.HandoffAsync(System.IO.Stream! stream, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.Composition.HandoffStream.HandoffStream(System.IO.Stream! stream, bool leaveOpen = false) -> void
TeeForge.Composition.HandoffStream.LeaveOpen.get -> bool
TeeForge.Composition.HandoffStream.MigrateAsync(System.IO.Stream! destination, TeeForge.Composition.MigratingStreamOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
TeeForge.Composition.HandoffStream.ReadAt(System.Span<byte> buffer, long offset) -> int
TeeForge.Composition.HandoffStream.ReadAtAsync(System.Memory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
TeeForge.Composition.HandoffStream.WriteAt(System.ReadOnlySpan<byte> buffer, long offset) -> void
TeeForge.Composition.HandoffStream.WriteAtAsync(System.ReadOnlyMemory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.Composition.MigratingStream
TeeForge.Composition.MigratingStream.CanReadAt.get -> bool
TeeForge.Composition.MigratingStream.CanWriteAt.get -> bool
TeeForge.Composition.MigratingStream.MigratingStream(System.IO.Stream! source, System.IO.Stream! destination, TeeForge.Composition.MigratingStreamOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> void
TeeForge.Composition.MigratingStream.MigrationCompletion.get -> System.Threading.Tasks.Task!
TeeForge.Composition.MigratingStream.Options.get -> TeeForge.Composition.MigratingStreamOptions!
TeeForge.Composition.MigratingStream.ReadAt(System.Span<byte> buffer, long offset) -> int
TeeForge.Composition.MigratingStream.ReadAtAsync(System.Memory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
TeeForge.Composition.MigratingStream.WriteAt(System.ReadOnlySpan<byte> buffer, long offset) -> void
TeeForge.Composition.MigratingStream.WriteAtAsync(System.ReadOnlyMemory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.Composition.MigratingStreamOptions
TeeForge.Composition.MigratingStreamOptions.BufferSize.get -> int
TeeForge.Composition.MigratingStreamOptions.LeaveDestinationOpen.get -> bool
TeeForge.Composition.MigratingStreamOptions.LeaveSourceOpen.get -> bool
TeeForge.Composition.MigratingStreamOptions.MigratingStreamOptions(bool leaveSourceOpen = false, bool leaveDestinationOpen = false, bool truncateSourceOnCompletion = false, int bufferSize = 81920) -> void
TeeForge.Composition.MigratingStreamOptions.TruncateSourceOnCompletion.get -> bool
```

## TeeForge.ErasureCoding

```text
const TeeForge.ErasureCoding.ErasureStreamOptions.DefaultBlockSize = 131072 -> int
override TeeForge.ErasureCoding.ErasureStream.CanRead.get -> bool
override TeeForge.ErasureCoding.ErasureStream.CanSeek.get -> bool
override TeeForge.ErasureCoding.ErasureStream.CanWrite.get -> bool
override TeeForge.ErasureCoding.ErasureStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
override TeeForge.ErasureCoding.ErasureStream.Flush() -> void
override TeeForge.ErasureCoding.ErasureStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.ErasureCoding.ErasureStream.Length.get -> long
override TeeForge.ErasureCoding.ErasureStream.Position.get -> long
override TeeForge.ErasureCoding.ErasureStream.Position.set -> void
override TeeForge.ErasureCoding.ErasureStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.ErasureCoding.ErasureStream.Read(System.Span<byte> buffer) -> int
override TeeForge.ErasureCoding.ErasureStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.ErasureCoding.ErasureStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.ErasureCoding.ErasureStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.ErasureCoding.ErasureStream.SetLength(long value) -> void
override TeeForge.ErasureCoding.ErasureStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.ErasureCoding.ErasureStream.Write(System.ReadOnlySpan<byte> buffer) -> void
override TeeForge.ErasureCoding.ErasureStream.WriteAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.ErasureCoding.ErasureStream.WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
static TeeForge.ErasureCoding.ErasureStream.Create(System.Collections.Generic.IReadOnlyList<System.IO.Stream!>! members, int dataShardCount, int parityShardCount, long logicalLength, int blockSize = 131072, TeeForge.ErasureCoding.ErasureStreamOptions? options = null) -> TeeForge.ErasureCoding.ErasureStream!
static TeeForge.ErasureCoding.ErasureStream.Open(System.Collections.Generic.IReadOnlyList<System.IO.Stream?>! members, int dataShardCount, int parityShardCount, long logicalLength, int blockSize = 131072, TeeForge.ErasureCoding.ErasureStreamOptions? options = null) -> TeeForge.ErasureCoding.ErasureStream!
static TeeForge.ErasureCoding.ErasureStreamOptions.Default.get -> TeeForge.ErasureCoding.ErasureStreamOptions!
TeeForge.ErasureCoding.ErasureStream
TeeForge.ErasureCoding.ErasureStream.BlockSize.get -> int
TeeForge.ErasureCoding.ErasureStream.CanReadAt.get -> bool
TeeForge.ErasureCoding.ErasureStream.CanWriteAt.get -> bool
TeeForge.ErasureCoding.ErasureStream.CompleteAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.ErasureCoding.ErasureStream.DataShardCount.get -> int
TeeForge.ErasureCoding.ErasureStream.LogicalLength.get -> long
TeeForge.ErasureCoding.ErasureStream.MissingMemberPositions.get -> System.Collections.Generic.IReadOnlyList<int>!
TeeForge.ErasureCoding.ErasureStream.ParityShardCount.get -> int
TeeForge.ErasureCoding.ErasureStream.ReadAt(System.Span<byte> buffer, long offset) -> int
TeeForge.ErasureCoding.ErasureStream.ReadAtAsync(System.Memory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
TeeForge.ErasureCoding.ErasureStream.WriteAt(System.ReadOnlySpan<byte> buffer, long offset) -> void
TeeForge.ErasureCoding.ErasureStream.WriteAtAsync(System.ReadOnlyMemory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.ErasureCoding.ErasureStreamOptions
TeeForge.ErasureCoding.ErasureStreamOptions.ErasureStreamOptions(bool requireAllMembers = true, bool leaveOpen = false, long maximumCacheBytes = 67108864, int readAheadBlockCount = 1) -> void
TeeForge.ErasureCoding.ErasureStreamOptions.LeaveOpen.get -> bool
TeeForge.ErasureCoding.ErasureStreamOptions.MaximumCacheBytes.get -> long
TeeForge.ErasureCoding.ErasureStreamOptions.ReadAheadBlockCount.get -> int
TeeForge.ErasureCoding.ErasureStreamOptions.RequireAllMembers.get -> bool
```

## TeeForge.Hashing

```text
~override TeeForge.Hashing.TeeHashAlgorithmId.Equals(object obj) -> bool
override TeeForge.Hashing.TeeHashAlgorithmId.GetHashCode() -> int
override TeeForge.Hashing.TeeHashAlgorithmId.ToString() -> string!
override TeeForge.Hashing.TeeHashStream.Dispose(bool disposing) -> void
static TeeForge.Hashing.TeeHashAlgorithmAdapter.ToTeeHashAlgorithm(System.Security.Cryptography.HashAlgorithmName algorithm) -> TeeForge.Hashing.TeeHashAlgorithm
static TeeForge.Hashing.TeeHashAlgorithmAdapter.TryToHashAlgorithmName(TeeForge.Hashing.TeeHashAlgorithm algorithm, out System.Security.Cryptography.HashAlgorithmName result) -> bool
static TeeForge.Hashing.TeeHashAlgorithmAdapter.TryToTeeHashAlgorithm(System.Security.Cryptography.HashAlgorithmName algorithm, out TeeForge.Hashing.TeeHashAlgorithm result) -> bool
static TeeForge.Hashing.TeeHashAlgorithmId.implicit operator TeeForge.Hashing.TeeHashAlgorithmId(System.Security.Cryptography.HashAlgorithmName algorithm) -> TeeForge.Hashing.TeeHashAlgorithmId
static TeeForge.Hashing.TeeHashAlgorithmId.implicit operator TeeForge.Hashing.TeeHashAlgorithmId(TeeForge.Hashing.TeeHashAlgorithm algorithm) -> TeeForge.Hashing.TeeHashAlgorithmId
static TeeForge.Hashing.TeeHashAlgorithmId.operator !=(TeeForge.Hashing.TeeHashAlgorithmId left, TeeForge.Hashing.TeeHashAlgorithmId right) -> bool
static TeeForge.Hashing.TeeHashAlgorithmId.operator ==(TeeForge.Hashing.TeeHashAlgorithmId left, TeeForge.Hashing.TeeHashAlgorithmId right) -> bool
TeeForge.Hashing.BroadcastHashStream
TeeForge.Hashing.BroadcastHashStream.BroadcastHashStream(System.Collections.Generic.IEnumerable<System.Security.Cryptography.HashAlgorithmName>! algorithms, out TeeForge.Hashing.TeeHashResults! results, System.IO.Stream! source, int readerCount, TeeForge.Broadcasting.BroadcastStreamOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> void
TeeForge.Hashing.BroadcastHashStream.BroadcastHashStream(System.Collections.Generic.IEnumerable<TeeForge.Hashing.TeeHashAlgorithm>! algorithms, out TeeForge.Hashing.TeeHashResults! results, System.IO.Stream! source, int readerCount, TeeForge.Broadcasting.BroadcastStreamOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> void
TeeForge.Hashing.BroadcastHashStream.BroadcastHashStream(System.Security.Cryptography.HashAlgorithmName algorithm, out TeeForge.Hashing.TeeHashResults! results, System.IO.Stream! source, int readerCount, TeeForge.Broadcasting.BroadcastStreamOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> void
TeeForge.Hashing.BroadcastHashStream.BroadcastHashStream(TeeForge.Hashing.TeeHashAlgorithm algorithm, out TeeForge.Hashing.TeeHashResults! results, System.IO.Stream! source, int readerCount, TeeForge.Broadcasting.BroadcastStreamOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> void
TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.Crc32 = 9 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.Crc64 = 10 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.MD5 = 1 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.SHA1 = 2 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.SHA256 = 3 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.SHA3_256 = 6 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.SHA3_384 = 7 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.SHA3_512 = 8 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.SHA384 = 4 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.SHA512 = 5 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.XxHash128 = 14 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.XxHash3 = 13 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.XxHash32 = 11 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithm.XxHash64 = 12 -> TeeForge.Hashing.TeeHashAlgorithm
TeeForge.Hashing.TeeHashAlgorithmAdapter
TeeForge.Hashing.TeeHashAlgorithmId
TeeForge.Hashing.TeeHashAlgorithmId.Equals(TeeForge.Hashing.TeeHashAlgorithmId other) -> bool
TeeForge.Hashing.TeeHashAlgorithmId.IsCryptographic.get -> bool
TeeForge.Hashing.TeeHashAlgorithmId.Name.get -> string!
TeeForge.Hashing.TeeHashAlgorithmId.TeeHashAlgorithmId() -> void
TeeForge.Hashing.TeeHashAlgorithmId.TeeHashAlgorithmId(System.Security.Cryptography.HashAlgorithmName algorithm) -> void
TeeForge.Hashing.TeeHashAlgorithmId.TeeHashAlgorithmId(TeeForge.Hashing.TeeHashAlgorithm algorithm) -> void
TeeForge.Hashing.TeeHashResult
TeeForge.Hashing.TeeHashResult.Algorithm.get -> TeeForge.Hashing.TeeHashAlgorithmId
TeeForge.Hashing.TeeHashResult.Base32.get -> string!
TeeForge.Hashing.TeeHashResult.Base64.get -> string!
TeeForge.Hashing.TeeHashResult.Base64Url.get -> string!
TeeForge.Hashing.TeeHashResult.Bytes.get -> System.ReadOnlyMemory<byte>
TeeForge.Hashing.TeeHashResult.Hex.get -> string!
TeeForge.Hashing.TeeHashResult.TeeHashResult(TeeForge.Hashing.TeeHashAlgorithmId algorithm, System.ReadOnlySpan<byte> bytes) -> void
TeeForge.Hashing.TeeHashResults
TeeForge.Hashing.TeeHashResults.ContainsKey(TeeForge.Hashing.TeeHashAlgorithmId key) -> bool
TeeForge.Hashing.TeeHashResults.Count.get -> int
TeeForge.Hashing.TeeHashResults.GetEnumerator() -> System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<TeeForge.Hashing.TeeHashAlgorithmId, TeeForge.Hashing.TeeHashResult!>>!
TeeForge.Hashing.TeeHashResults.IsComplete.get -> bool
TeeForge.Hashing.TeeHashResults.Keys.get -> System.Collections.Generic.IEnumerable<TeeForge.Hashing.TeeHashAlgorithmId>!
TeeForge.Hashing.TeeHashResults.this[TeeForge.Hashing.TeeHashAlgorithmId key].get -> TeeForge.Hashing.TeeHashResult!
TeeForge.Hashing.TeeHashResults.TryGetValue(TeeForge.Hashing.TeeHashAlgorithmId key, out TeeForge.Hashing.TeeHashResult! value) -> bool
TeeForge.Hashing.TeeHashResults.Values.get -> System.Collections.Generic.IEnumerable<TeeForge.Hashing.TeeHashResult!>!
TeeForge.Hashing.TeeHashStream
TeeForge.Hashing.TeeHashStream.TeeHashStream(System.Collections.Generic.IEnumerable<System.Security.Cryptography.HashAlgorithmName>! algorithms, out TeeForge.Hashing.TeeHashResults! results, System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, TeeForge.Mirroring.TeeBufferedStreamOptions? options = null) -> void
TeeForge.Hashing.TeeHashStream.TeeHashStream(System.Collections.Generic.IEnumerable<TeeForge.Hashing.TeeHashAlgorithm>! algorithms, out TeeForge.Hashing.TeeHashResults! results, System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, TeeForge.Mirroring.TeeBufferedStreamOptions? options = null) -> void
TeeForge.Hashing.TeeHashStream.TeeHashStream(System.Security.Cryptography.HashAlgorithmName algorithm, out TeeForge.Hashing.TeeHashResults! results, params System.IO.Stream![]! destinations) -> void
TeeForge.Hashing.TeeHashStream.TeeHashStream(TeeForge.Hashing.TeeHashAlgorithm algorithm, out TeeForge.Hashing.TeeHashResults! results, params System.IO.Stream![]! destinations) -> void
```

## TeeForge.Mirroring

```text
~override TeeForge.Mirroring.TeeStreamMismatch.Equals(object obj) -> bool
~override TeeForge.Mirroring.TeeStreamMismatch.ToString() -> string
override TeeForge.Mirroring.ReplicaStream.CanRead.get -> bool
override TeeForge.Mirroring.ReplicaStream.CanSeek.get -> bool
override TeeForge.Mirroring.ReplicaStream.CanTimeout.get -> bool
override TeeForge.Mirroring.ReplicaStream.CanWrite.get -> bool
override TeeForge.Mirroring.ReplicaStream.Dispose(bool disposing) -> void
override TeeForge.Mirroring.ReplicaStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
override TeeForge.Mirroring.ReplicaStream.Flush() -> void
override TeeForge.Mirroring.ReplicaStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Mirroring.ReplicaStream.Length.get -> long
override TeeForge.Mirroring.ReplicaStream.Position.get -> long
override TeeForge.Mirroring.ReplicaStream.Position.set -> void
override TeeForge.Mirroring.ReplicaStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.Mirroring.ReplicaStream.Read(System.Span<byte> buffer) -> int
override TeeForge.Mirroring.ReplicaStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.Mirroring.ReplicaStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.Mirroring.ReplicaStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.Mirroring.ReplicaStream.SetLength(long value) -> void
override TeeForge.Mirroring.ReplicaStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.Mirroring.ReplicaStream.Write(System.ReadOnlySpan<byte> buffer) -> void
override TeeForge.Mirroring.ReplicaStream.WriteAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Mirroring.ReplicaStream.WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
override TeeForge.Mirroring.ReplicaStream.WriteByte(byte value) -> void
override TeeForge.Mirroring.ReplicaStream.WriteTimeout.get -> int
override TeeForge.Mirroring.ReplicaStream.WriteTimeout.set -> void
override TeeForge.Mirroring.TeeBufferedStream.BeginRead(byte[]! buffer, int offset, int count, System.AsyncCallback? callback, object? state) -> System.IAsyncResult!
override TeeForge.Mirroring.TeeBufferedStream.BeginWrite(byte[]! buffer, int offset, int count, System.AsyncCallback? callback, object? state) -> System.IAsyncResult!
override TeeForge.Mirroring.TeeBufferedStream.CanRead.get -> bool
override TeeForge.Mirroring.TeeBufferedStream.CanSeek.get -> bool
override TeeForge.Mirroring.TeeBufferedStream.CanWrite.get -> bool
override TeeForge.Mirroring.TeeBufferedStream.CopyTo(System.IO.Stream! destination, int bufferSize) -> void
override TeeForge.Mirroring.TeeBufferedStream.CopyToAsync(System.IO.Stream! destination, int bufferSize, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Mirroring.TeeBufferedStream.Dispose(bool disposing) -> void
override TeeForge.Mirroring.TeeBufferedStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
override TeeForge.Mirroring.TeeBufferedStream.EndRead(System.IAsyncResult! asyncResult) -> int
override TeeForge.Mirroring.TeeBufferedStream.EndWrite(System.IAsyncResult! asyncResult) -> void
override TeeForge.Mirroring.TeeBufferedStream.Flush() -> void
override TeeForge.Mirroring.TeeBufferedStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Mirroring.TeeBufferedStream.Length.get -> long
override TeeForge.Mirroring.TeeBufferedStream.Position.get -> long
override TeeForge.Mirroring.TeeBufferedStream.Position.set -> void
override TeeForge.Mirroring.TeeBufferedStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.Mirroring.TeeBufferedStream.Read(System.Span<byte> buffer) -> int
override TeeForge.Mirroring.TeeBufferedStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.Mirroring.TeeBufferedStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.Mirroring.TeeBufferedStream.ReadByte() -> int
override TeeForge.Mirroring.TeeBufferedStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.Mirroring.TeeBufferedStream.SetLength(long value) -> void
override TeeForge.Mirroring.TeeBufferedStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.Mirroring.TeeBufferedStream.Write(System.ReadOnlySpan<byte> buffer) -> void
override TeeForge.Mirroring.TeeBufferedStream.WriteAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Mirroring.TeeBufferedStream.WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
override TeeForge.Mirroring.TeeBufferedStream.WriteByte(byte value) -> void
override TeeForge.Mirroring.TeeStream.CanRead.get -> bool
override TeeForge.Mirroring.TeeStream.CanSeek.get -> bool
override TeeForge.Mirroring.TeeStream.CanTimeout.get -> bool
override TeeForge.Mirroring.TeeStream.CanWrite.get -> bool
override TeeForge.Mirroring.TeeStream.Dispose(bool disposing) -> void
override TeeForge.Mirroring.TeeStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
override TeeForge.Mirroring.TeeStream.Flush() -> void
override TeeForge.Mirroring.TeeStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Mirroring.TeeStream.Length.get -> long
override TeeForge.Mirroring.TeeStream.Position.get -> long
override TeeForge.Mirroring.TeeStream.Position.set -> void
override TeeForge.Mirroring.TeeStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.Mirroring.TeeStream.Read(System.Span<byte> buffer) -> int
override TeeForge.Mirroring.TeeStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.Mirroring.TeeStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.Mirroring.TeeStream.ReadTimeout.get -> int
override TeeForge.Mirroring.TeeStream.ReadTimeout.set -> void
override TeeForge.Mirroring.TeeStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.Mirroring.TeeStream.SetLength(long value) -> void
override TeeForge.Mirroring.TeeStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.Mirroring.TeeStream.Write(System.ReadOnlySpan<byte> buffer) -> void
override TeeForge.Mirroring.TeeStream.WriteAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Mirroring.TeeStream.WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
override TeeForge.Mirroring.TeeStream.WriteTimeout.get -> int
override TeeForge.Mirroring.TeeStream.WriteTimeout.set -> void
override TeeForge.Mirroring.TeeStreamMismatch.GetHashCode() -> int
static TeeForge.Mirroring.ReplicaStreamOptions.Default.get -> TeeForge.Mirroring.ReplicaStreamOptions!
static TeeForge.Mirroring.TeeBufferedStreamOptions.Default.get -> TeeForge.Mirroring.TeeBufferedStreamOptions!
static TeeForge.Mirroring.TeeStreamMismatch.operator !=(TeeForge.Mirroring.TeeStreamMismatch left, TeeForge.Mirroring.TeeStreamMismatch right) -> bool
static TeeForge.Mirroring.TeeStreamMismatch.operator ==(TeeForge.Mirroring.TeeStreamMismatch left, TeeForge.Mirroring.TeeStreamMismatch right) -> bool
static TeeForge.Mirroring.TeeStreamOptions.Default.get -> TeeForge.Mirroring.TeeStreamOptions!
TeeForge.Mirroring.ReplicaStream
TeeForge.Mirroring.ReplicaStream.ReplicaStream(params System.IO.Stream![]! replicas) -> void
TeeForge.Mirroring.ReplicaStream.ReplicaStream(System.Collections.Generic.IEnumerable<System.IO.Stream!>! replicas, TeeForge.Mirroring.ReplicaStreamOptions? options = null) -> void
TeeForge.Mirroring.ReplicaStream.ReplicaStream(TeeForge.Mirroring.ReplicaStreamOptions! options, params System.IO.Stream![]! replicas) -> void
TeeForge.Mirroring.ReplicaStreamOptions
TeeForge.Mirroring.ReplicaStreamOptions.LeaveOpen.get -> bool
TeeForge.Mirroring.ReplicaStreamOptions.ReplicaStreamOptions(TeeForge.Mirroring.TeeStreamSynchronousMode synchronousMode = TeeForge.Mirroring.TeeStreamSynchronousMode.Sequential, bool leaveOpen = false) -> void
TeeForge.Mirroring.ReplicaStreamOptions.SynchronousMode.get -> TeeForge.Mirroring.TeeStreamSynchronousMode
TeeForge.Mirroring.TeeBufferedStream
TeeForge.Mirroring.TeeBufferedStream.BufferSize.get -> int
TeeForge.Mirroring.TeeBufferedStream.CanReadAt.get -> bool
TeeForge.Mirroring.TeeBufferedStream.CanWriteAt.get -> bool
TeeForge.Mirroring.TeeBufferedStream.OpenReadRangeAsync(long offset, long length, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<System.IO.Stream!>
TeeForge.Mirroring.TeeBufferedStream.ReadAt(System.Span<byte> buffer, long offset) -> int
TeeForge.Mirroring.TeeBufferedStream.ReadAtAsync(System.Memory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
TeeForge.Mirroring.TeeBufferedStream.TeeBufferedStream(int bufferSize, params System.IO.Stream![]! destinations) -> void
TeeForge.Mirroring.TeeBufferedStream.TeeBufferedStream(params System.IO.Stream![]! destinations) -> void
TeeForge.Mirroring.TeeBufferedStream.TeeBufferedStream(System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, TeeForge.Mirroring.TeeBufferedStreamOptions? options = null) -> void
TeeForge.Mirroring.TeeBufferedStream.TeeBufferedStream(TeeForge.Mirroring.TeeBufferedStreamOptions! options, params System.IO.Stream![]! destinations) -> void
TeeForge.Mirroring.TeeBufferedStream.UnderlyingStream.get -> TeeForge.Mirroring.TeeStream!
TeeForge.Mirroring.TeeBufferedStream.WriteAt(System.ReadOnlySpan<byte> buffer, long offset) -> void
TeeForge.Mirroring.TeeBufferedStream.WriteAtAsync(System.ReadOnlyMemory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.Mirroring.TeeBufferedStreamOptions
TeeForge.Mirroring.TeeBufferedStreamOptions.BufferSize.get -> int
TeeForge.Mirroring.TeeBufferedStreamOptions.TeeBufferedStreamOptions(TeeForge.Mirroring.TeeStreamMismatchBehavior mismatchBehavior = TeeForge.Mirroring.TeeStreamMismatchBehavior.ThrowAndContinue, TeeForge.Mirroring.TeeStreamSynchronousMode synchronousMode = TeeForge.Mirroring.TeeStreamSynchronousMode.Sequential, bool leaveOpen = false, int bufferSize = 4096) -> void
TeeForge.Mirroring.TeeStream
TeeForge.Mirroring.TeeStream.CanReadAt.get -> bool
TeeForge.Mirroring.TeeStream.CanWriteAt.get -> bool
TeeForge.Mirroring.TeeStream.OpenReadRangeAsync(long offset, long length, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<System.IO.Stream!>
TeeForge.Mirroring.TeeStream.ReadAt(System.Span<byte> buffer, long offset) -> int
TeeForge.Mirroring.TeeStream.ReadAtAsync(System.Memory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
TeeForge.Mirroring.TeeStream.TeeStream(params System.IO.Stream![]! destinations) -> void
TeeForge.Mirroring.TeeStream.TeeStream(System.Collections.Generic.IEnumerable<System.IO.Stream!>! destinations, TeeForge.Mirroring.TeeStreamOptions? options = null) -> void
TeeForge.Mirroring.TeeStream.TeeStream(TeeForge.Mirroring.TeeStreamOptions! options, params System.IO.Stream![]! destinations) -> void
TeeForge.Mirroring.TeeStream.WriteAt(System.ReadOnlySpan<byte> buffer, long offset) -> void
TeeForge.Mirroring.TeeStream.WriteAtAsync(System.ReadOnlyMemory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.Mirroring.TeeStreamConsistencyException
TeeForge.Mirroring.TeeStreamConsistencyException.Mismatches.get -> System.Collections.Generic.IReadOnlyList<TeeForge.Mirroring.TeeStreamMismatch>!
TeeForge.Mirroring.TeeStreamConsistencyException.OperationName.get -> string!
TeeForge.Mirroring.TeeStreamConsistencyException.PrimaryResult.get -> long?
TeeForge.Mirroring.TeeStreamConsistencyException.TeeStreamConsistencyException(string! operationName, long? primaryResult, System.Collections.Generic.IEnumerable<TeeForge.Mirroring.TeeStreamMismatch>! mismatches) -> void
TeeForge.Mirroring.TeeStreamMismatch
TeeForge.Mirroring.TeeStreamMismatch.Deconstruct(out int DestinationIndex, out long? DestinationResult, out long? FirstDifferingByteOffset) -> void
TeeForge.Mirroring.TeeStreamMismatch.DestinationIndex.get -> int
TeeForge.Mirroring.TeeStreamMismatch.DestinationIndex.init -> void
TeeForge.Mirroring.TeeStreamMismatch.DestinationResult.get -> long?
TeeForge.Mirroring.TeeStreamMismatch.DestinationResult.init -> void
TeeForge.Mirroring.TeeStreamMismatch.Equals(TeeForge.Mirroring.TeeStreamMismatch other) -> bool
TeeForge.Mirroring.TeeStreamMismatch.FirstDifferingByteOffset.get -> long?
TeeForge.Mirroring.TeeStreamMismatch.FirstDifferingByteOffset.init -> void
TeeForge.Mirroring.TeeStreamMismatch.TeeStreamMismatch() -> void
TeeForge.Mirroring.TeeStreamMismatch.TeeStreamMismatch(int DestinationIndex, long? DestinationResult, long? FirstDifferingByteOffset) -> void
TeeForge.Mirroring.TeeStreamMismatchBehavior
TeeForge.Mirroring.TeeStreamMismatchBehavior.ThrowAndContinue = 0 -> TeeForge.Mirroring.TeeStreamMismatchBehavior
TeeForge.Mirroring.TeeStreamMismatchBehavior.ThrowAndFault = 1 -> TeeForge.Mirroring.TeeStreamMismatchBehavior
TeeForge.Mirroring.TeeStreamMismatchBehavior.UsePrimary = 2 -> TeeForge.Mirroring.TeeStreamMismatchBehavior
TeeForge.Mirroring.TeeStreamOptions
TeeForge.Mirroring.TeeStreamOptions.LeaveOpen.get -> bool
TeeForge.Mirroring.TeeStreamOptions.MismatchBehavior.get -> TeeForge.Mirroring.TeeStreamMismatchBehavior
TeeForge.Mirroring.TeeStreamOptions.SynchronousMode.get -> TeeForge.Mirroring.TeeStreamSynchronousMode
TeeForge.Mirroring.TeeStreamOptions.TeeStreamOptions(TeeForge.Mirroring.TeeStreamMismatchBehavior mismatchBehavior = TeeForge.Mirroring.TeeStreamMismatchBehavior.ThrowAndContinue, TeeForge.Mirroring.TeeStreamSynchronousMode synchronousMode = TeeForge.Mirroring.TeeStreamSynchronousMode.Sequential, bool leaveOpen = false) -> void
TeeForge.Mirroring.TeeStreamSynchronousMode
TeeForge.Mirroring.TeeStreamSynchronousMode.Concurrent = 1 -> TeeForge.Mirroring.TeeStreamSynchronousMode
TeeForge.Mirroring.TeeStreamSynchronousMode.Sequential = 0 -> TeeForge.Mirroring.TeeStreamSynchronousMode
```

## TeeForge.Networking

```text
const TeeForge.Networking.MultipathStreamOptions.DefaultFramePayloadSize = 16384 -> int
override TeeForge.Networking.MultipathReceiverStream.CanRead.get -> bool
override TeeForge.Networking.MultipathReceiverStream.CanSeek.get -> bool
override TeeForge.Networking.MultipathReceiverStream.CanWrite.get -> bool
override TeeForge.Networking.MultipathReceiverStream.Dispose(bool disposing) -> void
override TeeForge.Networking.MultipathReceiverStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
override TeeForge.Networking.MultipathReceiverStream.Flush() -> void
override TeeForge.Networking.MultipathReceiverStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Networking.MultipathReceiverStream.Length.get -> long
override TeeForge.Networking.MultipathReceiverStream.Position.get -> long
override TeeForge.Networking.MultipathReceiverStream.Position.set -> void
override TeeForge.Networking.MultipathReceiverStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.Networking.MultipathReceiverStream.Read(System.Span<byte> buffer) -> int
override TeeForge.Networking.MultipathReceiverStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.Networking.MultipathReceiverStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.Networking.MultipathReceiverStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.Networking.MultipathReceiverStream.SetLength(long value) -> void
override TeeForge.Networking.MultipathReceiverStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.Networking.MultipathSenderStream.CanRead.get -> bool
override TeeForge.Networking.MultipathSenderStream.CanSeek.get -> bool
override TeeForge.Networking.MultipathSenderStream.CanWrite.get -> bool
override TeeForge.Networking.MultipathSenderStream.Dispose(bool disposing) -> void
override TeeForge.Networking.MultipathSenderStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
override TeeForge.Networking.MultipathSenderStream.Flush() -> void
override TeeForge.Networking.MultipathSenderStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Networking.MultipathSenderStream.Length.get -> long
override TeeForge.Networking.MultipathSenderStream.Position.get -> long
override TeeForge.Networking.MultipathSenderStream.Position.set -> void
override TeeForge.Networking.MultipathSenderStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.Networking.MultipathSenderStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.Networking.MultipathSenderStream.SetLength(long value) -> void
override TeeForge.Networking.MultipathSenderStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.Networking.MultipathSenderStream.Write(System.ReadOnlySpan<byte> buffer) -> void
override TeeForge.Networking.MultipathSenderStream.WriteAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Networking.MultipathSenderStream.WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
override TeeForge.Networking.NamedQuicStream.CanRead.get -> bool
override TeeForge.Networking.NamedQuicStream.CanSeek.get -> bool
override TeeForge.Networking.NamedQuicStream.CanWrite.get -> bool
override TeeForge.Networking.NamedQuicStream.DisposeAsync() -> System.Threading.Tasks.ValueTask
override TeeForge.Networking.NamedQuicStream.Flush() -> void
override TeeForge.Networking.NamedQuicStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Networking.NamedQuicStream.Length.get -> long
override TeeForge.Networking.NamedQuicStream.Position.get -> long
override TeeForge.Networking.NamedQuicStream.Position.set -> void
override TeeForge.Networking.NamedQuicStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.Networking.NamedQuicStream.Read(System.Span<byte> buffer) -> int
override TeeForge.Networking.NamedQuicStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.Networking.NamedQuicStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.Networking.NamedQuicStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.Networking.NamedQuicStream.SetLength(long value) -> void
override TeeForge.Networking.NamedQuicStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.Networking.NamedQuicStream.Write(System.ReadOnlySpan<byte> buffer) -> void
override TeeForge.Networking.NamedQuicStream.WriteAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.Networking.NamedQuicStream.WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
static TeeForge.Networking.MultipathControlMessage.CreateEndpointAdvertisement(string! endpointScheme, System.ReadOnlyMemory<byte> endpointData) -> TeeForge.Networking.MultipathControlMessage!
static TeeForge.Networking.MultipathControlMessage.CreateModeChangeRequest(TeeForge.Networking.MultipathStreamMode mode, int dataShardCount = 0, int parityShardCount = 0) -> TeeForge.Networking.MultipathControlMessage!
static TeeForge.Networking.MultipathControlMessage.CreatePathReceivingValidFrames(System.Guid pathId) -> TeeForge.Networking.MultipathControlMessage!
static TeeForge.Networking.MutualQuicConnection.ConnectAsync(System.Net.EndPoint! remoteEndPoint, string! targetHost, TeeForge.Networking.MutualQuicConnectionOptions! options, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<TeeForge.Networking.MutualQuicConnection!>
static TeeForge.Networking.MutualQuicConnection.IsSupported.get -> bool
static TeeForge.Networking.MutualQuicConnectionListener.IsSupported.get -> bool
static TeeForge.Networking.MutualQuicConnectionListener.ListenAsync(System.Net.IPEndPoint! listenEndPoint, TeeForge.Networking.MutualQuicConnectionOptions! options, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<TeeForge.Networking.MutualQuicConnectionListener!>
TeeForge.Networking.MultipathControlChannel
TeeForge.Networking.MultipathControlChannel.CanReceive.get -> bool
TeeForge.Networking.MultipathControlChannel.CanSend.get -> bool
TeeForge.Networking.MultipathControlChannel.Dispose() -> void
TeeForge.Networking.MultipathControlChannel.DisposeAsync() -> System.Threading.Tasks.ValueTask
TeeForge.Networking.MultipathControlChannel.MultipathControlChannel(System.IO.Stream! stream, bool leaveOpen = false) -> void
TeeForge.Networking.MultipathControlChannel.ReceiveAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<TeeForge.Networking.MultipathControlMessage?>
TeeForge.Networking.MultipathControlChannel.SendAsync(TeeForge.Networking.MultipathControlMessage! message, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.Networking.MultipathControlMessage
TeeForge.Networking.MultipathControlMessage.GetEndpointAdvertisement() -> TeeForge.Networking.MultipathEndpointAdvertisement!
TeeForge.Networking.MultipathControlMessage.GetModeChangeRequest() -> TeeForge.Networking.MultipathModeChangeRequest!
TeeForge.Networking.MultipathControlMessage.GetPathReceivingValidFrames() -> System.Guid
TeeForge.Networking.MultipathControlMessage.Kind.get -> TeeForge.Networking.MultipathControlMessageKind
TeeForge.Networking.MultipathControlMessageKind
TeeForge.Networking.MultipathControlMessageKind.EndpointAdvertisement = 2 -> TeeForge.Networking.MultipathControlMessageKind
TeeForge.Networking.MultipathControlMessageKind.ModeChangeRequest = 1 -> TeeForge.Networking.MultipathControlMessageKind
TeeForge.Networking.MultipathControlMessageKind.PathReceivingValidFrames = 0 -> TeeForge.Networking.MultipathControlMessageKind
TeeForge.Networking.MultipathEndpointAdvertisement
TeeForge.Networking.MultipathEndpointAdvertisement.Data.get -> System.ReadOnlyMemory<byte>
TeeForge.Networking.MultipathEndpointAdvertisement.Scheme.get -> string!
TeeForge.Networking.MultipathModeChangeRequest
TeeForge.Networking.MultipathModeChangeRequest.DataShardCount.get -> int
TeeForge.Networking.MultipathModeChangeRequest.Mode.get -> TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathModeChangeRequest.ParityShardCount.get -> int
TeeForge.Networking.MultipathProtectionState
TeeForge.Networking.MultipathProtectionState.ErasureProtected = 3 -> TeeForge.Networking.MultipathProtectionState
TeeForge.Networking.MultipathProtectionState.Mirrored = 2 -> TeeForge.Networking.MultipathProtectionState
TeeForge.Networking.MultipathProtectionState.Unavailable = 0 -> TeeForge.Networking.MultipathProtectionState
TeeForge.Networking.MultipathProtectionState.Unprotected = 1 -> TeeForge.Networking.MultipathProtectionState
TeeForge.Networking.MultipathReceiverStream
TeeForge.Networking.MultipathReceiverStream.AddPathAsync(System.IO.Stream! path, System.Func<System.IO.Stream!, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>! initializer, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.ValueTask<System.Guid>
TeeForge.Networking.MultipathReceiverStream.AddPathAsync(System.IO.Stream! path, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<System.Guid>
TeeForge.Networking.MultipathReceiverStream.MultipathReceiverStream() -> void
TeeForge.Networking.MultipathReceiverStream.MultipathReceiverStream(System.Guid expectedSessionId, TeeForge.Networking.MultipathStreamOptions! options) -> void
TeeForge.Networking.MultipathReceiverStream.MultipathReceiverStream(TeeForge.Networking.MultipathStreamOptions! options) -> void
TeeForge.Networking.MultipathReceiverStream.PathCount.get -> int
TeeForge.Networking.MultipathReceiverStream.RemovePathAsync(System.Guid pathId) -> System.Threading.Tasks.ValueTask<bool>
TeeForge.Networking.MultipathReceiverStream.SessionId.get -> System.Guid?
TeeForge.Networking.MultipathSenderState
TeeForge.Networking.MultipathSenderState.Completed = 2 -> TeeForge.Networking.MultipathSenderState
TeeForge.Networking.MultipathSenderState.Completing = 1 -> TeeForge.Networking.MultipathSenderState
TeeForge.Networking.MultipathSenderState.Disposed = 4 -> TeeForge.Networking.MultipathSenderState
TeeForge.Networking.MultipathSenderState.Faulted = 3 -> TeeForge.Networking.MultipathSenderState
TeeForge.Networking.MultipathSenderState.Open = 0 -> TeeForge.Networking.MultipathSenderState
TeeForge.Networking.MultipathSenderStatus
TeeForge.Networking.MultipathSenderStatus.DesiredMode.get -> TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathSenderStatus.EffectiveMode.get -> TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathSenderStatus.ErasureDataShardCount.get -> int
TeeForge.Networking.MultipathSenderStatus.ErasureParityShardCount.get -> int
TeeForge.Networking.MultipathSenderStatus.MembershipEpoch.get -> ulong
TeeForge.Networking.MultipathSenderStatus.PathCount.get -> int
TeeForge.Networking.MultipathSenderStatus.Protection.get -> TeeForge.Networking.MultipathProtectionState
TeeForge.Networking.MultipathSenderStatus.State.get -> TeeForge.Networking.MultipathSenderState
TeeForge.Networking.MultipathSenderStream
TeeForge.Networking.MultipathSenderStream.AddPathAsync(System.IO.Stream! path, System.Func<System.IO.Stream!, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>! initializer, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.ValueTask<System.Guid>
TeeForge.Networking.MultipathSenderStream.AddPathAsync(System.IO.Stream! path, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<System.Guid>
TeeForge.Networking.MultipathSenderStream.ChangeModeAsync(TeeForge.Networking.MultipathStreamMode mode, int? erasureDataShardCount = null, int? erasureParityShardCount = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.Networking.MultipathSenderStream.CompleteAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.Networking.MultipathSenderStream.DesiredMode.get -> TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathSenderStream.EffectiveMode.get -> TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathSenderStream.ErasureDataShardCount.get -> int
TeeForge.Networking.MultipathSenderStream.ErasureParityShardCount.get -> int
TeeForge.Networking.MultipathSenderStream.MultipathSenderStream() -> void
TeeForge.Networking.MultipathSenderStream.MultipathSenderStream(System.Guid sessionId, TeeForge.Networking.MultipathStreamOptions! options) -> void
TeeForge.Networking.MultipathSenderStream.MultipathSenderStream(TeeForge.Networking.MultipathStreamOptions! options) -> void
TeeForge.Networking.MultipathSenderStream.PathCount.get -> int
TeeForge.Networking.MultipathSenderStream.RemovePathAsync(System.Guid pathId) -> System.Threading.Tasks.ValueTask<bool>
TeeForge.Networking.MultipathSenderStream.SessionId.get -> System.Guid
TeeForge.Networking.MultipathSenderStream.Status.get -> TeeForge.Networking.MultipathSenderStatus!
TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathStreamMode.ErasureCode = 2 -> TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathStreamMode.Raid0 = 1 -> TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathStreamMode.Raid1 = 0 -> TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathStreamOptions
TeeForge.Networking.MultipathStreamOptions.ErasureDataShardCount.get -> int
TeeForge.Networking.MultipathStreamOptions.ErasureParityShardCount.get -> int
TeeForge.Networking.MultipathStreamOptions.FramePayloadSize.get -> int
TeeForge.Networking.MultipathStreamOptions.LeaveOpen.get -> bool
TeeForge.Networking.MultipathStreamOptions.MaximumReceiveFramePayloadSize.get -> int
TeeForge.Networking.MultipathStreamOptions.MaximumReceiveShardCount.get -> int
TeeForge.Networking.MultipathStreamOptions.MaximumReorderBytes.get -> long
TeeForge.Networking.MultipathStreamOptions.MaximumReorderGroups.get -> int
TeeForge.Networking.MultipathStreamOptions.Mode.get -> TeeForge.Networking.MultipathStreamMode
TeeForge.Networking.MultipathStreamOptions.MultipathStreamOptions(TeeForge.Networking.MultipathStreamMode mode = TeeForge.Networking.MultipathStreamMode.Raid1, int framePayloadSize = 16384, int erasureDataShardCount = 4, int erasureParityShardCount = 2, int pathQueueCapacity = 8, int maximumReorderGroups = 1024, System.TimeSpan? pathAvailabilityTimeout = null, bool leaveOpen = false, int receiveQueueCapacity = 64, int maximumReceiveFramePayloadSize = 1048576, int maximumReceiveShardCount = 255, long maximumReorderBytes = 67108864) -> void
TeeForge.Networking.MultipathStreamOptions.PathAvailabilityTimeout.get -> System.TimeSpan
TeeForge.Networking.MultipathStreamOptions.PathQueueCapacity.get -> int
TeeForge.Networking.MultipathStreamOptions.ReceiveQueueCapacity.get -> int
TeeForge.Networking.MutualQuicConnection
TeeForge.Networking.MutualQuicConnection.AcceptStreamAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<TeeForge.Networking.NamedQuicStream!>
TeeForge.Networking.MutualQuicConnection.DisposeAsync() -> System.Threading.Tasks.ValueTask
TeeForge.Networking.MutualQuicConnection.IsClient.get -> bool
TeeForge.Networking.MutualQuicConnection.LocalEndPoint.get -> System.Net.IPEndPoint!
TeeForge.Networking.MutualQuicConnection.NegotiatedApplicationProtocol.get -> System.Net.Security.SslApplicationProtocol
TeeForge.Networking.MutualQuicConnection.OpenRandomAccessAsync(string! name, TeeForge.Networking.QuicRandomAccessOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<TeeForge.Networking.QuicRandomAccessChannel!>
TeeForge.Networking.MutualQuicConnection.OpenStreamAsync(string! name, TeeForge.Networking.NamedQuicStreamOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<TeeForge.Networking.NamedQuicStream!>
TeeForge.Networking.MutualQuicConnection.RegisterRandomAccess(string! name, TeeForge.RandomAccess.ITeeRandomAccessStream! randomAccess) -> void
TeeForge.Networking.MutualQuicConnection.RemoteCertificate.get -> System.Security.Cryptography.X509Certificates.X509Certificate?
TeeForge.Networking.MutualQuicConnection.RemoteEndPoint.get -> System.Net.IPEndPoint!
TeeForge.Networking.MutualQuicConnection.UnregisterRandomAccess(string! name) -> bool
TeeForge.Networking.MutualQuicConnectionListener
TeeForge.Networking.MutualQuicConnectionListener.AcceptConnectionAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<TeeForge.Networking.MutualQuicConnection!>
TeeForge.Networking.MutualQuicConnectionListener.DisposeAsync() -> System.Threading.Tasks.ValueTask
TeeForge.Networking.MutualQuicConnectionListener.LocalEndPoint.get -> System.Net.IPEndPoint!
TeeForge.Networking.MutualQuicConnectionOptions
TeeForge.Networking.MutualQuicConnectionOptions.AllowedCompressions.get -> TeeForge.Networking.QuicStreamCompressionAlgorithms
TeeForge.Networking.MutualQuicConnectionOptions.ApplicationProtocol.get -> System.Net.Security.SslApplicationProtocol
TeeForge.Networking.MutualQuicConnectionOptions.DefaultCloseErrorCode.get -> long
TeeForge.Networking.MutualQuicConnectionOptions.DefaultStreamErrorCode.get -> long
TeeForge.Networking.MutualQuicConnectionOptions.HandshakeTimeout.get -> System.TimeSpan
TeeForge.Networking.MutualQuicConnectionOptions.IdleTimeout.get -> System.TimeSpan
TeeForge.Networking.MutualQuicConnectionOptions.LocalCertificatePath.get -> string!
TeeForge.Networking.MutualQuicConnectionOptions.LocalPrivateKeyPath.get -> string!
TeeForge.Networking.MutualQuicConnectionOptions.MaximumInboundBidirectionalStreams.get -> int
TeeForge.Networking.MutualQuicConnectionOptions.MaximumPendingNamedStreams.get -> int
TeeForge.Networking.MutualQuicConnectionOptions.MaximumRandomAccessRequestSize.get -> int
TeeForge.Networking.MutualQuicConnectionOptions.MaximumRandomAccessSessions.get -> int
TeeForge.Networking.MutualQuicConnectionOptions.MutualQuicConnectionOptions(string! localCertificatePath, string! localPrivateKeyPath, string! trustedPeerCertificatePath, System.Net.Security.SslApplicationProtocol applicationProtocol, System.TimeSpan? idleTimeout = null, System.TimeSpan? handshakeTimeout = null, long defaultCloseErrorCode = 0, long defaultStreamErrorCode = 0, int maximumInboundBidirectionalStreams = 100, int maximumPendingNamedStreams = 100, int maximumRandomAccessRequestSize = 1048576, int maximumRandomAccessSessions = 100, TeeForge.Networking.QuicStreamCompressionAlgorithms allowedCompressions = TeeForge.Networking.QuicStreamCompressionAlgorithms.All) -> void
TeeForge.Networking.MutualQuicConnectionOptions.TrustedPeerCertificatePath.get -> string!
TeeForge.Networking.NamedQuicStream
TeeForge.Networking.NamedQuicStream.Abort(System.Net.Quic.QuicAbortDirection direction, long errorCode) -> void
TeeForge.Networking.NamedQuicStream.CompleteWrites() -> void
TeeForge.Networking.NamedQuicStream.Compression.get -> TeeForge.Networking.QuicStreamCompression
TeeForge.Networking.NamedQuicStream.Id.get -> long
TeeForge.Networking.NamedQuicStream.Input.get -> System.IO.Pipelines.PipeReader!
TeeForge.Networking.NamedQuicStream.Name.get -> string!
TeeForge.Networking.NamedQuicStream.Output.get -> System.IO.Pipelines.PipeWriter!
TeeForge.Networking.NamedQuicStream.ReadsClosed.get -> System.Threading.Tasks.Task!
TeeForge.Networking.NamedQuicStream.WritesClosed.get -> System.Threading.Tasks.Task!
TeeForge.Networking.NamedQuicStreamOptions
TeeForge.Networking.NamedQuicStreamOptions.Compression.get -> TeeForge.Networking.QuicStreamCompression
TeeForge.Networking.NamedQuicStreamOptions.NamedQuicStreamOptions(TeeForge.Networking.QuicStreamCompression compression = TeeForge.Networking.QuicStreamCompression.None) -> void
TeeForge.Networking.QuicRandomAccessChannel
TeeForge.Networking.QuicRandomAccessChannel.CanReadAt.get -> bool
TeeForge.Networking.QuicRandomAccessChannel.CanWriteAt.get -> bool
TeeForge.Networking.QuicRandomAccessChannel.Compression.get -> TeeForge.Networking.QuicStreamCompression
TeeForge.Networking.QuicRandomAccessChannel.CompressionThreshold.get -> int
TeeForge.Networking.QuicRandomAccessChannel.MaximumRequestSize.get -> int
TeeForge.Networking.QuicRandomAccessChannel.Name.get -> string!
TeeForge.Networking.QuicRandomAccessChannel.ReadAt(System.Span<byte> buffer, long offset) -> int
TeeForge.Networking.QuicRandomAccessChannel.ReadAtAsync(System.Memory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
TeeForge.Networking.QuicRandomAccessChannel.WriteAt(System.ReadOnlySpan<byte> buffer, long offset) -> void
TeeForge.Networking.QuicRandomAccessChannel.WriteAtAsync(System.ReadOnlyMemory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.Networking.QuicRandomAccessOptions
TeeForge.Networking.QuicRandomAccessOptions.Compression.get -> TeeForge.Networking.QuicStreamCompression
TeeForge.Networking.QuicRandomAccessOptions.CompressionThreshold.get -> int
TeeForge.Networking.QuicRandomAccessOptions.QuicRandomAccessOptions(TeeForge.Networking.QuicStreamCompression compression = TeeForge.Networking.QuicStreamCompression.None, int compressionThreshold = 16384) -> void
TeeForge.Networking.QuicStreamCompression
TeeForge.Networking.QuicStreamCompression.BrotliFastest = 1 -> TeeForge.Networking.QuicStreamCompression
TeeForge.Networking.QuicStreamCompression.BrotliOptimal = 2 -> TeeForge.Networking.QuicStreamCompression
TeeForge.Networking.QuicStreamCompression.None = 0 -> TeeForge.Networking.QuicStreamCompression
TeeForge.Networking.QuicStreamCompressionAlgorithms
TeeForge.Networking.QuicStreamCompressionAlgorithms.All = TeeForge.Networking.QuicStreamCompressionAlgorithms.Uncompressed | TeeForge.Networking.QuicStreamCompressionAlgorithms.BrotliFastest | TeeForge.Networking.QuicStreamCompressionAlgorithms.BrotliOptimal -> TeeForge.Networking.QuicStreamCompressionAlgorithms
TeeForge.Networking.QuicStreamCompressionAlgorithms.BrotliFastest = 2 -> TeeForge.Networking.QuicStreamCompressionAlgorithms
TeeForge.Networking.QuicStreamCompressionAlgorithms.BrotliOptimal = 4 -> TeeForge.Networking.QuicStreamCompressionAlgorithms
TeeForge.Networking.QuicStreamCompressionAlgorithms.Uncompressed = 1 -> TeeForge.Networking.QuicStreamCompressionAlgorithms
```

## TeeForge.RandomAccess

```text
override TeeForge.RandomAccess.HttpRandomAccessStream.CanRead.get -> bool
override TeeForge.RandomAccess.HttpRandomAccessStream.CanSeek.get -> bool
override TeeForge.RandomAccess.HttpRandomAccessStream.CanWrite.get -> bool
override TeeForge.RandomAccess.HttpRandomAccessStream.Flush() -> void
override TeeForge.RandomAccess.HttpRandomAccessStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.RandomAccess.HttpRandomAccessStream.Length.get -> long
override TeeForge.RandomAccess.HttpRandomAccessStream.Position.get -> long
override TeeForge.RandomAccess.HttpRandomAccessStream.Position.set -> void
override TeeForge.RandomAccess.HttpRandomAccessStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.RandomAccess.HttpRandomAccessStream.Read(System.Span<byte> buffer) -> int
override TeeForge.RandomAccess.HttpRandomAccessStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.RandomAccess.HttpRandomAccessStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.RandomAccess.HttpRandomAccessStream.Seek(long offset, System.IO.SeekOrigin origin) -> long
override TeeForge.RandomAccess.HttpRandomAccessStream.SetLength(long value) -> void
override TeeForge.RandomAccess.HttpRandomAccessStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.Capacity.get -> int
override TeeForge.RandomAccess.RandomAccessMemoryStream.Capacity.set -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.CopyTo(System.IO.Stream! destination, int bufferSize) -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.CopyToAsync(System.IO.Stream! destination, int bufferSize, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.RandomAccess.RandomAccessMemoryStream.Dispose(bool disposing) -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.Flush() -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.FlushAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.RandomAccess.RandomAccessMemoryStream.GetBuffer() -> byte[]!
override TeeForge.RandomAccess.RandomAccessMemoryStream.Length.get -> long
override TeeForge.RandomAccess.RandomAccessMemoryStream.Position.get -> long
override TeeForge.RandomAccess.RandomAccessMemoryStream.Position.set -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.Read(byte[]! buffer, int offset, int count) -> int
override TeeForge.RandomAccess.RandomAccessMemoryStream.Read(System.Span<byte> buffer) -> int
override TeeForge.RandomAccess.RandomAccessMemoryStream.ReadAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task<int>!
override TeeForge.RandomAccess.RandomAccessMemoryStream.ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
override TeeForge.RandomAccess.RandomAccessMemoryStream.ReadByte() -> int
override TeeForge.RandomAccess.RandomAccessMemoryStream.Seek(long offset, System.IO.SeekOrigin loc) -> long
override TeeForge.RandomAccess.RandomAccessMemoryStream.SetLength(long value) -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.ToArray() -> byte[]!
override TeeForge.RandomAccess.RandomAccessMemoryStream.TryGetBuffer(out System.ArraySegment<byte> buffer) -> bool
override TeeForge.RandomAccess.RandomAccessMemoryStream.Write(byte[]! buffer, int offset, int count) -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.Write(System.ReadOnlySpan<byte> buffer) -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.WriteAsync(byte[]! buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
override TeeForge.RandomAccess.RandomAccessMemoryStream.WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
override TeeForge.RandomAccess.RandomAccessMemoryStream.WriteByte(byte value) -> void
override TeeForge.RandomAccess.RandomAccessMemoryStream.WriteTo(System.IO.Stream! stream) -> void
static TeeForge.RandomAccess.HttpRandomAccessStream.OpenAsync(System.Net.Http.HttpClient! client, System.Uri! requestUri, TeeForge.RandomAccess.HttpRandomAccessStreamOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TeeForge.RandomAccess.HttpRandomAccessStream!>!
static TeeForge.RandomAccess.HttpRandomAccessStreamOptions.Default.get -> TeeForge.RandomAccess.HttpRandomAccessStreamOptions!
static TeeForge.RandomAccess.TeeRandomAccess.TryGet(System.IO.Stream! stream, out TeeForge.RandomAccess.ITeeRandomAccessStream? randomAccess) -> bool
TeeForge.RandomAccess.HttpRandomAccessStream
TeeForge.RandomAccess.HttpRandomAccessStream.CanReadAt.get -> bool
TeeForge.RandomAccess.HttpRandomAccessStream.CanWriteAt.get -> bool
TeeForge.RandomAccess.HttpRandomAccessStream.OpenReadRangeAsync(long offset, long length, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<System.IO.Stream!>
TeeForge.RandomAccess.HttpRandomAccessStream.Options.get -> TeeForge.RandomAccess.HttpRandomAccessStreamOptions!
TeeForge.RandomAccess.HttpRandomAccessStream.ReadAt(System.Span<byte> buffer, long offset) -> int
TeeForge.RandomAccess.HttpRandomAccessStream.ReadAtAsync(System.Memory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
TeeForge.RandomAccess.HttpRandomAccessStream.RequestUri.get -> System.Uri!
TeeForge.RandomAccess.HttpRandomAccessStream.WriteAt(System.ReadOnlySpan<byte> buffer, long offset) -> void
TeeForge.RandomAccess.HttpRandomAccessStream.WriteAtAsync(System.ReadOnlyMemory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.RandomAccess.HttpRandomAccessStreamOptions
TeeForge.RandomAccess.HttpRandomAccessStreamOptions.HttpRandomAccessStreamOptions(TeeForge.RandomAccess.HttpRepresentationValidationMode validationMode = TeeForge.RandomAccess.HttpRepresentationValidationMode.WhenAvailable, int slowdownRetryCount = 3, System.TimeSpan? maximumSlowdownWait = null, int representationChangeRetryCount = 0, int rangeResumeRetryCount = 3, System.TimeSpan? retryBaseDelay = null) -> void
TeeForge.RandomAccess.HttpRandomAccessStreamOptions.MaximumSlowdownWait.get -> System.TimeSpan
TeeForge.RandomAccess.HttpRandomAccessStreamOptions.RangeResumeRetryCount.get -> int
TeeForge.RandomAccess.HttpRandomAccessStreamOptions.RepresentationChangeRetryCount.get -> int
TeeForge.RandomAccess.HttpRandomAccessStreamOptions.RetryBaseDelay.get -> System.TimeSpan
TeeForge.RandomAccess.HttpRandomAccessStreamOptions.SlowdownRetryCount.get -> int
TeeForge.RandomAccess.HttpRandomAccessStreamOptions.ValidationMode.get -> TeeForge.RandomAccess.HttpRepresentationValidationMode
TeeForge.RandomAccess.HttpRepresentationChangedException
TeeForge.RandomAccess.HttpRepresentationChangedException.HttpRepresentationChangedException(string! message, System.Exception! innerException) -> void
TeeForge.RandomAccess.HttpRepresentationChangedException.HttpRepresentationChangedException(string! message) -> void
TeeForge.RandomAccess.HttpRepresentationValidationMode
TeeForge.RandomAccess.HttpRepresentationValidationMode.None = 2 -> TeeForge.RandomAccess.HttpRepresentationValidationMode
TeeForge.RandomAccess.HttpRepresentationValidationMode.RequireStrongValidator = 1 -> TeeForge.RandomAccess.HttpRepresentationValidationMode
TeeForge.RandomAccess.HttpRepresentationValidationMode.WhenAvailable = 0 -> TeeForge.RandomAccess.HttpRepresentationValidationMode
TeeForge.RandomAccess.ITeeRandomAccessStream
TeeForge.RandomAccess.ITeeRandomAccessStream.CanReadAt.get -> bool
TeeForge.RandomAccess.ITeeRandomAccessStream.CanWriteAt.get -> bool
TeeForge.RandomAccess.ITeeRandomAccessStream.ReadAt(System.Span<byte> buffer, long offset) -> int
TeeForge.RandomAccess.ITeeRandomAccessStream.ReadAtAsync(System.Memory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
TeeForge.RandomAccess.ITeeRandomAccessStream.WriteAt(System.ReadOnlySpan<byte> buffer, long offset) -> void
TeeForge.RandomAccess.ITeeRandomAccessStream.WriteAtAsync(System.ReadOnlyMemory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.RandomAccess.ITeeRangeReadSource
TeeForge.RandomAccess.ITeeRangeReadSource.OpenReadRangeAsync(long offset, long length, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<System.IO.Stream!>
TeeForge.RandomAccess.RandomAccessMemoryStream
TeeForge.RandomAccess.RandomAccessMemoryStream.CanReadAt.get -> bool
TeeForge.RandomAccess.RandomAccessMemoryStream.CanWriteAt.get -> bool
TeeForge.RandomAccess.RandomAccessMemoryStream.OpenReadRangeAsync(long offset, long length, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<System.IO.Stream!>
TeeForge.RandomAccess.RandomAccessMemoryStream.RandomAccessMemoryStream() -> void
TeeForge.RandomAccess.RandomAccessMemoryStream.RandomAccessMemoryStream(byte[]! buffer, bool writable) -> void
TeeForge.RandomAccess.RandomAccessMemoryStream.RandomAccessMemoryStream(byte[]! buffer, int index, int count, bool writable, bool publiclyVisible) -> void
TeeForge.RandomAccess.RandomAccessMemoryStream.RandomAccessMemoryStream(byte[]! buffer, int index, int count, bool writable) -> void
TeeForge.RandomAccess.RandomAccessMemoryStream.RandomAccessMemoryStream(byte[]! buffer, int index, int count) -> void
TeeForge.RandomAccess.RandomAccessMemoryStream.RandomAccessMemoryStream(byte[]! buffer) -> void
TeeForge.RandomAccess.RandomAccessMemoryStream.RandomAccessMemoryStream(int capacity) -> void
TeeForge.RandomAccess.RandomAccessMemoryStream.ReadAt(System.Span<byte> buffer, long offset) -> int
TeeForge.RandomAccess.RandomAccessMemoryStream.ReadAtAsync(System.Memory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<int>
TeeForge.RandomAccess.RandomAccessMemoryStream.WriteAt(System.ReadOnlySpan<byte> buffer, long offset) -> void
TeeForge.RandomAccess.RandomAccessMemoryStream.WriteAtAsync(System.ReadOnlyMemory<byte> buffer, long offset, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask
TeeForge.RandomAccess.TeeRandomAccess
```
