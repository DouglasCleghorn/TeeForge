# TeeForge quickstart examples

Requires the .NET 10 SDK. Run the offline examples from the repository root.

```text
dotnet run --project samples/TeeForge.Quickstart -c Release
```

Run one example by adding `-- copy`, `-- hash`, `-- replicate`, `-- broadcast`,
`-- random-access`, or `-- remote-zip`. Each checks its output with a 30-second
deadline. To copy one entry from a range-enabled HTTP server to standard output,
pass its URL and the archive entry name.

```sh
dotnet run --project samples/TeeForge.Quickstart -c Release -- remote-zip "$ZIP_URL" manifest.txt
```

Documentation recipes are generated from the complete example classes by
`eng/update-docs.ps1`. Edit those classes first, regenerate docs, and run the
project. CI builds and runs it on Windows, Linux, and macOS.

See [the usage guide](../../docs/agent-guide.md) and
[generated recipes](../../docs/recipes/index.md).
