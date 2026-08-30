# QUIC file benchmark

This harness compares direct local file I/O with the same work performed by a
separate TeeForge QUIC server over the loopback interface. Both peers load their
certificate and private key from PEM files and pin the other peer's certificate.

The client measures:

- sequential reads and writes through a fresh named stream per transfer;
- random reads and writes through the random-access channel;
- direct `FileStream` and `System.IO.RandomAccess` baselines;
- configurable block sizes, random-access queue depths, iteration counts, and
  Brotli compression; and
- a warm-up pass, per-iteration console output, median summaries, and raw JSON.

Random-access queue depth is implemented by issuing that many operations at
once. Each remote operation uses its own native QUIC stream. The sequential
stream name is used only during its preface; QUIC's stream ID identifies the
stream afterward.

## Run

Build the two applications in Release mode:

```powershell
dotnet build TeeForge.slnx -c Release
```

Create disposable benchmark identities. These are test credentials, not
production certificates:

```powershell
dotnet run -c Release --no-build --project benchmarks/TeeForge.QuicBench.Client -- certificates --output artifacts/quic-file-benchmark/certs
```

Start the server in one terminal:

```powershell
dotnet run -c Release --no-build --project benchmarks/TeeForge.QuicBench.Server -- --port 45678 --file-size-mib 64 --work-dir artifacts/quic-file-benchmark/server --certificate artifacts/quic-file-benchmark/certs/server.crt.pem --private-key artifacts/quic-file-benchmark/certs/server.key.pem --trusted-peer-certificate artifacts/quic-file-benchmark/certs/client.crt.pem
```

Then run the client in another terminal:

```powershell
dotnet run -c Release --no-build --project benchmarks/TeeForge.QuicBench.Client -- --port 45678 --file-size-mib 64 --random-mib 8 --sequential-iterations 3 --random-iterations 2 --sequential-block-sizes 65536,1048576 --random-block-sizes 4096,65536,1048576 --queue-depths 1,4,16,32 --compression none --compression-threshold 16384 --work-dir artifacts/quic-file-benchmark/client --certificate artifacts/quic-file-benchmark/certs/client.crt.pem --private-key artifacts/quic-file-benchmark/certs/client.key.pem --trusted-peer-certificate artifacts/quic-file-benchmark/certs/server.crt.pem --output artifacts/quic-file-benchmark/results.json
```

Use distinct server and client work directories. The server accepts one client
and exits after the client's shutdown stream. Valid compression values are
`none`, `fastest`, and `optimal`. Compression applies to every sequential
stream; random-access payloads are compressed only at or above
`--compression-threshold`.

The retained August 2026 run and interpretation are in
[the experiment record](../TeeForge.Benchmarks/Experiments/2026-08-24-quic-file-io.md).

## Multi-gigabyte memory mode

`System.IO.MemoryStream` has an `int`-sized capacity, so it cannot represent a
single 2+ GiB stream. The benchmark's memory mode uses a long-addressable
`Stream` composed of separately valid `MemoryStream` segments. Each segment is
64 MiB by default, and positioned access is protected by a per-segment lock.
This allows the same sequential and random-access matrix to run over a genuine
multi-gigabyte in-memory backing store.

Start a 3 GiB memory server:

```powershell
dotnet run -c Release --no-build --project benchmarks/TeeForge.QuicBench.Server -- --storage memory --port 45681 --memory-size-gib 3 --memory-segment-mib 64 --certificate artifacts/quic-file-benchmark/certs/server.crt.pem --private-key artifacts/quic-file-benchmark/certs/server.key.pem --trusted-peer-certificate artifacts/quic-file-benchmark/certs/client.crt.pem
```

Run the matched client:

```powershell
dotnet run -c Release --no-build --project benchmarks/TeeForge.QuicBench.Client -- --storage memory --port 45681 --memory-size-gib 3 --memory-segment-mib 64 --random-mib 64 --sequential-iterations 1 --random-iterations 2 --sequential-block-sizes 65536,1048576 --random-block-sizes 4096,65536,1048576 --queue-depths 1,4,16,32 --compression none --certificate artifacts/quic-file-benchmark/certs/client.crt.pem --private-key artifacts/quic-file-benchmark/certs/client.key.pem --trusted-peer-certificate artifacts/quic-file-benchmark/certs/server.crt.pem --output artifacts/quic-memory-benchmark/3gib-direct-comparison.json
```

The default allocates 3 GiB in each process. Check available physical memory
before increasing it. `--memory-size-mib` is available for small smoke tests and
takes precedence over `--memory-size-gib`.

Memory mode reports three paths:

- `Direct` reads or writes the local segmented store without networking;
- `QUIC-Memory` transfers through the remote segmented store; and
- `QUIC-Direct` generates outgoing data or discards incoming data at the server,
  with no multi-gigabyte backing-store access.

The last two paths use identical QUIC framing, mTLS, stream creation, and client
buffers. Their difference isolates the cost of the segmented memory backing
store from transport cost.

The retained 3 GiB results are in
[the memory-stream experiment](../TeeForge.Benchmarks/Experiments/2026-08-25-quic-memory-stream.md).
