---
name: run-tedide
description: Build, run, and drive Tedide (the CC65 IDE). Use when asked to build Tedide, launch it, take a screenshot of its UI, verify a Terminal.Gui layout/menu/dialog change actually renders and behaves correctly, or interact with the running app.
---

Tedide is a .NET 10 / Terminal.Gui v2 console TUI application (`src/Tedide.App`,
entry point `Program.cs` -> `AppShell`). It is Windows-only (no tmux on this
box). Drive it via `.claude/skills/run-tedide/driver.ps1`, which launches it
inside a real Windows Terminal window, sends it real keystrokes, and captures
real screenshots of that window - see Gotchas for why this specific launch
method is required and nothing simpler works.

All paths below are relative to the repo root (`C:\GitHub\Tedide`, or
wherever this repo is checked out).

## Prerequisites

- .NET 10 SDK (already required to build the repo at all).
- Windows Terminal (`wt.exe`) - ships by default on Windows 11; on Windows 10
  it's the Microsoft Store app. Confirm with `Get-Command wt.exe`.
- PowerShell 7+ (`pwsh`) or Windows PowerShell both work; the driver only
  uses `System.Drawing`/`System.Windows.Forms` and a couple of `user32.dll`
  P/Invokes.

## Build

Build to a **separate output directory**, not the project's own `bin/` -
if a Tedide instance (yours, or one this driver launched and didn't clean
up) is already running, it locks `bin/`'s DLLs and the default build fails
with `MSB3027`/`MSB3021` ("being used by another process"). This is not
hypothetical - it happens routinely in a session that's iterating on a fix
and re-testing:

```powershell
dotnet build src/Tedide.App/Tedide.App.csproj -p:UseAppHost=false -o <scratch-dir>
```

`-p:UseAppHost=false` additionally skips generating `Tedide.App.exe`
(and its own separate lock potential) since the driver runs it via
`dotnet Tedide.App.dll` anyway.

