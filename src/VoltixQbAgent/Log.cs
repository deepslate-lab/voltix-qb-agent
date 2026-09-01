namespace VoltixQbAgent;

/// <summary>
/// Minimal rolling file logger (one file per day, 14 kept) + an in-memory
/// tail the status window renders. A daemon needs a paper trail — the manual
/// tool this agent descends from could get away without one.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Tail = new();
    public const int TailSize = 400;

    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static string[] Snapshot()
    {
        lock (Gate) return Tail.ToArray();
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
        lock (Gate)
        {
            Tail.Enqueue(line);
            while (Tail.Count > TailSize) Tail.Dequeue();
            try
            {
                var dir = Path.Combine(AppConfig.Dir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"agent-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
                CleanupOld(dir);
            }
            catch
            {
                // Logging must never take the agent down.
            }
        }
        LineWritten?.Invoke(line);
    }

    private static int _cleanupCounter;

    private static void CleanupOld(string dir)
    {
        if (++_cleanupCounter % 500 != 1) return;
        try
        {
            var files = Directory.GetFiles(dir, "agent-*.log").OrderByDescending(f => f).Skip(14);
            foreach (var f in files) File.Delete(f);
        }
        catch { /* best effort */ }
    }
}
