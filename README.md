# Tedide

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D6)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

A terminal (TUI) IDE for [cc65](https://cc65.github.io/) development, modeled loosely
on Visual Studio: a resizable solution explorer, a single-file source editor with 6502/ca65
syntax highlighting, a build output pane (with a separate Error List and a symbol browser) and a
menu/status bar, all driven by `cl65` - plus project/optimizer/compiler/linker settings, Find in
Files, a VICE emulator launcher, source-level debugging against VICE's own binary monitor
protocol, and a Recent Projects and Solutions list. A companion app, **Tedide.DocViewer**, browses
cc65's own manuals offline with a category tree, full-text search and bookmarks - see "Running the
Doc Viewer" below.

## Contents

- [Prerequisites](#prerequisites)
- [Solution layout](#solution-layout)
- [Running](#running)
- [Debugging](#debugging)
- [Running the Doc Viewer](#running-the-doc-viewer)
- [Publishing a standalone executable](#publishing-a-standalone-executable)
- [Project files](#project-files)
- [Project Settings dialog](#project-settings-dialog)
- [Editing](#editing)
- [Themes](#themes)
- [Status](#status)

## Prerequisites

- .NET SDK 10.0+
- The [cc65](https://cc65.github.io/) toolchain, with `cl65` on your `PATH`
- (Optional) [VICE](https://vice-emu.sourceforge.io) - needed for **Build > Run Project** (launches
  a project's built output in the matching Commodore emulator) and for **Debugging** (launches it
  with its binary monitor enabled instead - see "Debugging" below). Tedide looks for its
  executables at `C:\GTK3VICE-3.9-win64\bin` by default (see `ViceEmulator` in `Tedide.Build`) -
  configurable via **Project > Settings > VICE**, alongside `CC65_HOME` under its own **CC65** tab
  (both are per-machine toolchain settings, not project state, so they're saved once and apply to
  every project). Everything else, including Build Project and Clean Project, works fine without it.

## Solution layout

```text
Tedide.slnx
src/
  Tedide.Core/         Project & solution file model (.tproj / .tsln), cc65 target/optimization metadata,
                       plus parsers for cc65's generated output: LinkerMapFile (lnk.map), LabelsFile (.lbl)
                       and Debugging/DbgFile (.dbg - source-line <-> address resolution for the debugger)
  Tedide.Build/        Drives cl65 as an external process, parses its output into diagnostics, launches VICE
                       (optionally with its binary monitor enabled - see Tedide.Debug)
  Tedide.Debug/        A client for VICE's binary monitor protocol (breakpoints, registers, memory, stepping)
                       - what Tedide.App's Debug menu talks to once VICE is launched with -binarymonitor
  Tedide.Theming/      The nine color themes/scheme switching shared by Tedide.App and Tedide.DocViewer
  Tedide.App/          The Terminal.Gui TUI shell (menu bar, solution explorer, editor, output/error
                       list/symbols/debug panes, dialogs)
  Tedide.DocViewer/    A standalone cc65 manual browser - category tree, full-text search, bookmarks
tests/
  Tedide.Core.Tests/   Includes Fixtures/ - real lnk.map/.lbl/.dbg captured from a build, used by
                       LinkerMapFile/LabelsFile/DbgFile's own tests rather than hand-written fixtures
  Tedide.Build.Tests/
  Tedide.Debug.Tests/  Protocol-level tests (byte-exact request/response encoding) plus a fake TCP
                       server standing in for VICE, for ViceMonitorClient's request/response correlation
tools/
  Cc65DocsDbBuilder/   One-off converter: cc65's HTML manuals -> Docs.db (Markdown + FTS5 search index),
                       embedded in Tedide.DocViewer - see "Running the Doc Viewer" below
samples/
  HelloCBM/            A small multi-file sample solution/project, buildable for every Commodore cc65 target
    HelloCBM.tsln
    HelloCBM.tproj     src/*.c plus one hand-written src/border.s, -I include for the headers
    src/               main.c, screen.c, animation.c, input.c, delay.c, border.s (ca65 assembly)
                       (each gets its own .lst next to it if listing generation is on) - gitignored
    include/           screen.h, animation.h, input.h, delay.h, border.h
    lib/               Empty - drop a prebuilt .lib archive here to link against it
    bin/               Build output (HelloCBM.prg) - gitignored
  HelloPlus4/          A Plus/4-only sample touring TED chip features the C64's VIC-II/SID don't have
    HelloPlus4.tsln
    HelloPlus4.tproj   src/*.c, -I include for the headers - Target is Plus4, not cross-target
    src/               main.c, screen.c, palette.c, sound.c, speed.c, input.c, delay.c
                       (each gets its own .lst next to it if listing generation is on) - gitignored
    include/           screen.h, palette.h, sound.h, speed.h, input.h, delay.h
    lib/               Empty - drop a prebuilt .lib archive here to link against it
    bin/               Build output (HelloPlus4.prg) - gitignored
  C128_80/             A C128-only sample touring the VDC chip's 80-column text mode
    C128_80.tsln
    C128_80.tproj      src/*.c, -I include for the headers - Target is C128, not cross-target
    src/               main.c, screen.c, ruler.c, columns.c, contrast.c, input.c
                       (each gets its own .lst next to it if listing generation is on) - gitignored
    include/           screen.h, ruler.h, columns.h, contrast.h, input.h
    lib/               Empty - drop a prebuilt .lib archive here to link against it
    bin/               Build output (C128_80.prg) - gitignored
  Plus4colours/        A single-file demo of the Plus/4 TED chip's full 121-colour palette
    Plus4colours.tsln
    Plus4colours.tproj   src/main.c, -I include for the headers - Target is Plus4
    src/                 main.c
    include/             main.h
    lib/                 Empty - drop a prebuilt .lib archive here to link against it
    bin/                 Build output (Plus4colours.prg) - gitignored
  bounce/              A single-file demo bouncing characters around the screen
    bounce.tsln
    bounce.tproj         src/bounce.c, -I include for the headers - Target is C64
    src/                 bounce.c
    include/             main.h
    lib/                 Empty - drop a prebuilt .lib archive here to link against it
    bin/                 Build output (bounce.prg) - gitignored
  c16colours/          A single-file demo of the Commodore 16's full 16-colour palette
    c16colours.tsln
    c16colours.tproj     src/main.c, -I include for the headers - Target is C16
    src/                 main.c
    include/             main.h
    lib/                 Empty - drop a prebuilt .lib archive here to link against it
    bin/                 Build output (c16colours.prg) - gitignored
  inflate/             A single-file demo of a sprite that grows and shrinks in an off-screen buffer
    inflate.tsln
    inflate.tproj        src/inflate.c, -I include for the headers
    src/                 inflate.c
    include/             screen.h
    lib/                 Empty - drop a prebuilt .lib archive here to link against it
    bin/                 Build output (inflate.prg) - gitignored
  Nano128/             A nano-style full-screen text editor for the C128, in 80-column (VDC) mode
    Nano128.tsln
    Nano128.tproj        src/*.c, -I include for the headers - Target is C128, not cross-target
    src/                 main.c, screen.c, buffer.c, fileio.c, input.c, editor.c, vblank.c
                         (each gets its own .lst next to it if listing generation is on) - gitignored
    include/             screen.h, buffer.h, fileio.h, input.h, editor.h, vblank.h
    lib/                 Empty - drop a prebuilt .lib archive here to link against it
    bin/                 Build output (Nano128.prg) - gitignored
  CBMInfo/             A single-screen system/hardware info utility, cross-target like HelloCBM
    CBMInfo.tsln
    CBMInfo.tproj        src/*.c, -I include for the headers
    src/                 main.c, video.c
    include/             main.h, video.h
    lib/                 Empty - drop a prebuilt .lib archive here to link against it
    bin/                 Build output (CBMInfo.prg) - gitignored
```

## Running

```bash
dotnet run --project src/Tedide.App
```

From the **File** menu:

- **New Project...** to scaffold a fresh cc65 project (name, target platform, destination folder)
  using the same `src`/`include`/`lib`/`bin` layout as the bundled samples (see "Project files"
  below) - a starter `src/main.c` that `#include "main.h"`s a starter `include/main.h` (rather than
  `#include <conio.h>`/`<stdio.h>` directly), so the include/ folder and its `-I include` are
  exercised by a real, working include from the start, not just present but unused. `lib/` starts
  empty - drop a prebuilt cc65 `.lib` archive in it and it's linked in automatically, no `.tproj`
  change needed (every `.lib` file found there is passed to `ld65` after the compiled object
  files). `OutputFile` points at `bin/<Name><target extension>`.
- **Open Project...** and pick `samples/HelloCBM/HelloCBM.tsln` (or `samples/HelloCBM/HelloCBM.tproj`) for a
  working example - it's deliberately split across several `.c`/`.h`/`.s` files (see layout above) to
  show off the Solution Explorer's folder tree even though only one of them can be open for editing
  at a time (see "Editing" below), and its `border.s`/`animation.c` use per-target conditional
  compilation so the same sample builds correctly on every Commodore machine cc65 targets, not just
  the C64.
- **Open Project...** and pick `samples/HelloPlus4/HelloPlus4.tsln` for the opposite story - a Plus/4-only tour
  of TED-chip features the C64's VIC-II/SID can't do: `palette.c` cycles the border/background
  through TED's full 121-color palette (16 hues x 8 luminance levels, not the C64's fixed 16
  colors), `sound.c` pokes TED's sound registers directly for a voice-1 arpeggio and a burst from
  voice 2's dedicated noise generator, and `speed.c` benchmarks `fast()`/`slow()`, the C16/Plus4's
  CPU clock-doubling switch that the C64/128 doesn't have.
- **Open Project...** and pick `samples/C128_80/C128_80.tsln` for a tour of the C128's VDC-chip 80-column
  text mode, a feature none of cc65's other Commodore targets have (they're all fixed at 40 columns
  or fewer): `ruler.c` reports the real screen width via `screensize()` and draws a column-number
  ruler spanning every column to prove it, `columns.c` lays out two independent text columns 40
  characters apart that would collide in 40-column mode, and `contrast.c` switches live to
  40-column mode and back with `videomode()`, drawing the same ruler both times so the difference
  is visible side by side.
- **Open Project...** and pick `samples/Plus4colours/Plus4colours.tsln` for a single-file demo of
  the Plus/4's full TED palette - a grid of all 128 hue/luminance combinations (8 hues x 16
  luminance levels) drawn directly into screen/colour RAM, with row (luminance) and column (hue)
  labels.
- **Open Project...** and pick `samples/bounce/bounce.tsln` for a single-file demo bouncing five
  characters (`@`, `A`, `B`, `C`, `D`) diagonally around the screen, each reflecting off whichever
  edge it hits.
- **Open Project...** and pick `samples/c16colours/c16colours.tsln` for a single-file demo of the
  Commodore 16's full 16-colour palette, drawn as a labelled strip of colour blocks (0-F).
- **Open Project...** and pick `samples/inflate/inflate.tsln` for a single-file demo of a
  character-fill sprite that grows and shrinks between zero and the full screen size, redrawn each
  frame from an off-screen buffer.
- **Open Project...** and pick `samples/CBMInfo/CBMInfo.tsln` for a single-screen system/hardware
  info utility - `main.c` reports the machine model, CPU, clock speed, address/word width, RAM,
  text/graphics resolution and colour count via the same per-target `#if defined(__C64__)`-style
  conditional compilation as HelloCBM, while `video.c` detects PAL vs. NTSC *live* rather than from
  a compile-time target guess, by polling the VIC-II raster line register ($D012) for its wraparound
  point (262 lines for NTSC, 312 for PAL) - a good small example of direct hardware register access
  alongside the cc65 runtime library calls the other samples mostly stick to.
- **Open Project...** and pick `samples/Nano128/Nano128.tsln` for the largest sample - Nano128, a
  nano-style full-screen text editor for the C128, running in the VDC chip's 80-column mode from
  "C128_80" above. Commodore keyboards don't have most of the Ctrl+letter combos nano uses on a PC
  keyboard, so the eight function keys stand in for its Ctrl-shortcuts instead (F2 Save, F3/F4
  Search, F5/F6 Cut/Uncut Line, F7/F8 Page Up/Down, Home for start-of-line, Stop to open a
  different file) - except Exit, which really is Ctrl+X: Commodore's Ctrl masks a key to its low 5
  bits the same way a real terminal's does, and that happens to match nano's own binding exactly.
  Typed text is genuinely mixed-case (switching to the C128's lower/upper PETSCII character set, so
  Shift+letter capitalizes rather than producing a graphics symbol), and the title bar shows free
  heap RAM live, since `buffer.c` grows/shrinks each line's allocation as you type rather than
  reserving a worst-case block per line.
- **Recent Projects and Solutions** lists the 10 most-recently-opened `.tproj`/`.tsln` paths
  (persisted per-user, independent of any one project), numbered for Alt+1..9 accelerators like
  Visual Studio's own list. Selecting a stale entry (moved/deleted on disk) drops it from the list
  with an error instead of crashing.
- **Close Project** clears the currently loaded project(s) from the session (closing the open file
  first, prompting to save if modified) without touching anything on disk.

The Solution Explorer recurses through a project's directory tree, showing every `.c`/`.h`/`.s`/
`.asm`/`.inc`/`.cfg` file (not just the ones listed in `SourceFiles`, which is what's actually
passed to `cl65`) organized into the same folders they live in on disk - so `src/`, `include/`,
etc. show up as proper subfolders, and headers/linker config files show up for browsing/editing
even though they're never compiled directly. Build-output folders (`bin/`, `obj/`, `.git/`, `.vs/`)
are hidden from the tree, but a project that's actually been built gets its own **Generated Files**
node listing whichever of these exist and are enabled (see "Project Settings dialog" below): each
source file's own `.lst` (assembler listing), `lnk.map` (linker map), `{Name}.lbl` (VICE label
file) and `{Name}.dbg` (debug info). Right-click (or Shift+F10) a folder/file node for **New
File...**/**Rename File**/**Delete File** -
not offered on the project root itself, only inside one of its subfolders. **New File...** defaults
the new file's name to `newfile.h` in an `include` folder or `newfile.c` anywhere else (matching
whichever folder - or the folder of whichever file - was right-clicked). **Rename File** renames
in place (same folder) and keeps a project's `SourceFiles` in sync with the new path - removed if
the new name's extension isn't one cl65 compiles, added under the new path if it is, so renaming
e.g. `main.c` to `main.h` (or vice versa) is tracked correctly too, not just a same-extension
rename. The border between the Solution Explorer and the editor is a draggable splitter - drag it
to resize both panes.

Press **F5** (or **Build > Build Project**) to invoke `cl65` - once per source file (`-c`, compile
and assemble but don't link) so each gets its own assembler listing, then once more to link the
resulting object files into the output binary; output from every invocation streams live into the
**Output** tab as one build, and its success/failure (with an error count) is reported when it
finishes; a successful build additionally reports the output binary's size in bytes. Every
diagnostic `cl65`/`ca65`/`ld65` reported is also parsed into the **Error List** tab (severity/file/
line/message columns); activating a row jumps straight to that line, the file opened if it isn't
already. A source file that fails to compile doesn't stop the rest from being compiled too - only
the link step is skipped, so a single build surfaces every file's errors at once. **Build > Clean
Project** deletes the project's build artifacts (each source file's object file and assembler
listing, linker map, label file, debug info file, and the linked output binary) without
rebuilding. **F6** (or **Build > Run Project**) builds first, then launches the built output in
the VICE emulator matching the project's target, auto-starting it.

Two more tabs sit alongside Output/Error List: **Symbols** parses the project's `lnk.map`/`.lbl`
(if "Generate linker map file"/"Export labels" are on - see "Project Settings dialog" below) into a
filterable table of every module/segment/export/import/label, without disturbing the plain-text
editing of those files themselves - activating a row opens the underlying file and jumps to the
matching line. **Debug** shows the active debugging session's registers and status - see
"Debugging" below.

**Edit > Find in Files...** (Ctrl+Shift+F) searches every source/header/assembly file across the
loaded project(s) for a case-insensitive substring and lists every matching line; activating a
result opens that file and jumps the caret straight to the match. The editor's own right-click
context menu (alongside the library's default Undo/Redo/Cut/Copy/Paste/Select All) has Find,
Replace and Find in Files appended to it - Find/Replace open the same Find/Replace dialog as the
Edit menu's own (`Editor.InvokeCommand(Command.Find/Replace)`, exactly what that menu does), and
Find in Files pre-populates the search field with the current selection - just its first line, if
the selection spans more than one - and runs the search immediately, rather than opening to a
blank field.

**Edit > Go To Line...** (Ctrl+G) prompts for a line number (pre-filled with the caret's current
line, validated against the open document's actual line count) and jumps straight there.

Press **Ctrl+W** (or **File > Close File**) to close the open file. If it has unsaved changes
you're prompted to save, discard, or cancel first - the same prompt appears if you select a
*different* file in the Solution Explorer while the current one is modified, since opening a new
file replaces whatever's currently open (see "Editing" below).

## Debugging

Source-level debugging against a real running C64/128/Plus4/etc. via VICE's own **binary monitor
protocol** (`Tedide.Debug`'s `ViceMonitorClient`) - breakpoints, registers, and stepping by source
line, not just launching the emulator and watching it run.

1. Turn on **"Generate debug info"** on Project Settings' Linker tab (`-g`, plus `--dbgfile`
   forwarded through cl65 as `-Wl --dbgfile,path` since cl65 has no top-level flag for it) - without
   this there's no `.dbg` file to resolve breakpoints/addresses against source lines.
2. Set a breakpoint with **F9** (or **Debug > Toggle Breakpoint**) on the line the cursor's on. This
   is the only way to set one - Terminal.Gui.Editor's `Editor` has no clickable gutter to click a
   margin instead. Every enabled breakpoint's line is highlighted in the editor (a persistent red
   background) the moment it's set, not just while a debug session is stopped there. Breakpoints
   persist per-project in `{Name}.breakpoints.json`, next to the `.tproj` file (deliberately not
   part of the `.tproj` itself - see `BreakpointsFile` - toggling one shouldn't dirty the project's
   own build settings). **Debug > Breakpoints...** lists/toggles/deletes them all in one dialog -
   selecting a row jumps the editor straight to that breakpoint's file and line, same as the Error
   List/Symbols tabs.
3. **Debug > Start Debugging** (Shift+F5) builds, launches VICE with `-binarymonitor`, connects,
   opens (and centers the editor on) the source line containing `main()`, resolves every enabled
   breakpoint's source line to an address via the `.dbg` file, sets them, and starts running. The
   **Debug** tab is switched to automatically so its status/register panel is visible right away.
   The editor becomes read-only for the whole session - the running binary no longer matches
   whatever you'd type, and this also guarantees jumping between files while stopped never gets
   blocked by an "unsaved changes?" prompt. When a breakpoint is hit, the Debug tab shows the
   register snapshot and status (`Stopped at <file>:<line>`), and the editor jumps to and
   vertically centers the current line (highlighted via a `Terminal.Gui.Editor` line transformer,
   not the gutter - see step 2; the same centering happens for every other stop below, not just the
   first). **Continue** (Ctrl+F5) resumes; **Step** (F10) advances by *source line*, not raw 6502
   instruction - it single-steps repeatedly until the resolved location changes to a *different* C
   line (skipping over addresses that only resolve to cl65's own generated assembly, e.g. a
   function's prologue, so it doesn't stop one instruction early), so one Step press is one C
   statement, not one machine instruction - though stepping into a runtime library call (e.g.
   `printf`) does step into its implementation, same as any debugger without a "step over". **Stop
   Debugging** disconnects (leaving VICE itself running) and makes the editor editable again.

The binary monitor's own protocol details (header layout, command bytes, how a checkpoint hit is
reported, which register names VICE reports for the 6502) were confirmed against a real VICE 3.9
session while building this, not just the manual - see VICE's own manual,
[chapter 13](https://vice-emu.sourceforge.io/vice_13.html), if extending `ViceMonitorClient`
further.

Tedide logs to `%LocalAppData%\Tedide\logs\tedide-<date>.log` (Serilog, one file per day, 14 days
kept) - debug-level detail on the checkpoint/step/resume flow above, and a full stack trace for any
unhandled exception, since a crashed Terminal.Gui app otherwise just vanishes with no on-screen
trace. Worth checking first if the app ever closes unexpectedly.

## Running the Doc Viewer

```bash
dotnet run --project src/Tedide.DocViewer
```

A single window: a category/page tree of every cc65 manual on the left, and the selected page's
rendered Markdown on the right (via Terminal.Gui's own `Markdown` view). Both panes' content comes
from `Docs.db`, an embedded SQLite database built once ahead of time by `tools/Cc65DocsDbBuilder`
from cc65's own HTML manuals - regenerate it (and rebuild) if that tool's `SourceHtml/` changes.

- **Navigate** - **Back**/**Forward** (Alt+Left/Right) walk browser-style history; clicking a
  cross-page link (or pressing Enter on one) does too. A same-page `#slug` link is scrolled to
  directly by the Markdown view itself, without adding a history entry. **Search Documentation...**
  (Ctrl+F) runs a full-text search (Docs.db's FTS5 index) across every page at once.
- The content pane's right-click context menu (alongside the library's default Select All/Copy)
  has **Find...**, modeled on Tedide.App's own Editor Find - searches just the page currently on
  screen rather than every page, jumping to (scrolling toward) the next match and wrapping around
  once it reaches the end.
- **Bookmarks** - **Add/Remove Bookmark for Current Page** (Ctrl+D) toggles a bookmark for
  whatever's open (prompting for a label when adding one); **Saved Bookmarks** lists them all,
  each jumping straight back to its page.
- **Theme** - the same nine runtime-switchable color themes as Tedide.App (see "Themes" below);
  picking one here also changes what Tedide.App opens with next, and vice versa, since both share
  the same per-user `ThemeSettings`.
- **Help > About Tedide DocViewer...** - name, description, credits and a repo link (Tedide.App
  has the same **Help > About Tedide...**, its text adapted for the IDE rather than this viewer).

## Publishing a standalone executable

VS Code tasks build single, self-contained executables that run on a Windows machine with no .NET
runtime installed - **publish Tedide.App (standalone exe)** into `publish/Tedide.App.exe`, and
**publish Tedide.DocViewer (standalone exe)** into `publish-docviewer/Tedide.DocViewer.exe`
(alongside its `Docs.db`, copied there explicitly by a post-publish MSBuild target - see the
comment on `CopyDocsDbToPublishDir` in `Tedide.DocViewer.csproj` for why a plain
`CopyToPublishDirectory` isn't reliable enough for this on its own). **publish all (standalone
exes)** just runs both of those in sequence. Equivalent from the command line:

```bash
dotnet publish src/Tedide.App/Tedide.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:EnableCompressionInSingleFile=true -p:InvariantGlobalization=true -o publish

dotnet publish src/Tedide.DocViewer/Tedide.DocViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:EnableCompressionInSingleFile=true -p:InvariantGlobalization=true -o publish-docviewer
```

Each result is one ~40MB `.exe` (down from ~86MB without the last two flags: compressing the
self-contained .NET runtime bundled inside a single-file exe, and dropping ICU globalization data
neither app needs since nothing in either does culture-sensitive comparison/formatting) - drop it
anywhere and run it directly. `win-x64` is the only target tested; swap `-r` for another
[RID](https://learn.microsoft.com/dotnet/core/rid-catalog) to publish for a different platform.
Trimming (`-p:PublishTrimmed=true`) would shrink `Tedide.App.exe` further to ~14MB but isn't
enabled - it flags every JSON persistence class (project/solution loading, themes, layout, recent
projects, toolchain settings) as trim-unsafe, since none of them use a source-generated
`JsonSerializerContext`.

## Project files

A `.tproj` file is a small JSON document describing what `cl65` needs to build a project. Here's
an example, laid out the way GitHub's most common C project layout does (`src/`, `include/`,
`bin/`):

```json
{
  "Name": "HelloCBM",
  "Target": "C64",
  "OptimizationLevel": "Standard",
  "GenerateAssemblyListing": true,
  "AddSourceAsComment": true,
  "GenerateLinkerMap": false,
  "GenerateDebugInfo": false,
  "ExportLabels": false,
  "SourceFiles": ["src/main.c", "src/screen.c"],
  "OutputFile": "bin/HelloCBM.prg",
  "LinkerConfigPath": null,
  "IncludePaths": ["include"],
  "PreprocessorDefines": [],
  "ExtraArguments": []
}
```

| Field | What it controls |
| --- | --- |
| `Target` | Any of the platforms `cl65 -t` supports (`C64`, `Apple2`, `Nes`, `Atari`, ...; see `Cc65Target` in `Tedide.Core`) - the Project Settings dialog's dropdown restricts this to the nine Commodore 8-bit machines cc65 targets (`Cc65TargetExtensions.CommodoreTargets`), since that's Tedide's focus, but any value `cl65` accepts works if set by hand. |
| `OptimizationLevel` | One of cc65's optimizer presets (`None`, `Standard` = `-O`, `Inline` = `-Oi`, `Register` = `-Or`, `InlineKnownFunctions` = `-Os`, `Extended` = `-Ox`, or `Maximum` = `-Oirs`, combining the last three) - see `Cc65OptimizationLevel`. |
| `GenerateAssemblyListing` / `AddSourceAsComment` | Control the assembler listing cl65 can emit for each source file (`-l` / `-T`), alongside that file (e.g. `src/main.c` -> `src/main.lst`) - see "Project Settings dialog" below. Both default to `true`. |
| `GenerateLinkerMap` | Whether `ld65` emits a linker map (`-m`) to `lnk.map`, next to the project file - see "Symbols" under "Running" above. Defaults to `false`. |
| `ExportLabels` | Whether `ld65` emits a VICE-format label file (`-Ln`) to `{Name}.lbl`, next to the project file. Defaults to `false`. |
| `GenerateDebugInfo` | Whether cc65/ca65 embed debug info (`-g`) and `ld65` consolidates it into `{Name}.dbg` (`--dbgfile`, forwarded through cl65 as `-Wl --dbgfile,path` - it has no top-level flag for this). Required for the debugger - see "Debugging" above. Defaults to `false`. |
| `OutputFile` | Defaults to `<Name><platform-default-extension>` (e.g. `.prg` for C64, `.nes` for NES) in the project's own directory if not set; Tedide creates the output directory automatically if it doesn't exist yet (`ld65` itself won't). |
| `LinkerConfigPath` | A custom `ld65` linker config file (`-C`), relative to the project directory. `null`/blank uses cl65's built-in per-target default - a custom config typically *replaces* that default rather than layering on top of it. |
| `IncludePaths` | Directories passed to `cl65` as `-I <dir>` (compile-time only), each relative to the project directory - the first-class alternative to putting `-I` in `ExtraArguments`. |
| `PreprocessorDefines` | Preprocessor defines passed as `-D <value>` (compile-time only), each either `"NAME"` or `"NAME=VALUE"`. |
| `ExtraArguments` | Appended verbatim to the `cl65` command line, after the fields above - positional flags like a hand-written `-I`/`-D` here only affect source files listed after them on the command line, same as `IncludePaths`/`PreprocessorDefines`. |
| `lib/` folder | Not a `.tproj` field at all - just a folder Tedide scans on every build (`TedideProject.ResolvedLibFiles`). Every `.lib` file found directly inside it is passed to `ld65` at link time, after the compiled object files, so linking against a prebuilt cc65 library archive is a matter of dropping it in `lib/`, nothing more. |
| `{Name}.breakpoints.json` | Not a `.tproj` field either - a separate sidecar file for the project's debugger breakpoints (see "Debugging" above), kept out of `.tproj` since it's session/debugging state, not build configuration. |

A `.tsln` file just lists the `.tproj` files that make up a solution.

## Project Settings dialog

**Project > Settings...** opens one dialog covering everything above, as six tabs sharing a
single Save/Cancel footer:

- **Settings** - display name, target platform, output file override, extra `cl65` arguments,
  include paths, and preprocessor defines.
- **Optimizer** - the `OptimizationLevel` preset, with inline help text explaining what each of
  cc65's `-O`/`-Oi`/`-Or`/`-Os`/`-Ox`/`-Oirs` flags does.
- **Compiler** - two checkboxes: *Generate assembly listing file* (`-l`, written next to each
  source file, e.g. `src/Foo.c` -> `src/Foo.lst`, and surfaced in the Solution Explorer's
  Generated Files node once at least one exists) and *Include C source as comments in generated
  assembly* (`-T`, most useful together with the listing - it interleaves each C line as a comment
  above the 6502 instructions it compiled to). Tedide builds each source file in its own `cl65`
  invocation specifically so this is one listing per source file, not one covering the whole
  project - see "Running" above.
- **Linker** - *Generate linker map file* (`-m`), *Export labels* (`-Ln`), *Generate debug info*
  (`-g`/`--dbgfile` - needed for the debugger, see "Debugging" above), and a custom linker config
  file path (`-C`).
- **CC65** - the `CC65_HOME` environment variable (where cl65 finds target headers/libraries) -
  a per-machine toolchain setting, not project state, so it's saved once and applies to every
  project (see "Prerequisites" above).
- **VICE** - the VICE bin directory - likewise per-machine, not project state (see "Prerequisites"
  above).

Saving writes every project-bound field (all tabs except CC65/VICE, which are per-machine settings
saved separately) to the `.tproj` in one go. If the display name changed, the project's own folder
(and its `.tproj` file) is renamed to match - e.g. renaming "HelloGame" to "SuperGame" moves
`.../HelloGame/` to `.../SuperGame/` and `HelloGame.tproj` to `SuperGame.tproj` within it,
following the same "folder named after the project" convention File > New Project scaffolds.
Source files move along with the folder, so `SourceFiles` needs no changes; if the project belongs
to a solution, the solution's own reference to it is updated too. If a folder with the new name
already exists, the name change is still saved but the folder itself is left alone and an error
explains why.

## Editing

Source files open in a [`Terminal.Gui.Editor`](https://github.com/tui-cs/Editor) `Editor` view
(line numbers, folding support, undo/redo), not a plain text box. Syntax highlighting is applied
by file extension via `HighlightingManager.GetDefinitionByExtension` - `.c`/`.h` map to the
bundled C++ definition (close enough for C keywords), and `.s`/`.asm` get a hand-written 6502/ca65
definition of Tedide's own (`Tedide.App.Highlighting.Cc65AssemblyHighlighting`), covering line
comments, string/character literals, ca65 directives, labels, numeric literals (hex/binary/
decimal) and the 56 official 6502 mnemonics, since Terminal.Gui.Editor ships nothing for 6502
assembly itself. `.lst` gets a third definition built on top of that one
(`Tedide.App.Highlighting.Cc65ListingHighlighting`): each line's ca65-generated address/byte-dump
prefix (e.g. `0000A5r 1  A9 08` - see "Running" above) is its own muted color, and everything after
it - the assembled source line, including interleaved C source turned into ordinary `;`-comments
when `AddSourceAsComment` is on - is colored with the same rules as `.s`/`.asm`. `.cfg` (a custom
`ld65` linker config) gets its own definition too (`Cc65CfgHighlighting`) covering `MEMORY`/
`SEGMENTS` block keywords, attribute names/values, hex literals and `#`-comments. `lnk.map` and
`.lbl` (see "Symbols" under "Running" above) keep their own highlighters
(`Cc65LinkerMapHighlighting`/`Cc65LabelsHighlighting`) when opened directly as plain text, even
though the Symbols tab is usually the more convenient way to browse them.

Tedide is deliberately single-document: only one file can be open at a time. Selecting a
different file in the Solution Explorer (or opening one that's already open, which is a no-op)
replaces whatever's currently shown - prompting to save first if it has unsaved changes, the same
as **Close File**. `EditorPane` implements this with one `Editor` instance, constructed once and
reused for the lifetime of the app; opening a file swaps in a fresh `TextDocument` for that file
rather than creating a new `Editor`, and closing resets it to an empty, read-only placeholder.
This mirrors the document-swapping pattern used by Terminal.Gui.Editor's own reference app
(`tui-cs/Editor`'s "ted"), including its call to `Editor.ClearSelection()` before swapping
documents - confirmed by direct comparison against ted's source, down to matching its exact
`ConfirmSaveChanges`-style Save/Discard/Cancel flow before replacing a modified document. The
editor itself is never given a custom `Cursor`/`CursorStyle` - ted doesn't either, relying
entirely on the library's own default cursor behavior.

The menu and status bars are Terminal.Gui.Editor's own `EditorMenuBar`/`EditorStatusBar` - the
same components ted uses - rather than hand-rolled equivalents. Their auto-generated Edit/View
menus (Find/Replace/Undo/Redo/Cut/Copy/Paste/Select All; Line Numbers/Fold Indicators/Word Wrap/
Show Tabs/Scrollbars) and the status bar's row/column indicator (`Ln X, Col Y`, plus insert-mode
and language shortcuts) come wired to the editor already; only `EditorMenuBar`'s default File
menu is replaced with our own project-aware one (New/Open/Recent **Project**s and **Close
Solution**, rather than a single loose file - see "Running" above), a **Find in Files...** item is
appended to its own Edit menu, and Build/Project/Theme menus are added alongside it.
`EditorStatusBar`'s `ThemeDropDown` is hidden, since it drives
Terminal.Gui's own `ConfigurationManager`-based `ThemeManager` - a separate system from our own
`SchemeManager`-based theme switcher below, and leaving both active would let it silently
overwrite our custom themes.

## Themes

The **Theme** menu switches between nine color themes at runtime, with no restart needed - a
checkmark next to the active one always reflects the current theme, in both Tedide.App and
Tedide.DocViewer:

| Theme | Look |
| --- | --- |
| VS2026 Dark / VS2026 Light | Modern true-color palettes similar to current Visual Studio/VS Code themes. |
| Borland Turbo C | The classic navy-blue-background DOS IDE look, built from the 16-color ANSI palette for authenticity. |
| Monokai / Dracula | The well-known Sublime/TextMate and Dracula Theme dark palettes. |
| Solarized Dark / Solarized Light | Ethan Schoonover's low-contrast, accessibility-minded palette, both variants. |
| Commodore 64 | The C64's own boot-screen look (Pepto palette): blue background, light-blue text. |
| Amber Phosphor | A monochrome amber-on-black CRT terminal look, evoking early 6502-era terminals. |

Themes are implemented in `Tedide.App/Theming` (`ThemeSwitcher`) by registering five named
`Scheme`s ("Base", "Menu", "Dialog", "Accent", "Error") with Terminal.Gui's `SchemeManager`;
views resolve their scheme by name at draw time, so switching themes recolors every open view
immediately. The last-selected theme persists across runs (`ThemeSettings`, under the OS's
per-user application data folder, alongside the Recent Projects and Solutions list).

Two things worth knowing:

- **Terminal color depth matters.** A plain `cmd.exe` console window reports no ANSI color
  capability and forces 16-color rendering, which flattens the true-color themes' subtle grays
  down to near-identical blacks/whites. Run the app in a terminal that advertises true color
  (Windows Terminal, or the VS Code integrated terminal) to see them as designed. Borland Turbo C,
  Commodore 64 and Amber Phosphor look correct everywhere, since they're built from a 16-color (or
  monochrome) palette to begin with.
- **Syntax-token colors don't change with theme.** Both the bundled C/C++ highlighting definition
  and Tedide's own 6502/ca65 one (see "Editing" above) hardcode literal colors for keywords/
  strings/comments rather than resolving them through the Scheme system, so those specific hues
  stay fixed across themes - only the editor's background/plain-text colors (and all surrounding
  chrome) follow the theme.

## Status

Project/solution model, cc65 build integration with diagnostic parsing, VICE emulator launching,
the core IDE layout (resizable explorer / editor / output/error-list/symbols/debug panes), a
6502/ca65 syntax highlighter, Find in Files, Go To Line, source-level debugging against VICE's
binary monitor protocol (breakpoints with persistent in-editor highlighting, registers, source-line
stepping, a read-only editor and auto-centered current line while a session is active), a symbol
browser for linker maps/labels, a Recent Projects and Solutions list, nine runtime-switchable
themes, file-based logging for crash diagnosis, and standalone-executable publish tasks for both
Tedide.App and Tedide.DocViewer are all in place and tested. Not yet implemented: true
multi-project solution builds (a loaded solution's *first* project is always the one Build/Clean/
Run/Debug act on), and a visual editor for `.cfg` linker configs (syntax highlighting only today -
see "Editing" above).

Note: this app is built against **prerelease** builds of
[Terminal.Gui v2](https://github.com/gui-cs/Terminal.Gui) (`2.4.17`) and
[Terminal.Gui.Editor](https://github.com/tui-cs/Editor) (`2.5.7`, pinned to that same
Terminal.Gui version), since that's the current state of the only mature C#/.NET TUI framework
with the widget set (docking panes, tree views, tabs, menus, a real code editor) this kind of
IDE needs. Expect some API churn if you bump either package version.
