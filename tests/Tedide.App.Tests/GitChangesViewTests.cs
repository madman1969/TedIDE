using System.Diagnostics;
using Tedide.App.Views;
using Tedide.Git;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Tedide.App.Tests;

public sealed class GitChangesViewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-gitview-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task D_InTheChangesList_AsksToCompareTheSelectedFile()
    {
        // Rows start with their status letter, so a "D" row is what ListView's type-to-search
        // would have jumped to - swallowing the key.
        using (var git = Process.Start(new ProcessStartInfo("git", ["init", "-q"]) { WorkingDirectory = _dir, CreateNoWindow = true })!)
            await git.WaitForExitAsync();
        var path = Path.Combine(_dir, "main.c");
        File.WriteAllText(path, "x\n");
        var repository = (await GitRepository.FindAsync(_dir))!;
        var status = new GitStatus("main", null, 0, 0, true, [new GitFileStatus(path, '.', 'D')]);

        var view = new GitChangesView();
        view.SetStatus(repository, status);
        GitFileStatus? compared = null;
        view.CompareRequested += file => compared = file;

        view.SubViews.OfType<ListView>().First().NewKeyDownEvent(Key.D);

        Assert.Equal(path, compared?.Path);
    }
}
