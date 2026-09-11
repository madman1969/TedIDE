using Tedide.Core;

namespace Tedide.Build.Tests;

public class ViceEmulatorTests
{
    [Theory]
    [InlineData(Cc65Target.C64, "x64sc.exe")]
    [InlineData(Cc65Target.C128, "x128.exe")]
    [InlineData(Cc65Target.C16, "xplus4.exe")]
    [InlineData(Cc65Target.Plus4, "xplus4.exe")]
    [InlineData(Cc65Target.Vic20, "xvic.exe")]
    [InlineData(Cc65Target.Pet, "xpet.exe")]
    [InlineData(Cc65Target.Cbm510, "xcbm5x0.exe")]
    [InlineData(Cc65Target.Cbm610, "xcbm2.exe")]
    [InlineData(Cc65Target.Geos_Cbm, "x64sc.exe")]
    public void ExecutableNameFor_ReturnsTheMatchingViceExecutable_ForEveryCommodoreTarget(Cc65Target target, string expectedExecutable)
    {
        Assert.Equal(expectedExecutable, ViceEmulator.ExecutableNameFor(target));
    }

    [Fact]
    public void ExecutableNameFor_ReturnsTheSuperCpuEmulator_ForC64WhenEnableSuperCpuIsTrue()
    {
        Assert.Equal("xscpu64.exe", ViceEmulator.ExecutableNameFor(Cc65Target.C64, enableSuperCpu: true));
    }

    [Theory]
    [InlineData(Cc65Target.C128)]
    [InlineData(Cc65Target.Plus4)]
    [InlineData(Cc65Target.Vic20)]
    public void ExecutableNameFor_IgnoresEnableSuperCpu_ForEveryNonC64Target(Cc65Target target)
    {
        // The SuperCPU is a C64-specific accelerator cartridge - there's no equivalent "SuperCPU"
        // build of any other machine's emulator to switch to, so the flag is simply a no-op here.
        Assert.Equal(ViceEmulator.ExecutableNameFor(target), ViceEmulator.ExecutableNameFor(target, enableSuperCpu: true));
    }

    [Theory]
    [InlineData(Cc65Target.Apple2)]
    [InlineData(Cc65Target.Apple2Enh)]
    [InlineData(Cc65Target.Atari)]
    [InlineData(Cc65Target.Atari5200)]
    [InlineData(Cc65Target.Nes)]
    [InlineData(Cc65Target.Atmos)]
    [InlineData(Cc65Target.Lynx)]
    [InlineData(Cc65Target.None)]
    public void ExecutableNameFor_ReturnsNull_ForTargetsViceHasNoEmulatorFor(Cc65Target target)
    {
        Assert.Null(ViceEmulator.ExecutableNameFor(target));
    }

    [Fact]
    public void Constructor_DefaultsBinDirectory_ToDefaultBinDirectoryConstant()
    {
        var vice = new ViceEmulator();

        Assert.Equal(ViceEmulator.DefaultBinDirectory, vice.BinDirectory);
    }

    [Fact]
    public void BinDirectory_IsMutable_SoItCanBeChangedAfterConstruction()
    {
        var vice = new ViceEmulator();

        vice.BinDirectory = @"C:\SomewhereElse\bin";

        Assert.Equal(@"C:\SomewhereElse\bin", vice.BinDirectory);
    }

