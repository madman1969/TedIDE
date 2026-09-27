using System.Text;

namespace Tedide.Core;

/// <summary>
/// Converts between a list of command-line arguments and the single line of text a settings
/// field shows them as. Whitespace separates arguments except inside double quotes, so a path
/// containing spaces (<c>"C:\My Libs\include"</c>) survives as one argument; the quotes
/// themselves are removed, since each argument is passed to cl65 separately
/// (ProcessStartInfo.ArgumentList) and needs no shell quoting of its own.
/// </summary>
public static class ArgumentText
{
    public static List<string> Split(string text)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArgument = false; // Distinguishes an explicit "" (an empty argument) from no argument at all.

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasArgument = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasArgument)
                    arguments.Add(current.ToString());
                current.Clear();
                hasArgument = false;
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
        }

        if (hasArgument)
            arguments.Add(current.ToString());
        return arguments;
    }

    /// <summary>The inverse of <see cref="Split"/>: quotes any argument that's empty or contains
    /// whitespace, so splitting the result gives back the same list.</summary>
    public static string Join(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
}
