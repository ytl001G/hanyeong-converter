namespace HanYeong;

/// <summary>Diagnostic log. Never write typed text here — only lengths, modes and events.</summary>
static class Log
{
    // HANYEONG_LOG lets tests write somewhere else than the user's log.
    public static readonly string FilePath = Environment.GetEnvironmentVariable("HANYEONG_LOG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HanYeong", "hanyeong.log");
    static readonly string Dir = Path.GetDirectoryName(FilePath)!;

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
