using System.Text.Json;

namespace HanYeong;

sealed class AppRule
{
    public bool AutoMode { get; set; } = true;
    public bool Hotkey { get; set; } = true;
}

/// <summary>User settings, stored as JSON in %APPDATA%\HanYeong\settings.json.</summary>
sealed class Settings
{
    // HANYEONG_SETTINGS lets tests use a throwaway file.
    static readonly string FilePath = Environment.GetEnvironmentVariable("HANYEONG_SETTINGS") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HanYeong", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool AutoMode { get; set; } = true;

    /// <summary>The "running in the tray" hint is shown once, on the first manual start.</summary>
    public bool HasShownTrayHint { get; set; }

    public string ConvertHotkey { get; set; } = Hotkey.DefaultConvert.ToString();
    public string AutoToggleHotkey { get; set; } = Hotkey.DefaultAutoToggle.ToString();

    /// <summary>Words the auto mode must leave alone, either as typed (rk) or as they would become (and). Managed by the user.</summary>
    public SortedSet<string> ExceptionWords { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-app overrides keyed by executable name, e.g. "Code.exe".</summary>
    public Dictionary<string, AppRule> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"settings unreadable, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"settings save failed: {ex.Message}");
        }
    }
}
