using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Tedide.App;

/// <summary>
/// Modal dialogs and message boxes, for the classes behind the shell. The app shows them for real
/// (<see cref="TerminalDialogs"/>); a test answers them instead, since Terminal.Gui can't run a
/// modal outside a real terminal.
/// </summary>
internal interface IDialogs
{
    /// <summary>Shows <paramref name="dialog"/> until it closes; its result properties then say
    /// what was chosen.</summary>
    void Run(Dialog dialog);

    /// <summary>Shows a file picker until it closes; the files chosen, none if it was cancelled.</summary>
    IReadOnlyList<string> PickFiles(OpenDialog dialog);

    /// <summary>The index of the button chosen, or null if the box was closed without one.</summary>
    int? Query(string title, string message, params string[] buttons);

    /// <inheritdoc cref="Query"/>
    int? ErrorQuery(string title, string message, params string[] buttons);
}

internal sealed class TerminalDialogs : IDialogs
{
    public void Run(Dialog dialog) => Application.Run(dialog);

    public IReadOnlyList<string> PickFiles(OpenDialog dialog)
    {
        Application.Run(dialog);
        return dialog.FilePaths;
    }

    public int? Query(string title, string message, params string[] buttons) => TedideMessageBox.Query(title, message, buttons);

    public int? ErrorQuery(string title, string message, params string[] buttons) => TedideMessageBox.ErrorQuery(title, message, buttons);
}
