using System.Collections.Concurrent;

namespace Tedide.Core.Navigation;

/// <summary>
/// Every symbol in the loaded projects' sources and the cc65 headers they include, kept in memory
/// for code completion - which is asked for suggestions on every keystroke, so it must never read a
/// file or parse anything itself.
/// <para>
/// Kept current in two ways: the file being edited is re-scanned from the editor's text with
/// <see cref="Update"/> once typing pauses (a millisecond or two for a cc65-sized file), and
/// <see cref="Refresh"/> - run in the background - reads every project file that's new or changed on
/// disk, then every file their includes lead to. Only headers something includes are ever read, so
/// cc65's own include folder costs nothing until a file includes from it.
/// </para>
/// <para>Safe to use from several threads: lookups read immutable <see cref="FileSymbols"/>.</para>
/// </summary>
public sealed class SymbolIndex
{
    private sealed record Settings(
        IReadOnlyList<string> ProjectFiles, IReadOnlyList<string> IncludeDirectories, IReadOnlyList<string> LibraryDirectories,
        IReadOnlySet<string> Macros, int Generation);

    private volatile Settings _settings = new([], [], [], new HashSet<string>(), 0);
    private readonly ConcurrentDictionary<string, FileSymbols> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files whose symbols come from the editor - open, perhaps with unsaved edits - which
    /// <see cref="Refresh"/> leaves alone.</summary>
    private readonly ConcurrentDictionary<string, bool> _fromEditor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The loaded projects' source files.</summary>
    public IReadOnlyList<string> ProjectFiles => _settings.ProjectFiles;

    /// <summary>
    /// What's indexed and how: the projects' source files, the include folders an include is looked
    /// for in (the projects' own, then cc65's), and the macros that decide which <c>#if</c> branches
    /// count. Files scanned under different settings are re-scanned by the next <see cref="Refresh"/>
    /// (and the editor's file by the next <see cref="Update"/>); until then they still answer.
    /// </summary>
    public void Configure(IReadOnlyList<string> projectFiles, IReadOnlyList<string> includeDirectories,
        IReadOnlyList<string> libraryDirectories, IEnumerable<string> macros)
    {
        var current = _settings;
        var macroSet = new HashSet<string>(macros, StringComparer.Ordinal);
        var unchanged = current.ProjectFiles.SequenceEqual(projectFiles, StringComparer.OrdinalIgnoreCase)
            && current.IncludeDirectories.SequenceEqual(includeDirectories, StringComparer.OrdinalIgnoreCase)
            && current.LibraryDirectories.SequenceEqual(libraryDirectories, StringComparer.OrdinalIgnoreCase)
            && current.Macros.SetEquals(macroSet);
        if (unchanged)
            return;
        _settings = new Settings([.. projectFiles], [.. includeDirectories], [.. libraryDirectories], macroSet, current.Generation + 1);
    }

    /// <summary>The symbols of <paramref name="path"/> as last scanned, or null if it hasn't been.</summary>
    public FileSymbols? Get(string path) => _files.GetValueOrDefault(path);

    /// <summary>Re-scans <paramref name="path"/> from <paramref name="text"/> - the editor's - and
    /// keeps it that way (see <see cref="Refresh"/>) until <see cref="Release"/>.</summary>
    public FileSymbols Update(string path, string text)
    {
        var settings = _settings;
        var symbols = FileSymbols.Scan(path, text, settings.Macros, (from, name, system) => Resolve(settings, from, name, system),
            DateTime.MaxValue, settings.Generation);
        _fromEditor[path] = true;
        _files[path] = symbols;
        return symbols;
    }

    /// <summary>The file is no longer open: the next <see cref="Refresh"/> goes back to the disk's copy.</summary>
    public void Release(string path)
    {
        if (_fromEditor.TryRemove(path, out _))
            _files.TryRemove(path, out _);
    }

    /// <summary>
    /// Reads every project file that's new or has changed on disk since it was scanned, then every
    /// file the indexed files include that isn't indexed yet, until nothing new turns up. Meant for a
    /// background thread; a few dozen milliseconds for a sample-sized project the first time, and
    /// little more than a timestamp check per file after that.
    /// </summary>
    public void Refresh(CancellationToken cancellationToken = default)
    {
        var settings = _settings;
        var projectFiles = new HashSet<string>(settings.ProjectFiles, StringComparer.OrdinalIgnoreCase);

        // Files no longer in any project, and not included by anything, drop out below.
        var pending = new Queue<string>(settings.ProjectFiles);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(path))
                continue;
            var symbols = _fromEditor.ContainsKey(path) ? _files.GetValueOrDefault(path) : ReadIfChanged(settings, path);
            if (symbols is null)
                continue;
            foreach (var include in symbols.Includes)
                pending.Enqueue(include);
        }

        foreach (var stale in _files.Keys.Where(p => !seen.Contains(p) && !_fromEditor.ContainsKey(p)))
            _files.TryRemove(stale, out _);
    }

    private FileSymbols? ReadIfChanged(Settings settings, string path)
    {
        DateTime stamp;
        try
        {
            if (!File.Exists(path))
            {
                _files.TryRemove(path, out _);
                return null;
            }
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _files.GetValueOrDefault(path);
        }

        if (_files.TryGetValue(path, out var existing) && existing.Stamp == stamp && existing.Generation == settings.Generation)
            return existing;
        if (CodeNavigator.ReadFromDisk(path) is not { } text)
            return existing;

        var symbols = FileSymbols.Scan(path, text, settings.Macros, (from, name, system) => Resolve(settings, from, name, system),
            stamp, settings.Generation);
        // The editor may have claimed the file while it was being read.
        if (!_fromEditor.ContainsKey(path))
            _files[path] = symbols;
        return symbols;
    }

    /// <summary>
    /// The files <paramref name="path"/> includes, directly or through other includes, nearest first
    /// - only those already indexed, so this never touches the disk.
    /// </summary>
    public IReadOnlyList<FileSymbols> Reachable(string path)
    {
        var result = new List<FileSymbols>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
        var pending = new Queue<string>(Get(path)?.Includes ?? []);
        while (pending.TryDequeue(out var next))
        {
            if (!seen.Add(next) || Get(next) is not { } symbols)
                continue;
            result.Add(symbols);
            foreach (var include in symbols.Includes)
                pending.Enqueue(include);
        }
        return result;
    }

    /// <summary>The project files that are indexed.</summary>
    public IEnumerable<FileSymbols> Projects() => ProjectFiles.Select(Get).OfType<FileSymbols>();

    /// <summary>Where an include is found: for <c>&lt;name&gt;</c> the include folders first; for
    /// <c>"name"</c> the including file's own folder first - as cc65 looks.</summary>
    private static string? Resolve(Settings settings, string from, string name, bool isSystem)
    {
        var currentDirectory = System.IO.Path.GetDirectoryName(from) ?? string.Empty;
        IEnumerable<string> searched = isSystem
            ? [.. settings.IncludeDirectories, .. settings.LibraryDirectories, currentDirectory]
            : [currentDirectory, .. settings.IncludeDirectories, .. settings.LibraryDirectories];
        foreach (var directory in searched)
        {
            var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, name));
            if (File.Exists(candidate) || settings.ProjectFiles.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }
}
