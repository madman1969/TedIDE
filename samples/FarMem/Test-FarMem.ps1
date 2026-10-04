<#
.SYNOPSIS
Runs every FarTest program in VICE and reports which passed.

.DESCRIPTION
Build the FarMem solution in Tedide first (Build > Build Solution). Each test program then starts
in its VICE emulator with the same arguments Tedide's Run uses - the project's own "Extra VICE
arguments" included - plus VICE's debug cartridge, through which fartest.c ends the emulator with
0 when every check passed, or $80 + the first failing check's number. -warp makes each run take a
few seconds; -limitcycles stops a hung one.

VICE starts each program after a random delay, so timing-dependent bugs only show up now and then:
-Repeat runs each machine several times.

.EXAMPLE
./Test-FarMem.ps1
./Test-FarMem.ps1 -Project FarTestCBM610 -Screenshots shots
./Test-FarMem.ps1 -Repeat 10
#>
param(
    [string]$ViceBin = 'C:\GTK3VICE-3.9-win64\bin',
    [string[]]$Project = @(),
    # A folder to save each machine's screen to as it exits, to see the checks and backend.
    [string]$Screenshots,
    [int]$Repeat = 1
)

$ErrorActionPreference = 'Stop'

function Get-Emulator($p) {
    switch ($p.Target) {
        'C64' { if ($p.EnableSuperCpu) { 'xscpu64.exe' } else { 'x64sc.exe' } }
        'C128' { 'x128.exe' }
        'C16' { 'xplus4.exe' }
        'Plus4' { 'xplus4.exe' }
        'Cbm510' { 'xcbm5x0.exe' }
        'Cbm610' { 'xcbm2.exe' }
        default { throw "No emulator for $($p.Target)" }
    }
}

$results = foreach ($file in Get-ChildItem $PSScriptRoot -Filter 'FarTest*.tproj' -Recurse) {
    $p = Get-Content $file.FullName -Raw | ConvertFrom-Json
    if ($Project.Count -and $Project -notcontains $p.Name) { continue }
    $program = Join-Path $file.DirectoryName $p.OutputFile
    if (-not (Test-Path $program)) {
        [pscustomobject]@{ Project = $p.Name; Result = 'not built' }
        continue
    }

    # What Tedide's ViceEmulator.BuildArguments passes, then the project's own arguments.
    $arguments = @('-default', '-debugcart', '-warp', '-sounddev', 'dummy', '-limitcycles', '400000000',
        '-autostart', "`"$program`"")
    if ($p.Target -eq 'Cbm610') { $arguments += '-autostartprgmode', '1' }
    if ($p.Target -in 'C16', 'Plus4') {
        $ram = switch ([IO.Path]::GetFileName([string]$p.LinkerConfigPath).ToLowerInvariant()) {
            { $_ -in 'c16.cfg', 'c16-asm.cfg' } { 16 }
            'c16-32k.cfg' { 32 }
            default { if ($p.Target -eq 'C16') { 16 } else { 64 } }
        }
        $arguments += '-ramsize', $ram
    }
    if ($p.ViceArguments) { $arguments += $p.ViceArguments }
    if ($Screenshots) {
        New-Item -ItemType Directory -Force $Screenshots | Out-Null
        # The C128's plain -exitscreenshot is its 80-column screen; fartest uses the 40-column one.
        $option = if ($p.Target -eq 'C128') { '-exitscreenshotvicii' } else { '-exitscreenshot' }
        $arguments += $option, "`"$(Join-Path (Resolve-Path $Screenshots) "$($p.Name).png")`""
    }

    $started = Get-Date
    $failures = @(for ($run = 0; $run -lt $Repeat; $run++) {
        $code = (Start-Process -FilePath (Join-Path $ViceBin (Get-Emulator $p)) -ArgumentList $arguments -PassThru -Wait -WindowStyle Minimized).ExitCode
        if ($code -ge 0x80 -and $code -lt 0x100) { "check $($code - 0x80)" }
        elseif ($code -ne 0) { "no result (VICE exit code $code)" }
    })
    $result = if ($failures.Count -eq 0) { 'passed' + $(if ($Repeat -gt 1) { " $Repeat/$Repeat" }) }
        else { "FAILED $($failures.Count)/$($Repeat): $($failures -join ', ')" }
    [pscustomobject]@{ Project = $p.Name; Result = $result; Seconds = [int]((Get-Date) - $started).TotalSeconds }
}

$results | Format-Table -AutoSize
if ($results | Where-Object Result -notlike 'passed*') { exit 1 }
