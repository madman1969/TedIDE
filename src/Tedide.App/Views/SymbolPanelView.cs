using System.Data;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// The "Symbols" tab: a flat, filterable table combining every module/segment/export/import parsed
/// from the active project's linker map (<see cref="LinkerMapFile"/>) and every label parsed from
/// its VICE label file (<see cref="LabelsFile"/>) - the structured alternative to reading either
/// file as plain text, which is still how they open in the editor (see
/// <see cref="Cc65LinkerMapHighlighting"/>/<see cref="Cc65LabelsHighlighting"/>). Activating a row -
/// Enter, or a click, since <see cref="TableView"/>'s default mouse binding activates the clicked
/// cell - raises <see cref="LineActivated"/> with that entry's source file and line number, the
/// same way <see cref="ErrorListView.DiagnosticActivated"/> hands a selection back to
/// <c>AppShell</c> to open and jump to.
/// </summary>
public sealed class SymbolPanelView : View
{
    private readonly TextField _filterField;
    private readonly TableView _table;
    private IReadOnlyList<SymbolRow> _allRows = [];
    private IReadOnlyList<SymbolRow> _filteredRows = [];

    /// <summary>Raised when the user activates a row, with the file to open and the 1-based line to jump to.</summary>
    public event Action<(string FilePath, int LineNumber)>? LineActivated;

    public SymbolPanelView()
    {
        _filterField = new TextField { X = 0, Y = 0, Width = Dim.Fill() };
        _filterField.TextChanged += (_, _) => ApplyFilter();

        _table = new TableView
        {
            X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(),
            FullRowSelect = true,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        _table.Accepted += (_, _) => AcceptSelection();

        Add(_filterField, _table);
        SetRows([]);
    }

    /// <summary>
    /// Re-parses the active project's lnk.map/.lbl (whichever exist and are enabled - see
    /// <see cref="TedideProject.GenerateLinkerMap"/>/<see cref="TedideProject.ExportLabels"/>) and
    /// replaces the displayed rows. Passing null (no project loaded) clears the panel.
    /// </summary>
    public void Refresh(TedideProject? project)
    {
        var rows = new List<SymbolRow>();

        if (project is { GenerateLinkerMap: true } && File.Exists(project.ResolvedMapFile))
        {
            var map = LinkerMapFile.Parse(File.ReadAllText(project.ResolvedMapFile));
            foreach (var module in map.Modules)
                rows.Add(new SymbolRow("Module", module.Name, "", $"{module.Segments.Count} segment(s)", project.ResolvedMapFile, module.MapFileLineNumber));
            foreach (var segment in map.Segments)
                rows.Add(new SymbolRow("Segment", segment.Name, segment.Start.ToString("X6"), $"End={segment.End:X6} Size={segment.Size:X6}", project.ResolvedMapFile, segment.MapFileLineNumber));
            foreach (var export in map.Exports)
                rows.Add(new SymbolRow("Export", export.Name, export.Value.ToString("X6"), export.Flags, project.ResolvedMapFile, export.MapFileLineNumber));
            foreach (var import in map.Imports)
                rows.Add(new SymbolRow("Import", import.SymbolName, "", $"defined in {import.DefiningModule}, {import.References.Count} reference(s)", project.ResolvedMapFile, import.MapFileLineNumber));
        }

        if (project is { ExportLabels: true } && File.Exists(project.ResolvedLabelsFile))
        {
            foreach (var label in LabelsFile.Parse(File.ReadAllText(project.ResolvedLabelsFile)))
                rows.Add(new SymbolRow("Label", label.Name, label.Address.ToString("X6"), "", project.ResolvedLabelsFile, label.LabelFileLineNumber));
        }

        SetRows(rows);
    }

    private void SetRows(IReadOnlyList<SymbolRow> rows)
    {
        _allRows = rows;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = _filterField.Text;
        _filteredRows = string.IsNullOrWhiteSpace(filter)
            ? _allRows
            : _allRows.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var table = new DataTable();
        table.Columns.Add("Kind");
        table.Columns.Add("Name");
        table.Columns.Add("Address");
        table.Columns.Add("Detail");
        foreach (var row in _filteredRows)
            table.Rows.Add(row.Kind, row.Name, row.Address, row.Detail);

        _table.Table = new DataTableSource(table);
    }

    private void AcceptSelection()
    {
        if (_table.Value?.SelectedCell is not { } cell || cell.Y < 0 || cell.Y >= _filteredRows.Count)
            return;

        var row = _filteredRows[cell.Y];
        LineActivated?.Invoke((row.SourceFilePath, row.SourceLineNumber));
    }

    private sealed record SymbolRow(string Kind, string Name, string Address, string Detail, string SourceFilePath, int SourceLineNumber);
}
