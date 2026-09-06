# TeeForge documentation

Read the [public documentation site](https://teeforge-docs.douglas-cleghorn.chatgpt.site/)
or start locally with the [agent usage guide](agent-guide.md),
[runnable C# recipes](recipes/index.md), and [public API reference](api-reference.md).
The site serves HTML and Markdown at matching paths, supports text search, and
provides [llms.txt](../llms.txt) for agents.

## Versions and release status

The current API targets TeeForge 0.1.0 and .NET 10. Its snapshot records release 0.1.0. `docs/versions/<package-version>/` contains the
version-specific guide, examples, API signatures, and contracts. Documentation
for an unreleased version may change; a released snapshot should be retained
when the package advances. Never infer NuGet availability from a sample command.

## Update documentation

1. Edit `docs/agent-guide.template.md`, the contract pages, or the compiled C#
   files in `samples/TeeForge.Quickstart`.
2. Run `pwsh ./eng/update-docs.ps1` to regenerate the guide, recipes, current
   version snapshot, API reference, and root `llms.txt`.
3. Run `dotnet run --project samples/TeeForge.Quickstart -c Release`.
4. Run `pwsh ./eng/update-docs.ps1 -Check` to verify generated content is current.
5. Run `pwsh ./eng/build-docs-site.ps1` to produce the searchable static site.

The package version comes from the library project. Set `releaseStatus` in
`docs/documentation.json` to `released` only as part of publishing that version;
then regenerate and retain that version folder. When starting a new version,
change the project version and reset the status to `unreleased`. The site builder
includes every retained version and writes `versions.json` and a sitemap.

The static site checkout is `.local/teeforge-docs-site`. The builder restores its
hosting manifest from `docs/site/hosting.json` on a fresh checkout. That profile
identifies the existing TeeForge documentation Site; reuse it when publishing.
Only generated public documentation and the documentation site's own source
belong in that checkout. Publishing documentation does not publish a NuGet
package or commit other TeeForge changes.

## Discovery metadata

`docs/documentation.json` records the public site origin and intended GitHub
topics. The package project contains searchable NuGet tags. Its documentation
bundle is stored under `docs/` inside the package and is not injected into
consumer projects as build logic or agent instructions.
