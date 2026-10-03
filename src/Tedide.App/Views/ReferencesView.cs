using System.Data;
using Tedide.Core.Navigation;
using Terminal.Gui.Configuration;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// The "References" tab: a <see cref="TableView"/> of the most recent Find All References results
/// (File/Line/Code), with the definition rows picked out in the Accent colour. Activating a row raises
/// <see cref="ReferenceActivated"/> so the host can open the file at that spot - the same shape as
/// <see cref="ErrorListView"/>.
/// </summary>
public sealed class ReferencesView : TableView
{
    private IReadOnlyList<SymbolReference> _references = [];

    /// <summary>Raised when the user activates a row.</summary>
    public event Action<SymbolReference>? ReferenceActivated;

    public ReferencesView()
    {
        FullRowSelect = true;
        ViewportSettings = ViewportSettingsFlags.HasScrollBars;
        Style.RowColorGetter = args => args.RowIndex >= 0 && args.RowIndex < _references.Count && _references[args.RowIndex].IsDefinition
            ? SchemeManager.GetScheme("Accent")
            : null;
        SetReferences([], _ => string.Empty);
        Accepted += (_, _) => AcceptSelection();
    }

    /// <param name="displayPath">Turns a reference's absolute path into the shorter one shown in
    /// the File column, e.g. relative to its project.</param>
    public void SetReferences(IReadOnlyList<SymbolReference> references, Func<string, string> displayPath)
    {
        _references = references;

        var table = new DataTable();
        table.Columns.Add("File");
        table.Columns.Add("Line", typeof(int));
        table.Columns.Add("Code");
        foreach (var reference in references)
            table.Rows.Add(displayPath(reference.FilePath), reference.Line, reference.LineText.Trim());

        Table = new DataTableSource(table);
    }

    private void AcceptSelection()
    {
        if (Value?.SelectedCell is not { } cell || cell.Y < 0 || cell.Y >= _references.Count)
            return;

        ReferenceActivated?.Invoke(_references[cell.Y]);
    }
}
