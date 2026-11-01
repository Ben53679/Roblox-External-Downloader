param(
    [string]$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Notes = '',
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $SourceRoot 'RivalsExternal\RivalsExternal.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw "App project not found: $project" }

[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'The app project has no Version.' }
$tag = "v$version"
$releaseOut = Join-Path $PSScriptRoot 'release-out'
$appOut = Join-Path $releaseOut 'app'
$downloaderOut = Join-Path $releaseOut 'downloader'
New-Item -ItemType Directory -Force -Path $appOut, $downloaderOut | Out-Null

dotnet run --project (Join-Path $PSScriptRoot 'RobloxDownloader.Checks\RobloxDownloader.Checks.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Downloader regression checks failed; release cancelled.' }
dotnet run --project (Join-Path $SourceRoot 'RivalsExternal.Probe\RivalsExternal.Probe.csproj') -c Release -- --no-live
if ($LASTEXITCODE -ne 0) { throw 'App regression checks failed; release cancelled.' }

dotnet publish (Join-Path $PSScriptRoot 'RobloxDownloader\RobloxDownloader.csproj') -c Release -o $downloaderOut
if ($LASTEXITCODE -ne 0) { throw 'Downloader publish failed.' }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $appOut
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }

$downloader = Join-Path $downloaderOut 'roblox downloader.exe'
if (-not (Test-Path -LiteralPath $downloader)) { throw 'Downloader executable was not published.' }
$bundledUpdater = Join-Path $appOut 'RobloxExternal.Updater.exe'
Copy-Item -LiteralPath $downloader -Destination $bundledUpdater -Force

$zip = Join-Path $releaseOut 'RobloxExternal-win-x64.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -LiteralPath @((Join-Path $appOut 'RobloxExternal.exe'), $bundledUpdater) -DestinationPath $zip
$downloaderZip = Join-Path $releaseOut 'roblox-downloader-win-x64.zip'
if (Test-Path -LiteralPath $downloaderZip) { Remove-Item -LiteralPath $downloaderZip }
Compress-Archive -LiteralPath $downloader -DestinationPath $downloaderZip

if ([string]::IsNullOrWhiteSpace($Notes)) {
    $changelogPath = Join-Path $SourceRoot 'CHANGELOG.md'
    if (Test-Path -LiteralPath $changelogPath) {
        $rawChangelog = Get-Content -LiteralPath $changelogPath -Raw
        $escapedTag = [regex]::Escape($tag)
        if ($rawChangelog -match "(?ms)^##\s+$escapedTag\s*\r?\n(.+?)(?=^##\s+v|\z)") {
            $Notes = "### What's New in $tag`n" + $Matches[1].Trim()
        }
    }
}
if ([string]::IsNullOrWhiteSpace($Notes)) {
    $gitSubject = (git -C $SourceRoot log -1 --pretty=%B 2>$null | Out-String).Trim()
    $Notes = if ($gitSubject) { "### What's New in $tag`n- $gitSubject" } else { "### What's New in $tag" }
}
$notesFile = Join-Path $releaseOut 'release-notes.md'
Set-Content -LiteralPath $notesFile -Value $Notes -Encoding UTF8

if ($BuildOnly) {
    Write-Output "Release $tag prepared in $releaseOut"
    return
}

$repository = 'Ben53679/Roblox-External-Downloader'
$releaseCommit = (git -C $PSScriptRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve downloader source revision.' }
# Keep the update invisible to existing launchers until every asset is uploaded
# and its GitHub digest matches the local package.
gh release create $tag $zip $downloader $downloaderZip --repo $repository --target $releaseCommit --title $tag --notes-file $notesFile --draft
if ($LASTEXITCODE -ne 0) { throw 'GitHub draft release creation failed.' }
$releaseJson = gh release view $tag --repo $repository --json assets
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect release assets; release remains a draft.' }
$release = $releaseJson | ConvertFrom-Json
$expectedAssets = @{
    'RobloxExternal-win-x64.zip' = $zip
    'roblox.downloader.exe' = $downloader
    'roblox-downloader-win-x64.zip' = $downloaderZip
}
foreach ($name in $expectedAssets.Keys) {
    $asset = @($release.assets | Where-Object { $_.name -ceq $name })
    $local = Get-Item -LiteralPath $expectedAssets[$name]
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $local.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.Count -ne 1 -or $asset[0].state -ne 'uploaded' -or
        $asset[0].size -ne $local.Length -or $asset[0].digest -ne $digest) {
        throw "Release asset failed verification: $name. Release remains a draft."
    }
}
gh release edit $tag --repo $repository --draft=false --latest
if ($LASTEXITCODE -ne 0) { throw 'Could not publish the verified draft release.' }
