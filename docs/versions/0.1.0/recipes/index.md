# TeeForge runnable C# recipes

TeeForge 0.1.0 · .NET 10 · **released**

- [Copy a C# stream to multiple destinations](copy.md): Select the extension with a destination collection; caller streams remain open and unflushed.
- [Calculate multiple hashes while copying a stream](hash.md): Compute SHA-256 and XXH3 in one copy; access the SHA-256 result with either identifier form.
- [Replicate writes to multiple writable streams](replicate.md): Use a write-only wrapper and explicitly retain destination ownership.
- [Broadcast a stream to independent readers](broadcast.md): Start all consumers concurrently so slow-reader backpressure can make progress.
- [Read byte ranges without changing stream Position](random-access.md): Use explicit offsets and bounded range streams with independent cursors.
