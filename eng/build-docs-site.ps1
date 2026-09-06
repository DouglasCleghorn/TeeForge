param([string] $OutputDirectory = '.local/teeforge-docs-site/dist')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'update-docs.ps1') -Check
$config = Get-Content -Raw (Join-Path $repoRoot 'docs/documentation.json') | ConvertFrom-Json
[xml] $project = Get-Content -Raw (Join-Path $repoRoot 'src/TeeForge/TeeForge.csproj')
$currentVersion = [string] $project.Project.PropertyGroup.Version
$origin = $config.siteUrl.TrimEnd('/')
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
if ($OutputDirectory -eq '.local/teeforge-docs-site/dist') {
    $hostingPath = Join-Path (Split-Path $outputRoot -Parent) '.openai/hosting.json'
    $hostingProfile = [IO.File]::ReadAllText((Join-Path $repoRoot 'docs/site/hosting.json'))
    if (Test-Path -LiteralPath $hostingPath) {
        $existing = Get-Content -Raw -LiteralPath $hostingPath | ConvertFrom-Json
        if ($existing.project_id -ne ($hostingProfile | ConvertFrom-Json).project_id) {
            throw 'The local documentation Site differs from the tracked hosting profile. Do not create another Site.'
        }
    }
    else {
        [IO.Directory]::CreateDirectory((Split-Path $hostingPath -Parent)) | Out-Null
        [IO.File]::WriteAllText("$hostingPath.tmp", $hostingProfile, $utf8)
        Move-Item -LiteralPath "$hostingPath.tmp" -Destination $hostingPath
    }
}
function Write-Public([string] $path, [string] $content) {
    $destination = Join-Path $outputRoot $path
    [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
    [IO.File]::WriteAllText($destination, $content, $utf8)
}
function Encode([string] $text) { return [Net.WebUtility]::HtmlEncode($text) }
$versions = @(Get-ChildItem -Path (Join-Path $repoRoot 'docs/versions/*/version.json') | ForEach-Object {
    Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
})
if (!$versions.Count) { throw 'No documentation versions were generated.' }
$search = [Collections.Generic.List[object]]::new()
$htmlPaths = [Collections.Generic.List[string]]::new()
function Render-Page([string] $title, [string] $body, [string] $version, [string] $status, [string] $path, [string] $rawPath) {
    $options = ($versions | ForEach-Object {
        $selected = if ($_.version -eq $version) { ' selected' } else { '' }
        '<option value="/' + $_.version + '/index.html"' + $selected + '>' + (Encode "$($_.version) ($($_.status))") + '</option>'
    }) -join "`n"
    $links = [ordered]@{
        'Overview' = 'index.html'; 'Usage guide' = 'agent-guide.html'; 'C# recipes' = 'recipes/index.html';
        'Public API' = 'api-reference.html'; 'Specification' = 'specification.html';
        'Replication' = 'replica-stream.html'; 'Erasure coding' = 'erasure-stream.html'; 'Multipath' = 'multipath-stream.html'
    }
    $navigation = ($links.GetEnumerator() | ForEach-Object {
        $href = "/$version/$($_.Value)"
        $active = if ($href -eq "/$path") { ' aria-current="page"' } else { '' }
        '<a href="' + $href + '"' + $active + '>' + $_.Key + '</a>'
    }) -join "`n"
    $raw = if ($rawPath) { '<a class="raw" href="/' + $rawPath + '">Read Markdown ↗</a>' } else { '<a class="raw" href="/llms.txt">Agent index ↗</a>' }
    $alternate = if ($rawPath) { '<link rel="alternate" type="text/markdown" href="' + $origin + '/' + $rawPath + '">' } else { '' }
    $html = @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>$(Encode $title) · TeeForge</title>
<meta name="description" content="TeeForge $version .NET 10 stream documentation: tested C# examples, API signatures, ownership, completion, and concurrency.">
<link rel="canonical" href="$origin/$path">$alternate<link rel="describedby" href="$origin/llms.txt">
<link rel="stylesheet" href="/style.css"><script defer src="/search.js"></script></head>
<body><a class="skip" href="#content">Skip to content</a><div class="layout">
<aside><a class="brand" href="/">Tee<span>Forge</span></a><p class="caption">Stream composition for .NET</p>
<label class="nav-label" for="versions">Documentation version</label><select class="versions" id="versions">$options</select>
<span class="nav-label">Explore</span><nav aria-label="Documentation">$navigation</nav>
<span class="nav-label">Resources</span><nav aria-label="Resources"><a href="/llms.txt">llms.txt</a><a href="/versions.json">Version manifest</a><a href="https://github.com/DouglasCleghorn/TeeForge">GitHub repository ↗</a></nav></aside>
<main id="content"><div class="topbar"><span class="status">$version · $status · .NET 10</span>$raw</div>
<label class="search-label" for="search">Search APIs and examples</label><input id="search" type="search" placeholder="Try CopyToAsync, hashes, or ownership" autocomplete="off"><div id="search-results" aria-live="polite"></div>
<article>$body</article><footer>TeeForge documentation · Match this version to your installed package. <a href="/llms.txt">Markdown index</a></footer></main></div></body></html>
"@
    Write-Public $path $html
    $htmlPaths.Add($path)
}
foreach ($versionInfo in $versions) {
    $version = $versionInfo.version
    $sourceRoot = Join-Path $repoRoot "docs/versions/$version"
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.md') {
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\', '/')
        $markdown = [IO.File]::ReadAllText($file.FullName)
        $titleMatch = [regex]::Match($markdown, '(?m)^# (.+)$')
        $title = if ($titleMatch.Success) { $titleMatch.Groups[1].Value.Trim() } else { $relative }
        $body = (ConvertFrom-Markdown -InputObject $markdown).Html
        # Retain external URLs; internal Markdown links have matching HTML routes.
        $body = [regex]::Replace($body, 'href="(?!https?://|mailto:)([^"#]+)\.md(#[^"]*)?"', 'href="$1.html$2"')
        $path = "$version/" + [IO.Path]::ChangeExtension($relative, '.html')
        Render-Page $title $body $version $versionInfo.status $path "$version/$relative"
        Write-Public "$version/$relative" $markdown
        $search.Add([ordered]@{ text = $markdown; url = "/$path"; version = $version; title = $title })
    }
    Write-Public "$version/version.json" ($versionInfo | ConvertTo-Json)
}
$landingContent = @"
<h1>One byte sequence.<br>More ways to move it.</h1>
<p class="home-lead">Choose a stream API, run a working C# example, and check the contracts that make a composition correct.</p>
<p><strong>Current documentation: $currentVersion ($($config.releaseStatus)).</strong> Unreleased documentation may describe APIs not yet available on NuGet.</p>
<div class="cards"><a class="card" href="/$currentVersion/agent-guide.html"><strong>Choose the right API →</strong><p>A compact guide to installation, stream ownership, completion, and common mistakes.</p></a>
<a class="card" href="/$currentVersion/recipes/index.html"><strong>Run five C# recipes →</strong><p>Copy to multiple destinations, hash, replicate writes, broadcast readers, and read byte ranges.</p></a>
<a class="card" href="/$currentVersion/api-reference.html"><strong>Look up a signature →</strong><p>The exact public API, grouped by namespace and checked by the compiler's API analyzer.</p></a>
<a class="card" href="/llms.txt"><strong>Read with an AI agent →</strong><p>Direct Markdown links to versioned guides, examples, and behavioral contracts.</p></a></div>
<h2>Built around ordinary .NET streams</h2><p>TeeForge targets .NET 10 and composes with Stream and System.IO.Pipelines. Start with the recipe for your task; then verify ownership, cancellation, and completion before integrating it.</p>
"@
Render-Page 'TeeForge documentation' $landingContent $currentVersion $config.releaseStatus 'index.html' ''
Write-Public 'llms.txt' ([IO.File]::ReadAllText((Join-Path $repoRoot 'llms.txt')))
Write-Public 'versions.json' (ConvertTo-Json -InputObject @($versions | ForEach-Object { [ordered]@{ url = "$origin/$($_.version)/index.html"; version = $_.version; status = $_.status; framework = $_.framework } }) -Depth 4)
Write-Public 'search-index.json' (ConvertTo-Json -InputObject $search.ToArray() -Depth 4 -Compress)
foreach ($asset in @('style.css', 'search.js')) { Write-Public $asset ([IO.File]::ReadAllText((Join-Path $repoRoot "docs/site/$asset"))) }
$sitemapEntries = ($htmlPaths | ForEach-Object { '<url><loc>' + (Encode "$origin/$_") + '</loc></url>' }) -join "`n"
Write-Public 'sitemap.xml' ('<?xml version="1.0" encoding="utf-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">' + $sitemapEntries + '</urlset>')
Write-Public 'robots.txt' ("User-agent: *`nAllow: /`nSitemap: $origin/sitemap.xml`n")
foreach ($path in $htmlPaths) {
    $html = [IO.File]::ReadAllText((Join-Path $outputRoot $path))
    foreach ($link in [regex]::Matches($html, '(?:href|src)="([^"#]+)(?:#[^"]*)?"')) {
        $href = [Net.WebUtility]::HtmlDecode($link.Groups[1].Value)
        if ($href -match '^(https?://|mailto:|data:)') { continue }
        $target = if ($href.StartsWith('/')) { Join-Path $outputRoot $href.TrimStart('/') } else { Join-Path (Split-Path (Join-Path $outputRoot $path) -Parent) $href }
        if (!(Test-Path -LiteralPath $target)) { throw "Broken local link in ${path}: $href" }
    }
}
Write-Output "Built $($htmlPaths.Count) documentation pages with Markdown, search, sitemap, and validated local links at $outputRoot."
