$ErrorActionPreference = 'Stop'

Push-Location (Join-Path $PSScriptRoot '..')
try {
    # Dependabot can miss consumers of updated ProjectReferences. Restore the
    # complete solution, including Windows tools and experimental projects.
    dotnet restore TeeForge.slnx --force-evaluate -p:RestoreLockedMode=false
    if ($LASTEXITCODE -ne 0) { throw 'Could not regenerate dependency lock files.' }

    dotnet restore TeeForge.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Regenerated dependency lock files failed locked restore.' }
}
finally {
    Pop-Location
}
