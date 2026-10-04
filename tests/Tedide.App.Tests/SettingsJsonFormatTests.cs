using System.Text.Json;
using System.Text.Json.Serialization;
using Tedide.Core;
using Tedide.Theming;

namespace Tedide.App.Tests;

/// <summary>The per-user settings files are written exactly as the reflection-based serializer did,
/// with each file's own options - see Tedide.Core.Tests' JsonFormatTests.</summary>
public class SettingsJsonFormatTests
{
    private static readonly JsonSerializerOptions Plain = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions EnumNames = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void LayoutSettings() =>
        Assert.Equal(
            JsonSerializer.Serialize(new LayoutSettings { ExplorerEditorSplitPercent = 30, TopRowHeightPercent = 60 }, Plain),
            JsonFile.Serialize(new LayoutSettings { ExplorerEditorSplitPercent = 30, TopRowHeightPercent = 60 }, AppJsonContext.Default.LayoutSettings));

    [Fact]
    public void RecentProjectsSettings()
    {
        var recent = new RecentProjectsSettings { Paths = [@"C:\Projects\CBMInfo\CBMInfo.tsln", @"C:\Projects\bounce\bounce.tproj"] };
        Assert.Equal(JsonSerializer.Serialize(recent, Plain), JsonFile.Serialize(recent, AppJsonContext.Default.RecentProjectsSettings));
    }

    [Fact]
    public void ToolchainSettings()
    {
        var toolchain = new ToolchainSettings { Cc65Home = @"C:\CC65", ViceBinDirectory = null };
        Assert.Equal(JsonSerializer.Serialize(toolchain, Plain), JsonFile.Serialize(toolchain, AppJsonContext.Default.ToolchainSettings));
    }

    [Fact]
    public void ThemeSettings_KeepsTheThemeName()
    {
        var theme = new ThemeSettings { Theme = AppTheme.SolarizedLight };
        var json = JsonFile.Serialize(theme, ThemingJsonContext.Default.ThemeSettings);

        Assert.Equal(JsonSerializer.Serialize(theme, EnumNames), json);
        Assert.Contains("\"SolarizedLight\"", json);
    }
}
