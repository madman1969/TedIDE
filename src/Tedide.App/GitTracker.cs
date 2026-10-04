using Serilog;
using Tedide.Git;
using Terminal.Gui.App;

namespace Tedide.App;

/// <summary>
/// The git status of every repository the loaded projects live in (a solution's projects may sit
/// in different ones), kept current in the background: refreshes are coalesced (several requests
/// close together run one <c>git status</c>), never overlap, and run every few seconds anyway, so a
/// commit made in another terminal shows up too. <see cref="Changed"/> fires on the UI thread.
/// With no git installed, or no project in a repository, everything here stays empty.
/// </summary>
internal sealed class GitTracker
{
    private readonly Func<(IReadOnlyList<string> Directories, string? PrimaryDirectory)> _source;
    private readonly Action<Action> _onUiThread;
    private readonly Dictionary<string, GitRepository?> _repositoryByDirectory = new(StringComparer.OrdinalIgnoreCase);
    private List<GitRepository> _repositories = [];
    private bool _scheduled, _refreshing, _refreshAgain;

    /// <param name="source">The project folders, and the folder whose repository is the main one -
    /// the solution's - read on the UI thread at the start of each refresh.</param>
    public GitTracker(Func<(IReadOnlyList<string>, string?)> source, Action<Action> onUiThread)
    {
        _source = source;
        _onUiThread = onUiThread;
    }

    /// <summary>Every changed file in any of the repositories, by full path.</summary>
    public IReadOnlyDictionary<string, GitFileStatus> Files { get; private set; } = new Dictionary<string, GitFileStatus>();

    /// <summary>The solution's repository - the one the Git tab and the status bar's branch show.</summary>
    public GitRepository? Primary { get; private set; }

    public GitStatus? PrimaryStatus { get; private set; }

    public event Action? Changed;

    /// <summary>The repository holding <paramref name="path"/> - the innermost, for a nested one.</summary>
    public GitRepository? RepositoryFor(string path) =>
        _repositories.Where(r => r.Contains(path)).OrderByDescending(r => r.Root.Length).FirstOrDefault();

    /// <summary>Starts the periodic refresh. Call once, after the UI is up.</summary>
    public void Start() =>
        Application.AddTimeout(TimeSpan.FromSeconds(5), () =>
        {
            RequestRefresh();
            return true;
        });

    /// <summary>After the projects themselves change (opened, added, removed): find their
    /// repositories again rather than trusting the ones found before.</summary>
    public void ProjectsChanged()
    {
        _repositoryByDirectory.Clear();
        RequestRefresh();
    }

    /// <summary>Refreshes soon - requests in the next 300ms share one refresh. Call on the UI thread.</summary>
    public void RequestRefresh()
    {
        if (_scheduled)
            return;
        _scheduled = true;
        Application.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            _scheduled = false;
            if (_refreshing)
                _refreshAgain = true;
            else
                BackgroundTask.Watch(RefreshAsync(_source()), exception => Log.Error(exception, "Refreshing git status failed"));
            return false;
        });
    }

    private async Task RefreshAsync((IReadOnlyList<string> Directories, string? PrimaryDirectory) projects)
    {
        _refreshing = true;
        try
        {
            foreach (var directory in projects.Directories.Append(projects.PrimaryDirectory).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_repositoryByDirectory.ContainsKey(directory))
                    _repositoryByDirectory[directory] = await GitRepository.FindAsync(directory);
            }

            var repositories = _repositoryByDirectory.Values.OfType<GitRepository>()
                .DistinctBy(r => r.Root, StringComparer.OrdinalIgnoreCase).ToList();
            var files = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
            GitStatus? primaryStatus = null;
            var primary = projects.PrimaryDirectory is { } primaryDirectory ? _repositoryByDirectory.GetValueOrDefault(primaryDirectory) : null;
            foreach (var repository in repositories)
            {
                if (await repository.GetStatusAsync() is not { } status)
                    continue;
                foreach (var file in status.Files)
                    files[file.Path] = file;
                if (repository.Root == primary?.Root)
                    primaryStatus = status;
            }

            _onUiThread(() =>
            {
                _repositories = repositories;
                Files = files;
                Primary = primary;
                PrimaryStatus = primaryStatus;
                Changed?.Invoke();
            });
        }
        catch (Exception ex)
        {
            // Never worth an error dialog: the git information just stays as it was.
            Log.Warning(ex, "Refreshing git status failed");
        }
        finally
        {
            _onUiThread(() =>
            {
                _refreshing = false;
                if (_refreshAgain)
                {
                    _refreshAgain = false;
                    RequestRefresh();
                }
            });
        }
    }
}
