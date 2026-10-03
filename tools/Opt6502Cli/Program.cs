// Command-line front end to Tedide.Build's Opt6502Optimizer, taking the same arguments as the
// opt6502 program it replaces:
//
//   opt6502 [-size|-speed] [-asm ca65] [-cpu 6502|65c02|65816] [-quiet] input.s [output.s]
//
// Prints one "opt6502-stats: ..." line (see Opt6502Stats.ToStatsLine) and exits 1 on any error.
// Only ca65 syntax is supported. OPT6502_INLINE_LIMIT=n inlines only the first n runtime calls in
// -speed mode - a debugging aid for bisecting a miscompile.
using System.Text;
using Tedide.Build;
using Tedide.Build.Opt6502;
using Tedide.Core;

var mode = Opt6502Mode.Speed;  // the original program's default
var cpu = "6502";
string? input = null;
var output = "output.asm";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-speed":
            mode = Opt6502Mode.Speed;
            break;
        case "-size":
            mode = Opt6502Mode.Size;
            break;
        case "-quiet":
            break;
        case "-asm" when i + 1 < args.Length:
            if (!args[++i].Equals("ca65", StringComparison.OrdinalIgnoreCase))
                return Fail($"Only ca65 syntax is supported, not '{args[i]}'.");
            break;
        case "-cpu" when i + 1 < args.Length:
            cpu = args[++i];
            break;
        default:
            if (input is null)
                input = args[i];
            else
                output = args[i];
            break;
    }
}

if (input is null)
{
    Console.WriteLine("Usage: opt6502 [-size|-speed] [-asm ca65] [-cpu 6502|65c02|65816] [-quiet] input.s [output.s]");
    return 1;
}

int? inlineLimit = int.TryParse(Environment.GetEnvironmentVariable("OPT6502_INLINE_LIMIT"), out var limit) ? limit : null;

try
{
    // Latin-1 round-trips every byte, so nothing in the source is altered by decoding it.
    var source = File.ReadAllText(input, Encoding.Latin1);
    var result = Opt6502Optimizer.Optimize(source, new Opt6502Options(mode, cpu, inlineLimit));
    File.WriteAllText(output, result.Output, Encoding.Latin1);
    Console.WriteLine(result.Stats.ToStatsLine());
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"Error: {message}");
    return 1;
}
