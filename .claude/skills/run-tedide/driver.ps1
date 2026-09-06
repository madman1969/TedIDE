<#
.SYNOPSIS
    Headless-ish driver for Tedide (a Terminal.Gui v2 console TUI app) on Windows: launches it
    inside a real Windows Terminal window, drives it with real keystrokes, and captures real
    screenshots - see SKILL.md for why this is the only launch method that actually renders.

.DESCRIPTION
    Dot-source this file to get the functions below, or run it directly to execute the built-in
    smoke test (launch, screenshot, open File menu, screenshot, close menu, screenshot, quit).

    Functions (all operate on "the Tedide window" - found by MainWindowTitle):
      Start-TedideApp   -Exe <path to Tedide.App.dll>
      Get-TedideWindow                                    # returns the Process, or $null
      Save-TedideScreenshot -Name <file stem> [-OutDir <dir>]
      Send-TedideKeys  -Keys <SendKeys syntax string>      # e.g. "%f" = Alt+F, "{ESC}", "{ENTER}"
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
Add-Type -Namespace Win32 -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
"@ -ErrorAction SilentlyContinue

function Get-TedideWindow {
    Get-Process WindowsTerminal -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -eq "Tedide - CC65 IDE" } |
        Select-Object -First 1
}

function Start-TedideApp {
    param([Parameter(Mandatory)][string]$Exe)
    if (-not (Test-Path $Exe)) { throw "Tedide.App.dll not found at '$Exe' - build it first (see SKILL.md)." }
    # wt.exe (Windows Terminal) is the launch mechanism, not an incidental choice: Terminal.Gui's
    # startup relies on being hosted by a real terminal emulator that answers its ANSI capability
    # queries (size, colors, etc.) - see SKILL.md Gotchas. A bare AllocConsole (e.g. via
    # ProcessStartInfo directly) never gets those answered and hangs forever inside Application.Run.
    # "-w new" forces a dedicated window rather than reusing/accumulating tabs in whatever Windows
    # Terminal window last had focus - important since that could otherwise be a real window the
    # user has open themselves, title collision and all.
    Start-Process wt.exe -ArgumentList @("-w", "new", "new-tab", "--title", "TedideTest", "dotnet", "`"$Exe`"") | Out-Null
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 250
        if (Get-TedideWindow) { Start-Sleep -Milliseconds 500; return }
    }
    throw "Tedide window did not appear within 5s."
}

function Save-TedideScreenshot {
    param([Parameter(Mandatory)][string]$Name, [string]$OutDir = $OutDir)
    $wt = Get-TedideWindow
    if (-not $wt) { Write-Host "[shot $Name] no Tedide window found"; return }
    # Bitmap.Save() throws an opaque "A generic error occurred in GDI+." (not a clear
    # DirectoryNotFoundException) if OutDir doesn't exist yet - easy to hit since callers often
    # pass a fresh scratch/output directory.
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $rect = New-Object Win32.Win+RECT
    [Win32.Win]::GetWindowRect($wt.MainWindowHandle, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $path = Join-Path $OutDir "$Name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "[shot $Name] -> $path"
    return $path
}

function Send-TedideKeys {
    param([Parameter(Mandatory)][string]$Keys, [int]$SettleMs = 400)
    $wt = Get-TedideWindow
    if (-not $wt) { throw "No Tedide window to send keys to." }
    [Win32.Win]::SetForegroundWindow($wt.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds $SettleMs
}

function Stop-TedideApp {
    $wt = Get-TedideWindow
    if ($wt) { Stop-Process -Id $wt.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
        Where-Object { $_.CommandLine -like "*Tedide.App*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
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
