using System.Text.Json;

namespace Tedide.App;

/// <summary>
/// Persists user-configured toolchain paths across runs: the CC65_HOME environment variable value
/// and the VICE emulator's bin directory (see <see cref="Tedide.Build.ViceEmulator"/>). Both are
/// edited via ProjectSettingsDialog's "CC65" and "VICE" tabs, but neither is project state - they're
/// specific to this machine's toolchain install, not any one project - so like
/// <see cref="Theming.ThemeSettings"/> and <see cref="RecentProjectsSettings"/> this lives under the
/// OS's per-user application data folder rather than a .tproj/.tsln file. AppShell applies both
/// values (CC65_HOME to this process's environment, ViceBinDirectory to its <see cref="Tedide.Build.ViceEmulator"/>
/// instance) on startup and again immediately after either tab is saved.
/// </summary>
public sealed class ToolchainSettings
{
    public string? Cc65Home { get; set; }
    public string? ViceBinDirectory { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide", "toolchain.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads the previously saved settings, or defaults (both null/unset) if none have been saved
    /// yet or the file can't be read - a missing/corrupt settings file should never stop the app
    /// from starting.
    /// </summary>
    public static ToolchainSettings Load()
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<ToolchainSettings>(json, JsonOptions) ?? new ToolchainSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ToolchainSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
