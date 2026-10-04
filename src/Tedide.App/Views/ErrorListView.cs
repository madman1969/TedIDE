using System.Data;
using Tedide.Build;
using Tedide.Core;
using Terminal.Gui.Configuration;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// The "Error List" tab: a <see cref="TableView"/> listing the most recent build's
/// <see cref="BuildDiagnostic"/>s (Severity/File/Line/Message). Activating a row - Enter, or a
/// click, since <see cref="TableView"/>'s default mouse binding activates the clicked cell -
/// raises <see cref="DiagnosticActivated"/> with the corresponding diagnostic so the host can
/// open its file and jump to the line, the same way <see cref="FindInFilesDialog"/>'s results
/// list hands its selection back to <c>AppShell</c>.
/// </summary>
public sealed class ErrorListView : TableView
{
    /// <summary>The rows shown: <see cref="_build"/>, with a checked file's own entries replaced
    /// by its <see cref="_live"/> ones.</summary>
    private IReadOnlyList<BuildDiagnostic> _diagnostics = [];

    private IReadOnlyList<BuildDiagnostic> _build = [];

    /// <summary>Checking as you type's latest results, by file - fresher than the last build's.</summary>
    private readonly Dictionary<string, IReadOnlyList<BuildDiagnostic>> _live = new(StringComparer.OrdinalIgnoreCase);

    private Func<string, string>? _displayPath;

    /// <summary>Raised when the user activates a row.</summary>
    public event Action<BuildDiagnostic>? DiagnosticActivated;

    public ErrorListView()
    {
        FullRowSelect = true;
        // Auto-shown (only appears once the diagnostics list overflows the viewport) - same as
        // the Results list in FindInFilesDialog, the Output pane, and EditorPane's editor.
        ViewportSettings = ViewportSettingsFlags.HasScrollBars;
        // Colors the whole row by severity (matching AppendOutputLine's per-line coloring in the
        // Output tab) - set once here rather than per SetDiagnostics call, since Style is a
        // separate object from Table and survives Table being reassigned; the getter reads
        // _diagnostics fresh at draw time regardless of which SetDiagnostics call populated it.
        Style.RowColorGetter = args => args.RowIndex >= 0 && args.RowIndex < _diagnostics.Count
            ? _diagnostics[args.RowIndex].Severity switch
            {
                DiagnosticSeverity.Error => SchemeManager.GetScheme("Error"),
                DiagnosticSeverity.Warning => SchemeManager.GetScheme("Warning"),
                _ => null,
            }
            : null;
        SetDiagnostics([]);
        Accepted += (_, _) => AcceptSelection();
    }

    /// <summary>Replaces the displayed list, e.g. with a fresh <see cref="BuildResult.Diagnostics"/> after each build (or an empty list at the start of one).</summary>
    /// <param name="displayPath">How to show a file path - the host shortens absolute ones to
    /// their project-relative form. As-is when not given.</param>
    /// <remarks>A build is newer than any check made before it, so its list replaces theirs.</remarks>
    public void SetDiagnostics(IReadOnlyList<BuildDiagnostic> diagnostics, Func<string, string>? displayPath = null)
    {
        _build = diagnostics;
        _live.Clear();
        Show(diagnostics, displayPath);
    }

    /// <summary>
    /// Checking as you type's results for <paramref name="file"/>: they replace that file's entries
    /// from the last build (or an earlier check) - null removes them, for a file that's been
    /// closed. Problems it found in headers the file includes are listed too, under the header.
    /// </summary>
    public void SetLiveDiagnostics(string file, IReadOnlyList<BuildDiagnostic>? diagnostics, Func<string, string>? displayPath = null)
    {
        if (diagnostics is null)
            _live.Remove(file);
        else
            _live[file] = diagnostics;
        Show(Merge(_build, _live), displayPath ?? _displayPath);
    }

    /// <summary>The build's entries, less any for a file that's been checked since, then every
    /// check's - each once, in the build's order first.</summary>
    internal static IReadOnlyList<BuildDiagnostic> Merge(IReadOnlyList<BuildDiagnostic> build,
        IReadOnlyDictionary<string, IReadOnlyList<BuildDiagnostic>> live) =>
        build.Where(d => !live.ContainsKey(d.FilePath))
            .Concat(live.Values.SelectMany(d => d))
            .Distinct()
            .ToList();

    private void Show(IReadOnlyList<BuildDiagnostic> diagnostics, Func<string, string>? displayPath)
    {
        _displayPath = displayPath;
        _diagnostics = diagnostics;

        var table = new DataTable();
        table.Columns.Add("Severity");
        // The host names the project only in a solution with more than one.
        var showProject = diagnostics.Any(d => d.Project is not null);
        if (showProject)
            table.Columns.Add("Project");
        table.Columns.Add("File");
        table.Columns.Add("Line", typeof(int));
        table.Columns.Add("Message");
        foreach (var diagnostic in diagnostics)
        {
            var file = diagnostic.FilePath.Length > 0 && displayPath is not null ? displayPath(diagnostic.FilePath) : diagnostic.FilePath;
            if (showProject)
                table.Rows.Add(diagnostic.Severity.ToString(), diagnostic.Project ?? "", file, diagnostic.Line, diagnostic.Message);
            else
                table.Rows.Add(diagnostic.Severity.ToString(), file, diagnostic.Line, diagnostic.Message);
        }

        Table = new DataTableSource(table);
    }

    private void AcceptSelection()
    {
        if (Value?.SelectedCell is not { } cell || cell.Y < 0 || cell.Y >= _diagnostics.Count)
            return;

        DiagnosticActivated?.Invoke(_diagnostics[cell.Y]);
    }
}
