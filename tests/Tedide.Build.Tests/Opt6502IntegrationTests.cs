using Tedide.Core;

namespace Tedide.Build.Tests;

/// <summary>
/// The build's optimizer step and --cpu handling - see Cc65Toolchain.BuildCompileSteps and
/// Opt6502Stats. BuildAsync tests use a .cmd stand-in for cl65 that writes a small cc65-style
/// assembly file to whatever -o path it's given, so the real optimizer runs on it without cc65
/// being installed.
/// </summary>
public class Opt6502IntegrationTests
{
    [Theory]
    [InlineData(Cc65Target.C64, false, "6502")]
    [InlineData(Cc65Target.C64, true, "65816")]
    [InlineData(Cc65Target.Vic20, false, "6502")]
    [InlineData(Cc65Target.Vic20, true, "6502")] // SuperCPU is C64-only - ignored elsewhere
    [InlineData(Cc65Target.C128, false, "6502")]
    [InlineData(Cc65Target.Pet, false, "6502")]
    public void CompileSteps_PassTheProjectsCpu_ToBothCl65Steps(Cc65Target target, bool superCpu, string expectedCpu)
    {
        var project = new TedideProject { Name = "Test", Target = target, EnableSuperCpu = superCpu, SourceFiles = ["src/main.c"] };

        var steps = Cc65Toolchain.BuildCompileSteps(project, "src/main.c");

        Assert.All(steps, step => Assert.Equal(expectedCpu, step.Arguments[step.Arguments.IndexOf("--cpu") + 1]));
    }

    [Fact]
    public void CompileSteps_PassTheCpu_WhenAssemblingAHandWrittenSource()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, EnableSuperCpu = true, SourceFiles = ["src/fast.s"] };

        var step = Assert.Single(Cc65Toolchain.BuildCompileSteps(project, "src/fast.s"));

        Assert.Equal("65816", step.Arguments[step.Arguments.IndexOf("--cpu") + 1]);
    }

    [Fact]
    public void CompileSteps_WithoutOpt6502_NeverRunIt()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["src/main.c"] };

        Assert.All(Cc65Toolchain.BuildCompileSteps(project, "src/main.c"), step => Assert.Equal(BuildTool.Cl65, step.Tool));
    }

    [Fact]
    public void CompileSteps_WithOpt6502_OptimizeCc65sAssemblyIntoTheFileThatIsAssembled()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, UseOpt6502 = true, SourceFiles = ["src/foo.c"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var steps = Cc65Toolchain.BuildCompileSteps(project, "src/foo.c");

            var unoptimized = Path.Combine("obj", "src", "foo.c.cc65.s");
            var optimized = Path.Combine("obj", "src", "foo.c.s");
            Assert.Equal([BuildTool.Cl65, BuildTool.Opt6502, BuildTool.Cl65], steps.Select(s => s.Tool));
            Assert.Equal(unoptimized, steps[0].Arguments[steps[0].Arguments.IndexOf("-o") + 1]);
            Assert.Equal([unoptimized, optimized], steps[1].Arguments[^2..]);
            Assert.Equal(optimized, steps[2].Arguments[^1]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CompileSteps_WithOpt6502_LeaveHandWrittenAssemblyAlone()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, UseOpt6502 = true, SourceFiles = ["src/border.s"] };

        var step = Assert.Single(Cc65Toolchain.BuildCompileSteps(project, "src/border.s"));

        Assert.Equal(BuildTool.Cl65, step.Tool);
    }

    [Theory]
    [InlineData(Cc65Target.C64, false, "6502")]
    [InlineData(Cc65Target.C64, true, "65816")]
    [InlineData(Cc65Target.Plus4, true, "6502")]
    public void Opt6502Options_UseTheProjectsCpu(Cc65Target target, bool superCpu, string expectedCpu)
    {
        var project = new TedideProject { Name = "Test", Target = target, EnableSuperCpu = superCpu };

        Assert.Equal(expectedCpu, Cc65Toolchain.BuildOpt6502Options(project).Cpu);
    }

    [Theory]
    [InlineData(Opt6502Mode.Size)]
    [InlineData(Opt6502Mode.Speed)]
    public void Opt6502Options_UseTheProjectsMode(Opt6502Mode mode)
    {
        var project = new TedideProject { Name = "Test", Opt6502Mode = mode };

        Assert.Equal(mode, Cc65Toolchain.BuildOpt6502Options(project).Mode);
    }

    [Fact]
    public async Task BuildAsync_OptimizesEachCFilesAssembly_AndShowsItsSavings_AndTheBuildTotal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var fakeCl65 = WriteFakeCl65(dir);
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, UseOpt6502 = true, SourceFiles = ["main.c", "screen.c"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var result = await new Cc65Toolchain(fakeCl65).BuildAsync(project);

            Assert.True(result.Succeeded, string.Join("\n", result.RawOutputLines));
            Assert.Contains("opt6502: main.c: 1 optimization (1 jump to next line), ~3 bytes and ~3 cycles saved", result.RawOutputLines);
            Assert.Contains("opt6502: screen.c: 1 optimization (1 jump to next line), ~3 bytes and ~3 cycles saved", result.RawOutputLines);
            Assert.Contains("opt6502: total for 2 C files: 2 optimizations (2 jump to next line), ~6 bytes and ~6 cycles saved", result.RawOutputLines);
            // The assemble step's input is the optimized file: the jump to the next line is gone.
            var optimized = File.ReadAllText(project.ResolvedGeneratedAssemblyFileFor("main.c"));
            Assert.DoesNotContain("jmp     L0001", optimized);
            Assert.Contains("L0001:\trts", optimized);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ReportsAMissingGeneratedFile_AsAnOpt6502Error_AndFails()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            // A cl65 that "succeeds" without writing anything - so there's no assembly to optimize.
            var fakeCl65 = Path.Combine(dir.FullName, "fake-cl65.cmd");
            File.WriteAllText(fakeCl65, "@exit /b 0\r\n");
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, UseOpt6502 = true, SourceFiles = ["main.c"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var result = await new Cc65Toolchain(fakeCl65).BuildAsync(project);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("main.c.cc65.s", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A stand-in for cl65 that copies a small cc65-style assembly file (with one jump to the next
    /// line for the optimizer to remove) to whatever <c>-o</c> path each invocation is given.
    /// </summary>
    private static string WriteFakeCl65(DirectoryInfo dir)
    {
        var canned = Path.Combine(dir.FullName, "canned.s");
        File.WriteAllText(canned, string.Join("\r\n",
            "\t.fopt\t\tcompiler,\"cc65 v 2.19 - Git b75f872\"",
            "\t.setcpu\t\t\"6502\"",
            "\t.importzp\tsp",
            ".segment\t\"CODE\"",
            ".proc\t_main: near",
            "\tlda     #$00",
            "\tjmp     L0001",
            "L0001:\trts",
            ".endproc",
            ""));
        return WriteScript(dir, "fake-cl65.cmd", string.Join("\r\n",
            "@echo off",
            "set OUT=",
            ":loop",
            "if \"%~1\"==\"\" goto done",
            "if \"%~1\"==\"-o\" set \"OUT=%~2\"",
            "shift",
            "goto loop",
            ":done",
            $"if defined OUT copy /y \"{canned}\" \"%OUT%\" >nul",
            "exit /b 0"));
    }

    private static string WriteScript(DirectoryInfo dir, string name, string body)
    {
        var path = Path.Combine(dir.FullName, name);
        File.WriteAllText(path, body + "\r\n");
        return path;
    }
}
