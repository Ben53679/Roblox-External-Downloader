using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RobloxDownloader.Checks;

internal static class Checks
{
    private static int Main(string[] args)
    {
        if (args.Contains("--show-failure"))
        {
            Console.WriteLine("INSTALLATION FAILED");
            Console.WriteLine("Another downloader is already running. Close its window, or end the stuck downloader in Task Manager, then retry.");
            Console.WriteLine("Log: %LOCALAPPDATA%\\RobloxExternal\\downloader.log");
            return 0;
        }
        try
        {
            RunAsync().GetAwaiter().GetResult();
            VerifyConsoleProgress();
            Console.WriteLine("Downloader checks passed: fresh install, Unicode paths, update, hashes, settings, offline, cancellation, lock contention, bounded setup, native shortcuts and CMD progress.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }

    private static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "RobloxDownloader-checks-" + Guid.NewGuid().ToString("N"), "Fresh user ä 中文");
        Directory.CreateDirectory(root);
        try
        {
            var fixture = new FixtureHandler();
            using var client = new HttpClient(fixture);
            var progress = new Progress();
            await File.WriteAllTextAsync(Path.Combine(root, "config.json"), "settings-preserved");
            var fresh = await Program.ResolveInstallation(client, root, progress, CancellationToken.None);
            Require(fresh.Tag == "v0.2.20" && File.ReadAllText(fresh.Executable) == "app-v0.2.20", "Fresh anonymous install failed.");
            Require(File.Exists(Path.Combine(Path.GetDirectoryName(fresh.Executable)!, "RobloxExternal.Updater.exe")), "Bundled updater missing.");
            Require(Program.ReadInstalled(root) == fresh, "Installed state did not persist.");
            var requests = fixture.PackageRequests;
            var current = await Program.ResolveInstallation(client, root, progress, CancellationToken.None);
            Require(current == fresh && fixture.PackageRequests == requests, "Current app was downloaded again.");

            fixture.Tag = "v0.2.21";
            var updated = await Program.ResolveInstallation(client, root, progress, CancellationToken.None);
            Require(updated.Tag == "v0.2.21" && File.ReadAllText(updated.Executable) == "app-v0.2.21", "New version was not installed.");
            Require(File.ReadAllText(Path.Combine(root, "config.json")) == "settings-preserved", "Update changed user settings.");
            Require(File.Exists(fresh.Executable), "Old app was removed before the caller confirmed installation success.");

            fixture.Tag = "v0.2.22";
            fixture.BadHash = true;
            var fallback = await Program.ResolveInstallation(client, root, progress, CancellationToken.None);
            Require(fallback == updated && Program.ReadInstalled(root) == updated, "Bad download replaced working installation.");
            Require(!Directory.EnumerateDirectories(Path.Combine(root, "versions"), ".download-*").Any(), "Failed download left staging directories.");
            fixture.BadHash = false;
            fixture.Offline = true;
            Require(await Program.ResolveInstallation(client, root, progress, CancellationToken.None) == updated, "Offline startup lost installed app.");
            var empty = Path.Combine(root, "new offline user");
            Directory.CreateDirectory(empty);
            try { await Program.ResolveInstallation(client, empty, progress, CancellationToken.None); throw new Exception("Fresh offline install silently succeeded."); }
            catch (InvalidOperationException ex) { Require(ex.Message.Contains("Could not install"), "Fresh network failure lacks an actionable error."); }

            using (var held = new FileStream(Path.Combine(root, "downloader.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                try { using var unexpected = await StartupSupport.AcquireLockAsync(root, progress.SetProgress, CancellationToken.None, 150); throw new Exception("Two downloaders acquired the same lock."); }
                catch (IOException ex) { Require(ex.Message.Contains("already running"), "Duplicate startup was not explained."); }
            }
            using (await StartupSupport.AcquireLockAsync(root, progress.SetProgress, CancellationToken.None)) { }

            using (var release = new ManualResetEventSlim())
            {
                Require(!await StartupSupport.RunOptionalStaAsync(() => release.Wait(), TimeSpan.FromMilliseconds(60), CancellationToken.None), "Hung optional setup blocked startup.");
                release.Set();
            }
            Require(!await StartupSupport.RunOptionalStaAsync(() => throw new UnauthorizedAccessException(), TimeSpan.FromSeconds(1), CancellationToken.None), "Disabled shell setup was fatal.");
            var shortcut = Path.Combine(root, "Roblox External.lnk");
            Require(await StartupSupport.RunOptionalStaAsync(() => StartMenuShortcut.Create(shortcut, updated.Executable, root), TimeSpan.FromSeconds(3), CancellationToken.None) && new FileInfo(shortcut).Length > 100, "Native shell shortcut creation failed.");

            var launcher = Path.Combine(root, "roblox downloader.exe");
            await File.WriteAllTextAsync(launcher, "old-launcher");
            var replacement = Path.Combine(root, "new-launcher.exe");
            await File.WriteAllTextAsync(replacement, "new-launcher");
            using (var held = new FileStream(launcher, FileMode.Open, FileAccess.Read, FileShare.Read))
                await StartupSupport.TryInstallLauncherAsync(root, replacement, _ => { }, CancellationToken.None);
            Require(File.ReadAllText(launcher) == "old-launcher", "Locked launcher was damaged.");
            await StartupSupport.TryInstallLauncherAsync(root, replacement, _ => { }, CancellationToken.None);
            Require(File.ReadAllText(launcher) == "new-launcher", "Atomic launcher replacement failed.");

            using var stalled = new StalledStream();
            try { await StartupSupport.ReadWithTimeoutAsync(stalled, new byte[1], TimeSpan.FromMilliseconds(60), CancellationToken.None); throw new Exception("Stalled download did not time out."); }
            catch (TimeoutException) { }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await Program.ResolveInstallation(client, root, progress, cancelled.Token); throw new Exception("Cancellation launched the offline fallback."); }
            catch (OperationCanceledException) { }
            try { StartupSupport.DeleteVersionDirectory(Path.Combine(root, "versions"), root); throw new Exception("Cleanup escaped the versions directory."); }
            catch (IOException) { }
        }
        finally
        {
            var parent = Directory.GetParent(root)!.FullName;
            var tempBoundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!parent.StartsWith(tempBoundary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(parent).StartsWith("RobloxDownloader-checks-")) throw new Exception("Invalid test cleanup path.");
            Directory.Delete(parent, true);
        }
    }

    private static void VerifyConsoleProgress()
    {
        using var output = new StringWriter();
        var progress = new ConsoleProgress(output);
        progress.SetVersionTag("v0.2.23");
        progress.SetProgress(5, "Checking for updates", "Connecting to GitHub");
        progress.SetProgress(100, "Ready", "v0.2.23");
        var text = output.ToString();
        Require(text.Contains("Latest release: v0.2.23") &&
            text.Contains("[  5%] Checking for updates") &&
            text.Contains("[100%] Ready"), "CMD progress output is incomplete.");
    }

    private sealed class Progress : IInstallProgress
    {
        public void SetProgress(int percent, string status, string detail) { }
        public void SetVersionTag(string tag) { }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        internal string Tag = "v0.2.20";
        internal bool Offline, BadHash;
        internal int PackageRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException("Simulated offline connection");
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                var app = archive.CreateEntry("RobloxExternal.exe");
                app.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using (var writer = new StreamWriter(app.Open())) writer.Write("app-" + Tag);
                var updater = archive.CreateEntry("RobloxExternal.Updater.exe");
                updater.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using (var writer = new StreamWriter(updater.Open())) writer.Write("bundled-updater");
            }
            var package = memory.ToArray();
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases/latest"))
            {
                var digest = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
                var json = JsonSerializer.Serialize(new { tag_name = Tag, assets = new[] { new { name = "RobloxExternal-win-x64.zip", browser_download_url = "https://github.com/fixture/package.zip", digest = "sha256:" + (BadHash ? new string('0', 64) : digest) } } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            PackageRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        }
    }

    private sealed class StalledStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default) =>
            new(new TaskCompletionSource<int>().Task); // Deliberately ignores cancellation.
    }
}
