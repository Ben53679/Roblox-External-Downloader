namespace RobloxDownloader;

internal interface IInstallProgress
{
    void SetProgress(int percent, string status, string detail);
    void SetVersionTag(string tag);
}

internal static class StartupSupport
{
    internal static async Task<FileStream> AcquireLockAsync(string root, Action<int, string, string> progress,
        CancellationToken cancellation, int waitMilliseconds = 3000)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(root, "downloader.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                // Only sharing/lock violations mean another downloader is running.
                if ((ex.HResult & 0xffff) is not (32 or 33)) throw;
                if (Environment.TickCount64 - started >= waitMilliseconds)
                    throw new IOException("Another downloader is already running. Close its window, or end the stuck roblox downloader process in Task Manager, then retry.", ex);
                progress(0, "Waiting for another downloader…", "Checking whether the earlier process is still running");
                await Task.Delay(100, cancellation);
            }
        }
    }

    internal static async Task<bool> RunOptionalStaAsync(Action action, TimeSpan timeout, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(true); }
            catch { completion.TrySetResult(false); }
        }) { IsBackground = true, Name = "Optional Start menu setup" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { return await completion.Task.WaitAsync(timeout, cancellation); }
        catch (TimeoutException) { return false; }
    }

    internal static async Task TryInstallLauncherAsync(string root, string source, Action<string> log,
        CancellationToken cancellation)
    {
        var target = Path.Combine(root, "roblox downloader.exe");
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        var temporary = target + ".update-" + Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, timeout.Token);
            File.Move(temporary, target, true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        { log("Launcher refresh unavailable; continuing from the downloaded copy: " + ex.Message); }
        finally { try { File.Delete(temporary); } catch { } }
    }

    internal static async Task<int> ReadWithTimeoutAsync(Stream source, Memory<byte> buffer, TimeSpan timeout,
        CancellationToken cancellation)
    {
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        stalled.CancelAfter(timeout);
        try { return await source.ReadAsync(buffer, stalled.Token).AsTask().WaitAsync(stalled.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("The download stopped responding. Please retry."); }
    }

    internal static void DeleteVersionDirectory(string versions, string target)
    {
        var boundary = Path.GetFullPath(versions).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(target);
        if (!resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup target is outside the version directory.");
        try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); } catch { }
    }
}
