using System.Collections.ObjectModel;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog for viewing/editing a loaded project's settings, as three tabs sharing one Save/
/// Cancel footer: "Settings" (display name, cc65 target, output file override, extra cl65
/// arguments), "Optimizer" (the cc65 compiler optimization preset - see
/// <see cref="Cc65OptimizationLevel"/>), and "Compiler" (other cc65 compile-time flags: whether to
/// emit an assembler listing file per source file, and whether to interleave C source as comments
/// in them). Source
/// files aren't edited here - that's the
/// Solution Explorer's right-click New File/Delete File job (see <see cref="SolutionExplorerTree"/>).
/// On "Save", writes every field from all three tabs onto the given <see cref="TedideProject"/> in
/// one go and persists it to disk; the caller is responsible for refreshing anything that displays
/// project state (e.g. the Solution Explorer's "Name (target)" node text).
/// </summary>
public sealed class ProjectSettingsDialog : Dialog
{
    private readonly TextField _nameField;
    private readonly DropDownList _targetField;
    private readonly TextField _outputFileField;
    private readonly TextField _extraArgumentsField;
    private readonly DropDownList _optimizationLevelField;
    private readonly CheckBox _generateListingField;
    private readonly CheckBox _addSourceAsCommentField;

    /// <summary>True if the user chose Save (and the project was updated and saved to disk).</summary>
    public bool Saved { get; private set; }

    public ProjectSettingsDialog(TedideProject project)
    {
        Title = $"Project Settings - {project.Name}";
        Width = 78;
        Height = 26;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);

