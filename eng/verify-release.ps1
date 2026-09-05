param(
    [Parameter(Mandatory)]
    [ValidateSet('release', 'workflow_dispatch')]
    [string] $EventName,
    [string] $Tag = '',
    [bool] $IsPrerelease = $false,
    [Parameter(Mandatory)]
    [string] $ExpectedCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$head = git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedCommit) {
    throw 'Checkout does not match the triggering commit.'
}
git merge-base --is-ancestor $head origin/main
if ($LASTEXITCODE -ne 0) { throw 'Release candidates must already be on main.' }

$changes = git status --porcelain
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Release validation requires a clean checkout.' }

$version = dotnet msbuild src/TeeForge/TeeForge.csproj -nologo -getProperty:PackageVersion
if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate the package version.' }
$version = ($version -join "`n").Trim()
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw 'Expected a three-part package version with an optional prerelease suffix.'
}

if ($EventName -eq 'release') {
    if ($Tag -cne "v$version") { throw 'Release tag must match the version in the project.' }
    $tagCommit = git rev-parse --verify "refs/tags/$Tag^{commit}"
    if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $head) { throw 'Release tag does not point to this checkout.' }
    if ($IsPrerelease -ne $version.Contains('-')) {
        throw 'GitHub prerelease status must match the package version suffix.'
    }
}

Write-Output $version