    [Fact]
    public void BuildArguments_OmitsBinaryMonitorFlag_WhenEnableBinaryMonitorIsFalse()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };

        var args = ViceEmulator.BuildArguments(project, enableBinaryMonitor: false);

        Assert.DoesNotContain("-binarymonitor", args);
    }

    [Fact]
    public void BuildArguments_IncludesBinaryMonitorFlag_WhenEnableBinaryMonitorIsTrue()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };

        var args = ViceEmulator.BuildArguments(project, enableBinaryMonitor: true);

        Assert.Contains("-binarymonitor", args);
    }

    [Fact]
    public void BuildArguments_AlwaysIncludesAutostartAndTheResolvedOutputFile()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };

        var args = ViceEmulator.BuildArguments(project, enableBinaryMonitor: false);

        Assert.Equal(["-autostart", project.ResolvedOutputFile], args);
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("vic20.cfg", "none")]
    [InlineData("some-custom-config.cfg", "none")]
    [InlineData("vic20-asm-3k.cfg", "3k")]
    [InlineData("vic20-tgi.cfg", "8k")]
    [InlineData("vic20-32k.cfg", "all")]
    [InlineData("vic20-asm-32k.cfg", "all")]
    [InlineData("VIC20-32K.CFG", "all")]
    public void Vic20MemorySpecFor_MapsKnownCc65ConfigNames_ToTheMatchingViceMemoryPreset(string? linkerConfigPath, string expectedSpec)
    {
        Assert.Equal(expectedSpec, ViceEmulator.Vic20MemorySpecFor(linkerConfigPath));
    }

    [Fact]
    public void Vic20MemorySpecFor_MatchesByFileNameOnly_RegardlessOfDirectory()
    {
        Assert.Equal("all", ViceEmulator.Vic20MemorySpecFor(@"C:\CC65\cfg\vic20-32k.cfg"));
    }

    [Fact]
    public void BuildArguments_IncludesMemoryFlag_ForVic20Projects()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.Vic20, LinkerConfigPath = "vic20-32k.cfg" };

        var args = ViceEmulator.BuildArguments(project, enableBinaryMonitor: false);

        Assert.Contains("-memory", args);
        Assert.Contains("all", args);
    }

    [Fact]
    public void BuildArguments_AssumesUnexpanded_WhenAVic20ProjectHasNoCustomLinkerConfig()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.Vic20, LinkerConfigPath = null };

        var args = ViceEmulator.BuildArguments(project, enableBinaryMonitor: false);

        Assert.Contains("-memory", args);
        Assert.Contains("none", args);
    }

    [Fact]
    public void BuildArguments_OmitsMemoryFlag_ForNonVic20Projects()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };

        var args = ViceEmulator.BuildArguments(project, enableBinaryMonitor: false);

        Assert.DoesNotContain("-memory", args);
    }

    [Fact]
    public void Launch_ThrowsNotSupportedException_WhenTargetHasNoViceEmulator()
    {
        var vice = new ViceEmulator();
        var project = new TedideProject { Name = "Test", Target = Cc65Target.Apple2 };

        var ex = Assert.Throws<NotSupportedException>(() => vice.Launch(project));

        Assert.Contains("apple2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_ThrowsFileNotFoundException_WhenTheEmulatorIsNotAtBinDirectory()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var vice = new ViceEmulator(dir.FullName);
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };

            var ex = Assert.Throws<FileNotFoundException>(() => vice.Launch(project));

            Assert.Equal(Path.Combine(dir.FullName, "x64sc.exe"), ex.FileName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Launch_StartsTheProcessAndStreamsItsExitLine_WhenTheExecutableExists()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            // A real, always-present, near-instant Windows executable stands in for the VICE
            // emulator here - Launch only cares that *some* process starts at the expected path,
            // not what it actually does, so copying one under an emulator's expected filename
            // (x64sc.exe) exercises Process.Start/output redirection/the Exited handler for real,
            // without depending on VICE actually being installed (this whole class is
            // Windows-only already - see its hardcoded .exe names - so a Windows system
            // executable is a fair stand-in, not a new platform assumption).
            var fakeExecutablePath = Path.Combine(dir.FullName, "x64sc.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"), fakeExecutablePath);

            var vice = new ViceEmulator(dir.FullName);
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, OutputFile = "out.prg" };

            var lines = new List<string>();
            var exited = new TaskCompletionSource();
            vice.Launch(project, line =>
            {
                lines.Add(line);
                if (line.Contains("exited", StringComparison.Ordinal))
                    exited.TrySetResult();
            });

            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains(lines, l => l.Contains("x64sc.exe exited", StringComparison.Ordinal));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Launch_LooksForTheSuperCpuEmulator_WhenTheProjectHasEnableSuperCpuSet()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var vice = new ViceEmulator(dir.FullName);
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, EnableSuperCpu = true };

            var ex = Assert.Throws<FileNotFoundException>(() => vice.Launch(project));

            Assert.Equal(Path.Combine(dir.FullName, "xscpu64.exe"), ex.FileName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Launch_LooksInTheCurrentBinDirectory_NotTheOneFromConstruction()
    {
        // BinDirectory is deliberately mutable (see its own doc comment) so AppShell can update an
        // existing ViceEmulator in place when the user changes the VICE tab - Launch must honor
        // whatever it's set to at call time, not a value captured at construction.
        var originalDir = Directory.CreateTempSubdirectory();
        var updatedDir = Directory.CreateTempSubdirectory();
        try
        {
            var vice = new ViceEmulator(originalDir.FullName)
            {
                BinDirectory = updatedDir.FullName,
            };
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };

            var ex = Assert.Throws<FileNotFoundException>(() => vice.Launch(project));

            Assert.Equal(Path.Combine(updatedDir.FullName, "x64sc.exe"), ex.FileName);
        }
        finally
        {
            originalDir.Delete(recursive: true);
            updatedDir.Delete(recursive: true);
        }
    }
}
