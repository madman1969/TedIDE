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
    private IReadOnlyList<BuildDiagnostic> _diagnostics = [];

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
    public void SetDiagnostics(IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        _diagnostics = diagnostics;

        var table = new DataTable();
        table.Columns.Add("Severity");
        table.Columns.Add("File");
        table.Columns.Add("Line", typeof(int));
        table.Columns.Add("Message");
        foreach (var diagnostic in diagnostics)
            table.Rows.Add(diagnostic.Severity.ToString(), diagnostic.FilePath, diagnostic.Line, diagnostic.Message);

        Table = new DataTableSource(table);
    }

    private void AcceptSelection()
    {
        if (Value?.SelectedCell is not { } cell || cell.Y < 0 || cell.Y >= _diagnostics.Count)
            return;

        DiagnosticActivated?.Invoke(_diagnostics[cell.Y]);
    }
}
