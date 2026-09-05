param(
    [string] $Project = 'benchmarks/TeeForge.Benchmarks',
    [string[]] $BenchmarkArguments = @('--erasure-stream-files', '--data-mib', '256', '--random-operations', '65536', '--block-size', '131072'),
    [ValidateRange(5, 10000)] [int] $Samples = 5,
    [ValidateRange(1, 100)] [int] $Warmups = 1,
    [string[]] $CaseColumns = @('BlockSizeBytes', 'Runtime'),
    [string] $CpuModel,
    [string] $OutputRoot = 'artifacts/sampled-benchmarks'
)

# Contract: the harness accepts --output <directory> and writes exactly one
# wide numeric CSV. CaseColumns identify non-metric columns. Each invocation
# runs in a fresh process and its entire output is retained.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repository
try {
    function Get-SourceFingerprint {
        $snapshot = [Text.StringBuilder]::new()
        $changes = (& git -c "safe.directory=$repository" -c core.safecrlf=false diff HEAD --binary) -join "`n"
        [void] $snapshot.Append($changes)
        foreach ($path in @(& git -c "safe.directory=$repository" ls-files --others --exclude-standard | Sort-Object)) {
            [void] $snapshot.Append($path)
            [void] $snapshot.Append((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
        }
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($snapshot.ToString())))
    }
    if ($BenchmarkArguments -contains '--output') {
        throw 'The runner owns --output so earlier evidence cannot be overwritten.'
    }
    if ([string]::IsNullOrWhiteSpace($CpuModel)) {
        if ($IsWindows) {
            $CpuModel = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
        } elseif ($IsLinux) {
            $CpuModel = ((Get-Content /proc/cpuinfo | Where-Object { $_ -match '^model name\s*:' } | Select-Object -First 1) -split ':', 2)[1].Trim()
        } elseif ($IsMacOS) {
            $CpuModel = (& sysctl -n machdep.cpu.brand_string).Trim()
        }
    }
    if ([string]::IsNullOrWhiteSpace($CpuModel)) { throw 'Supply -CpuModel to record machine provenance.' }

    & dotnet build $Project -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
    $sdk = (& dotnet --version).Trim()
    $commit = (& git -c "safe.directory=$repository" rev-parse HEAD).Trim()
    $dirty = @(& git -c "safe.directory=$repository" status --porcelain).Count -ne 0
    $sourceHash = Get-SourceFingerprint
    $parameters = ConvertTo-Json -InputObject $BenchmarkArguments -Compress
    $runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N')
    $destination = Join-Path $OutputRoot $runId
    New-Item -ItemType Directory -Path $destination | Out-Null
    $rawPath = Join-Path $destination 'results-raw.csv'
    $aggregatePath = Join-Path $destination 'results-aggregate.csv'
    $rows = [Collections.Generic.List[object]]::new()
    $metrics = $null
    $expectedCases = $null
    $failures = 0

    for ($index = -$Warmups; $index -lt $Samples; $index++) {
        $phase = if ($index -lt 0) { 'warmup' } else { 'measured' }
        $sampleDirectory = Join-Path $destination "$phase-$([Math]::Abs($index))"
        New-Item -ItemType Directory -Path $sampleDirectory | Out-Null
        $timestamp = [DateTime]::UtcNow.ToString('O')
        $commandArguments = @('run', '--project', $Project, '-c', 'Release', '--no-build', '--no-restore', '--') + $BenchmarkArguments + @('--output', $sampleDirectory)
        $clock = [Diagnostics.Stopwatch]::StartNew()
        & dotnet @commandArguments *> (Join-Path $sampleDirectory 'process.log')
        $exitCode = $LASTEXITCODE
        $clock.Stop()
        $currentCommit = (& git -c "safe.directory=$repository" rev-parse HEAD).Trim()
        $currentSourceHash = Get-SourceFingerprint
        $sourceChanged = $currentCommit -ne $commit -or $currentSourceHash -ne $sourceHash
        $files = @(Get-ChildItem -LiteralPath $sampleDirectory -Filter '*.csv' -File)
        $success = $exitCode -eq 0 -and $files.Count -eq 1 -and -not $sourceChanged
        $data = @()
        if ($success) {
            try { $data = @(Import-Csv -LiteralPath $files[0].FullName) }
            catch { $success = $false }
        }
        $success = $success -and $data.Count -gt 0
        if ($success) {
            foreach ($column in $CaseColumns) {
                if ($data[0].PSObject.Properties.Name -notcontains $column) { $success = $false }
            }
        }
        if ($success) {
            $caseGroups = @($data | Group-Object -Property $CaseColumns)
            if (@($caseGroups | Where-Object Count -gt 1).Count -gt 0) {
                $success = $false
            }
            $caseNames = ConvertTo-Json -InputObject @($caseGroups.Name | Sort-Object) -Compress
            if ($null -eq $expectedCases) { $expectedCases = $caseNames }
            if ($expectedCases -ne $caseNames) { $success = $false }
            $sampleMetrics = @($data[0].PSObject.Properties.Name | Where-Object { $_ -notin $CaseColumns })
            if ($null -eq $metrics) { $metrics = $sampleMetrics }
            if (($metrics -join ',') -ne ($sampleMetrics -join ',')) { $success = $false }
            foreach ($record in $data) {
                foreach ($metric in $sampleMetrics) {
                    $numeric = 0.0
                    if (-not [double]::TryParse($record.$metric, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $numeric) -or -not [double]::IsFinite($numeric)) {
                        $success = $false
                    }
                }
            }
        }
        if (-not $success) { $failures++; $data = @($null) }
        foreach ($record in $data) {
            $values = [ordered]@{
                TimestampUtc = $timestamp; GitCommit = $commit; GitDirty = $dirty; SourceSha256 = $sourceHash
                CpuModel = $CpuModel; OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription
                SDK = $sdk; Configuration = 'Release'; Project = $Project; Parameters = $parameters
                Phase = $phase; RunIndex = $index; ProcessElapsedSeconds = $clock.Elapsed.TotalSeconds
                Status = $(if ($success) { 'Passed' } else { 'Failed' }); ExitCode = $exitCode
            }
            foreach ($column in $CaseColumns) { $values[$column] = if ($success) { $record.$column } else { '' } }
            foreach ($metric in $metrics) { $values[$metric] = if ($success) { $record.$metric } else { '' } }
            $rows.Add([pscustomobject]$values)
        }
        # Rewrite within this unique invocation so failure rows get the same
        # columns even if the first successful sample arrives later.
        $columns = @($rows[-1].PSObject.Properties.Name)
        $rows | Select-Object -Property $columns | Export-Csv -LiteralPath $rawPath -NoTypeInformation
        Write-Host "$phase ${index}: $($rows[-1].Status)"
        if ($sourceChanged) { throw "Source changed during sampling; evidence retained in $destination." }
    }

    $aggregates = foreach ($group in @($rows | Where-Object { $_.Phase -eq 'measured' -and $_.Status -eq 'Passed' } | Group-Object -Property $CaseColumns)) {
        foreach ($metric in $metrics) {
            $values = @($group.Group | ForEach-Object { [double]::Parse($_.$metric, [Globalization.CultureInfo]::InvariantCulture) } | Sort-Object)
            $count = $values.Count
            $mean = ($values | Measure-Object -Average).Average
            $middle = [int][Math]::Floor($count / 2)
            $median = if ($count % 2) { $values[$middle] } else { ($values[$middle - 1] + $values[$middle]) / 2 }
            $squared = ($values | ForEach-Object { [Math]::Pow($_ - $mean, 2) } | Measure-Object -Sum).Sum
            $aggregate = [ordered]@{ GitCommit = $commit; SourceSha256 = $sourceHash; CpuModel = $CpuModel; Project = $Project; Parameters = $parameters }
            foreach ($column in $CaseColumns) { $aggregate[$column] = $group.Group[0].$column }
            $aggregate.Metric = $metric
            $aggregate.SampleCount = $count
            $aggregate.Mean = $mean
            $aggregate.Median = $median
            $aggregate.Minimum = $values[0]
            $aggregate.Maximum = $values[-1]
            $aggregate.SampleStandardDeviation = if ($count -gt 1) { [Math]::Sqrt($squared / ($count - 1)) } else { '' }
            $aggregate.Complete = $count -eq $Samples -and $failures -eq 0
            [pscustomobject]$aggregate
        }
    }
    if (@($aggregates).Count -gt 0) { $aggregates | Export-Csv -LiteralPath $aggregatePath -NoTypeInformation }
    else { 'Metric,SampleCount,Complete' | Set-Content -LiteralPath $aggregatePath }
    if ($failures -ne 0) { throw "$failures invocation(s) failed; all evidence retained in $destination." }
    Write-Host "Saved raw and aggregate evidence in $destination."
} finally {
    Pop-Location
}
