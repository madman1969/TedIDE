namespace Tedide.Core.Navigation;

/// <summary>A caret position in a source file: 1-based line and column, in characters.</summary>
public readonly record struct CaretLocation(string FilePath, int Line, int Column);

/// <summary>
/// Navigate Backward/Forward, as in Visual Studio: the places the caret jumped away from (Go To
/// Definition, a reference, a Find in Files result, Go To Line, ...). Ordinary caret moves and
/// typing aren't recorded, only where each jump left from, so Backward returns there.
/// </summary>
public sealed class NavigationHistory
{
    /// <summary>The oldest entries drop off past this many.</summary>
    public const int MaxEntries = 100;

    private readonly List<CaretLocation> _back = [];
    private readonly List<CaretLocation> _forward = [];

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Records the place a jump is leaving. A new jump clears Forward, as in a browser,
    /// and the same place twice in a row is kept once.</summary>
    public void RecordJump(CaretLocation from)
    {
        _forward.Clear();
        Push(_back, from);
    }

    /// <summary>The place before the last jump, or null if there's none. <paramref name="current"/>
    /// (null if no file is open) is kept for <see cref="GoForward"/>.</summary>
    public CaretLocation? GoBack(CaretLocation? current) => Move(_back, _forward, current);

    /// <summary>Undoes a <see cref="GoBack"/>: the place it left, or null if there's none.</summary>
    public CaretLocation? GoForward(CaretLocation? current) => Move(_forward, _back, current);

    /// <summary>Points every entry for <paramref name="oldPath"/> - a file, or a folder and
    /// everything in it - at <paramref name="newPath"/>, after a rename.</summary>
    public void MovePath(string oldPath, string newPath)
    {
        Rewrite(_back, oldPath, newPath);
        Rewrite(_forward, oldPath, newPath);
    }

    /// <summary>Drops every entry for a deleted file.</summary>
    public void RemoveFile(string path)
    {
        _back.RemoveAll(l => SamePath(l.FilePath, path));
        _forward.RemoveAll(l => SamePath(l.FilePath, path));
    }

    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
    }

    private static CaretLocation? Move(List<CaretLocation> from, List<CaretLocation> to, CaretLocation? current)
    {
        if (from.Count == 0)
            return null;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        if (current is { } here)
            Push(to, here);
        return target;
    }

    private static void Push(List<CaretLocation> list, CaretLocation location)
    {
        if (list.Count > 0 && SamePlace(list[^1], location))
            return;
        list.Add(location);
        if (list.Count > MaxEntries)
            list.RemoveAt(0);
    }

    private static void Rewrite(List<CaretLocation> list, string oldPath, string newPath)
    {
        var folderPrefix = oldPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        for (var i = 0; i < list.Count; i++)
        {
            var path = list[i].FilePath;
            if (SamePath(path, oldPath))
                list[i] = list[i] with { FilePath = newPath };
            else if (path.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                list[i] = list[i] with { FilePath = Path.Combine(newPath, path[folderPrefix.Length..]) };
        }
    }

    private static bool SamePlace(CaretLocation a, CaretLocation b) =>
        SamePath(a.FilePath, b.FilePath) && a.Line == b.Line && a.Column == b.Column;

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
