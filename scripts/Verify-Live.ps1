<#
.SYNOPSIS
    Drives a real Tedide through its main features in Windows Terminal, as a check before pushing:
    what the unit tests can't reach - the window itself, dialogs, keys, cc65 and VICE.

.DESCRIPTION
    Builds the app, makes a throwaway copy of samples/CBMInfo as a git repository (with a remote
    and a second branch), and then, through the run-tedide driver
    (.claude/skills/run-tedide/driver.ps1):
      - opens it from File > Recent Projects, with src/video.c open and edited
      - Go To Line, Go To Definition, Navigate Backward, Find All References, Find in Files
      - the Git tab: Compare, Blame, File History, staging
      - Build Solution
      - debugging in VICE: start, stop at main, step, stop (left out with -SkipDebug)
    Each step checks that Tedide is still running and, where it opens a dialog or stops in the
    debugger, that the window title says so. A screenshot of every step goes to -OutDir. At the
    end, the Windows Application log is checked for a .NET crash.

    Takes about two minutes and types into the Tedide window the whole time: don't use the
    keyboard or switch windows while it runs. Your Tedide settings (%LocalAppData%\Tedide) are
    backed up first and restored afterwards, and the throwaway repository is deleted.

    Needs Windows Terminal, git, cc65 (cl65 on PATH, or CC65_HOME) and, unless -SkipDebug, VICE.
    Exits with 0 when every step passed, 1 otherwise.

.PARAMETER OutDir
    Where the screenshots go. Defaults to %TEMP%\tedide-verify.

.PARAMETER SkipDebug
    Leaves out the debugging steps - for a machine without VICE.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Verify-Live.ps1
