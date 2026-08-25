# Center the protocol on a mutual QUIC connection

TeeForge authenticates both endpoints during QUIC's TLS 1.3 handshake. Each
role loads an X.509 certificate and matching unencrypted private key from local
PEM files, requires the peer certificate, and pins that certificate to a
separate local trust file. The connection owns this authenticated relationship
and carries multiple independent application streams opened by either endpoint.

Application stream names are dynamic rather than configured in a connection
manifest. A new bidirectional QUIC stream begins with a bounded, versioned,
uncompressed preface containing its application name and requested compression.
The name is not part of later payload framing: QUIC's native stream ID already
identifies the physical stream. Exactly one live stream pair may hold a name.
If both endpoints open the same unused name concurrently, the client-initiated
stream wins deterministically; active duplicates are rejected, and disposing
the winner releases the name for reuse.

The opener selects compression per named stream and the receiver admits or
rejects that selection according to its connection policy. Accepted compression
is transparent and applies to the complete payload in both directions, with
separate directional contexts. A connection-wide shared context is deliberately
avoided because it would couple independently delivered QUIC streams and expose
cross-stream compression state.

Random access is a separately negotiated connection-level service backed by a
caller-owned `ITeeRandomAccessStream`. Every positional request uses a new
bidirectional QUIC stream rather than reusing a pool, preserving independent
flow control, cancellation, and failure. A short negotiated service handle
replaces the service name on repeated requests. Request and response payloads
at or above the configured threshold use the negotiated compression algorithm;
smaller payloads remain uncompressed. Request size and concurrent inbound stream
limits bound resource use.

## References

- [.NET QUIC overview](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview)
- [.NET QUIC configuration options](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options)
- [RFC 9000: QUIC transport](https://www.rfc-editor.org/rfc/rfc9000.html)
- [.NET `BrotliStream`](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.brotlistream?view=net-10.0)
