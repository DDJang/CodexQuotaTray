namespace CodexQuotaTray.App.Services;

internal sealed class LanDiagnosticBuffer
{
    private const int MaximumEntries = 200;
    private readonly object gate = new();
    private readonly Queue<string> entries = new();

    internal void Record(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        lock (gate)
        {
            entries.Enqueue(line);
            while (entries.Count > MaximumEntries) entries.Dequeue();
        }
        System.Diagnostics.Debug.WriteLine(message);
    }

    internal string CreateDiagnosticText()
    {
        lock (gate)
        {
            return entries.Count == 0 ? "LAN 诊断: 暂无记录" : string.Join(Environment.NewLine, entries);
        }
    }
}
