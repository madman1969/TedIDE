using Tedide.Core;

namespace Tedide.Build.Tests;

/// <summary>
/// The build's opt6502 step and --cpu handling - see Cc65Toolchain.BuildCompileSteps and
/// Opt6502Stats. BuildAsync tests use .cmd stand-ins for cl65 and opt6502, so they don't need
/// either one installed.
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
    public void Opt6502Arguments_AreQuiet_Ca65Syntax_AndTheProjectsCpu(Cc65Target target, bool superCpu, string expectedCpu)
    {
        var project = new TedideProject { Name = "Test", Target = target, EnableSuperCpu = superCpu };

        var args = Cc65Toolchain.BuildOpt6502Arguments(project, "in.s", "out.s");

        Assert.Equal(["-quiet", "-asm", "ca65", "-cpu", expectedCpu, "in.s", "out.s"], args);
    }

    [Fact]
    public void ResolveOpt6502Path_PrefersTheConfiguredPath()
    {
        Assert.Equal(@"D:\tools\opt6502.exe", Cc65Toolchain.ResolveOpt6502Path(@" D:\tools\opt6502.exe ", Path.GetTempPath()));
    }

    [Fact]
    public void ResolveOpt6502Path_UsesTheCopyBesideTedide_WhenNoneIsConfigured()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var beside = Path.Combine(dir.FullName, Cc65Toolchain.Opt6502ExecutableName);
            File.WriteAllText(beside, "");

            Assert.Equal(beside, Cc65Toolchain.ResolveOpt6502Path(null, dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOpt6502Path_FallsBackToPath_WhenNothingIsConfiguredOrBesideTedide()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Equal("opt6502", Cc65Toolchain.ResolveOpt6502Path("  ", dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ShowsEachFilesOpt6502Savings_AndTheBuildTotal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var fakeCl65 = WriteScript(dir, "fake-cl65.cmd", "@exit /b 0");
            var fakeOpt6502 = WriteScript(dir, "fake-opt6502.cmd",
                "@echo opt6502-stats: optimizations=3 removed=3 rewritten=0 bytes=8 cycles=9 reload=0 constant=1 transfer=0 jump=2 unreachable=0 stz=0");
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, UseOpt6502 = true, SourceFiles = ["main.c", "screen.c"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var result = await new Cc65Toolchain(fakeCl65, fakeOpt6502).BuildAsync(project);

            Assert.True(result.Succeeded, string.Join("\n", result.RawOutputLines));
            Assert.Contains("opt6502: main.c: 3 optimizations (1 repeated constant load, 2 jump to next line), ~8 bytes and ~9 cycles saved", result.RawOutputLines);
            Assert.Contains("opt6502: screen.c: 3 optimizations (1 repeated constant load, 2 jump to next line), ~8 bytes and ~9 cycles saved", result.RawOutputLines);
            Assert.Contains("opt6502: total for 2 C files: 6 optimizations (2 repeated constant load, 4 jump to next line), ~16 bytes and ~18 cycles saved", result.RawOutputLines);
            Assert.DoesNotContain(result.RawOutputLines, l => l.StartsWith(Opt6502Stats.LinePrefix, StringComparison.Ordinal));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ReportsAnOpt6502Error_AsADiagnostic_AndFails()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var fakeCl65 = WriteScript(dir, "fake-cl65.cmd", "@exit /b 0");
            var fakeOpt6502 = WriteScript(dir, "fake-opt6502.cmd", "@echo Error: Cannot open obj\\main.c.cc65.s 1>&2\r\n@exit /b 1");
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, UseOpt6502 = true, SourceFiles = ["main.c"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var result = await new Cc65Toolchain(fakeCl65, fakeOpt6502).BuildAsync(project);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("Cannot open", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ExplainsHowToGetOpt6502_WhenItCannotBeRun()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var fakeCl65 = WriteScript(dir, "fake-cl65.cmd", "@exit /b 0");
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, UseOpt6502 = true, SourceFiles = ["main.c"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var result = await new Cc65Toolchain(fakeCl65, Path.Combine(dir.FullName, "missing-opt6502.exe")).BuildAsync(project);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Contains("build.cmd", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string WriteScript(DirectoryInfo dir, string name, string body)
    {
        var path = Path.Combine(dir.FullName, name);
        File.WriteAllText(path, body + "\r\n");
        return path;
    }
}
