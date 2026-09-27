using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cc65DocsDbBuilder;

/// <summary>
/// Generates the "Plus/4 and C16 memory map" page from cc65's own definitions for those machines,
/// copied unchanged from a cc65 install into <c>SourceHtml/Cc65Defs/</c>: <c>_ted.h</c> (the TED
/// register struct, one commented field per register), <c>_6551.h</c> and <c>plus4.h</c> (the
/// Plus/4's ACIA), <c>cbm264.h</c> (TED colours) and <c>asminc/plus4.inc</c> (the zero-page, system
/// and I/O addresses cc65's runtime uses, which <c>c16.inc</c> simply includes). Every address and
/// description on the page comes from those files - like the cc65 manuals, they're under the zlib
/// licence, reproduced at the end of the page. No openly licensed full Plus/4 memory map exists.
/// </summary>
public static partial class Plus4MemoryMapGenerator
{
    public const string PageId = "plus4-memory-map";

    public const string Description = "Plus/4 and C16 memory map: TED registers, ACIA and system locations, generated from cc65's headers.";

    private const int TedBase = 0xFF00;
    private const int AciaBase = 0xFD00;

    /// <summary>One register of a C struct laid over the hardware (<c>struct __ted</c>, <c>struct __6551</c>).</summary>
    public sealed record StructField(int Offset, int Length, string Name, string Comment);

    /// <summary>One <c>NAME := $ADDR ; comment</c> (or <c>NAME = value</c>) line of a ca65 include,
    /// under the <c>; ----</c> section heading it follows.</summary>
    public sealed record AsmSymbol(string Section, string Name, string Value, bool IsAddress, string Comment);

