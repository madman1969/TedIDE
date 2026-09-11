using System.ComponentModel;
using System.Diagnostics;
using Tedide.Core;

namespace Tedide.Build;

/// <summary>
/// Launches a project's built output in the matching VICE emulator (the standard Commodore 8/16-bit
/// emulator suite - https://vice-emu.sourceforge.io), auto-starting the program.
/// </summary>
public sealed class ViceEmulator(string binDirectory = ViceEmulator.DefaultBinDirectory)
{
    public const string DefaultBinDirectory = @"C:\GTK3VICE-3.9-win64\bin";

    /// <summary>
    /// Not constructor-only (unlike most of this codebase's config-holding properties) - AppShell
    /// reassigns this in place when the user changes it via ProjectSettingsDialog's "VICE" tab, so
    /// the change takes effect for the next launch without needing a new ViceEmulator instance.
    /// </summary>
    public string BinDirectory { get; set; } = binDirectory;

    /// <summary>
    /// The VICE emulator executable for each Commodore machine cc65 can target - see
    /// <see cref="Cc65TargetExtensions.CommodoreTargets"/>. Returns null for a target VICE has no
    /// emulator for (any non-Commodore cc65 target).
    /// </summary>
    /// <param name="enableSuperCpu">
    /// When true and <paramref name="target"/> is <see cref="Cc65Target.C64"/>, returns VICE's
    /// dedicated SuperCPU emulator (xscpu64.exe) instead of the plain C64 one - see
    /// <see cref="TedideProject.EnableSuperCpu"/>. Ignored for every other target: the SuperCPU is
    /// a C64-specific accelerator cartridge, so there's no equivalent "SuperCPU" build of any other
    /// machine's emulator to switch to.
    /// </param>
    public static string? ExecutableNameFor(Cc65Target target, bool enableSuperCpu = false) => target switch
    {
        Cc65Target.C64 when enableSuperCpu => "xscpu64.exe",
        Cc65Target.C64 => "x64sc.exe",
        Cc65Target.C128 => "x128.exe",
        Cc65Target.C16 => "xplus4.exe",
        Cc65Target.Plus4 => "xplus4.exe",
        Cc65Target.Vic20 => "xvic.exe",
        Cc65Target.Pet => "xpet.exe",
        Cc65Target.Cbm510 => "xcbm5x0.exe",
        Cc65Target.Cbm610 => "xcbm2.exe",
        Cc65Target.Geos_Cbm => "x64sc.exe",
        _ => null,
    };

