using System.ComponentModel;
using System.Text;
using Tedide.Core;

namespace Tedide.Build;

/// <summary>
/// Checks one source file's text for errors the way a build would compile it, for checking as you
/// type: cc65 for C, ca65 for assembly - never cl65, the optimizer, the assembler step for C or the
/// linker, which a build needs but a check doesn't. The text (unsaved edits included) goes to a
/// temporary copy, never to the file itself or obj/; it's compiled with the project's target, CPU,
/// include paths and defines, from the project folder, so its headers resolve as in a build.
/// Takes about 0.1s, almost all of it starting the process.
/// </summary>
public sealed class SourceChecker(string cl65Path = "cl65")
{
    /// <summary>cc65 and ca65: beside cl65 when that's a full path, else on PATH like it.</summary>
    public string Cc65Path { get; } = Cc65Toolchain.ToolBeside(cl65Path, "cc65");

    public string Ca65Path { get; } = Cc65Toolchain.ToolBeside(cl65Path, "ca65");

    /// <summary>Whether <paramref name="path"/> is a file this can check - C or assembly source.</summary>
    public static bool CanCheck(string path) =>
        TedideProject.IsCSourceFile(path) || Path.GetExtension(path).ToLowerInvariant() is ".s" or ".asm";

    /// <summary>
    /// The errors and warnings in <paramref name="text"/>, the current text of
    /// <paramref name="sourcePath"/> (a file of <paramref name="project"/>). Ones in the file itself
    /// carry <paramref name="sourcePath"/>; ones in a header it includes carry that header's full
    /// path. Null if the compiler couldn't be started (cc65 isn't installed, say).
    /// </summary>
    /// <param name="libraryIncludePaths">The include folders of the libraries the project links,
    /// after its own - as in a build.</param>
    public async Task<IReadOnlyList<BuildDiagnostic>?> CheckAsync(TedideProject project, string sourcePath, string text,
        IReadOnlyList<string> libraryIncludePaths, CancellationToken cancellationToken = default)
    {
        // A folder of its own each time, so two checks (two Tedide windows) can't collide.
        var scratch = Directory.CreateTempSubdirectory("tedide-check-").FullName;
        try
        {
            var copy = Path.Combine(scratch, Path.GetFileName(sourcePath));
            // Latin-1 keeps one byte per character, so columns and line numbers match the editor;
            // cc65 reads source as bytes anyway.
            await File.WriteAllTextAsync(copy, text, Encoding.Latin1, cancellationToken).ConfigureAwait(false);
            var isC = TedideProject.IsCSourceFile(sourcePath);
            var arguments = isC
                ? CompileArguments(project, sourcePath, copy, Path.Combine(scratch, "out.s"), libraryIncludePaths)
                : AssembleArguments(project, sourcePath, copy, Path.Combine(scratch, "out.o"));
            var startInfo = ToolProcess.StartInfo(isC ? Cc65Path : Ca65Path, arguments, project.Directory);

            ToolResult result;
            try
            {
                result = await ToolProcess.RunAsync(startInfo, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception)
            {
                return null;
            }

            var lines = (result.Output + "\n" + result.Error).Split('\n').Select(l => l.TrimEnd('\r'));
            return Cc65DiagnosticParser.ParseAll(lines)
                .Select(d => d with { FilePath = Resolve(project, d.FilePath, copy, sourcePath) })
                .ToList();
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // A compiler killed by a cancelled check may still hold it for a moment - it's in
                // the temp folder, so leaving it is harmless.
            }
        }
    }

    /// <summary>A reported path as a full one: the copy is the source file; anything else (a
    /// header) is relative to the project folder, where the compiler ran.</summary>
    private static string Resolve(TedideProject project, string reported, string copy, string sourcePath)
    {
        if (reported.Length == 0)
            return reported;
        var full = Path.GetFullPath(Path.Combine(project.Directory, reported));
        return string.Equals(full, Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase) ? sourcePath : full;
    }

