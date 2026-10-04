using Terminal.Gui.ViewBase;

namespace Tedide.App;

/// <summary>
/// The shell services <see cref="NavigationCommands"/>, <see cref="BuildCommands"/> and
/// <see cref="GitIntegration"/> share: output, dialogs, background work, error handling, files and
/// the bottom pane. <see cref="AppShell"/> implements it.
/// </summary>
internal interface IShell
{
    IDialogs Dialogs { get; }

    void AppendOutputLine(string line);

    /// <summary>Starts a task without waiting for it, reporting a failure.</summary>
    void Fire(Task task, string what);

    /// <summary>Runs <paramref name="body"/>, reporting a file error in a dialog instead of
    /// crashing; false if it failed.</summary>
    bool Guard(string action, Action body);

    void OpenFile(string path);
    bool SaveFile(string path);
    bool SaveAll();
    void OpenProjectOrSolution(string path);

    /// <summary>Output in front, with focus.</summary>
    void ShowOutputTab();

    /// <summary>Output in front, focus left where it is.</summary>
    void BringOutputForward();

    /// <summary>The bottom-pane tab holding <paramref name="content"/> in front, with focus.</summary>
    void ShowPane(View content);
}
