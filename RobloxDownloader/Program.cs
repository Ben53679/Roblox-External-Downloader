using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RobloxDownloader;

internal static class Program
{
    private const string Repository = "Ben53679/Roblox-External-Downloader";
    private const string PackageName = "RobloxExternal-win-x64.zip";
    private const string AppName = "RobloxExternal.exe";

    private static async Task<int> Main(string[] args)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxExternal");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            try { Console.Title = "Roblox External Installer"; } catch (IOException) { }
            Console.WriteLine("ROBLOX EXTERNAL - INSTALLER / UPDATER");
            Console.WriteLine("Press Ctrl+C to cancel.\n");
            var installer = new ConsoleProgress(Console.Out);
            Installed? launchTarget = null;
            Directory.CreateDirectory(root);
            Log(root, $"Downloader 1.0.5 starting; PID {Environment.ProcessId}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            using (var processLock = await StartupSupport.AcquireLockAsync(root, installer.SetProgress, cancellation.Token))
            {
                installer.SetProgress(2, "Preparing launcher", "Setting up local files");
                await StartupSupport.TryInstallLauncherAsync(root, Environment.ProcessPath!,
                    message => Log(root, message), cancellation.Token);
                var shortcuts = await StartupSupport.RunOptionalStaAsync(() => CreateStartMenuShortcuts(root),
                    TimeSpan.FromSeconds(2), cancellation.Token);
                if (!shortcuts) Log(root, "Start menu shortcut setup timed out or failed; continuing without shortcuts.");
                if (!args.Contains("--install-only", StringComparer.OrdinalIgnoreCase))
                {
                    using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RobloxExternalDownloader", "1.0.5"));
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                    launchTarget = await ResolveInstallation(client, root, installer, cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                installer.SetProgress(96, "Cleaning up old versions", "Keeping only the newest installed version");
                CleanupOldVersions(root, launchTarget?.Executable ?? ReadInstalled(root)?.Executable);
                installer.SetProgress(100, "Ready", launchTarget?.Tag ?? "Launcher installed");
                if (!args.Contains("--no-launch", StringComparer.OrdinalIgnoreCase) && launchTarget is not null)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Process.Start(new ProcessStartInfo(launchTarget.Executable)
                    {
                        WorkingDirectory = Path.GetDirectoryName(launchTarget.Executable)!,
                        Arguments = "--from-updater",
                        UseShellExecute = false
                    });
                }
            }
            Log(root, "Startup completed.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Log(root, "Cancelled by user.");
            Console.WriteLine("\nInstallation cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Log(root, "Startup failed: " + ex);
            Console.Error.WriteLine("\nINSTALLATION FAILED");
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Log: " + Path.Combine(root, "downloader.log"));
            if (!args.Contains("--no-pause", StringComparer.OrdinalIgnoreCase) && !Console.IsInputRedirected)
            {
                Console.WriteLine("\nPress Enter to close this window.");
                Console.ReadLine();
            }
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    internal static async Task<Installed> ResolveInstallation(HttpClient client, string root, IInstallProgress installer,
        CancellationToken cancellation)
    {
        var installed = ReadInstalled(root);
        installer.SetProgress(5, "Checking for updates…", "Connecting to GitHub release server");
        Log(root, "Checking latest release.");
        try
        {
            var release = await GetLatestRelease(client, cancellation);
            installer.SetVersionTag(release.Tag);
            if (installed?.Tag != release.Tag || !File.Exists(installed.Executable))
                return await DownloadAndInstall(client, root, release, installer, cancellation);
            installer.SetProgress(90, "Up to date", $"Latest version ({release.Tag}) is already installed");
            return installed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log(root, "Update failed: " + ex);
            if (installed is null || !File.Exists(installed.Executable))
                throw new InvalidOperationException("Could not install the app: " + ex.Message + " Check your connection and retry.", ex);
            installer.SetProgress(90, "Offline mode", "Using installed version " + installed.Tag);
            return installed;
        }
    }

    internal static async Task<Release> GetLatestRelease(HttpClient client, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("The release has no version tag.");

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != PackageName) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release download URL is invalid.");
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
                throw new InvalidDataException("The release is missing a valid SHA-256 digest.");
            return new Release(tag, uri, digest[7..]);
        }
        throw new InvalidDataException($"The latest release has no {PackageName} asset.");
    }

    internal static async Task<Installed> DownloadAndInstall(HttpClient client, string root, Release release, IInstallProgress installer,
        CancellationToken cancellation)
    {
        var versions = Path.Combine(root, "versions");
        Directory.CreateDirectory(versions);
        var staging = Path.Combine(versions, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zipPath = Path.Combine(staging, "package.zip");
            installer.SetProgress(10, $"Downloading {release.Tag}…", "Starting package download");
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            using (var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token))
            {
                headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                await using var target = File.Create(zipPath);
                var buffer = new byte[65536];
                long downloaded = 0;
                var lastReportTick = 0L;
                while (true)
                {
                    var read = await StartupSupport.ReadWithTimeoutAsync(source, buffer, TimeSpan.FromSeconds(30), cancellation);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    downloaded += read;
                    var now = Environment.TickCount64;
                    if (now - lastReportTick < 30 && downloaded != totalBytes) continue;
                    lastReportTick = now;
                    var downMb = downloaded / (1024d * 1024d);
                    if (totalBytes > 0)
                    {
                        var totalMb = totalBytes / (1024d * 1024d);
                        var ratio = Math.Clamp((double)downloaded / totalBytes, 0d, 1d);
                        var pct = 12 + (int)Math.Round(ratio * 66);
                        installer.SetProgress(pct, $"Downloading {release.Tag}…", $"{downMb:0.0} MB / {totalMb:0.0} MB");
                    }
                    else
                    {
                        installer.SetProgress(45, $"Downloading {release.Tag}…", $"{downMb:0.0} MB downloaded");
                    }
                }
            }

            installer.SetProgress(82, "Verifying package…", "Checking SHA-256 signature");
            await using (var file = File.OpenRead(zipPath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
                if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded package failed its SHA-256 check.");
            }

            installer.SetProgress(88, "Installing update…", $"Extracting {release.Tag}");
            var extracted = Path.Combine(staging, "app");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extracted), cancellation);
            if (!File.Exists(Path.Combine(extracted, AppName)))
                throw new InvalidDataException($"The package does not contain {AppName}.");

            // Preserve the bundled updater so the app can replace this launcher
            // after our process exits, including upgrades from older launchers.
            cancellation.ThrowIfCancellationRequested();
            StopRunningAppInstances(versions);
            var final = Path.Combine(versions, "app-" + Guid.NewGuid().ToString("N"));
            Directory.Move(extracted, final);
            var installed = new Installed(release.Tag, Path.Combine(final, AppName));
            var statePath = Path.Combine(root, "installed.json");
            var stateTemp = statePath + ".tmp";
            await File.WriteAllTextAsync(stateTemp, JsonSerializer.Serialize(installed), cancellation);
            File.Move(stateTemp, statePath, true);
            Log(root, "Installed " + release.Tag);
            return installed;
        }
        finally
        {
            StartupSupport.DeleteVersionDirectory(versions, staging);
        }
    }

