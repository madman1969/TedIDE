using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Git > Add Remote...: the name and address of a remote to push to - typically "origin" and a
/// new, empty GitHub repository's address, which a project made with Git > Create Repository
/// doesn't have yet.
/// </summary>
public sealed class AddRemoteDialog : Dialog
{
    internal readonly TextField _nameField;
    internal readonly TextField _urlField;
    private readonly Label _errorLabel;

    /// <summary>The remote's name, once Add was chosen with both fields filled in; else null.</summary>
    public string? RemoteName { get; internal set; }

    /// <summary>The remote's address, once Add was chosen; else null.</summary>
    public string? Url { get; internal set; }

    public AddRemoteDialog(string defaultName = "origin")
    {
        Title = "Add Remote";
        Width = 76;
        Height = 21;
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        var nameLabel = new Label { Text = "Name:", X = 0, Y = 0 };
        _nameField = new TextField { X = 0, Y = 2, Width = Dim.Fill(1), Text = defaultName };

        var urlLabel = new Label { Text = "Address:", X = 0, Y = 4 };
        _urlField = new TextField { X = 0, Y = 6, Width = Dim.Fill(1), Text = "" };

        var helpLabel = new Label
        {
            Text = "Create the repository on GitHub first, and leave it empty: no\n"
                + "README, .gitignore or licence. Then paste the HTTPS address its\n"
                + "page shows, such as https://github.com/you/MyGame.git. Push in\n"
                + "the Git tab then publishes to it.",
            X = 0, Y = 8, Width = Dim.Fill(1), Height = 4,
            HotKeySpecifier = Tedide.Theming.TerminalGuiWorkarounds.NoHotKey,
        };

        _errorLabel = new Label { Text = "", X = 0, Y = 13, Width = Dim.Fill(1), SchemeName = "Error" };

        var addButton = new Button { Text = "_Add", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        addButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (Validate(_nameField.Text, _urlField.Text) is { } problem)
            {
                _errorLabel.Text = problem;
                _errorLabel.SetNeedsDraw();
                // Back to the field to fix, rather than left on the button - once the button has
                // finished with the key, or it takes focus back.
                FocusSoon(problem.Contains("name", StringComparison.Ordinal) ? _nameField : _urlField);
                return;
            }
            RemoteName = _nameField.Text.Trim();
            Url = _urlField.Text.Trim();
            Application.RequestStop(this);
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([nameLabel, _nameField, urlLabel, _urlField, helpLabel, _errorLabel, addButton, cancelButton]);
        // The name is filled in already: start where there's typing to do.
        FocusSoon(_urlField);
    }

    /// <summary>Focuses <paramref name="field"/> on the next main-loop pass - after the dialog has
    /// opened, or after the button whose key is being handled is done.</summary>
    private static void FocusSoon(View field) =>
        Application.AddTimeout(TimeSpan.Zero, () =>
        {
            field.SetFocus();
            return false;
        });

    /// <summary>What's wrong with a remote's name and address, or null if they'll do. Git checks the
    /// address itself on the first push - this only catches the obvious.</summary>
    internal static string? Validate(string name, string url)
    {
        name = name.Trim();
        url = url.Trim();
        if (name.Length == 0)
            return "Give the remote a name - origin is the usual one.";
        if (name.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or ':' or '\\' or '~' or '^' or '?' or '*' or '['))
            return $"'{name}' can't be a remote's name - use letters, digits, '-' or '_'.";
        if (url.Length == 0)
            return "Paste the repository's address.";
        if (url.Any(char.IsWhiteSpace))
            return "The address can't contain spaces.";
        return null;
    }
}
