using Tedide.App.Views;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Tests;

/// <summary>
/// The shell, for testing the classes behind it (<see cref="BuildCommands"/>,
/// <see cref="NavigationCommands"/>, <see cref="GitIntegration"/>): records what they write and
/// show, opens files in a real <see cref="EditorPane"/>, and answers dialogs through
/// <see cref="Dialogs"/>.
/// </summary>
internal sealed class FakeShell(EditorPane editorPane) : IShell
{
    public FakeDialogs Dialogs { get; } = new();
    IDialogs IShell.Dialogs => Dialogs;

    public List<string> Output { get; } = [];
    public List<Task> Fired { get; } = [];

    /// <summary>The actions that failed with a file error, as <see cref="AppShell"/>'s Guard reports them.</summary>
    public List<string> Failures { get; } = [];

    /// <summary>What <see cref="IShell.SaveAll"/> answers - false as if a save failed.</summary>
    public bool SaveAllSucceeds { get; set; } = true;

    public List<string> Opened { get; } = [];
    public View? PaneShown { get; private set; }
    public int OutputShown { get; private set; }

    public void AppendOutputLine(string line) => Output.Add(line);

    public void Fire(Task task, string what) => Fired.Add(task);

    /// <summary>Waits for every task started with <see cref="Fire"/>, including ones they start.</summary>
    public async Task SettleAsync()
    {
        for (var done = 0; done < Fired.Count; done++)
            await Fired[done];
    }

    public bool Guard(string action, Action body)
    {
        try
        {
            body();
            return true;
        }
        catch (Exception ex) when (AppShell.IsFileError(ex))
        {
            Failures.Add($"{action} failed: {ex.Message}");
            return false;
        }
    }

    public void OpenFile(string path) => editorPane.Open(path);

    public bool SaveFile(string path) => Guard($"Saving {Path.GetFileName(path)}", () =>
    {
        if (editorPane.Save(path) is { } notice)
            AppendOutputLine(notice);
    });

    public bool SaveAll() => SaveAllSucceeds;

    public void OpenProjectOrSolution(string path) => Opened.Add(path);

    public void ShowOutputTab() => OutputShown++;

    public void BringOutputForward() => OutputShown++;

    public void ShowPane(View content) => PaneShown = content;
}

/// <summary>Dialogs answered by the test: <see cref="Answer"/> plays the user's part in a modal
/// dialog, and message boxes take their answers from <see cref="QueryAnswers"/>.</summary>
internal sealed class FakeDialogs : IDialogs
{
    public List<Dialog> Shown { get; } = [];

    /// <summary>Called with each dialog shown, to set what the user chose. Left null, every dialog
    /// is cancelled.</summary>
    public Action<Dialog>? Answer { get; set; }

    /// <summary>The button each <see cref="Query"/> chooses, in order; null closes the box. Once
    /// empty, every query is closed.</summary>
    public Queue<int?> QueryAnswers { get; } = new();

    /// <summary>Every message box, as "title: message".</summary>
    public List<string> Messages { get; } = [];

    public List<string> Errors { get; } = [];

    public void Run(Dialog dialog)
    {
        Shown.Add(dialog);
        Answer?.Invoke(dialog);
    }

    /// <summary>The files each <see cref="PickFiles"/> chooses, in order; once empty, every picker
    /// is cancelled.</summary>
    public Queue<IReadOnlyList<string>> FilePicks { get; } = new();

    public IReadOnlyList<string> PickFiles(OpenDialog dialog) => FilePicks.TryDequeue(out var files) ? files : [];

    public int? Query(string title, string message, params string[] buttons)
    {
        Messages.Add($"{title}: {message}");
        return QueryAnswers.TryDequeue(out var answer) ? answer : null;
    }

    public int? ErrorQuery(string title, string message, params string[] buttons)
    {
        Errors.Add($"{title}: {message}");
        return 0;
    }
}
