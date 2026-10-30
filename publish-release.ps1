param(
    [string]$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
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

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $appOut
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
dotnet publish (Join-Path $PSScriptRoot 'RobloxDownloader\RobloxDownloader.csproj') -c Release -o $downloaderOut
if ($LASTEXITCODE -ne 0) { throw 'Downloader publish failed.' }

$zip = Join-Path $releaseOut 'RobloxExternal-win-x64.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -LiteralPath (Join-Path $appOut 'RobloxExternal.exe') -DestinationPath $zip
$downloader = Join-Path $downloaderOut 'roblox downloader.exe'
if (-not (Test-Path -LiteralPath $downloader)) { throw 'Downloader executable was not published.' }

gh release create $tag $zip $downloader --repo Ben53679/Roblox-External-Downloader --title $tag --generate-notes --latest
if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed.' }
