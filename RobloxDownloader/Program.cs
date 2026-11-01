using System.Diagnostics;
using System.Drawing.Drawing2D;
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
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxExternal");
        Directory.CreateDirectory(root);
        FileStream processLock;
        try
        {
            processLock = new FileStream(Path.Combine(root, "downloader.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return; }
        using var lockHandle = processLock;

        try
        {
            InstallLauncher(root);
            if (args.Contains("--install-only", StringComparer.OrdinalIgnoreCase))
            {
                CleanupOldVersions(root, ReadInstalled(root)?.Executable);
                return;
            }

            Installed? launchTarget = null;
            Exception? fatalError = null;
            using (var installer = new InstallerForm())
            {
                installer.Shown += (_, _) =>
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            installer.SetProgress(5, "Checking for updates…", "Connecting to GitHub release server");
                            var installed = ReadInstalled(root);
                            try
                            {
                                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RobloxExternalDownloader", "1.0.3"));
                                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                                var release = await GetLatestRelease(client).ConfigureAwait(false);
                                installer.SetVersionTag(release.Tag);
                                if (installed?.Tag != release.Tag || !File.Exists(installed.Executable))
                                {
                                    installed = await DownloadAndInstall(client, root, release, installer).ConfigureAwait(false);
                                }
                                else
                                {
                                    installer.SetProgress(90, "Up to date", $"Latest version ({release.Tag}) is already installed");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log(root, "Update check failed: " + ex);
                                if (installed is null || !File.Exists(installed.Executable))
                                    throw new InvalidOperationException("Could not download the app. Check your internet connection and try again.", ex);
                                installer.SetProgress(90, "Offline mode", "Using installed version " + installed.Tag);
                            }

                            installer.SetProgress(96, "Cleaning up old versions…", "Keeping only the newest installed version");
                            CleanupOldVersions(root, installed.Executable);

                            installer.SetProgress(100, "Launching Roblox External…", installed.Tag);
                            await Task.Delay(250).ConfigureAwait(false);
                            launchTarget = installed;
                        }
                        catch (Exception ex)
                        {
                            fatalError = ex;
                        }
                        finally
                        {
                            installer.Finish();
                        }
                    });
                };
                Application.Run(installer);
            }

            if (fatalError is not null) throw fatalError;
            if (!args.Contains("--no-launch", StringComparer.OrdinalIgnoreCase) &&
                launchTarget is not null && File.Exists(launchTarget.Executable))
            {
                Process.Start(new ProcessStartInfo(launchTarget.Executable)
                {
                    WorkingDirectory = Path.GetDirectoryName(launchTarget.Executable)!,
                    UseShellExecute = false
                });
            }
        }
        catch (Exception ex)
        {
            Log(root, "Startup failed: " + ex);
            MessageBox.Show(ex.Message, "Roblox External Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private static async Task<Installed> DownloadAndInstall(HttpClient client, string root, Release release, InstallerForm installer)
    {
        var versions = Path.Combine(root, "versions");
        Directory.CreateDirectory(versions);
        var staging = Path.Combine(versions, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zipPath = Path.Combine(staging, "package.zip");
            installer.SetProgress(10, $"Downloading {release.Tag}…", "Starting package download");
            using (var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(zipPath);
                var buffer = new byte[65536];
                long downloaded = 0;
                var lastReportTick = 0L;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
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
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded package failed its SHA-256 check.");
            }

            installer.SetProgress(88, "Installing update…", $"Extracting {release.Tag}");
            var extracted = Path.Combine(staging, "app");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extracted));
            if (!File.Exists(Path.Combine(extracted, AppName)))
                throw new InvalidDataException($"The package does not contain {AppName}.");

            // Preserve the bundled updater so the app can replace this launcher
            // after our process exits, including upgrades from older launchers.
            StopRunningAppInstances(versions);
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
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
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
                    Directory.Delete(fullDir, true);
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

    private sealed class InstallerForm : Form
    {
        private static readonly Color BgDark = Color.FromArgb(14, 18, 25);
        private static readonly Color CardBg = Color.FromArgb(20, 26, 36);
        private static readonly Color BorderColor = Color.FromArgb(38, 49, 64);
        private static readonly Color AccentMint = Color.FromArgb(98, 224, 187);
        private static readonly Color TextPrimary = Color.FromArgb(236, 242, 248);
        private static readonly Color TextMuted = Color.FromArgb(142, 160, 178);

        private int progressPercent;
        private string statusText = "Initializing…";
        private string detailText = "Preparing installation workspace";
        private string versionTag = "LATEST";

        internal InstallerForm()
        {
            Text = "Roblox External Installer";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 195);
            BackColor = BgDark;
            ForeColor = TextPrimary;
            DoubleBuffered = true;
            TopMost = true;
            ShowInTaskbar = true;
        }

        internal void SetVersionTag(string tag)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(() => SetVersionTag(tag)); } catch { }
                return;
            }
            versionTag = tag.ToUpperInvariant();
            Invalidate();
        }

        internal void SetProgress(int percent, string status, string detail)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(() => SetProgress(percent, status, detail)); } catch { }
                return;
            }
            progressPercent = Math.Clamp(percent, 0, 100);
            statusText = status;
            detailText = detail;
            Invalidate();
        }

        internal void Finish()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(Finish); } catch { }
                return;
            }
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var borderPen = new Pen(BorderColor, 1);
            g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

            using var accentBrush = new SolidBrush(AccentMint);
            g.FillRectangle(accentBrush, 1, 1, Width - 2, 3);

            using var eyebrowFont = new Font("Segoe UI", 8f, FontStyle.Bold);
            using var titleFont = new Font("Segoe UI", 13.5f, FontStyle.Bold);
            using var statusFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            using var detailFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            using var pctFont = new Font("Segoe UI", 10f, FontStyle.Bold);
            using var primaryBrush = new SolidBrush(TextPrimary);
            using var mutedBrush = new SolidBrush(TextMuted);

            g.DrawString("ROBLOX EXTERNAL · AUTO UPDATER", eyebrowFont, accentBrush, 24, 20);
            g.DrawString("Installation & Update", titleFont, primaryBrush, 24, 38);

            // Version pill on top-right
            var pillSize = g.MeasureString(versionTag, eyebrowFont);
            var pillRect = new RectangleF(Width - pillSize.Width - 40, 22, pillSize.Width + 16, 22);
            using (var pillBg = new SolidBrush(CardBg))
            using (var pillBorder = new Pen(BorderColor, 1))
            {
                g.FillRectangle(pillBg, pillRect);
                g.DrawRectangle(pillBorder, pillRect.X, pillRect.Y, pillRect.Width, pillRect.Height);
            }
            g.DrawString(versionTag, eyebrowFont, accentBrush, pillRect.X + 8, pillRect.Y + 4);

            // Status card
            var cardRect = new Rectangle(24, 78, Width - 48, 92);
            using (var cardBrush = new SolidBrush(CardBg))
            {
                g.FillRectangle(cardBrush, cardRect);
                g.DrawRectangle(borderPen, cardRect);
            }

            g.DrawString(statusText, statusFont, primaryBrush, 38, 92);
            var pctLabel = $"{progressPercent}%";
            var pctWidth = g.MeasureString(pctLabel, pctFont).Width;
            g.DrawString(pctLabel, pctFont, accentBrush, Width - 38 - pctWidth, 91);

            // Progress bar track
            var barRect = new Rectangle(38, 120, Width - 76, 10);
            using (var trackBrush = new SolidBrush(Color.FromArgb(32, 42, 56)))
            {
                g.FillRectangle(trackBrush, barRect);
            }
            var fillWidth = (int)Math.Round(barRect.Width * (progressPercent / 100.0));
            if (fillWidth > 0)
            {
                g.FillRectangle(accentBrush, new Rectangle(barRect.X, barRect.Y, fillWidth, barRect.Height));
            }

            g.DrawString(detailText, detailFont, mutedBrush, 38, 140);
        }
    }
}