    /// <summary>
    /// Launches the emulator matching <paramref name="project"/>'s target, auto-starting its
    /// built output file. Returns immediately without waiting for the emulator to exit - VICE
    /// runs indefinitely until the user closes it, so this just wires up live streaming (if
    /// <paramref name="onOutputLine"/> is given) rather than blocking on it. Throws
    /// <see cref="NotSupportedException"/> if the target has no VICE emulator, or
    /// <see cref="FileNotFoundException"/> if that emulator isn't installed at
    /// <see cref="BinDirectory"/>.
    /// </summary>
    /// <param name="project">The project whose built output to auto-start.</param>
    /// <param name="onOutputLine">
    /// Optional callback invoked for each line of the emulator's stdout/stderr as it runs, and
    /// once more when it exits. VICE is mostly a GUI app and rarely writes much, but it does log
    /// things like ROM/cartridge load errors here. Omit to run without redirecting output at all.
    /// </param>
    /// <param name="enableBinaryMonitor">
    /// When true, also passes "-binarymonitor" (VICE then listens on its default 127.0.0.1:6502)
    /// so a debugging session (Tedide.Debug's ViceMonitorClient) can connect to this instance.
    /// Defaults to false so a plain Build &gt; Run Project launch is unaffected.
    /// </param>
    public void Launch(TedideProject project, Action<string>? onOutputLine = null, bool enableBinaryMonitor = false)
    {
        var executableName = ExecutableNameFor(project.Target, project.EnableSuperCpu)
            ?? throw new NotSupportedException($"VICE has no emulator for target '{project.Target.ToCl65Id()}'.");

        var executablePath = Path.Combine(BinDirectory, executableName);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException($"VICE emulator not found at '{executablePath}'.", executablePath);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = onOutputLine is not null,
            RedirectStandardError = onOutputLine is not null,
        };
        foreach (var arg in BuildArguments(project, enableBinaryMonitor))
            startInfo.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{executablePath}'.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not launch '{executablePath}': {ex.Message}", ex);
        }

        if (onOutputLine is null)
            return;

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => onOutputLine($"------ {executableName} exited (code {process.ExitCode}) ------");
    }

    /// <summary>The VICE command-line arguments for launching <paramref name="project"/>'s built output - a pure function, split out from <see cref="Launch"/> so it's directly unit-testable without spawning a process.</summary>
    internal static List<string> BuildArguments(TedideProject project, bool enableBinaryMonitor)
    {
        var args = new List<string> { "-autostart", project.ResolvedOutputFile };
        if (enableBinaryMonitor)
            args.Add("-binarymonitor");

        // VICE remembers whichever RAM expansion was last configured (in its own persisted
        // settings), which has nothing to do with this project - passing -memory explicitly every
        // time, based on which linker config this build actually used, is what makes xvic's actual
        // memory map match what the linker already assumed instead of whatever xvic was last left
        // in from some other project.
        if (project.Target == Cc65Target.Vic20)
        {
            args.Add("-memory");
            args.Add(Vic20MemorySpecFor(project.LinkerConfigPath));
        }

        return args;
    }

    /// <summary>
    /// Maps a VIC-20 linker config to the VICE "-memory" preset (see xvic's own "-memory
    /// &lt;spec&gt;" option: none/3k/8k/16k/24k/all) that provides the same RAM expansion it
    /// assumes - by file name only (cc65's own bundled vic20*.cfg files, matched case-insensitively,
    /// wherever they actually live - Tedide.App's own Project Settings dialog defaults its Linker
    /// tab's Browse button to cc65's cfg/ folder, where these all live together), not by parsing an
    /// arbitrary custom config's MEMORY block. <paramref name="linkerConfigPath"/>
    /// null/blank (<see cref="TedideProject.LinkerConfigPath"/>'s own "target default" meaning) or
    /// any unrecognized file name falls back to "none" - the plain unexpanded VIC-20 cc65's own
    /// default vic20.cfg itself targets.
    /// </summary>
    internal static string Vic20MemorySpecFor(string? linkerConfigPath)
    {
        if (string.IsNullOrEmpty(linkerConfigPath))
            return "none";

        return Path.GetFileName(linkerConfigPath).ToLowerInvariant() switch
        {
            // Needs block 0 (3K at $0400) - cc65's only 3K config is asm-only (no C runtime
            // startup code), but it's matched here too in case a project's linker script is a
            // customized copy of it that still shares the name.
            "vic20-asm-3k.cfg" => "3k",
            // Needs at least block 1 (8K at $2000) - vic20-tgi.cfg's own comment says "at least,
            // 8K expansion RAM" for the vic20-hi.tgi driver; its __HIMEM__ default of $4000 only
            // actually uses block 1, though a project could raise __HIMEM__ to use more.
            "vic20-tgi.cfg" => "8k",
            // Needs blocks 1-3 (8K+16K+24K at $2000-$7FFF, MAIN runs up to $8000 in both) - cc65
            // labels these "32K" after the real RAM cartridge they model, which also populates
            // block 0 - "all" (every block, including 0) is the closest VICE preset to that real
            // hardware, a strict superset of what these configs' own MEMORY block actually needs.
            "vic20-32k.cfg" or "vic20-asm-32k.cfg" => "all",
            _ => "none",
        };
    }
}
