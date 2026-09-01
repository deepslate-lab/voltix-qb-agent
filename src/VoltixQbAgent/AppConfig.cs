using System.Text.Json;

namespace VoltixQbAgent;

/// <summary>
/// Local pairing config — everything else (watermarks, schedules, tuning)
/// lives server-side so the agent stays stateless across reinstalls.
/// Stored at %LOCALAPPDATA%\VoltixQbAgent\config.json.
/// </summary>
public sealed class AppConfig
{
    public string VoltixUrl { get; set; } = "";
    public string AgentKey { get; set; } = "";

    /// <summary>
    /// Optional. Empty = attach to whatever company file QuickBooks currently
    /// has open (QB must be running). A full .qbw path enables unattended
    /// mode: QBFC opens the file itself, which requires the "allow automatic
    /// login" grant in QB's Integrated Applications preferences.
    /// </summary>
    public string CompanyFilePath { get; set; } = "";

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoltixQbAgent");

    private static string FilePath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to read config: {ex.Message}");
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool IsPaired => !string.IsNullOrWhiteSpace(VoltixUrl) && !string.IsNullOrWhiteSpace(AgentKey);
}
