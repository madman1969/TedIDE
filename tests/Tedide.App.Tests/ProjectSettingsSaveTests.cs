using Tedide.App.Views;
using Tedide.Core;
using Terminal.Gui.Views;

namespace Tedide.App.Tests;

/// <summary>
/// <see cref="ProjectSettingsDialog"/> filled in and saved the way its Save button does - with its
/// CC65 and VICE tabs read from and saved to a scratch toolchain file, never the user's own.
/// </summary>
public sealed class ProjectSettingsSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-settings-").FullName;
    private readonly TedideProject _project;
    private readonly TedideProject _library;

    public ProjectSettingsSaveTests()
    {
        _project = NewProject("Game", ProjectOutputType.Application);
        _project.EnableSuperCpu = true;
        _project.LinkerConfigPath = "c64-custom.cfg";
        _project.Save();
        _library = NewProject("Gfx", ProjectOutputType.Library);
        new ToolchainSettings { Cc65Home = @"C:\cc65", ViceBinDirectory = @"D:\vice\bin" }.Save(ToolchainFile);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string ToolchainFile => Path.Combine(_dir, "toolchain.json");

    private TedideProject NewProject(string name, ProjectOutputType outputType)
    {
        var directory = Path.Combine(_dir, name);
        Directory.CreateDirectory(directory);
        var project = new TedideProject { Name = name, Target = Cc65Target.C64, OutputType = outputType, SourceFiles = ["main.c"] };
        project.Save(Path.Combine(directory, name + ".tproj"));
        return project;
    }

    private ProjectSettingsDialog Open() => new(_project, [_project, _library], ToolchainFile);

    private TedideProject Reloaded() => TedideProject.Load(_project.FilePath!);

    [Fact]
    public void TheDialog_ShowsTheProjectAndTheToolchainSettings()
    {
        var dialog = Open();

        Assert.Equal("Game", dialog._nameField.Text);
        Assert.Equal("c64", dialog._targetField.Text);
        Assert.Equal(@"C:\cc65", dialog._cc65HomeField.Text);
        Assert.Equal(@"D:\vice\bin", dialog._viceBinDirectoryField.Text);
        Assert.Equal(CheckState.Checked, dialog._enableSuperCpuField.Value);
        Assert.Equal("Gfx", Assert.Single(dialog._referenceFields).Library.Name);  // only libraries can be referenced
    }

    [Fact]
    public void Save_WritesEveryTabOntoTheProject_AndTheToolchainSettings()
    {
        var dialog = Open();
        dialog._nameField.Text = "  Shooter ";
        dialog._outputFileField.Text = "bin/shooter.prg";
        dialog._extraArgumentsField.Text = "-t c64 \"--asm-define FOO=1\"";
        dialog._includePathsField.Text = "include ../shared";
        dialog._preprocessorDefinesField.Text = "DEBUG LEVEL=2";
        dialog._optimizationLevelField.Text = Cc65OptimizationLevel.InlineKnownFunctions.DisplayName();
        dialog._generateListingField.Value = CheckState.UnChecked;
        dialog._generateLinkerMapField.Value = CheckState.Checked;
        dialog._exportLabelsField.Value = CheckState.Checked;
        dialog._generateDebugInfoField.Value = CheckState.Checked;
        dialog._useOpt6502Field.Value = CheckState.Checked;
        dialog._favourSpeedField.Value = CheckState.Checked;
        dialog._referenceFields[0].Field.Value = CheckState.Checked;
        dialog._preBuildField.Text = "echo one\n\n   \necho two";
        dialog._postBuildField.Text = "echo done";
        dialog._cc65HomeField.Text = "  ";
        dialog._viceBinDirectoryField.Text = @"E:\vice";

        Assert.Null(dialog.Save());

        Assert.True(dialog.Saved);
        var saved = Reloaded();
        Assert.Equal("Shooter", saved.Name);
        Assert.Equal("bin/shooter.prg", saved.OutputFile);
        Assert.Equal(["-t", "c64", "--asm-define FOO=1"], saved.ExtraArguments);
        Assert.Equal(["include", "../shared"], saved.IncludePaths);
        Assert.Equal(["DEBUG", "LEVEL=2"], saved.PreprocessorDefines);
        Assert.Equal(Cc65OptimizationLevel.InlineKnownFunctions, saved.OptimizationLevel);
        Assert.False(saved.GenerateAssemblyListing);
        Assert.True(saved.GenerateLinkerMap && saved.ExportLabels && saved.GenerateDebugInfo && saved.UseOpt6502);
        Assert.Equal(Opt6502Mode.Speed, saved.Opt6502Mode);
        Assert.Equal([Path.Combine("..", "Gfx", "Gfx.tproj")], saved.ProjectReferences);
        Assert.Equal(["echo one", "echo two"], saved.PreBuildCommands);
        Assert.Equal(["echo done"], saved.PostBuildCommands);
        var toolchain = ToolchainSettings.Load(ToolchainFile);
        Assert.Null(toolchain.Cc65Home);  // blank unsets it
        Assert.Equal(@"E:\vice", toolchain.ViceBinDirectory);
    }

    [Fact]
    public void ChangingTheTarget_ClearsTheLinkerConfig_AndTurnsOffSuperCpu()
    {
        var dialog = Open();

        dialog._targetField.Text = "vic20";
        Assert.Equal("", dialog._linkerConfigPathField.Text);
        Assert.False(dialog._enableSuperCpuField.Enabled);
        Assert.Null(dialog.Save());

        var saved = Reloaded();
        Assert.Equal(Cc65Target.Vic20, saved.Target);
        Assert.False(saved.EnableSuperCpu);
        Assert.Null(saved.LinkerConfigPath);

        dialog._targetField.Text = "c64";
        Assert.True(dialog._enableSuperCpuField.Enabled);
    }

    [Theory]
    [InlineData("target", "Invalid target")]
    [InlineData("name", "Invalid name")]
    [InlineData("optimization", "Invalid optimization level")]
    public void AnInvalidField_IsReported_AndNothingIsSaved(string field, string expectedTitle)
    {
        var before = File.ReadAllText(_project.FilePath!);
        var dialog = Open();
        switch (field)
        {
            case "target": dialog._targetField.Text = "spectrum"; break;
            case "name": dialog._nameField.Text = "   "; break;
            default: dialog._optimizationLevelField.Text = "-O9"; break;
        }

        Assert.Equal(expectedTitle, dialog.Save()?.Title);

        Assert.False(dialog.Saved);
        Assert.Equal(before, File.ReadAllText(_project.FilePath!));
    }

    [Fact]
    public void AReferenceToAProjectOutsideTheSolution_IsKept()
    {
        _project.ProjectReferences = [Path.Combine("..", "Elsewhere", "Elsewhere.tproj"), Path.Combine("..", "Gfx", "Gfx.tproj")];
        var dialog = Open();
        Assert.Equal(CheckState.Checked, dialog._referenceFields[0].Field.Value);

        dialog._referenceFields[0].Field.Value = CheckState.UnChecked;
        Assert.Null(dialog.Save());

        Assert.Equal([Path.Combine("..", "Elsewhere", "Elsewhere.tproj")], Reloaded().ProjectReferences);
    }

    [Fact]
    public void AFailedSave_IsReported_AndTheDialogStaysOpen()
    {
        File.SetAttributes(_project.FilePath!, FileAttributes.ReadOnly);
        var dialog = Open();

        var problem = dialog.Save();

        Assert.Equal("Could Not Save", problem?.Title);
        Assert.False(dialog.Saved);
    }
}
