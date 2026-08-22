param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = "artifacts/package",

    [Parameter(Mandatory = $false)]
    [string] $ExpectedVersion = "0.1.0"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$resolvedDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $resolvedDirectory -Filter "TeeForge.*.nupkg" -File |
    Where-Object { $_.Name -notlike "*.snupkg" })
$symbols = @(Get-ChildItem -LiteralPath $resolvedDirectory -Filter "TeeForge.*.snupkg" -File)

if ($packages.Count -ne 1) {
    throw "Expected exactly one TeeForge nupkg in '$resolvedDirectory'; found $($packages.Count)."
}

if ($symbols.Count -ne 1) {
    throw "Expected exactly one TeeForge snupkg in '$resolvedDirectory'; found $($symbols.Count)."
}

$package = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $entries = @($package.Entries | ForEach-Object { $_.FullName })
    $required = @(
        "lib/net10.0/TeeForge.dll",
        "lib/net10.0/TeeForge.xml",
        "README.md",
        "CHANGELOG.md",
        "LICENSE",
        "THIRD-PARTY-NOTICES.txt"
    )

    foreach ($entry in $required) {
        if ($entries -notcontains $entry) {
            throw "Package is missing required entry '$entry'."
        }
    }

    $unexpectedFrameworks = @($entries | Where-Object {
        $_ -like "lib/*" -and $_ -notlike "lib/net10.0/*"
    })
    if ($unexpectedFrameworks.Count -ne 0) {
        throw "Package contains unexpected target-framework entries: $($unexpectedFrameworks -join ', ')."
    }

    $nuspecEntries = @($package.Entries | Where-Object { $_.FullName -like "*.nuspec" })
    if ($nuspecEntries.Count -ne 1) {
        throw "Expected exactly one nuspec; found $($nuspecEntries.Count)."
    }

    $nuspecEntry = $nuspecEntries[0]
    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try {
        [xml] $nuspec = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $metadata = $nuspec.package.metadata
    if ($metadata.id -ne "TeeForge") {
        throw "Unexpected package ID '$($metadata.id)'."
    }

    if ($metadata.version -ne $ExpectedVersion) {
        throw "Package version '$($metadata.version)' does not match '$ExpectedVersion'."
    }

    $dependencies = @($nuspec.SelectNodes("//*[local-name()='dependency']"))
    if ($dependencies.Count -ne 0) {
        throw "TeeForge must not have runtime NuGet dependencies."
    }
}
finally {
    $package.Dispose()
}

$symbolPackage = [System.IO.Compression.ZipFile]::OpenRead($symbols[0].FullName)
try {
    $symbolEntries = @($symbolPackage.Entries | ForEach-Object { $_.FullName })
    if ($symbolEntries -notcontains "lib/net10.0/TeeForge.pdb") {
        throw "Symbol package is missing the portable PDB."
    }

}
finally {
    $symbolPackage.Dispose()
}

Write-Host "Verified package and symbols for TeeForge $ExpectedVersion."
