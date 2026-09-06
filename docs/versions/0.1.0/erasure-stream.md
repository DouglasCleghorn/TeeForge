# ErasureStream

`ErasureStream` maps one fixed-length byte sequence onto `k` data streams and
`m` parity streams using systematic Reed-Solomon coding. Members contain only
encoded payload: no persistent header, journal, identity, or membership record.

## Write and reopen

```csharp
using TeeForge.ErasureCoding;

var options = new ErasureStreamOptions(leaveOpen: true);
await using (ErasureStream encoded = ErasureStream.Create(
    members, dataShardCount: 4, parityShardCount: 2,
    logicalLength: source.Length, blockSize: 128 * 1024, options))
{
    await source.CopyToAsync(encoded);
    await encoded.CompleteAsync();
}

await using ErasureStream decoded = ErasureStream.Open(
    members, 4, 2, source.Length, 128 * 1024, options);
await decoded.CopyToAsync(destination);
```

Keep member order, logical length, block size, and data/parity counts externally.
For forward-only members, supply fresh readers positioned at payload byte zero.
Seekable members are addressed from offset zero. `Create` truncates seekable
writable members; use it only for outputs whose contents may be replaced.

## Stream behavior

- Capabilities follow the available member capabilities.
- Ordinary reads/writes use logical `Position`. Positional operations preserve
  it; writes to the same codeword serialize.
- One codeword covers `k * BlockSize` logical bytes. Each member receives one
  `BlockSize` payload, including zero padding in the final codeword.
- The logical length is fixed; `SetLength` is unsupported.
- Forward-only writers must supply exactly the declared length and call
  `CompleteAsync`. This emits the final partial codeword and flushes members.
  `Flush` does not finalize a partial codeword; disposal does not replace completion.
- The stream owns members unless `LeaveOpen` is true. `Open` never initializes
  or truncates member contents.
- The cache budget controls retained entries. Active operations may temporarily
  require additional complete codewords, so bound caller concurrency as well.

`RequireAllMembers` defaults to true. For degraded reads, set it to false and
supply a `null` at each missing member position. At least `k` readable members
are required. Missing members make the stream read-only. Reed-Solomon recovers
known missing members; this layout does not identify silently corrupted members.

Partial writes can succeed on some members and fail on others. There is no
transactional recovery or safe automatic retry guarantee. Flush behavior is
the behavior supplied by the underlying streams.

The default block size remains 128 KiB. Existing benchmark observations are
historical evidence; changing defaults requires equivalent sampled comparisons
under the [benchmark policy](https://github.com/DouglasCleghorn/TeeForge/blob/v0.1.0/docs/benchmarks/README.md).

Run the [forward-only sample](https://github.com/DouglasCleghorn/TeeForge/blob/v0.1.0/samples/TeeForge.Streaming/README.md) to encode,
reopen, and recover with two missing members.
