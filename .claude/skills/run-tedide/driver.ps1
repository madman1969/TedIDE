<#
.SYNOPSIS
    Headless-ish driver for Tedide (a Terminal.Gui v2 console TUI app) on Windows: launches it
    inside a real Windows Terminal window, drives it with real keystrokes, and captures real
    screenshots - see SKILL.md for why this is the only launch method that actually renders.

.DESCRIPTION
    Dot-source this file to get the functions below, or run it directly to execute the built-in
    smoke test (launch, screenshot, open File menu, screenshot, close menu, screenshot, quit).

    Functions (all operate on the window and process Start-TedideApp launched - tracked by window
    handle and process id, so a dialog changing the title doesn't lose it):
      Start-TedideApp   -Exe <path to Tedide.App.dll>
      Get-TedideWindow                                    # .MainWindowHandle and .Title, or $null
      Save-TedideScreenshot -Name <file stem> [-OutDir <dir>]
      Send-TedideKeys  -Keys <SendKeys syntax string>      # e.g. "%f" = Alt+F, "{ESC}", "{ENTER}"
      Send-TedideClick -X <px> -Y <px> [-Right]           # window-relative, as in a screenshot
      Stop-TedideApp

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File driver.ps1 -Exe C:\path\to\Tedide.App.dll
#>
param(
    [string]$Exe = "$PSScriptRoot\..\..\..\src\Tedide.App\bin\Debug\net10.0\Tedide.App.dll",
    [string]$OutDir = "$PSScriptRoot"
)
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace TedideDriver -Name Native -MemberDefinition @"
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
[DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

public static System.Collections.Generic.List<IntPtr> TerminalWindows()
{
    var found = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((hWnd, unused) =>
    {
        var name = new System.Text.StringBuilder(256);
        GetClassName(hWnd, name, name.Capacity);
        if (name.ToString() == "CASCADIA_HOSTING_WINDOW_CLASS" && IsWindowVisible(hWnd))
            found.Add(hWnd);
        return true;
    }, IntPtr.Zero);
    return found;
}

public static string Title(IntPtr hWnd)
{
    var text = new System.Text.StringBuilder(512);
    GetWindowText(hWnd, text, text.Capacity);
    return text.ToString();
}
"@ -ErrorAction SilentlyContinue

# The window and the dotnet process Start-TedideApp launched. Everything works on these, not on a
# window title: Tedide's title changes while a dialog is open (Go To Line, Compare, a message box),
# and Windows Terminal runs all its windows in ONE WindowsTerminal process - so matching that
# process's MainWindowTitle lost the window under a dialog, and stopping "the" process closed every
# terminal window the user had open, not just the test one.
$script:TedideHwnd = [IntPtr]::Zero
$script:TedidePid = $null

function Get-TedideWindow {
    # Returns an object with .MainWindowHandle and .Title, or $null once the window has gone.
    $hwnd = $script:TedideHwnd
    if ($hwnd -eq [IntPtr]::Zero -or -not [TedideDriver.Native]::IsWindow($hwnd)) {
        # Not launched from this script run (e.g. dot-sourced afresh): fall back to the title,
        # which only matches while no dialog is open.
        $hwnd = [TedideDriver.Native]::TerminalWindows() |
            Where-Object { [TedideDriver.Native]::Title($_) -eq "Tedide - CC65 IDE" } |
            Select-Object -First 1
        if (-not $hwnd) { return $null }
    }
    [pscustomobject]@{ MainWindowHandle = $hwnd; Title = [TedideDriver.Native]::Title($hwnd) }
}

function Get-TedideProcessIds {
    param([Parameter(Mandatory)][string]$Exe)
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($Exe, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        ForEach-Object { $_.ProcessId }
}

function Start-TedideApp {
    param([Parameter(Mandatory)][string]$Exe)
    if (-not (Test-Path $Exe)) { throw "Tedide.App.dll not found at '$Exe' - build it first (see SKILL.md)." }
    $Exe = (Resolve-Path $Exe).Path
    $windowsBefore = @([TedideDriver.Native]::TerminalWindows())
    $pidsBefore = @(Get-TedideProcessIds -Exe $Exe)
    $script:TedideHwnd = [IntPtr]::Zero
    $script:TedidePid = $null
    # wt.exe (Windows Terminal) is the launch mechanism, not an incidental choice: Terminal.Gui's
    # startup relies on being hosted by a real terminal emulator that answers its ANSI capability
    # queries (size, colors, etc.) - see SKILL.md Gotchas. A bare AllocConsole (e.g. via
    # ProcessStartInfo directly) never gets those answered and hangs forever inside Application.Run.
    # "-w new" forces a dedicated window rather than reusing/accumulating tabs in whatever Windows
    # Terminal window last had focus - important since that could otherwise be a real window the
    # user has open themselves.
    Start-Process wt.exe -ArgumentList @("-w", "new", "new-tab", "--title", "TedideTest", "dotnet", "`"$Exe`"") | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ($script:TedideHwnd -eq [IntPtr]::Zero) {
            $new = [TedideDriver.Native]::TerminalWindows() | Where-Object { $windowsBefore -notcontains $_ } | Select-Object -First 1
            if ($new) { $script:TedideHwnd = $new }
        }
        if (-not $script:TedidePid) {
            $script:TedidePid = Get-TedideProcessIds -Exe $Exe | Where-Object { $pidsBefore -notcontains $_ } | Select-Object -First 1
        }
        # The app sets this title once it's up and drawing.
        if ($script:TedideHwnd -ne [IntPtr]::Zero -and $script:TedidePid -and
            [TedideDriver.Native]::Title($script:TedideHwnd) -eq "Tedide - CC65 IDE") {
            Start-Sleep -Milliseconds 500
            return
        }
    }
    Stop-TedideApp
    throw "Tedide window did not appear within 10s."
}

function Save-TedideScreenshot {
    param([Parameter(Mandatory)][string]$Name, [string]$OutDir = $OutDir)
    $wt = Get-TedideWindow
    if (-not $wt) { Write-Host "[shot $Name] no Tedide window found"; return }
    # Bitmap.Save() throws an opaque "A generic error occurred in GDI+." (not a clear
    # DirectoryNotFoundException) if OutDir doesn't exist yet - easy to hit since callers often
    # pass a fresh scratch/output directory.
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $rect = New-Object TedideDriver.Native+RECT
    [TedideDriver.Native]::GetWindowRect($wt.MainWindowHandle, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $path = Join-Path $OutDir "$Name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "[shot $Name] -> $path (window title: $($wt.Title))"
    return $path
}

function Send-TedideKeys {
    param([Parameter(Mandatory)][string]$Keys, [int]$SettleMs = 400)
    $wt = Get-TedideWindow
    if (-not $wt) { throw "No Tedide window to send keys to." }
    [TedideDriver.Native]::SetForegroundWindow($wt.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
    # SendKeys types into whatever has focus - never let keys leak to another window.
    if ([TedideDriver.Native]::GetForegroundWindow() -ne $wt.MainWindowHandle) {
        throw "Couldn't focus the Tedide window - not sending '$Keys'."
    }
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds $SettleMs
}

function Send-TedideClick {
    # Clicks at (X, Y) in pixels from the window's top-left corner - the same coordinates as a
    # Save-TedideScreenshot image, so read them straight off a screenshot.
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y, [switch]$Right, [int]$SettleMs = 400)
    $wt = Get-TedideWindow
    if (-not $wt) { throw "No Tedide window to click." }
    [TedideDriver.Native]::SetForegroundWindow($wt.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
    if ([TedideDriver.Native]::GetForegroundWindow() -ne $wt.MainWindowHandle) {
        throw "Couldn't focus the Tedide window - not clicking."
    }
    $rect = New-Object TedideDriver.Native+RECT
    [TedideDriver.Native]::GetWindowRect($wt.MainWindowHandle, [ref]$rect) | Out-Null
    [TedideDriver.Native]::SetCursorPos($rect.Left + $X, $rect.Top + $Y) | Out-Null
    Start-Sleep -Milliseconds 100
    # MOUSEEVENTF_LEFTDOWN/UP = 0x2/0x4, RIGHTDOWN/UP = 0x8/0x10
    $down, $up = if ($Right) { 0x8, 0x10 } else { 0x2, 0x4 }
    [TedideDriver.Native]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 50
    [TedideDriver.Native]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $SettleMs
}

function Stop-TedideApp {
    # Only what Start-TedideApp launched: its dotnet process, then its own window (WM_CLOSE) -
    # never the WindowsTerminal process, which also hosts the user's other terminal windows.
    if ($script:TedidePid) { Stop-Process -Id $script:TedidePid -Force -ErrorAction SilentlyContinue }
    if ($script:TedideHwnd -ne [IntPtr]::Zero -and [TedideDriver.Native]::IsWindow($script:TedideHwnd)) {
        Start-Sleep -Milliseconds 300
        [TedideDriver.Native]::PostMessage($script:TedideHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }
    $script:TedideHwnd = [IntPtr]::Zero
    $script:TedidePid = $null
}

# Run the built-in smoke test only when executed directly (not dot-sourced).
if ($MyInvocation.InvocationName -ne '.') {
    try {
        Start-TedideApp -Exe $Exe
        Save-TedideScreenshot -Name "smoke_01_launch"
        Send-TedideKeys -Keys "%f"        # Alt+F: open File menu
        Save-TedideScreenshot -Name "smoke_02_file_menu_open"
        Send-TedideKeys -Keys "{ESC}"     # dismiss it
        Save-TedideScreenshot -Name "smoke_03_file_menu_closed"
    }
    finally {
        Stop-TedideApp
    }
}
