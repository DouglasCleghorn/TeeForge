# TeeForge runnable C# recipes

TeeForge 0.2.0 · .NET 10 · **unreleased**

- [Copy a C# stream to multiple destinations](copy.md): Select the extension with a destination collection; caller streams remain open and unflushed.
- [Calculate multiple hashes while copying a stream](hash.md): Compute SHA-256 and XXH3 in one copy; access the SHA-256 result with either identifier form.
- [Replicate writes to multiple writable streams](replicate.md): Use a write-only wrapper and explicitly retain destination ownership.
- [Broadcast a stream to independent readers](broadcast.md): Start all consumers concurrently so slow-reader backpressure can make progress.
- [Read byte ranges without changing stream Position](random-access.md): Use explicit offsets and bounded range streams with independent cursors.
- [Read one entry from a remote ZIP or NuGet package](remote-zip.md): Open a range-enabled URL with a bounded read-ahead window and let ZipArchive locate the entry. Append a URL and entry name to the command to write its contents to standard output.
- [Read one column from one remote Parquet row group](remote-parquet.md): Use Parquet.Net with a bounded HTTP range window. Append a range-enabled URL, a top-level DOUBLE column name, and a zero-based row group index to summarize the selected values. Both remote recipes report requests and requested range bytes on standard error.
