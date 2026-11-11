using System.Text.Json;

namespace OrchardSound;

public static class ExportPreferences
{
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Branding.SettingsDirectoryName, "preferences.json");
    public static string? ReadFolder()
    {
        try
        {
            if (!File.Exists(SettingsPath) || new FileInfo(SettingsPath).Length > 16384) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            string? folder = document.RootElement.GetProperty("ExportFolder").GetString();
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? folder : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
    public static void SaveFolder(string folder)
    {
        string target = SettingsPath; Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new { ExportFolder = Path.GetFullPath(folder) })); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