    private static void StopRunningAppInstances(string versionsRoot)
    {
        var normalizedRoot = Path.GetFullPath(versionsRoot) + Path.DirectorySeparatorChar;
        foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppName)))
        {
            using (proc)
            {
                try
                {
                    if (proc.Id == Environment.ProcessId) continue;
                    var path = proc.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch { }
            }
        }
    }

    private static void CleanupOldVersions(string root, string? keepExecutable)
    {
        try
        {
            var versions = Path.Combine(root, "versions");
            if (!Directory.Exists(versions)) return;
            var keepDirectory = !string.IsNullOrWhiteSpace(keepExecutable)
                ? Path.GetDirectoryName(Path.GetFullPath(keepExecutable))
                : null;
            foreach (var dir in Directory.GetDirectories(versions))
            {
                var fullDir = Path.GetFullPath(dir);
                if (keepDirectory is not null &&
                    fullDir.Equals(keepDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    StartupSupport.DeleteVersionDirectory(versions, fullDir);
                    Log(root, "Removed old version directory: " + Path.GetFileName(fullDir));
                }
                catch (Exception ex)
                {
                    Log(root, $"Could not remove {Path.GetFileName(fullDir)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log(root, "Version cleanup failed: " + ex.Message);
        }
    }

    internal static Installed? ReadInstalled(string root)
    {
        try
        {
            var statePath = Path.Combine(root, "installed.json");
            if (!File.Exists(statePath)) return null;
            var state = JsonSerializer.Deserialize<Installed>(File.ReadAllText(statePath));
            if (state is null) return null;
            var versions = Path.GetFullPath(Path.Combine(root, "versions")) + Path.DirectorySeparatorChar;
            var executable = Path.GetFullPath(state.Executable);
            return executable.StartsWith(versions, StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(executable).Equals(AppName, StringComparison.OrdinalIgnoreCase)
                ? state with { Executable = executable }
                : null;
        }
        catch { return null; }
    }

    private static void CreateStartMenuShortcuts(string root)
    {
        var target = Path.Combine(root, "roblox downloader.exe");

        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        Directory.CreateDirectory(programs);
        foreach (var name in new[] { "Roblox External", "Roblox Extenral" })
        {
            try
            {
                StartMenuShortcut.Create(Path.Combine(programs, name + ".lnk"), target, root);
            }
            catch (Exception ex) { Log(root, "Could not create Start menu shortcut: " + ex.Message); }
        }
    }

    private static void Log(string root, string message)
    {
        try { File.AppendAllText(Path.Combine(root, "downloader.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch { }
    }

    internal sealed record Release(string Tag, Uri DownloadUrl, string Sha256);
    internal sealed record Installed(string Tag, string Executable);

}
