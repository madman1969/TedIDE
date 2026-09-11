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
        return args;
    }
}