#>
param(
    [string]$OutDir = (Join-Path $env:TEMP 'tedide-verify'),
    [switch]$SkipDebug
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# Kept apart from -OutDir: dot-sourcing the driver runs its own param block, which resets $OutDir
# to the driver's folder - and this script empties its screenshot folder first.
$shots = $OutDir
. (Join-Path $repo '.claude\skills\run-tedide\driver.ps1')

$work = Join-Path $env:TEMP "tedide-verify-work-$PID"
$settings = Join-Path $env:LocalAppData 'Tedide'
$settingsBackup = Join-Path $work 'settings-backup'
$app = Join-Path $work 'app'
$solutionDir = Join-Path $work 'CBMInfo'
$started = Get-Date
$results = [System.Collections.Generic.List[object]]::new()
$vicesBefore = @(Get-Process x64sc, x64, xscpu64, xvic, xplus4, xpet, x128 -ErrorAction SilentlyContinue | ForEach-Object Id)

function Invoke-Git {
    # Invoke-Git <directory> <git arguments...> - a plain function, so "-q" and the like reach git.
    $directory, $arguments = $args
    & git -C $directory @arguments 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git $($arguments -join ' ') failed in $directory" }
}

function New-ScratchSolution {
    # samples/CBMInfo's sources and project files, committed, pushed to a local bare remote, plus a
    # branch - so the Git tab, blame, history and the branch list all have something to show.
    New-Item -ItemType Directory -Force $solutionDir | Out-Null
    $sample = Join-Path $repo 'samples\CBMInfo'
    foreach ($item in 'include', 'src', 'lib', 'CBMInfo.tproj', 'CBMInfo.tsln') {
        Copy-Item (Join-Path $sample $item) $solutionDir -Recurse
    }
    "bin/`nobj/`n*.dbg`n*.lbl`nlnk.map`n*.session.json`n*.breakpoints.json`n" | Set-Content (Join-Path $solutionDir '.gitignore') -NoNewline
    $remote = Join-Path $work 'origin.git'
    & git init -q --bare -b main $remote
    Invoke-Git $solutionDir init -q -b main
    Invoke-Git $solutionDir config user.name 'Tedide Verify'
    Invoke-Git $solutionDir config user.email 'verify@example.com'
    Invoke-Git $solutionDir config core.autocrlf false
    Invoke-Git $solutionDir add .
    Invoke-Git $solutionDir commit -q -m 'Start the CBMInfo sample'
    Invoke-Git $solutionDir remote add origin $remote
    Invoke-Git $solutionDir push -q -u origin main
    Invoke-Git $solutionDir branch feature/sid-voices
    # The sample's breakpoint (in main), for the debugging steps.
    Copy-Item (Join-Path $sample 'CBMInfo.breakpoints.json') $solutionDir
    # Opening the solution reopens these tabs, so no clicking through the Solution Explorer.
    '{ "LastOpenFile": "src/video.c", "OpenFiles": [ "src/video.c" ] }' |
        Set-Content (Join-Path $solutionDir 'CBMInfo.session.json') -Encoding utf8
}

function Backup-Settings {
    New-Item -ItemType Directory -Force $settingsBackup, $settings | Out-Null
    Get-ChildItem $settings -Filter *.json -File | Copy-Item -Destination $settingsBackup
    @{ Paths = @(Join-Path $solutionDir 'CBMInfo.tsln') } | ConvertTo-Json | Set-Content (Join-Path $settings 'recent.json') -Encoding utf8
}

function Restore-Settings {
    if (-not (Test-Path $settingsBackup)) { return }
    $kept = @(Get-ChildItem $settingsBackup -Filter *.json -File | ForEach-Object Name)
    Get-ChildItem $settings -Filter *.json -File | Where-Object { $kept -notcontains $_.Name } | Remove-Item
    Get-ChildItem $settingsBackup -Filter *.json -File | Copy-Item -Destination $settings -Force
}

function Test-TedideRunning {
    $script:TedidePid -and (Get-Process -Id $script:TedidePid -ErrorAction SilentlyContinue)
}

function Wait-Title([string]$Like, [int]$Seconds) {
    for ($i = 0; $i -lt $Seconds * 4; $i++) {
        if ((Get-TedideWindow).Title -like $Like) { return $true }
        Start-Sleep -Milliseconds 250
    }
    $false
}

$mainTitle = 'Tedide - CC65 IDE'

function Step {
    # Runs one step, then checks Tedide is still running and, with -Title, that the window title
    # matches (a dialog's title, or the debugger's status) within -Wait seconds. Screenshots it.
    param([string]$Name, [scriptblock]$Action, [string]$Title, [int]$Wait = 5)
    $problem = $null
    try {
        # A dialog an earlier step left open would swallow this step's keys.
        if ((Get-TedideWindow).Title -notlike "$mainTitle*") {
            $problem = "'$((Get-TedideWindow).Title)' was left open"
            Close-Dialog
        }
        & $Action
        if (-not $problem -and $Title -and -not (Wait-Title $Title $Wait)) {
            $problem = "the window title is '$((Get-TedideWindow).Title)', expected '$Title'"
        }
        Start-Sleep -Milliseconds 500
    }
    catch { $problem = $_.Exception.Message }
    if (-not (Test-TedideRunning)) { $problem = 'Tedide is no longer running' }
    if (Get-TedideWindow) {
        [TedideDriver.Native]::SetForegroundWindow((Get-TedideWindow).MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 300
        Save-TedideScreenshot -Name ('{0:00}_{1}' -f ($results.Count + 1), ($Name -replace '\W+', '_').Trim('_')) -OutDir $shots | Out-Null
    }
    $results.Add([pscustomobject]@{ Step = $Name; Passed = -not $problem; Problem = $problem })
    Write-Host ($(if ($problem) { "FAIL  $Name - $problem" } else { "ok    $Name" }))
    if (-not (Test-TedideRunning)) { throw 'Tedide is no longer running - stopping here.' }
}

function Close-Dialog {
    # Only a dialog: the main window's title is "Tedide - CC65 IDE", plus the debugger's state while
    # debugging - and Esc on the main window quits.
    if ((Get-TedideWindow).Title -notlike "$mainTitle*") { Send-TedideKeys -Keys '{ESC}'; [void](Wait-Title "$mainTitle*" 5) }
}

function Set-CaretLine([int]$Line) {
    Send-TedideKeys -Keys '^g'
    [void](Wait-Title 'Go To Line*' 5)
    Send-TedideKeys -Keys "$Line{ENTER}"
    [void](Wait-Title $mainTitle 5)
}

try {
    # Only ever a folder of this script's own screenshots.
    if (Test-Path $shots) { Get-ChildItem $shots -Filter '*.png' | Remove-Item -Force }
    New-Item -ItemType Directory -Force $shots, $work | Out-Null

    Write-Host 'Building Tedide...'
    & dotnet build (Join-Path $repo 'src\Tedide.App\Tedide.App.csproj') -p:UseAppHost=false -o $app -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
    New-ScratchSolution
    Backup-Settings

    Write-Host "Driving Tedide - keep your hands off the keyboard until it's done."
    Start-TedideApp -Exe (Join-Path $app 'Tedide.App.dll')

    Step 'Open the solution from Recent Projects' {
        Send-TedideKeys -Keys '%f'
        Send-TedideKeys -Keys '{DOWN}{DOWN}{RIGHT}{ENTER}'
        Start-Sleep -Seconds 3
    }
    Step 'Edit video.c (git change bars and blame)' {
        Set-CaretLine 36
        Send-TedideKeys -Keys '{END}  /* bit 8 */'
        Set-CaretLine 24
        Start-Sleep -Seconds 4
    }
    Step 'Go To Definition (F12)' {
        Set-CaretLine 19
        Send-TedideKeys -Keys '{F12}'
        Start-Sleep -Seconds 2
        Close-Dialog
    }
    Step 'Navigate Backward (Alt+Left)' { Send-TedideKeys -Keys '%{LEFT}'; Start-Sleep -Seconds 1 }
    Step 'Find All References (Shift+F12)' { Send-TedideKeys -Keys '+{F12}'; Start-Sleep -Seconds 2; Close-Dialog }
    Step 'Find in Files (Alt+Shift+F)' {
        Send-TedideKeys -Keys '%+f'
        Start-Sleep -Seconds 1
        Send-TedideKeys -Keys 'rasterline{ENTER}'
        Start-Sleep -Seconds 2
    } -Title 'Find in Files*'
    Close-Dialog
    Step 'Save, and open the Git tab' {
        Send-TedideKeys -Keys '^s'
        Send-TedideKeys -Keys '%v'
        Send-TedideKeys -Keys 'g'
        Start-Sleep -Seconds 4
        # The tab opens with Branches focused. Changes comes 13 stops on: History, Amend, the
        # message, the nine buttons, then the list. A plain letter anywhere else is one of the
        # bottom pane's tab hotkeys.
        Send-TedideKeys -Keys ('{TAB}' * 13)
    }
    Step 'Git: Compare with Last Commit (D)' { Send-TedideKeys -Keys 'd' } -Title 'Compare*'
    Close-Dialog
    Step 'Git: Blame (B)' { Send-TedideKeys -Keys 'b' } -Title 'Blame*'
    Close-Dialog
    Step 'Git: File History (H)' { Send-TedideKeys -Keys 'h' } -Title 'History*'
    Close-Dialog
    Step 'Git: stage the change (Space)' { Send-TedideKeys -Keys ' '; Start-Sleep -Seconds 3 }
    Step 'Build Solution (Ctrl+B)' {
        Send-TedideKeys -Keys '^b'
        Start-Sleep -Seconds 12
        Send-TedideKeys -Keys '%v'
        Send-TedideKeys -Keys 'o'
        Start-Sleep -Seconds 1
    }
    if (-not $SkipDebug) {
        Step 'Start debugging (F5)' { Send-TedideKeys -Keys '{F5}' } -Title "$mainTitle - Stopped*" -Wait 60
        Step 'Step Over (F10)' { Send-TedideKeys -Keys '{F10}'; Start-Sleep -Seconds 1 } -Title "$mainTitle - Stopped*" -Wait 20
        Step 'Stop debugging (Shift+F5)' { Send-TedideKeys -Keys '+{F5}' } -Title $mainTitle -Wait 15
    }
}
catch {
    $results.Add([pscustomobject]@{ Step = 'Run'; Passed = $false; Problem = $_.Exception.Message })
    Write-Host "FAIL  $($_.Exception.Message)"
}
finally {
    Stop-TedideApp
    Get-Process x64sc, x64, xscpu64, xvic, xplus4, xpet, x128 -ErrorAction SilentlyContinue |
        Where-Object { $vicesBefore -notcontains $_.Id } | Stop-Process -Force
    Restore-Settings
    if (Test-Path $work) {
        # git makes its object files read-only.
        Get-ChildItem $work -Recurse -File -Force | ForEach-Object { $_.IsReadOnly = $false }
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# A crash that took the process down shows up here even when no step caught it.
$crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime'; StartTime = $started } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like '*Tedide*' })
if ($crashes.Count -gt 0) {
    $results.Add([pscustomobject]@{ Step = 'Windows Application log'; Passed = $false; Problem = ($crashes[0].Message -split "`n")[0..3] -join ' ' })
}

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ''
Write-Host "$($results.Count - $failed.Count) of $($results.Count) steps passed. Screenshots: $shots"
if ($failed.Count -gt 0) {
    $failed | ForEach-Object { Write-Host "  $($_.Step): $($_.Problem)" }
    exit 1
}
exit 0
