namespace Tedide.DocViewer;

/// <summary>One visited location - a page, optionally scrolled to a specific heading.</summary>
public sealed record NavigationEntry(string FileName, string? Anchor);

/// <summary>
/// A browser-style back/forward stack: <see cref="Push"/> records a newly-visited page (dropping any
/// "forward" entries past the current position, same as a real browser does once you navigate
/// somewhere new after going back), while <see cref="GoBack"/>/<see cref="GoForward"/> move within
/// the existing list without recording a new visit. Like a browser, each visit also remembers how
/// far down the page was scrolled when it was left (<see cref="RememberScrollOffset"/>), so going
/// back returns there rather than to the top. In-memory only - unlike <see cref="DocBookmarks"/>,
/// history isn't meant to survive a restart.
/// </summary>
public sealed class NavigationHistory
{
    private sealed class Visit(NavigationEntry entry)
    {
        public NavigationEntry Entry { get; } = entry;
        public int? ScrollOffset { get; set; }
    }

    private readonly List<Visit> _visits = [];
    private int _index = -1;

    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _visits.Count - 1;

    /// <summary>The visit Back/Forward last moved to, or the last one pushed; null if there's none.</summary>
    public NavigationEntry? Current => _index >= 0 ? _visits[_index].Entry : null;

    /// <summary>How far down <see cref="Current"/> was scrolled when it was last left; null if it
    /// hasn't been left yet.</summary>
    public int? CurrentScrollOffset => _index >= 0 ? _visits[_index].ScrollOffset : null;

    public void Push(NavigationEntry entry)
    {
        if (_index >= 0 && _visits[_index].Entry == entry)
            return; // already here (e.g. re-selecting the same tree node) - not a new visit.

        _visits.RemoveRange(_index + 1, _visits.Count - _index - 1);
        _visits.Add(new Visit(entry));
        _index = _visits.Count - 1;
    }

    /// <summary>Records how far down <see cref="Current"/> is scrolled - called as it's left, so
    /// coming back to it can return there.</summary>
    public void RememberScrollOffset(int offset)
    {
        if (_index >= 0)
            _visits[_index].ScrollOffset = offset;
    }

    public NavigationEntry? GoBack()
    {
        if (!CanGoBack)
            return null;
        _index--;
        return _visits[_index].Entry;
    }

    public NavigationEntry? GoForward()
    {
        if (!CanGoForward)
            return null;
        _index++;
        return _visits[_index].Entry;
    }
}