        var tabs = new Tabs { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2) };
        var settingsTab = BuildSettingsTab(project, out _nameField, out _targetField, out _outputFileField, out _extraArgumentsField);
        var optimizerTab = BuildOptimizerTab(project, out _optimizationLevelField);
        var compilerTab = BuildCompilerTab(project, out _generateListingField, out _addSourceAsCommentField);
        tabs.Add(settingsTab);
        tabs.Add(optimizerTab);
        tabs.Add(compilerTab);

        // The primary action: Accent-scheme so it visually pops against the dialog's normal
        // chrome, the same accent color the app uses for the menu bar's own highlighted items.
        var saveButton = new Button { Text = "_Save", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        saveButton.Accepting += (_, e) =>
        {
            if (!Cc65TargetExtensions.TryParse(_targetField.Text, out var target))
            {
                MessageBox.ErrorQuery(Application.Instance, "Invalid target", $"'{_targetField.Text}' is not a known cc65 target.", ["OK"]);
                e.Handled = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(_nameField.Text))
            {
                MessageBox.ErrorQuery(Application.Instance, "Invalid name", "Name cannot be blank.", ["OK"]);
                e.Handled = true;
                return;
            }

            if (!Cc65OptimizationLevelExtensions.TryParse(_optimizationLevelField.Text, out var optimizationLevel))
            {
                MessageBox.ErrorQuery(Application.Instance, "Invalid optimization level", $"'{_optimizationLevelField.Text}' is not a known optimization level.", ["OK"]);
                e.Handled = true;
                return;
            }

            project.Name = _nameField.Text.Trim();
            project.Target = target;
            project.OutputFile = string.IsNullOrWhiteSpace(_outputFileField.Text) ? null : _outputFileField.Text.Trim();
            project.ExtraArguments = _extraArgumentsField.Text
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            project.OptimizationLevel = optimizationLevel;
            project.GenerateAssemblyListing = _generateListingField.Value == CheckState.Checked;
            project.AddSourceAsComment = _addSourceAsCommentField.Value == CheckState.Checked;
            project.Save();

            Saved = true;
            Application.RequestStop(this);
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([tabs, saveButton, cancelButton]);
    }

    /// <summary>
    /// Builds the "Settings" tab (name, cc65 target, output file override, extra cl65
    /// arguments, source file count) and hands back its input fields via <see langword="out"/>
    /// parameters so the constructor can assign them to readonly instance fields.
    /// </summary>
    private static View BuildSettingsTab(
        TedideProject project,
        out TextField nameField,
        out DropDownList targetField,
        out TextField outputFileField,
        out TextField extraArgumentsField)
    {
        var tab = new View { Title = "_Settings", Width = Dim.Fill(), Height = Dim.Fill() };

        var nameLabel = new Label { Text = "Name:", X = 0, Y = 0 };
        nameField = new TextField { X = 0, Y = 1, Width = Dim.Fill(), Text = project.Name };

        // Restricted to Commodore hardware - see Cc65TargetExtensions.CommodoreTargets. If the
        // project's current target falls outside that list (e.g. set by hand-editing the .tproj,
        // or before this restriction existed), it's still shown here so Save doesn't silently
        // change it - it just won't appear in the dropdown's own options.
        var targetLabel = new Label { Text = "Target (Commodore only):", X = 0, Y = 3 };
        targetField = new DropDownList
        {
            X = 0, Y = 4, Width = Dim.Fill(),
            Source = new ListWrapper<string>(new ObservableCollection<string>(
                Cc65TargetExtensions.CommodoreTargets.Select(t => t.ToCl65Id()))),
            Text = project.Target.ToCl65Id(),
        };

        var outputLabel = new Label { Text = $"Output file (blank = {project.Name}{project.Target.DefaultOutputExtension()}):", X = 0, Y = 6 };
        outputFileField = new TextField { X = 0, Y = 7, Width = Dim.Fill(), Text = project.OutputFile ?? string.Empty };

        var extraArgsLabel = new Label { Text = "Extra cl65 arguments:", X = 0, Y = 9 };
        extraArgumentsField = new TextField { X = 0, Y = 10, Width = Dim.Fill(), Text = string.Join(' ', project.ExtraArguments) };

        var infoLabel = new Label
        {
            Text = $"{project.SourceFiles.Count} source file(s) in {project.Directory}",
            X = 0, Y = 12, Width = Dim.Fill(),
        };

        tab.Add(nameLabel, nameField, targetLabel, targetField, outputLabel, outputFileField, extraArgsLabel, extraArgumentsField, infoLabel);
        return tab;
    }

    /// <summary>Builds the "Optimizer" tab (the <see cref="Cc65OptimizationLevel"/> preset dropdown, plus a help block explaining each flag).</summary>
    private static View BuildOptimizerTab(TedideProject project, out DropDownList optimizationLevelField)
    {
        var tab = new View { Title = "_Optimizer", Width = Dim.Fill(), Height = Dim.Fill() };

        var levelLabel = new Label { Text = "Optimization level:", X = 0, Y = 0 };
        optimizationLevelField = new DropDownList
        {
            X = 0, Y = 1, Width = Dim.Fill(),
            Source = new ListWrapper<string>(new ObservableCollection<string>(
                Enum.GetValues<Cc65OptimizationLevel>().Select(l => l.DisplayName()))),
            Text = project.OptimizationLevel.DisplayName(),
        };

        var helpLabel = new Label
        {
            Text = "-O      Optimize code\n" +
                   "-Oi     Optimize code, inline functions (increases code size)\n" +
                   "-Or     Optimize code, honor the register keyword\n" +
                   "-Os     Optimize code, inline some known functions\n" +
                   "-Ox     Optimize code, extended optimizations\n" +
                   "-Oirs   Combines -Oi, -Or and -Os (most aggressive setting)",
            X = 0, Y = 3, Width = Dim.Fill(), Height = 6,
        };

        tab.Add(levelLabel, optimizationLevelField, helpLabel);
        return tab;
    }

    /// <summary>Builds the "Compiler" tab: other cc65 compile-time flags - the -l assembler listing toggle and the -T source-as-comment toggle.</summary>
    private static View BuildCompilerTab(TedideProject project, out CheckBox generateListingField, out CheckBox addSourceAsCommentField)
    {
        var tab = new View { Title = "_Compiler", Width = Dim.Fill(), Height = Dim.Fill() };

        generateListingField = new CheckBox
        {
            Text = "Generate assembly listing file (-l)",
            X = 0, Y = 0,
            Value = project.GenerateAssemblyListing ? CheckState.Checked : CheckState.UnChecked,
        };

        var listingHelpLabel = new Label
        {
            Text = "Writes an assembler listing (interleaved source and generated 6502\n" +
                   "assembly) for each source file, next to that file, e.g. src/Foo.c -> src/Foo.lst.",
            X = 0, Y = 2, Width = Dim.Fill(), Height = 2,
        };

        addSourceAsCommentField = new CheckBox
        {
            Text = "Include C source as comments in generated assembly (-T)",
            X = 0, Y = 5,
            Value = project.AddSourceAsComment ? CheckState.Checked : CheckState.UnChecked,
        };

        var sourceHelpLabel = new Label
        {
            Text = "Interleaves each C line as a comment above the 6502 instructions it\n" +
                   "compiled to - most useful together with the listing file above.",
            X = 0, Y = 7, Width = Dim.Fill(), Height = 2,
        };

        tab.Add(generateListingField, listingHelpLabel, addSourceAsCommentField, sourceHelpLabel);
        return tab;
    }
}
