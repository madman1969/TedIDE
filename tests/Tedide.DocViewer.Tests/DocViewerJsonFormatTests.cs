using System.Text.Json;
using Tedide.Core;
using Tedide.DocViewer;

namespace Tedide.DocViewer.Tests;

/// <summary>The Doc Viewer's settings files are written exactly as the reflection-based serializer
/// did - see Tedide.Core.Tests' JsonFormatTests.</summary>
public class DocViewerJsonFormatTests
{
    private static readonly JsonSerializerOptions Plain = new() { WriteIndented = true };

    [Fact]
    public void Bookmarks()
    {
        var bookmarks = new DocBookmarks { Items = [new Bookmark("cc65/coding.md", "constants", "Constants"), new Bookmark("c64/sprites.md", null, "Sprites")] };
        Assert.Equal(JsonSerializer.Serialize(bookmarks, Plain), JsonFile.Serialize(bookmarks, DocViewerJsonContext.Default.DocBookmarks));
    }

    [Fact]
    public void Layout()
    {
        var layout = new DocViewerLayoutSettings { ContentPaneWidthPercent = 64, CollapsedBooks = ["VICE Manual", "Wikipedia"] };
        Assert.Equal(JsonSerializer.Serialize(layout, Plain), JsonFile.Serialize(layout, DocViewerJsonContext.Default.DocViewerLayoutSettings));
    }
}
