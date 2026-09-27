using System.Text;

namespace Tedide.Core;

/// <summary>
/// Reads and writes a source file's text without changing any byte the user didn't edit. Plain
/// File.ReadAllText/WriteAllText assume UTF-8, so a byte that isn't valid UTF-8 - common in cc65
/// sources, e.g. a Latin-1 "©" in a comment or a PETSCII-range byte in a string literal - was
/// read as U+FFFD and written back that way on the next save, silently corrupting the file
/// (confirmed: "\xe9\xa0" came back as a single EF BF BD). The file's own encoding is detected
/// on read and reused on write instead.
/// </summary>
public static class SourceFileText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Returns the file's text and the encoding it was stored in: a BOM decides it when present
    /// (UTF-8/UTF-16/UTF-32, and the same BOM is written back); otherwise UTF-8 if every byte is
    /// valid UTF-8, else Latin-1 - which maps each byte 0-255 to the same code point, so any byte
    /// sequence at all round-trips through it unchanged.
    /// </summary>
    public static (string Text, Encoding Encoding) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);

        foreach (var withBom in new Encoding[] { Encoding.UTF8, Encoding.UTF32, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var preamble = withBom.GetPreamble();
            if (preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble))
                return (withBom.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), withBom);
        }

        try
        {
            return (StrictUtf8.GetString(bytes), Utf8NoBom);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> in <paramref name="encoding"/> (as returned by
    /// <see cref="Read"/>), BOM included if it had one. If the text now contains a character that
    /// encoding can't represent - typing "€" into a Latin-1 file, say - it's written as UTF-8
    /// instead rather than silently turned into "?", and the encoding actually used is returned
    /// so the caller can say so and keep using it.
    /// </summary>
    public static Encoding Write(string path, string text, Encoding encoding)
    {
        if (!CanRepresent(encoding, text))
            encoding = Utf8NoBom;

        File.WriteAllText(path, text, encoding);
        return encoding;
    }

    private static bool CanRepresent(Encoding encoding, string text)
    {
        // Every Unicode encoding can represent any text; only single-byte ones like Latin-1 can't.
        if (encoding is UTF8Encoding or UnicodeEncoding or UTF32Encoding)
            return true;
        return encoding.GetString(encoding.GetBytes(text)) == text;
    }
}
