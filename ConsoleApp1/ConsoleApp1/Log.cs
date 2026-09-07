namespace YuPinTuiSong;

/// <summary>极简日志：控制台 + logs/yyyy-MM-dd.log。</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _dir;

    public static void Init(string baseDir) => _dir = Path.Combine(baseDir, "logs");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERRO", msg);
    public static void Debug(string msg) => Write("DBUG", msg);

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch { /* 控制台不可写时忽略 */ }
            try
            {
                if (_dir == null) return;
                Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
            catch { /* 日志失败不影响主流程 */ }
        }
    }
}
