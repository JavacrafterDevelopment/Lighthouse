using System.Text;
using System.Text.Json;

namespace Lighthouse;

/// <summary>
/// User preferences, kept in data\settings.json beside the executable so they
/// travel with the folder like everything else. Written by hand rather than
/// through a serialiser: there are two fields, and a corrupt or half-written file
/// should never stop the app from starting.
/// </summary>
public sealed class Settings
{
    /// <summary>Closing hides to the tray instead of quitting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>"light" or "dark".</summary>
    public string Theme { get; set; } = "light";

    public bool IsDark => Theme.Equals("dark", StringComparison.OrdinalIgnoreCase);

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "data", "settings.json");

    public static Settings Load()
    {
        var settings = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return settings;

            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;

            if (root.TryGetProperty("closeToTray", out var tray) &&
                tray.ValueKind is JsonValueKind.True or JsonValueKind.False)
                settings.CloseToTray = tray.GetBoolean();

            if (root.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.String)
            {
                string value = theme.GetString() ?? "light";
                settings.Theme = value.Equals("dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
            }
        }
        catch
        {
            // Unreadable or malformed: fall back to defaults rather than refusing to run.
        }
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteBoolean("closeToTray", CloseToTray);
                w.WriteString("theme", IsDark ? "dark" : "light");
                w.WriteEndObject();
            }

            File.WriteAllText(FilePath, Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch
        {
            // Read-only location (a locked-down USB stick, say): keep the settings for
            // this session and carry on.
        }
    }
}
