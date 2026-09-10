using System.Data;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog listing every breakpoint in a <see cref="BreakpointsFile"/> - the fallback for
/// setting/managing breakpoints since Terminal.Gui.Editor's Editor has no clickable gutter to set
/// them from directly (confirmed via reflection against 2.5.7 - GutterOptions is a closed
/// LineNumbers/Folding enum, no pluggable renderer). "Toggle Breakpoint" (F9, set from the editor's
/// current cursor line - see AppShell) is the everyday way to add one; this dialog is for reviewing
/// and bulk-managing them. Every change (toggle/delete) saves immediately, so there's no separate
/// Save/Cancel state - only "Close".
/// </summary>
public sealed class BreakpointsDialog : Dialog
{
    private readonly BreakpointsFile _breakpoints;
    private readonly string _savePath;
    private readonly TableView _table;

    public BreakpointsDialog(BreakpointsFile breakpoints, string savePath)
    {
        _breakpoints = breakpoints;
        _savePath = savePath;

        Title = "Breakpoints";
        Width = 70;
        Height = 20;
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        _table = new TableView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3),
            FullRowSelect = true,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        Refresh();

        var toggleButton = new Button { Text = "_Toggle Enabled", X = 0, Y = Pos.AnchorEnd(1), Width = 18 };
        toggleButton.Accepting += (_, e) => { ToggleSelected(); e.Handled = true; };

        var deleteButton = new Button { Text = "_Delete", X = Pos.Right(toggleButton) + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        deleteButton.Accepting += (_, e) => { DeleteSelected(); e.Handled = true; };

        var closeButton = new Button { Text = "_Close", IsDefault = true, SchemeName = "Accent", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(1), Width = 12 };
        closeButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([_table, toggleButton, deleteButton, closeButton]);
    }

    private void Refresh()
    {
        var table = new DataTable();
        table.Columns.Add("Enabled");
        table.Columns.Add("File");
        table.Columns.Add("Line", typeof(int));
        foreach (var breakpoint in _breakpoints.Breakpoints)
            table.Rows.Add(breakpoint.Enabled ? "Yes" : "No", breakpoint.SourceFile, breakpoint.Line);
        _table.Table = new DataTableSource(table);
    }

    private void ToggleSelected()
    {
        if (SelectedIndex() is not { } index)
            return;

        var current = _breakpoints.Breakpoints[index];
        _breakpoints.Breakpoints[index] = current with { Enabled = !current.Enabled };
        SaveAndRefresh();
    }

    private void DeleteSelected()
    {
        if (SelectedIndex() is not { } index)
            return;

        _breakpoints.Breakpoints.RemoveAt(index);
        SaveAndRefresh();
    }

    private int? SelectedIndex()
    {
        var row = _table.Value?.SelectedCell.Y ?? -1;
        return row >= 0 && row < _breakpoints.Breakpoints.Count ? row : null;
    }

    private void SaveAndRefresh()
    {
        _breakpoints.Save(_savePath);
        Refresh();
    }
}
