# TeeForge quickstart examples

Requires the .NET 10 SDK. Run the offline examples from the repository root.

```text
dotnet run --project samples/TeeForge.Quickstart -c Release
```

Run one example by adding `-- copy`, `-- hash`, `-- replicate`, `-- broadcast`,
`-- random-access`, `-- remote-zip`, or `-- remote-parquet`. Each checks its output with a 30-second
deadline. To copy one entry from a range-enabled HTTP server to standard output,
pass its URL and the archive entry name.

```sh
dotnet run --project samples/TeeForge.Quickstart -c Release -- remote-zip "$ZIP_URL" manifest.txt
```

To summarize a top-level DOUBLE column from one row group, pass the Parquet
URL, column name, and zero-based group index. This example reads trip distances
from the first row group of the January 2024 NYC yellow taxi dataset.

```sh
dotnet run --project samples/TeeForge.Quickstart -c Release -- remote-parquet \
  https://d37ci6vzurychx.cloudfront.net/trip-data/yellow_tripdata_2024-01.parquet trip_distance 0
```

The Parquet reader loads the selected column into memory. Its range window is
64 KiB. Both remote recipes report the HTTP request count and the sum of
requested range bytes to standard error, including the probe and any retries.
The Parquet.Net dependency belongs to the quickstart.

Documentation recipes are generated from the complete example classes by
`eng/update-docs.ps1`. Edit those classes first, regenerate docs, and run the
project. CI builds and runs it on Windows, Linux, and macOS.

See [the usage guide](../../docs/agent-guide.md) and
[generated recipes](../../docs/recipes/index.md).
