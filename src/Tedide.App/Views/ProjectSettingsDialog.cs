using System.Collections.ObjectModel;
using Tedide.Build;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog for viewing/editing a loaded project's settings, as seven tabs sharing one Save/
/// Cancel footer: "Settings" (display name, cc65 target, output file override, extra cl65
/// arguments, include paths, preprocessor defines), "Optimizer" (the cc65 compiler optimization preset - see
/// <see cref="Cc65OptimizationLevel"/>), "Compiler" (other cc65 compile-time flags: whether to
/// emit an assembler listing file per source file, and whether to interleave C source as comments
/// in them), "Linker" (ld65 link-time flags: whether to emit a linker map file and/or a VICE-format
/// label file), "SuperCPU" (whether to launch this project in VICE's dedicated SuperCPU emulator -
/// only enabled while Target is C64, see <see cref="TedideProject.EnableSuperCpu"/>), "CC65" (the
/// CC65_HOME environment variable), and "VICE" (the VICE emulator's bin directory). Source files
/// aren't edited here - that's the Solution Explorer's right-click New File/Delete File job (see
/// <see cref="SolutionExplorerTree"/>).
/// On "Save", writes every field from the project-bound tabs onto the given
/// <see cref="TedideProject"/> in one go and persists it to disk; the "CC65"/"VICE" tabs aren't
/// project state (they're this machine's toolchain install, not any one project) so they're
/// persisted separately to <see cref="ToolchainSettings"/> instead - the caller (AppShell) reloads
/// and applies that immediately after a save, and is also responsible for refreshing anything that
/// displays project state (e.g. the Solution Explorer's "Name (target)" node text).
/// </summary>
public sealed class ProjectSettingsDialog : Dialog
{
    private readonly TextField _nameField;
    private readonly DropDownList _targetField;
    private readonly TextField _outputFileField;
    private readonly TextField _extraArgumentsField;
    private readonly TextField _includePathsField;
    private readonly TextField _preprocessorDefinesField;
    private readonly DropDownList _optimizationLevelField;
    private readonly CheckBox _generateListingField;
    private readonly CheckBox _addSourceAsCommentField;
    private readonly CheckBox _generateLinkerMapField;
    private readonly CheckBox _exportLabelsField;
    private readonly CheckBox _generateDebugInfoField;
    private readonly TextField _linkerConfigPathField;
    private readonly TextField _cc65HomeField;
    private readonly TextField _viceBinDirectoryField;
    private readonly CheckBox _enableSuperCpuField;

    /// <summary>True if the user chose Save (and the project was updated and saved to disk).</summary>
    public bool Saved { get; private set; }

    public ProjectSettingsDialog(TedideProject project)
    {
        Title = $"Project Settings - {project.Name}";
        Width = 101; // 30% wider than the original 78
        // Tall enough for the "Settings" tab's six label/field pairs, each now with a blank row
        // above and below its field - see the "every field needs clearance on all 4 sides"
        // convention - plus the Tabs control's own header/border chrome, and +2 for each tab's own
        // Padding.Thickness(2,1,2,1) below (1 row top, 1 row bottom - Tabs' border/tab-strip chrome
        // does not by itself give a tab's content view any inset from its own edges).
        Height = 42;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        // Dialogs default to Movable|Resizable in Terminal.Gui (see ViewArrangement) - fixed size
        // here since there's nothing in this static, fixed-position layout that benefits from
        // resizing.
        Arrangement &= ~ViewArrangement.Resizable;

        var tabs = new Tabs { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2) };
        var settingsTab = BuildSettingsTab(
            project, out _nameField, out _targetField, out _outputFileField, out _extraArgumentsField,
            out _includePathsField, out _preprocessorDefinesField);
        var optimizerTab = BuildOptimizerTab(project, out _optimizationLevelField);
        var compilerTab = BuildCompilerTab(project, out _generateListingField, out _addSourceAsCommentField);
        var linkerTab = BuildLinkerTab(project, out _generateLinkerMapField, out _exportLabelsField, out _generateDebugInfoField, out _linkerConfigPathField);
        var superCpuTab = BuildSuperCpuTab(project, out _enableSuperCpuField);
        var cc65Tab = BuildCc65Tab(out _cc65HomeField);
        var viceTab = BuildViceTab(out _viceBinDirectoryField);
        tabs.Add(settingsTab);
        tabs.Add(optimizerTab);
        tabs.Add(compilerTab);
        tabs.Add(linkerTab);
        tabs.Add(superCpuTab);
        tabs.Add(cc65Tab);
        tabs.Add(viceTab);

