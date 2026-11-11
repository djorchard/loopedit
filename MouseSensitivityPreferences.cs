using System.Text.Json;

namespace OrchardSound;

public sealed record MouseSensitivitySettings(bool Enabled = false, int Speed = 10);
public static class MouseSensitivityPreferences
{
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Branding.SettingsDirectoryName, "mouse-sensitivity.json");
    public static MouseSensitivitySettings Read(string? path = null)
    {
        try
        {
            path ??= SettingsPath;
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return new();
            var value = JsonSerializer.Deserialize<MouseSensitivitySettings>(File.ReadAllText(path));
            return value != null && value.Speed is >= 1 and <= 20 ? value : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static void Save(MouseSensitivitySettings settings, string? path = null)
    {
        if (settings.Speed is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(settings));
        path ??= SettingsPath; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