## Run (agent path)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\run-tedide\driver.ps1 -Exe <scratch-dir>\Tedide.App.dll -OutDir <dir-for-screenshots>
```

Run directly like this, it launches the app and executes a **built-in smoke
test**: launch -> screenshot -> open File menu (Alt+F) -> screenshot -> close
it (Esc) -> screenshot -> quit. Read the resulting PNGs with the Read tool to
actually look at them - a blank or garbled window is a failure to launch, not
success.

For anything beyond the smoke test, **dot-source** the file instead to get
its functions individually, then write a small script (inline, or as a
`.ps1` in your own scratchpad - keep `driver.ps1` itself generic) that calls
them in the sequence your scenario needs:

```powershell
. .claude\skills\run-tedide\driver.ps1   # no -Exe needed just to load functions
Start-TedideApp -Exe <scratch-dir>\Tedide.App.dll
Save-TedideScreenshot -Name "01_launch" -OutDir <dir>
Send-TedideKeys -Keys "%f"          # Alt+F - SendKeys syntax: {ESC} {ENTER} {DOWN} {UP} {LEFT} {RIGHT} {TAB} ^ (Ctrl) % (Alt) + (Shift)
Save-TedideScreenshot -Name "02_file_menu" -OutDir <dir>
Send-TedideKeys -Keys "{DOWN}{DOWN}{RIGHT}"   # navigate to and expand "Recent Projects and Solutions"
Save-TedideScreenshot -Name "03_recent_submenu" -OutDir <dir>
Stop-TedideApp
```

Function reference (all in `driver.ps1`):

| Function | Does |
|---|---|
| `Start-TedideApp -Exe <path>` | Launches `dotnet <path>` inside a **new** Windows Terminal window (`wt -w new`), waits for it to appear. |
| `Get-TedideWindow` | Returns the `Process` for the Tedide-titled Windows Terminal window, or `$null`. |
| `Save-TedideScreenshot -Name <stem> [-OutDir <dir>]` | Captures the actual window (not a console-buffer read - see Gotchas) to `<OutDir>\<stem>.png`. |
| `Send-TedideKeys -Keys <SendKeys string>` | Focuses the window, sends keys via `System.Windows.Forms.SendKeys`, waits ~400ms to settle. |
| `Stop-TedideApp` | Kills the Windows Terminal window and any orphaned `dotnet ... Tedide.App.dll` process. |

**Side effect to know about**: Tedide persists real per-user state to
`%LocalAppData%\Tedide\` (`recent.json` for the Recent Projects list,
`theme.json`). Driving the Recent Projects menu, opening projects, etc. will
touch that file for real. If you're about to exercise anything that reads
or writes it, back it up first and restore it after:

```powershell
Copy-Item "$env:LocalAppData\Tedide\recent.json" "$env:LocalAppData\Tedide\recent.json.bak" -ErrorAction SilentlyContinue
# ... run the driver ...
Copy-Item "$env:LocalAppData\Tedide\recent.json.bak" "$env:LocalAppData\Tedide\recent.json" -ErrorAction SilentlyContinue -Force
Remove-Item "$env:LocalAppData\Tedide\recent.json.bak" -ErrorAction SilentlyContinue
```

## Run (human path)

Double-click, or `dotnet run --project src/Tedide.App`, or the repo's
existing VS Code task - opens a normal interactive window. Useless for an
agent (nothing to screenshot or send keys to without the mechanics above).

## Gotchas

- **A bare console never renders anything - only a real terminal emulator
  does.** This was the expensive lesson: launching `dotnet Tedide.App.dll`
  via `ProcessStartInfo` (`UseShellExecute=true`, hidden or visible window,
  focused or not, with `DisableRealDriverIO=1` and/or `TEDIDE_DRIVER=ansi`
  set) reliably **hangs forever inside `Application.Run()`** and never
  writes a single byte of output, confirmed against both the ANSI driver
  over redirected pipes and the native "windows" driver over a classic
  `AllocConsole`-created console (hidden or visible, with or without a
  synthetic `WINDOW_BUFFER_SIZE_EVENT`/key event injected via
  `WriteConsoleInput`). This reproduced identically on the last-committed,
  human-verified-working version of the app, ruling out an app bug - it's
  specific to the launch mechanism. Terminal.Gui's startup evidently depends
  on being hosted by something that behaves like a real terminal (answering
  ANSI capability queries, etc.) - Windows Terminal (`wt.exe`) does this
  because it's a genuine terminal emulator; a process you `AllocConsole`
  yourself is not. **Always launch via `wt.exe`.**
- **`ReadConsoleOutputCharacter`/`AttachConsole` cannot read a `wt.exe`-hosted
  app's content**, even though the app is running and rendering correctly.
  Windows Terminal hosts console apps over ConPTY, which does not maintain
  the classic Win32 console screen buffer that API reads from - Windows
  Terminal renders directly from the raw ANSI/VT byte stream instead. Use a
  **real window screenshot** (`GetWindowRect` + `Graphics.CopyFromScreen`,
  as `Save-TedideScreenshot` does) instead of any console-buffer API.
- **`wt -w 0` accumulates tabs** into whatever Windows Terminal window last
  had focus across separate invocations - and could be a window the user
  actually has open. Use `-w new` (as `Start-TedideApp` does) for an
  isolated window every time.
- **CreateProcess via raw ConPTY P/Invoke is a real path but a fiddly one**:
  a hand-rolled `CreatePseudoConsole`/`STARTUPINFOEX` P/Invoke attempt hit
  `ERROR_INVALID_PARAMETER` from `UpdateProcThreadAttribute` because the
  `lpValue` parameter must be a pointer *to* the `HPCON` handle
  (`Marshal.AllocHGlobal` + `WriteIntPtr`), not the handle value itself -
  and `CreateProcess` must be pinned to the `CreateProcessW` entry point
  (`EntryPoint="CreateProcessW", CharSet=CharSet.Unicode`) or it silently
  mis-marshals the command line. Not pursued further once `wt.exe` itself
  proved simpler and already worked - noted here only so a future attempt
  at a lower-level driver doesn't have to rediscover both gotchas.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `MSB3027`/`MSB3021` "being used by another process" on build | A Tedide instance (yours or a leftover test one) has the default `bin/` locked. Build to a scratch `-o` dir instead (see Build), or close the running instance. |
| `Get-TedideWindow` returns `$null` right after `Start-TedideApp` | The app takes a moment past the window merely existing before its first draw - `Start-TedideApp` already waits up to 5s and settles 500ms further; if it's still not ready, add a longer `Start-Sleep` before your first `Save-TedideScreenshot`/`Send-TedideKeys`. |
| Screenshot shows a blank/all-white window | Almost always means you launched it the wrong way (not via `wt.exe`) - see the first Gotcha. |
| Two (or more) "Tedide - CC65 IDE" tabs in one screenshot | Leftover tab from a previous run that didn't get cleaned up - always pair `Start-TedideApp` with a `finally { Stop-TedideApp }`, and `Get-Process WindowsTerminal \| Stop-Process -Force` to hard-reset between unrelated test sessions (note: this closes **all** Windows Terminal windows, including any the user has open - only do this when you're sure that's acceptable). |
| `Save-TedideScreenshot` throws "A generic error occurred in GDI+." | `-OutDir` doesn't exist yet - `Bitmap.Save()` throws this opaque error instead of a clear "directory not found" one. `Save-TedideScreenshot` now creates `-OutDir` itself, but a from-scratch `-OutDir` you pass elsewhere still needs to exist first. |