        // The SuperCPU toggle only makes sense while Target is C64 (see BuildSuperCpuTab's own
        // comment) - kept in sync live as the Settings tab's own target dropdown changes, not just
        // set once from the project's value when this dialog opened, so switching Target away from
        // C64 (or back) during this same visit greys the checkbox out (or back in) immediately.
        _targetField.TextChanged += (_, _) =>
        {
            var supportsSuperCpu = string.Equals(_targetField.Text, Cc65Target.C64.ToCl65Id(), StringComparison.OrdinalIgnoreCase);
            _enableSuperCpuField.Enabled = supportsSuperCpu;
            if (!supportsSuperCpu)
                _enableSuperCpuField.Value = CheckState.UnChecked;
        };

        // The primary action: Accent-scheme so it visually pops against the dialog's normal
        // chrome, the same accent color the app uses for the menu bar's own highlighted items.
        var saveButton = new Button { Text = "_Save", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        saveButton.Accepting += (_, e) =>
        {
            if (!Cc65TargetExtensions.TryParse(_targetField.Text, out var target))
            {
                TedideMessageBox.ErrorQuery("Invalid target", $"'{_targetField.Text}' is not a known cc65 target.", ["OK"]);
                e.Handled = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(_nameField.Text))
            {
                TedideMessageBox.ErrorQuery("Invalid name", "Name cannot be blank.", ["OK"]);
                e.Handled = true;
                return;
            }

            if (!Cc65OptimizationLevelExtensions.TryParse(_optimizationLevelField.Text, out var optimizationLevel))
            {
                TedideMessageBox.ErrorQuery("Invalid optimization level", $"'{_optimizationLevelField.Text}' is not a known optimization level.", ["OK"]);
                e.Handled = true;
                return;
            }

            project.Name = _nameField.Text.Trim();
            project.Target = target;
            project.OutputFile = string.IsNullOrWhiteSpace(_outputFileField.Text) ? null : _outputFileField.Text.Trim();
            project.ExtraArguments = _extraArgumentsField.Text
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            project.IncludePaths = _includePathsField.Text
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            project.PreprocessorDefines = _preprocessorDefinesField.Text
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            project.OptimizationLevel = optimizationLevel;
            project.GenerateAssemblyListing = _generateListingField.Value == CheckState.Checked;
            project.AddSourceAsComment = _addSourceAsCommentField.Value == CheckState.Checked;
            project.GenerateLinkerMap = _generateLinkerMapField.Value == CheckState.Checked;
            project.ExportLabels = _exportLabelsField.Value == CheckState.Checked;
            project.GenerateDebugInfo = _generateDebugInfoField.Value == CheckState.Checked;
            project.LinkerConfigPath = string.IsNullOrWhiteSpace(_linkerConfigPathField.Text) ? null : _linkerConfigPathField.Text.Trim();
            project.EnableSuperCpu = _enableSuperCpuField.Value == CheckState.Checked;
            project.Save();

            // Not project state - this machine's toolchain install, not any one project - so it's
            // persisted separately rather than onto the TedideProject above. AppShell reloads and
            // applies it (CC65_HOME to this process's environment, ViceBinDirectory to its
            // ViceEmulator instance) immediately after this dialog closes.
            new ToolchainSettings
            {
                Cc65Home = _cc65HomeField.Text.Trim() is { Length: > 0 } cc65Home ? cc65Home : null,
                ViceBinDirectory = _viceBinDirectoryField.Text.Trim() is { Length: > 0 } viceBinDirectory ? viceBinDirectory : null,
            }.Save();

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
        out TextField extraArgumentsField,
        out TextField includePathsField,
        out TextField preprocessorDefinesField)
    {
        var tab = new View { Title = " _Settings ", Width = Dim.Fill(), Height = Dim.Fill() };
        // A real Padding adornment, same as the Dialog itself uses - Tabs' own header/border
        // chrome does NOT give its content view any inset of its own, so without this every
        // field/label sat flush against the tab's edges.
        tab.Padding.Thickness = new Thickness(2, 1, 2, 1);

        // Every field below sits 2 rows under its own label (a blank row between them) and is at
        // least 2 rows above whatever follows it (likewise) - see the "every field needs
        // clearance on all 4 sides" convention.
        var nameLabel = new Label { Text = "Name:", X = 0, Y = 0 };
        nameField = new TextField { X = 0, Y = 2, Width = Dim.Fill(1), Text = project.Name };

        // Restricted to Commodore hardware - see Cc65TargetExtensions.CommodoreTargets. If the
        // project's current target falls outside that list (e.g. set by hand-editing the .tproj,
        // or before this restriction existed), it's still shown here so Save doesn't silently
        // change it - it just won't appear in the dropdown's own options.
        var targetLabel = new Label { Text = "Target (Commodore only):", X = 0, Y = 4 };
        targetField = new DropDownList
        {
            X = 0, Y = 6, Width = Dim.Fill(1),
            Source = new ListWrapper<string>(new ObservableCollection<string>(
                Cc65TargetExtensions.CommodoreTargets.Select(t => t.ToCl65Id()))),
            Text = project.Target.ToCl65Id(),
        };

        var outputLabel = new Label { Text = $"Output file (blank = {project.Name}{project.Target.DefaultOutputExtension()}):", X = 0, Y = 8 };
        outputFileField = new TextField { X = 0, Y = 10, Width = Dim.Fill(1), Text = project.OutputFile ?? string.Empty };

        var extraArgsLabel = new Label { Text = "Extra cl65 arguments:", X = 0, Y = 12 };
        extraArgumentsField = new TextField { X = 0, Y = 14, Width = Dim.Fill(1), Text = string.Join(' ', project.ExtraArguments) };

        var includePathsLabel = new Label { Text = "Include paths (-I, space-separated):", X = 0, Y = 16 };
        includePathsField = new TextField { X = 0, Y = 18, Width = Dim.Fill(1), Text = string.Join(' ', project.IncludePaths) };

        var definesLabel = new Label { Text = "Preprocessor defines (-D, space-separated, NAME or NAME=VALUE):", X = 0, Y = 20 };
        preprocessorDefinesField = new TextField { X = 0, Y = 22, Width = Dim.Fill(1), Text = string.Join(' ', project.PreprocessorDefines) };

        var infoLabel = new Label
        {
            Text = $"{project.SourceFiles.Count} source file(s) in {project.Directory}",
            X = 0, Y = 24, Width = Dim.Fill(1),
        };

        tab.Add(
            nameLabel, nameField, targetLabel, targetField, outputLabel, outputFileField,
            extraArgsLabel, extraArgumentsField, includePathsLabel, includePathsField,
            definesLabel, preprocessorDefinesField, infoLabel);
        return tab;
    }

    /// <summary>Builds the "Optimizer" tab (the <see cref="Cc65OptimizationLevel"/> preset dropdown, plus a help block explaining each flag).</summary>
    private static View BuildOptimizerTab(TedideProject project, out DropDownList optimizationLevelField)
    {
        var tab = new View { Title = " _Optimizer ", Width = Dim.Fill(), Height = Dim.Fill() };
        // See BuildSettingsTab's comment on this same line - every tab needs its own Padding.
        tab.Padding.Thickness = new Thickness(2, 1, 2, 1);

        var levelLabel = new Label { Text = "Optimization level:", X = 0, Y = 0 };
        optimizationLevelField = new DropDownList
        {
            X = 0, Y = 2, Width = Dim.Fill(1),
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
            X = 0, Y = 4, Width = Dim.Fill(1), Height = 6,
        };

        tab.Add(levelLabel, optimizationLevelField, helpLabel);
        return tab;
    }

    /// <summary>Builds the "Compiler" tab: other cc65 compile-time flags - the -l assembler listing toggle and the -T source-as-comment toggle.</summary>
    private static View BuildCompilerTab(TedideProject project, out CheckBox generateListingField, out CheckBox addSourceAsCommentField)
    {
        var tab = new View { Title = " _Compiler ", Width = Dim.Fill(), Height = Dim.Fill() };
        // See BuildSettingsTab's comment on this same line - every tab needs its own Padding.
        tab.Padding.Thickness = new Thickness(2, 1, 2, 1);

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

    /// <summary>Builds the "Linker" tab: ld65 link-time flags - the -m linker map toggle, the -Ln
    /// label file toggle, and a custom -C linker configuration file path.</summary>
    private static View BuildLinkerTab(
        TedideProject project,
        out CheckBox generateLinkerMapField,
        out CheckBox exportLabelsField,
        out CheckBox generateDebugInfoField,
        out TextField linkerConfigPathField)
    {
        var tab = new View { Title = " _Linker ", Width = Dim.Fill(), Height = Dim.Fill() };
        // See BuildSettingsTab's comment on this same line - every tab needs its own Padding.
        tab.Padding.Thickness = new Thickness(2, 1, 2, 1);

        generateLinkerMapField = new CheckBox
        {
            Text = "Generate linker map file (-m)",
            X = 0, Y = 0,
            Value = project.GenerateLinkerMap ? CheckState.Checked : CheckState.UnChecked,
        };

        var mapHelpLabel = new Label
        {
            Text = "Writes ld65's linker map (every segment's address/size and where each\n" +
                   "object file's symbols ended up) to lnk.map, next to the project file.",
            X = 0, Y = 2, Width = Dim.Fill(), Height = 2,
        };

        exportLabelsField = new CheckBox
        {
            Text = "Export labels (-Ln)",
            X = 0, Y = 5,
            Value = project.ExportLabels ? CheckState.Checked : CheckState.UnChecked,
        };

        var labelsHelpLabel = new Label
        {
            Text = "Writes a VICE-format label file ({Name}.lbl, next to the project file) that\n" +
                   "can be loaded into VICE's own monitor or another machine-language monitor\n" +
                   "that understands the same format, to resolve addresses back to symbol names.",
            X = 0, Y = 7, Width = Dim.Fill(), Height = 3,
        };

        generateDebugInfoField = new CheckBox
        {
            Text = "Generate debug info (-g / --dbgfile)",
            X = 0, Y = 11,
            Value = project.GenerateDebugInfo ? CheckState.Checked : CheckState.UnChecked,
        };

        var debugInfoHelpLabel = new Label
        {
            Text = "Embeds source-line debug info and writes it to {Name}.dbg, next to the project\n" +
                   "file - needed for source-level debugging (breakpoints, current-line highlighting).",
            X = 0, Y = 13, Width = Dim.Fill(), Height = 2,
        };

        var linkerConfigLabel = new Label { Text = "Custom linker config (-C, blank = target default):", X = 0, Y = 16 };
        linkerConfigPathField = new TextField { X = 0, Y = 18, Width = Dim.Fill(12), Text = project.LinkerConfigPath ?? string.Empty };
        var browseButton = FileBrowseButton.Create(
            linkerConfigPathField, y: 18, "Select Linker Config File",
            initialPath: DefaultLinkerConfigPath(project.Target, Environment.GetEnvironmentVariable("CC65_HOME")),
            extensions: ".cfg");

        var linkerConfigHelpLabel = new Label
        {
            Text = "A custom ld65 config typically replaces the target default above, rather than\n" +
                   "layering on top of it - see the ld65 manual for how -C and -t interact.",
            X = 0, Y = 20, Width = Dim.Fill(1), Height = 2,
        };

        tab.Add(
            generateLinkerMapField, mapHelpLabel, exportLabelsField, labelsHelpLabel,
            generateDebugInfoField, debugInfoHelpLabel,
            linkerConfigLabel, linkerConfigPathField, browseButton, linkerConfigHelpLabel);
        return tab;
    }

    /// <summary>
    /// Where cc65's own default linker config for <paramref name="target"/> would live (e.g.
    /// "vic20.cfg" for the VIC-20, next to cc65's other alternate configs for it like
    /// "vic20-32k.cfg" for a RAM-expanded machine) under <paramref name="cc65Home"/>'s own cfg/
    /// folder - what the Linker tab's "Browse" button (see BuildLinkerTab) opens to by default, so
    /// picking one of cc65's own bundled configs is a matter of browsing the same folder cl65
    /// itself already resolves target defaults from, rather than hunting for cc65's install
    /// directory by hand. A pure function (<paramref name="cc65Home"/> passed in rather than read
    /// from the environment here) purely so it's directly unit-testable, same as
    /// <see cref="Tedide.Build.ViceEmulator.BuildArguments"/> - BuildLinkerTab's own caller passes
    /// <c>Environment.GetEnvironmentVariable("CC65_HOME")</c>, which is what's actually in effect
    /// right now (<see cref="AppShell"/> applies a configured CC65_HOME to the environment at
    /// startup and on every save, but an external CC65_HOME the user set outside Tedide is honored
    /// too - see BuildCc65Tab's own comment - and only the environment reflects that, not
    /// <see cref="ToolchainSettings"/> alone). Null if <paramref name="cc65Home"/> is null/blank -
    /// nothing to point at.
    /// </summary>
    internal static string? DefaultLinkerConfigPath(Cc65Target target, string? cc65Home) =>
        string.IsNullOrEmpty(cc65Home) ? null : Path.Combine(cc65Home, "cfg", $"{target.ToCl65Id()}.cfg");

    /// <summary>
    /// Builds the "SuperCPU" tab: a single toggle for <see cref="TedideProject.EnableSuperCpu"/> -
    /// whether Build/Run/Debug should launch this project in VICE's dedicated SuperCPU emulator
    /// (xscpu64.exe) instead of the plain C64 one (see
    /// <see cref="Tedide.Build.ViceEmulator.ExecutableNameFor"/>). The SuperCPU is a C64-specific
    /// accelerator cartridge, so the checkbox only makes sense - and starts out enabled/disabled
    /// accordingly - while <paramref name="project"/>'s current <see cref="TedideProject.Target"/>
    /// is C64; the constructor keeps its <see cref="View.Enabled"/> state in sync with the Settings
    /// tab's own target dropdown afterwards, since the user can change that without closing this
    /// dialog first.
    /// </summary>
    private static View BuildSuperCpuTab(TedideProject project, out CheckBox enableSuperCpuField)
    {
        var tab = new View { Title = " _SuperCPU ", Width = Dim.Fill(), Height = Dim.Fill() };
        // See BuildSettingsTab's comment on this same line - every tab needs its own Padding.
        tab.Padding.Thickness = new Thickness(2, 1, 2, 1);

        var supportsSuperCpu = project.Target == Cc65Target.C64;

        enableSuperCpuField = new CheckBox
        {
            Text = "Enable SuperCPU support",
            X = 0, Y = 0,
            Value = project.EnableSuperCpu ? CheckState.Checked : CheckState.UnChecked,
            Enabled = supportsSuperCpu,
        };

        var helpLabel = new Label
        {
            Text = "The SuperCPU is a C64-specific accelerator cartridge (up to 20 MHz) - only\n" +
                   "available while Target (Settings tab) is C64; greyed out otherwise. When on,\n" +
                   "Build > Run Project and Debug > Start Debugging launch VICE's dedicated\n" +
                   "xscpu64.exe instead of x64sc.exe.",
            X = 0, Y = 2, Width = Dim.Fill(1), Height = 4,
        };

        tab.Add(enableSuperCpuField, helpLabel);
        return tab;
    }

    /// <summary>
    /// Builds the "CC65" tab: the CC65_HOME environment variable, shown and edited directly (not
    /// project state - see the Save handler). Blank means "unset".
    /// </summary>
    private static View BuildCc65Tab(out TextField cc65HomeField)
    {
        // No mnemonic (unlike the other three tabs) - "CC65" has no letter free to underline
        // without colliding with "_Compiler"'s C, and a digit mnemonic renders invisible
        // (foreground/background collide) when this tab is selected.
        var tab = new View { Title = " CC65 ", Width = Dim.Fill(), Height = Dim.Fill() };
        // See BuildSettingsTab's comment on this same line - every tab needs its own Padding.
        tab.Padding.Thickness = new Thickness(2, 1, 2, 1);

        // HotKeySpecifier disabled so the literal "_" in "CC65_HOME" isn't parsed as a mnemonic
        // marker (which would swallow it and color the "H" instead - Label parses hotkeys same as
        // Button/Tabs titles do).
        var homeLabel = new Label { Text = "CC65_HOME:", X = 0, Y = 0, HotKeySpecifier = new System.Text.Rune(0xFFFF) };
        cc65HomeField = new TextField
        {
            X = 0, Y = 2, Width = Dim.Fill(12),
            Text = ToolchainSettings.Load().Cc65Home ?? string.Empty,
        };
        var browseButton = DirectoryBrowseButton.Create(cc65HomeField, y: 2);

        // HotKeySpecifier disabled here too - this text also contains a literal "_" (in
        // "CC65_HOME"), same gotcha as homeLabel above.
        var helpLabel = new Label
        {
            Text = "Root directory of the cc65 installation (contains its include/ and lib/\n" +
                   "subfolders) - cl65 uses this to find target-specific headers and libraries.\n" +
                   "Saved to Tedide's settings and applied to this process's environment on Save;\n" +
                   "leave blank to unset it (won't affect a CC65_HOME set outside Tedide).",
            X = 0, Y = 4, Width = Dim.Fill(1), Height = 4,
            HotKeySpecifier = new System.Text.Rune(0xFFFF),
        };

        tab.Add(homeLabel, cc65HomeField, browseButton, helpLabel);
        return tab;
    }

    /// <summary>
    /// Builds the "VICE" tab: the directory containing the VICE emulator executables (x64sc.exe
    /// etc - see <see cref="ViceEmulator.ExecutableNameFor"/>), shown and edited directly (not
    /// project state - see the Save handler and <see cref="ToolchainSettings"/>).
    /// </summary>
    private static View BuildViceTab(out TextField viceBinDirectoryField)
    {
        var tab = new View { Title = " _VICE ", Width = Dim.Fill(), Height = Dim.Fill() };
        // See BuildSettingsTab's comment on this same line - every tab needs its own Padding.
        tab.Padding.Thickness = new Thickness(2, 1, 2, 1);

        var binLabel = new Label { Text = "VICE bin directory:", X = 0, Y = 0 };
        viceBinDirectoryField = new TextField
        {
            X = 0, Y = 2, Width = Dim.Fill(12),
            Text = ToolchainSettings.Load().ViceBinDirectory ?? ViceEmulator.DefaultBinDirectory,
        };
        var browseButton = DirectoryBrowseButton.Create(viceBinDirectoryField, y: 2);

        var helpLabel = new Label
        {
            Text = "The VICE install's bin/ folder - contains the emulator executables (x64sc.exe,\n" +
                   "xplus4.exe, etc) that Build > Run Project launches. Saved to Tedide's settings\n" +
                   "and applied immediately, so a changed path takes effect without restarting Tedide.",
            X = 0, Y = 4, Width = Dim.Fill(1), Height = 3,
        };

        tab.Add(binLabel, viceBinDirectoryField, browseButton, helpLabel);
        return tab;
    }
}
