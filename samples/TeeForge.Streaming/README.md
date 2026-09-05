# Forward-only erasure streaming

Run from the repository root:

```text
dotnet run --project samples/TeeForge.Streaming -c Release
```

The example generates 16 MiB + 777 deterministic bytes without buffering the
whole input. It writes six member files through wrappers that prohibit seeking,
calls `CompleteAsync`, reopens all six as forward-only readers, and verifies
every decoded byte. It repeats verification with one data and one parity member
missing. The partial final codeword exercises padding and exact logical length.

The stream cache is configured to one 4+2 codeword (384 KiB), with no read-ahead.
File and copy buffers add bounded memory. Member files are retained in a unique
`artifacts/streaming` directory. This is a correctness example, not a benchmark.

An object-upload adapter can occupy the same forward-only member boundary.
This sample uses local files and does not implement any object-store SDK or
multi-object publication protocol. The caller retains geometry and member order.
