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
        "teeforge-icon.png",
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

    if ($metadata.title -ne "TeeForge") {
        throw "Unexpected package title '$($metadata.title)'."
    }

    if ($metadata.authors -ne "Doug Cleghorn") {
        throw "Unexpected package authors '$($metadata.authors)'."
    }

    $expectedDescription = "High-performance .NET streams for mirrored I/O, buffered fan-out, multi-hashing, broadcast pipelines, sparse storage, and HTTP range reads."
    if ($metadata.description -ne $expectedDescription) {
        throw "Unexpected package description '$($metadata.description)'."
    }

    $license = $metadata.license
    if ($null -eq $license -or $license.InnerText -ne "MIT" -or
        $license.GetAttribute("type") -ne "expression") {
        throw "Package must use the MIT SPDX license expression."
    }

    if ($metadata.readme -ne "README.md") {
        throw "Unexpected package README path '$($metadata.readme)'."
    }

    if ($metadata.icon -ne "teeforge-icon.png") {
        throw "Unexpected package icon path '$($metadata.icon)'."
    }

    $iconEntry = $package.GetEntry("teeforge-icon.png")
    if ($iconEntry.Length -gt 1MB) {
        throw "Package icon exceeds NuGet's 1 MB limit."
    }

    $iconHeader = [byte[]]::new(24)
    $iconStream = $iconEntry.Open()
    try {
        $read = $iconStream.Read($iconHeader, 0, $iconHeader.Length)
    }
    finally {
        $iconStream.Dispose()
    }

    $pngSignature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    $validPngSignature = $read -eq $iconHeader.Length
    for ($index = 0; $validPngSignature -and $index -lt $pngSignature.Length; $index++) {
        $validPngSignature = $iconHeader[$index] -eq $pngSignature[$index]
    }

    if (-not $validPngSignature) {
        throw "Package icon is not a valid PNG."
    }

    $iconWidth = [System.Net.IPAddress]::NetworkToHostOrder(
        [System.BitConverter]::ToInt32($iconHeader, 16))
    $iconHeight = [System.Net.IPAddress]::NetworkToHostOrder(
        [System.BitConverter]::ToInt32($iconHeader, 20))
    if ($iconWidth -ne 128 -or $iconHeight -ne 128) {
        throw "Package icon must be 128x128; found ${iconWidth}x${iconHeight}."
    }

    if ($metadata.projectUrl -ne "https://github.com/DouglasCleghorn/TeeForge") {
        throw "Unexpected project URL '$($metadata.projectUrl)'."
    }

    if ([string]::IsNullOrWhiteSpace($metadata.releaseNotes)) {
        throw "Package release notes are missing."
    }

    $repository = $metadata.repository
    if ($null -eq $repository -or $repository.type -ne "git" -or
        $repository.url -ne "https://github.com/DouglasCleghorn/TeeForge") {
        throw "Package repository metadata is missing or incorrect."
    }

    $dependencies = @($nuspec.SelectNodes("//*[local-name()='dependency']"))
    if ($dependencies.Count -ne 1) {
        throw "TeeForge must have exactly one runtime NuGet dependency; found $($dependencies.Count)."
    }

    $dependency = $dependencies[0]
    if ($dependency.id -ne "System.IO.Hashing" -or $dependency.version -ne "10.0.11") {
        throw "Unexpected runtime dependency '$($dependency.id)' version '$($dependency.version)'."
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
