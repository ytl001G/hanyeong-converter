namespace HanYeong;

/// <summary>Diagnostic log. Never write typed text here — only lengths, modes and events.</summary>
static class Log
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HanYeong");
    public static readonly string FilePath = Path.Combine(Dir, "hanyeong.log");

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
