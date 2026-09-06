# TeeForge quickstart examples

Requires the .NET 10 SDK. Run all five examples from the repository root:

```text
dotnet run --project samples/TeeForge.Quickstart -c Release
```

Run one example by adding `-- copy`, `-- hash`, `-- replicate`, `-- broadcast`,
or `-- random-access`. Each example checks its output and fails the process on
an error; the runner supplies a 30-second cancellation deadline. The examples
use memory streams and need no external services or files.

Documentation recipes are generated from the complete example classes by
`eng/update-docs.ps1`. Edit those classes first, regenerate docs, and run the
project. CI builds and runs it on Windows, Linux, and macOS.

See [the usage guide](../../docs/agent-guide.md) and
[generated recipes](../../docs/recipes/index.md).
