# Roblox External Downloader

Download `roblox-downloader-win-x64.zip` from the [latest release](https://github.com/Ben53679/Roblox-External-Downloader/releases/latest), extract **`roblox downloader.exe`**, and run it on Windows x64. It installs itself under `%LOCALAPPDATA%\RobloxExternal` and adds **Roblox External** and **Roblox Extenral** shortcuts to the current user's Start menu. The second name supports the requested search spelling.

On each launch, the downloader checks this repository's latest stable GitHub release. If its version tag differs from the installed one, it downloads `RobloxExternal-win-x64.zip`, verifies the SHA-256 digest reported by GitHub, installs it into a versioned folder, and starts `RobloxExternal.exe`. If GitHub cannot be reached, it starts the previously installed version. The first launch needs internet access. No GitHub sign-in is needed to download public releases.

The app's per-game and custom-profile settings remain in `%LOCALAPPDATA%\RobloxExternal\game-profiles.json`, with `config.json` kept for compatibility. Updates preserve both files. Update diagnostics are written to `%LOCALAPPDATA%\RobloxExternal\downloader.log`.

## Build the downloader

Install the .NET 10 SDK on Windows and run:

```powershell
dotnet publish RobloxDownloader/RobloxDownloader.csproj -c Release -o out
```

## Publish a new app version

The app source is maintained in a separate private repository. From a checkout containing the app source, run `publish-release.ps1` with its root path. This builds both binaries, packages the app, and creates a GitHub release. Release tags should match the version in `RivalsExternal.csproj`.

```powershell
./publish-release.ps1 -SourceRoot 'C:\path\to\app-source'
```

The downloader fetches the latest stable release. Publish each app update as a new release with `RobloxExternal-win-x64.zip`; users receive it at their next launch.

The release script uploads a draft, checks all three assets against their local sizes and SHA-256 hashes, then publishes it as the latest stable release. Incomplete uploads remain drafts and are not offered to existing users. Release notes come from the matching version in the app's `CHANGELOG.md`.
