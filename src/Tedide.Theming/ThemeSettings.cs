using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tedide.Theming;

/// <summary>
/// The small on-disk settings file that remembers the user's last-selected <see cref="AppTheme"/>
/// across runs. This is a per-user preference (like an IDE's own settings), not project state, so
/// it lives under the OS's per-user application data folder rather than any .tproj/.tsln file.
/// </summary>
public sealed class ThemeSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Vs2026Dark;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Loads the previously saved settings, or defaults (VS2026 Dark) if none have been saved yet
    /// or the file can't be read - a missing/corrupt settings file should never stop the app from
    /// starting.
    /// </summary>
    public static ThemeSettings Load() => Load(FilePath);

    public void Save() => Save(FilePath);

    /// <summary>The path-taking forms, so tests never touch the user's real settings file.</summary>
    internal static ThemeSettings Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ThemeSettings>(json, JsonOptions) ?? new ThemeSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ThemeSettings();
        }
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
