using System.Text.Json;

namespace ConnectMe;

/// <summary>Remembers the last iMac and choices in %AppData%\ConnectMe\settings.json.</summary>
public sealed class Settings
{
    public string LastHost { get; set; } = "";
    public string LastDeviceName { get; set; } = "";
    public int Fps { get; set; } = 30;
    public int BitrateMbps { get; set; } = 15;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConnectMe", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
