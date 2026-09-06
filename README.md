# Tedide

A terminal (TUI) IDE for [cc65](https://cc65.github.io/) development, modeled loosely
on Visual Studio: a solution explorer, a single-file source editor, a build output pane
and a menu/status bar, all driven by `cl65`.

## Prerequisites

- .NET SDK 10.0+
- The [cc65](https://cc65.github.io/) toolchain, with `cl65` on your `PATH`

## Solution layout

```text
Tedide.sln
src/
  Tedide.Core/     Project & solution file model (.tproj / .tsln), cc65 target metadata
  Tedide.Build/     Drives cl65 as an external process, parses its output into diagnostics
  Tedide.App/       The Terminal.Gui TUI shell (menu bar, solution explorer, editor, output pane)
tests/
  Tedide.Core.Tests/
  Tedide.Build.Tests/
samples/
  HelloC64.tsln      A small multi-file sample solution/project targeting the C64
  HelloC64/
    HelloC64.tproj     src/*.c, plus -I include for the headers (see "Project files" below)
    src/               main.c, screen.c, animation.c, input.c, delay.c
    include/           screen.h, animation.h, input.h, delay.h
    bin/               Build output (HelloC64.prg) - gitignored, created on first build
```

## Running

```bash
dotnet run --project src/Tedide.App
```

From the **File** menu:

- **Open Project...** and pick `samples/HelloC64.tsln` (or `samples/HelloC64/HelloC64.tproj`) for a
  working example - it's deliberately split across several `.c`/`.h` files (see layout above) to
  show off the Solution Explorer's folder tree even though only one of them can be open for editing
  at a time (see "Editing" below).
- **New Project...** to scaffold a fresh cc65 project (name, target platform, destination folder) with a starter `main.c`.

The Solution Explorer recurses through a project's directory tree, showing every `.c`/`.h`/`.s`/
`.asm`/`.inc` file (not just the ones listed in `SourceFiles`, which is what's actually passed to
`cl65`) organized into the same folders they live in on disk - so `src/`, `include/`, etc. show up
as proper subfolders, and headers show up for browsing/editing even though they're never compiled
directly. Build-output folders (`bin/`, `obj/`, `.git/`, `.vs/`) are hidden from the tree.

Press **F5** (or use **Build > Build Project**) to invoke `cl65`; output streams live into the
**Output** pane, and the build's success/failure is reported when it finishes.

Press **Ctrl+W** (or **File > Close File**) to close the open file. If it has unsaved changes
you're prompted to save, discard, or cancel first - the same prompt appears if you select a
*different* file in the Solution Explorer while the current one is modified, since opening a new
file replaces whatever's currently open (see "Editing" below).

## Publishing a standalone executable

The VS Code task **publish Tedide.App (standalone exe)** builds Tedide itself (not a cc65 project -
see above for that) into a single, self-contained `Tedide.App.exe` under `publish/` that runs on a
Windows machine with no .NET runtime installed. Equivalent from the command line:

```bash
dotnet publish src/Tedide.App/Tedide.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

The result is one ~80MB `.exe` (the .NET runtime bundled in accounts for most of that) - drop it
anywhere and run it directly. `win-x64` is the only target tested; swap `-r` for another
[RID](https://learn.microsoft.com/dotnet/core/rid-catalog) to publish for a different platform.

## Project files

A `.tproj` file is a small JSON document describing what `cl65` needs to build a project. Here's
the sample's, which separates sources from headers the way GitHub's most common C project layout
does (`src/`, `include/`, `bin/`):

```json
{
  "Name": "HelloC64",
  "Target": "C64",
  "SourceFiles": ["src/main.c", "src/screen.c"],
  "OutputFile": "bin/HelloC64.prg",
  "ExtraArguments": ["-I", "include"]
}
```

`Target` is any of the platforms `cl65 -t` supports (`C64`, `Apple2`, `Nes`, `Atari`, ...; see
`Cc65Target` in `Tedide.Core`). `OutputFile` defaults to `<Name><platform-default-extension>`
(e.g. `.prg` for C64, `.nes` for NES) in the project's own directory if not set; Tedide creates
the output directory automatically if it doesn't exist yet (`ld65` itself won't).

`ExtraArguments` are passed to `cl65` *before* `SourceFiles` - `-I <dir>` (used above to find
`include/`) and similar flags only affect source files listed after them on the command line.

A `.tsln` file just lists the `.tproj` files that make up a solution.

## Editing

Source files open in a [`Terminal.Gui.Editor`](https://github.com/tui-cs/Editor) `Editor` view
(line numbers, folding support, undo/redo), not a plain text box. Syntax highlighting is applied
by file extension via `HighlightingManager.GetDefinitionByExtension` - `.c`/`.h` currently map to
the bundled C++ definition (close enough for C keywords), which is the closest built-in match;
cc65's `.s`/`.asm` assembly sources have no bundled definition yet, so they open unhighlighted.

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
menu is replaced with our own project-aware one (New/Open **Project** rather than a single loose
file). `EditorStatusBar`'s `ThemeDropDown` is hidden, since it drives Terminal.Gui's own
`ConfigurationManager`-based `ThemeManager` - a separate system from our own `SchemeManager`-based
theme switcher below, and leaving both active would let it silently overwrite our custom themes.

## Themes

The **Theme** menu switches between three color themes at runtime, with no restart needed:

- **VS2026 Dark** / **VS2026 Light** - modern true-color palettes similar to current Visual Studio/VS Code themes.
- **Borland Turbo C** - the classic navy-blue-background DOS IDE look, built from the 16-color ANSI palette for authenticity.

Themes are implemented in `Tedide.App/Theming` (`ThemeSwitcher`) by registering five named
`Scheme`s ("Base", "Menu", "Dialog", "Accent", "Error") with Terminal.Gui's `SchemeManager`;
views resolve their scheme by name at draw time, so switching themes recolors every open view
immediately.

Two things worth knowing:

- **Terminal color depth matters.** A plain `cmd.exe` console window reports no ANSI color
  capability and forces 16-color rendering, which flattens the Dark/Light themes' subtle grays
  down to near-identical blacks/whites. Run the app in a terminal that advertises true color
  (Windows Terminal, or the VS Code integrated terminal) to see them as designed. Borland Turbo C
  looks correct everywhere, since it's built from the 16-color palette to begin with.
- **Syntax-token colors don't change with theme.** Terminal.Gui.Editor's bundled C/C++ highlighting
  definition (see "Editing" above) hardcodes literal colors for keywords/strings/comments rather
  than resolving them through the Scheme system, so those specific hues stay fixed across themes -
  only the editor's background/plain-text colors (and all surrounding chrome) follow the theme.

## Status

This is an early skeleton: project/solution model, cc65 build integration with diagnostic
parsing, the core IDE layout (explorer / editor / output), theming, and a standalone-executable
publish task are in place and tested. Not yet implemented: an error-list pane with jump-to-line,
multi-project builds, and a custom 6502 assembly syntax definition.

Note: this app is built against **prerelease** builds of
[Terminal.Gui v2](https://github.com/gui-cs/Terminal.Gui) (`2.4.17`) and
[Terminal.Gui.Editor](https://github.com/tui-cs/Editor) (`2.5.7`, pinned to that same
Terminal.Gui version), since that's the current state of the only mature C#/.NET TUI framework
with the widget set (docking panes, tree views, tabs, menus, a real code editor) this kind of
IDE needs. Expect some API churn if you bump either package version.
