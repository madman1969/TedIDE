using System.Reflection;

namespace Tedide.DocViewer;

/// <summary>Reads a bundled cc65 manual page's raw HTML from the assembly's embedded resources.</summary>
public static class Cc65DocLoader
{
    private static readonly Assembly Assembly = typeof(Cc65DocLoader).Assembly;

    /// <summary>Loads <paramref name="fileName"/> (a <see cref="Cc65DocEntry.FileName"/>, without
    /// its ".html" extension) as raw HTML text.</summary>
    public static string LoadHtml(string fileName)
    {
        var resourceName = $"Tedide.DocViewer.Cc65Docs.{fileName}.html";
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded doc resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