    public static string Generate(string defsDir)
    {
        string Read(string file) => File.ReadAllText(Path.Combine(defsDir, file));

        var ted = ParseStruct(Read("_ted.h"), "__ted");
        var acia = ParseStruct(Read("_6551.h"), "__6551");
        var symbols = ParseAsmInclude(Read("plus4.inc"));
        var cbm264 = Read("cbm264.h");
        var tedHeader = Read("_ted.h");

        var sb = new StringBuilder();
        sb.Append("# Plus/4 and C16 memory map\n\n");
        sb.Append("The hardware registers and system locations cc65 knows about on the Commodore Plus/4, C16 and 116, ");
        sb.Append("generated from cc65's own definitions for them. Addresses are the same on all three machines - cc65's ");
        sb.Append("`c16.inc` just includes `plus4.inc` - except that only the Plus/4 has the 6551 ACIA. ");
        sb.Append("See [Commodore Plus/4 specific information](plus4.html) and [Commodore 16/116 specific information](c16.html) ");
        sb.Append("for how cc65 programs use memory on these machines.\n\n");
        sb.Append("In C, include `<plus4.h>` or `<c16.h>` and use the `TED` (and, on the Plus/4, `ACIA`) structs; in ");
        sb.Append("assembler, include `plus4.inc` or `c16.inc` and use the symbols below.\n\n");

        // --- TED ---
        var asmByAddress = symbols.Where(s => s.IsAddress)
            .GroupBy(s => ParseNumber(s.Value))
            .ToDictionary(g => g.Key, g => g.Select(s => s.Name).ToList());
        sb.Append($"## TED registers (${TedBase:X4}-${TedBase + ted.Sum(f => f.Length) - 1:X4})\n\n");
        sb.Append("The TED (7360/8360) handles video, sound, timers, the keyboard latch and ROM/RAM banking. ");
        sb.Append("C: `TED.<field>` (from `_ted.h`); assembler: the `plus4.inc` symbol, where it defines one.\n\n");
        sb.Append("| Address | C field | Assembler symbol | Description |\n");
        sb.Append("|---|---|---|---|\n");
        foreach (var field in ted)
        {
            var address = TedBase + field.Offset;
            var addressText = field.Length == 1 ? $"${address:X4}" : $"${address:X4}-${address + field.Length - 1:X4}";
            var fieldText = field.Length == 1 ? $"`{field.Name}`" : $"`{field.Name}[{field.Length}]`";
            var asmText = asmByAddress.TryGetValue(address, out var names)
                ? string.Join(", ", names.Select(n => $"`{n}`"))
                : "";
            sb.Append($"| {addressText} | {fieldText} | {asmText} | {field.Comment} |\n");
        }
        sb.Append('\n');

        // --- Colours ---
        sb.Append("## TED colours\n\n");
        sb.Append("A TED colour byte is a base colour (bits 0-3) OR'd with a luminance (bits 4-6), plus bit 7 to blink ");
        sb.Append("(from `cbm264.h`). cc65's `COLOR_*` constants combine the two to approximate the C64's palette.\n\n");
        foreach (var (prefix, heading) in new[] { ("BCOLOR_", "Base colour"), ("CATTR_", "Luminance / blink") })
        {
            sb.Append($"| Value | {heading} |\n");
            sb.Append("|---|---|\n");
            foreach (var (name, value) in Defines(cbm264, prefix))
                sb.Append($"| {value} | `{name}` |\n");
            sb.Append('\n');
        }

        // --- ACIA ---
        sb.Append($"## 6551 ACIA (${AciaBase:X4}-${AciaBase + acia.Sum(f => f.Length) - 1:X4}, Plus/4 only)\n\n");
        sb.Append("The Plus/4's built-in RS-232 serial port. C: `ACIA.<field>` (from `plus4.h` and `_6551.h`).\n\n");
        sb.Append("| Address | C field | Description |\n");
        sb.Append("|---|---|---|\n");
        foreach (var field in acia)
            sb.Append($"| ${AciaBase + field.Offset:X4} | `{field.Name}` | {field.Comment} |\n");
        sb.Append('\n');

        // --- plus4.inc sections other than I/O (the TED table covers those) ---
        foreach (var section in symbols.GroupBy(s => s.Section))
        {
            var entries = section.Where(s => !(s.IsAddress && ParseNumber(s.Value) >= TedBase && ParseNumber(s.Value) < TedBase + 0x40)).ToList();
            if (entries.Count == 0)
                continue;
            sb.Append($"## {section.Key}\n\n");
            sb.Append("From `plus4.inc`.\n\n");
            sb.Append("| Address / value | Assembler symbol | Description |\n");
            sb.Append("|---|---|---|\n");
            foreach (var entry in entries.OrderBy(e => e.IsAddress ? 0 : 1).ThenBy(e => e.IsAddress ? ParseNumber(e.Value) : 0))
            {
                var address = entry.IsAddress ? ParseNumber(entry.Value) : 0;
                var value = !entry.IsAddress ? entry.Value : address < 0x100 ? $"${address:X2}" : $"${address:X4}";
                sb.Append($"| {value} | `{entry.Name}` | {entry.Comment} |\n");
            }
            sb.Append('\n');
        }

        // --- Source and licence ---
        sb.Append("## Source and licence\n\n");
        sb.Append("Generated by Tedide's documentation builder from these cc65 files, copied unchanged from a cc65 V2.19 ");
        sb.Append("install: `include/_ted.h`, `include/_6551.h`, `include/plus4.h`, `include/cbm264.h` and `asminc/plus4.inc` ");
        sb.Append("(cc65 project, https://github.com/cc65/cc65). This page is an altered form of them: their definitions ");
        sb.Append("rearranged into tables, with the introductory text and section notes above added by Tedide. ");
        sb.Append("Where cc65 writes `TED_T4HI` for $FF05 it's reproduced as-is; `_ted.h` calls that register `t3_hi`.\n\n");
        sb.Append("Copyright of the headers, as they state it:\n\n");
        foreach (var file in new[] { "_ted.h", "_6551.h", "plus4.h", "cbm264.h" })
            sb.Append($"- `{file}`: {CopyrightLine(Read(file))}\n");
        sb.Append("\nThe licence notice they carry (cc65 as a whole, `plus4.inc` included, is under the same zlib licence):\n\n");
        var notice = LicenceNotice(tedHeader).ToList();
        while (notice.Count > 0 && notice[^1].Length == 0)
            notice.RemoveAt(notice.Count - 1);
        foreach (var line in notice)
            sb.Append($"> {line}\n");
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>The fields of <c>struct <paramref name="structName"/></c>, with their byte offsets -
    /// every field is an <c>unsigned char</c>, optionally an array.</summary>
    public static List<StructField> ParseStruct(string header, string structName)
    {
        var body = Regex.Match(header, $@"struct\s+{Regex.Escape(structName)}\s*\{{(?<body>.*?)\}};", RegexOptions.Singleline);
        if (!body.Success)
            throw new InvalidOperationException($"struct {structName} not found");

        var fields = new List<StructField>();
        var offset = 0;
        foreach (Match m in FieldLine().Matches(body.Groups["body"].Value))
        {
            var length = m.Groups["len"].Success ? int.Parse(m.Groups["len"].Value, CultureInfo.InvariantCulture) : 1;
            fields.Add(new StructField(offset, length, m.Groups["name"].Value, m.Groups["comment"].Value.Trim()));
            offset += length;
        }
        return fields;
    }

    /// <summary>The symbol definitions of a ca65 include file, each tagged with the section comment
    /// (the line after a <c>; ----</c> rule) it falls under. Aliases of other symbols are skipped.</summary>
    public static List<AsmSymbol> ParseAsmInclude(string include)
    {
        var symbols = new List<AsmSymbol>();
        var section = "";
        var lines = include.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("; ---", StringComparison.Ordinal) && i + 1 < lines.Length)
            {
                section = lines[++i].TrimStart(';').Trim();
                continue;
            }
            var m = SymbolLine().Match(line);
            if (!m.Success)
                continue;
            var isAddress = m.Groups["op"].Value == ":=";
            var value = m.Groups["value"].Value;
            if (!value.StartsWith('$') && !char.IsDigit(value[0]))
                continue; // an alias, e.g. ENABLE_ROM := TED_ROMSEL
            symbols.Add(new AsmSymbol(section, m.Groups["name"].Value, value, isAddress, m.Groups["comment"].Value.Trim()));
        }
        return symbols;
    }

    private static int ParseNumber(string value) =>
        value.StartsWith('$')
            ? int.Parse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(value, CultureInfo.InvariantCulture);

    private static List<(string Name, string Value)> Defines(string header, string prefix) =>
        DefineLine().Matches(header)
            .Where(m => m.Groups["name"].Value.StartsWith(prefix, StringComparison.Ordinal))
            .Select(m => (m.Groups["name"].Value, "$" + m.Groups["value"].Value[2..].ToUpperInvariant()))
            .ToList();

    /// <summary>The banner's "(C) year(s) author" line(s), whitespace collapsed.</summary>
    private static string CopyrightLine(string header) =>
        string.Join("; ", header.Replace("\r\n", "\n").Split('\n')
            .TakeWhile(l => l.StartsWith("/*", StringComparison.Ordinal))
            .Select(l => Regex.Replace(l.Trim().Trim('/').Trim('*'), @"\s+", " ").Trim())
            .Where(l => l.StartsWith("(C)", StringComparison.Ordinal)));

    /// <summary>The zlib notice from a cc65 header's banner comment: from "This software" to the end
    /// of clause 3, unwrapped from its <c>/* ... */</c> box into one line per paragraph or clause.</summary>
    private static IEnumerable<string> LicenceNotice(string header)
    {
        var boxLines = header.Replace("\r\n", "\n").Split('\n')
            .TakeWhile(l => l.StartsWith("/*", StringComparison.Ordinal))
            .Select(l => l.Trim().TrimStart('/').TrimStart('*').TrimEnd('/').TrimEnd('*').Trim())
            .SkipWhile(l => !l.StartsWith("This software", StringComparison.Ordinal))
            .ToList();

        var paragraph = new StringBuilder();
        foreach (var line in boxLines)
        {
            if ((line.Length == 0 || Regex.IsMatch(line, @"^\d\.")) && paragraph.Length > 0)
            {
                yield return paragraph.ToString();
                yield return "";
                paragraph.Clear();
            }
            if (line.Length > 0)
                paragraph.Append(paragraph.Length > 0 ? " " : "").Append(line);
        }
        if (paragraph.Length > 0)
            yield return paragraph.ToString();
    }

    [GeneratedRegex(@"unsigned\s+char\s+(?<name>\w+)(\[(?<len>\d+)\])?\s*;\s*/\*(?<comment>.*?)\*/")]
    private static partial Regex FieldLine();

    [GeneratedRegex(@"^(?<name>\w+)\s*(?<op>:=|=)\s*(?<value>\S+)\s*(;\s*(?<comment>.*))?$")]
    private static partial Regex SymbolLine();

    [GeneratedRegex(@"#define\s+(?<name>\w+)\s+(?<value>0x[0-9A-Fa-f]+)")]
    private static partial Regex DefineLine();
}
