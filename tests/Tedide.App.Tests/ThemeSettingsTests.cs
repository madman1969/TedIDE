using Tedide.Theming;

namespace Tedide.App.Tests;

/// <summary>
/// The remembered theme choice: a missing or damaged settings file must never stop either app
/// from starting, and the file keeps the theme's name (not its number) so reordering the enum
/// can't silently switch someone's theme.
/// </summary>
public sealed class ThemeSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TedideThemeSettingsTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "Tedide", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsTheTheme_CreatingTheFolder()
    {
        new ThemeSettings { Theme = AppTheme.SolarizedLight }.Save(SettingsPath);

        Assert.Equal(AppTheme.SolarizedLight, ThemeSettings.Load(SettingsPath).Theme);
    }

    [Fact]
    public void Save_WritesTheThemeByName()
    {
        new ThemeSettings { Theme = AppTheme.Commodore64 }.Save(SettingsPath);

        Assert.Contains("\"Commodore64\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Load_ReadsAFileWrittenByHand()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "Theme": "AmberPhosphor" }""");

        Assert.Equal(AppTheme.AmberPhosphor, ThemeSettings.Load(SettingsPath).Theme);
    }

    [Theory]
    [InlineData(null)]                              // no file yet (first run)
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{ "Theme": "NoSuchTheme" }""")]
    [InlineData("null")]
    public void Load_FallsBackToTheDefault_WhenTheFileIsMissingOrDamaged(string? contents)
    {
        if (contents is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, contents);
        }

        Assert.Equal(AppTheme.Vs2026Dark, ThemeSettings.Load(SettingsPath).Theme);
    }

    [Fact]
    public void Load_FallsBackToTheDefault_WhenThePathIsADirectory()
    {
        Directory.CreateDirectory(SettingsPath);

        Assert.Equal(AppTheme.Vs2026Dark, ThemeSettings.Load(SettingsPath).Theme);
    }

    [Fact]
    public void Save_Throws_WhenTheFileCantBeWritten()
    {
        // ThemeSwitcher.Apply relies on this to report it via SaveFailed rather than crash.
        Directory.CreateDirectory(SettingsPath);

        Assert.ThrowsAny<Exception>(() => new ThemeSettings().Save(SettingsPath));
    }

    [Fact]
    public void EveryTheme_HasItsOwnDisplayName()
    {
        var names = Enum.GetValues<AppTheme>().Select(t => t.DisplayName()).ToList();

        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Count, names.Distinct().Count());
        // A theme added without a display name falls back to its enum identifier - fine for
        // "Monokai", but a multi-word one would show as e.g. "SolarizedDark" in the Theme menu.
        var multiWord = Enum.GetValues<AppTheme>().Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.ToString(), "[a-z0-9][A-Z]"));
        Assert.All(multiWord, theme => Assert.NotEqual(theme.ToString(), theme.DisplayName()));
    }

    [Theory]
    [InlineData(AppTheme.Vs2026Dark, "VS2026 Dark")]
    [InlineData(AppTheme.BorlandTurboC, "Borland Turbo C")]
    [InlineData(AppTheme.Commodore64, "Commodore 64")]
    public void DisplayName_IsTheHumanReadableName(AppTheme theme, string expected)
    {
        Assert.Equal(expected, theme.DisplayName());
    }
}
