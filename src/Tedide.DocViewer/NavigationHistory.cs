namespace Tedide.DocViewer;

/// <summary>One visited location - a page, optionally scrolled to a specific heading.</summary>
public sealed record NavigationEntry(string FileName, string? Anchor);

/// <summary>
/// A browser-style back/forward stack: <see cref="Push"/> records a newly-visited page (dropping any
/// "forward" entries past the current position, same as a real browser does once you navigate
/// somewhere new after going back), while <see cref="GoBack"/>/<see cref="GoForward"/> move within
/// the existing list without recording a new visit. In-memory only - unlike <see cref="DocBookmarks"/>,
/// history isn't meant to survive a restart.
/// </summary>
public sealed class NavigationHistory
{
    private readonly List<NavigationEntry> _entries = [];
    private int _index = -1;

    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    public void Push(NavigationEntry entry)
    {
        if (_index >= 0 && _entries[_index] == entry)
            return; // already here (e.g. re-selecting the same tree node) - not a new visit.

        _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(entry);
        _index = _entries.Count - 1;
    }

    public NavigationEntry? GoBack()
    {
        if (!CanGoBack)
            return null;
        _index--;
        return _entries[_index];
    }

    public NavigationEntry? GoForward()
    {
        if (!CanGoForward)
            return null;
        _index++;
        return _entries[_index];
    }
}
