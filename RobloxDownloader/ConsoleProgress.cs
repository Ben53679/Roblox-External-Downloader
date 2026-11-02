namespace RobloxDownloader;

internal sealed class ConsoleProgress(TextWriter output) : IInstallProgress
{
    private readonly object gate = new();
    private string lastStatus = "";
    private int lastPercent = -1;
    private long lastPrintedAt;

    public void SetVersionTag(string tag)
    {
        lock (gate) output.WriteLine("Latest release: " + tag);
    }

    public void SetProgress(int percent, string status, string detail)
    {
        percent = Math.Clamp(percent, 0, 100);
        lock (gate)
        {
            var now = Environment.TickCount64;
            if (status == lastStatus && percent < 100 &&
                percent - lastPercent < 5 && now - lastPrintedAt < 500) return;
            output.WriteLine($"[{percent,3}%] {status} - {detail}");
            lastPercent = percent;
            lastStatus = status;
            lastPrintedAt = now;
        }
    }
}