    /// <summary>
    /// cc65's arguments for checking a C file: the build's target, CPU, include paths and defines
    /// (see <see cref="Cc65Toolchain.BuildGenerateAssemblyArguments"/>), with the source's own folder
    /// first so its <c>#include "..."</c> files are found from the copy. No optimization or debug
    /// flags - they don't change what's an error, and cost time.
    /// </summary>
    internal static List<string> CompileArguments(TedideProject project, string sourcePath, string copy, string output,
        IReadOnlyList<string> libraryIncludePaths)
    {
        var args = new List<string> { "-t", project.Target.ToCl65Id(), "--cpu", project.ResolvedCc65Cpu };
        args.AddRange(["-I", Path.GetDirectoryName(Path.GetFullPath(Path.Combine(project.Directory, sourcePath)))!]);
        foreach (var includePath in project.IncludePaths)
            args.AddRange(["-I", includePath]);
        foreach (var includePath in libraryIncludePaths)
            args.AddRange(["-I", includePath]);
        foreach (var define in project.PreprocessorDefines)
            args.AddRange(["-D", define]);
        args.AddRange(CompilerOptions(project.ExtraArguments));
        args.AddRange(["-o", output, copy]);
        return args;
    }

    /// <summary>ca65's arguments for checking an assembly file: the build's target and CPU, and
    /// the source's own folder for its <c>.include</c> files.</summary>
    internal static List<string> AssembleArguments(TedideProject project, string sourcePath, string copy, string output)
    {
        var args = new List<string> { "-t", project.Target.ToCl65Id(), "--cpu", project.ResolvedCc65Cpu };
        args.AddRange(["-I", Path.GetDirectoryName(Path.GetFullPath(Path.Combine(project.Directory, sourcePath)))!]);
        args.AddRange(AssemblerOptions(project.ExtraArguments));
        args.AddRange(["-o", output, copy]);
        return args;
    }

    /// <summary>cl65 options that mean the same to cc65 and take a value.</summary>
    private static readonly HashSet<string> CompilerOptionsWithValue = ["-I", "-D", "-W", "--include-dir", "--standard"];

    /// <summary>cl65 options that mean the same to cc65 and stand alone.</summary>
    private static readonly HashSet<string> CompilerFlags = ["-Cl", "--static-locals", "-j", "--signed-chars", "-r", "--register-vars"];

    /// <summary>
    /// The project's extra cl65 arguments that cc65 understands too. The rest - optimization,
    /// listing, linker and assembler options - are left out: cc65 rejects options it doesn't know,
    /// and none of them changes what's an error.
    /// </summary>
    internal static IEnumerable<string> CompilerOptions(IReadOnlyList<string> extraArguments)
    {
        for (var i = 0; i < extraArguments.Count; i++)
        {
            var argument = extraArguments[i];
            if (CompilerOptionsWithValue.Contains(argument) && i + 1 < extraArguments.Count)
            {
                yield return argument;
                yield return extraArguments[++i];
            }
            else if (CompilerFlags.Contains(argument) || argument.StartsWith("-D", StringComparison.Ordinal) && argument.Length > 2
                     || argument.StartsWith("-I", StringComparison.Ordinal) && argument.Length > 2)
            {
                yield return argument;
            }
        }
    }

    /// <summary>The project's extra cl65 arguments meant for the assembler, as ca65 spells them:
    /// <c>--asm-define</c> is ca65's <c>-D</c>, <c>--asm-include-dir</c> its <c>-I</c>.</summary>
    internal static IEnumerable<string> AssemblerOptions(IReadOnlyList<string> extraArguments)
    {
        for (var i = 0; i + 1 < extraArguments.Count; i++)
        {
            switch (extraArguments[i])
            {
                case "--asm-define":
                    yield return "-D";
                    yield return extraArguments[++i];
                    break;
                case "--asm-include-dir":
                    yield return "-I";
                    yield return extraArguments[++i];
                    break;
            }
        }
    }
}
