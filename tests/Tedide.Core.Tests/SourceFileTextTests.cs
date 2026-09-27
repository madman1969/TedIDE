using System.Text;

namespace Tedide.Core.Tests;

public class SourceFileTextTests
{
    [Fact]
    public void ReadThenWrite_LeavesANonUtf8FileByteForByteIdentical()
    {
        // The corruption this guards against, confirmed with the old File.ReadAllText/WriteAllText:
        // Latin-1 "©", "é" and a non-breaking space all came back as U+FFFD (EF BF BD), and the
        // two bytes E9 A0 collapsed into a single replacement character.
        byte[] original = [.. "/* "u8, 0xA9, .. " 1985 */\nchar s[] = \""u8, 0xE9, 0xA0, .. "\";\n"u8];
        var path = WriteTemp(original);
        try
        {
            var (text, encoding) = SourceFileText.Read(path);
            SourceFileText.Write(path, text, encoding);

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Contains("©", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadThenWrite_KeepsAUtf8File_WithOrWithoutItsBom(bool withBom)
    {
        byte[] body = "int café = 1; // ✓\r\n"u8.ToArray();
        byte[] original = withBom ? [0xEF, 0xBB, 0xBF, .. body] : body;
        var path = WriteTemp(original);
        try
        {
            var (text, encoding) = SourceFileText.Read(path);
            SourceFileText.Write(path, text, encoding);

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal("int café = 1; // ✓\r\n", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadThenWrite_KeepsAUtf16File()
    {
        var original = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("int x;\n")).ToArray();
        var path = WriteTemp(original);
        try
        {
            var (text, encoding) = SourceFileText.Read(path);
            SourceFileText.Write(path, text, encoding);

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal("int x;\n", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_FallsBackToUtf8_WhenTheTextNoLongerFitsTheFilesEncoding()
    {
        // Typing "€" into a Latin-1 file: Latin-1 has no byte for it, and writing it anyway would
        // silently turn it into "?". UTF-8 is used instead, and reported via the return value.
        var path = WriteTemp([0x41, 0xE9, 0x0A]); // "Aé\n" in Latin-1
        try
        {
            var (text, encoding) = SourceFileText.Read(path);
            Assert.Equal(Encoding.Latin1.CodePage, encoding.CodePage);

            var used = SourceFileText.Write(path, text + "€", encoding);

            Assert.Equal(Encoding.UTF8.CodePage, used.CodePage);
            Assert.Equal("Aé\n€", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tedide-{Guid.NewGuid():N}.c");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
