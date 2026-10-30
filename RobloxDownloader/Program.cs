using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace RobloxDownloader;

internal static class Program
{
    private const string Repository = "Ben53679/Roblox-External-Downloader";
    private const string PackageName = "RobloxExternal-win-x64.zip";
    private const string AppName = "RobloxExternal.exe";

    [STAThread]
    private static async Task Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxExternal");
        Directory.CreateDirectory(root);
        using var mutex = new Mutex(true, @"Local\RobloxExternal.Downloader", out var firstInstance);
        if (!firstInstance) return;

        try
        {
            InstallLauncher(root);
            if (args.Contains("--install-only", StringComparer.OrdinalIgnoreCase)) return;

            var installed = ReadInstalled(root);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RobloxExternalDownloader", "1.0"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                var release = await GetLatestRelease(client);
                if (installed?.Tag != release.Tag || !File.Exists(installed.Executable))
                    installed = await DownloadAndInstall(client, root, release);
            }
            catch (Exception ex)
            {
                Log(root, "Update check failed: " + ex);
                if (installed is null || !File.Exists(installed.Executable))
                    throw new InvalidOperationException("Could not download the app. Check your internet connection and try again.", ex);
            }

            Process.Start(new ProcessStartInfo(installed.Executable)
            {
                WorkingDirectory = Path.GetDirectoryName(installed.Executable)!,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Log(root, "Startup failed: " + ex);
            MessageBox.Show(ex.Message, "Roblox External Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static async Task<Release> GetLatestRelease(HttpClient client)
    {
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
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

    private static async Task<Installed> DownloadAndInstall(HttpClient client, string root, Release release)
    {
        var versions = Path.Combine(root, "versions");
        Directory.CreateDirectory(versions);
        var staging = Path.Combine(versions, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zipPath = Path.Combine(staging, "package.zip");
            using (var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(zipPath);
                await source.CopyToAsync(target);
            }

            await using (var file = File.OpenRead(zipPath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded package failed its SHA-256 check.");
            }

            var extracted = Path.Combine(staging, "app");
            ZipFile.ExtractToDirectory(zipPath, extracted);
            if (!File.Exists(Path.Combine(extracted, AppName)))
                throw new InvalidDataException($"The package does not contain {AppName}.");

            var final = Path.Combine(versions, "app-" + Guid.NewGuid().ToString("N"));
            Directory.Move(extracted, final);
            var installed = new Installed(release.Tag, Path.Combine(final, AppName));
            var statePath = Path.Combine(root, "installed.json");
            var stateTemp = statePath + ".tmp";
            await File.WriteAllTextAsync(stateTemp, JsonSerializer.Serialize(installed));
            File.Move(stateTemp, statePath, true);
            Log(root, "Installed " + release.Tag);
            return installed;
        }
        finally
        {
            Directory.Delete(staging, true);
        }
    }

    private static Installed? ReadInstalled(string root)
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

    private static void InstallLauncher(string root)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the downloader executable.");
        var target = Path.Combine(root, "roblox downloader.exe");
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, target, true);

        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        Directory.CreateDirectory(programs);
        foreach (var name in new[] { "Roblox External", "Roblox Extenral" })
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(Path.Combine(programs, name + ".lnk"));
                shortcut.TargetPath = target;
                shortcut.WorkingDirectory = root;
                shortcut.Description = "Download and start the latest Roblox External release";
                shortcut.Save();
            }
            catch (Exception ex) { Log(root, "Could not create Start menu shortcut: " + ex.Message); }
        }
    }

    private static void Log(string root, string message)
    {
        try { File.AppendAllText(Path.Combine(root, "downloader.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch { }
    }

    private sealed record Release(string Tag, Uri DownloadUrl, string Sha256);
    private sealed record Installed(string Tag, string Executable);
}
