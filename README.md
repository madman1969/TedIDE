# Tedide

A terminal (TUI) IDE for [cc65](https://cc65.github.io/) development, modeled loosely
on Visual Studio: a resizable solution explorer, a single-file source editor with 6502/ca65
syntax highlighting, a build output pane and a menu/status bar, all driven by `cl65` - plus
project/optimizer/compiler settings, Find in Files, a VICE emulator launcher, and a Recent
Projects and Solutions list.

## Prerequisites

- .NET SDK 10.0+
- The [cc65](https://cc65.github.io/) toolchain, with `cl65` on your `PATH`
- (Optional) [VICE](https://vice-emu.sourceforge.io) - only needed for **Build > Run Project**,
  which launches a project's built output in the matching Commodore emulator. Tedide looks for it
  at `C:\GTK3VICE-3.9-win64` by default (see `ViceEmulator` in `Tedide.Build`); everything else,
  including Build Project and Clean Project, works fine without it.

## Solution layout

```text
Tedide.slnx
src/
  Tedide.Core/       Project & solution file model (.tproj / .tsln), cc65 target/optimization metadata
  Tedide.Build/       Drives cl65 as an external process, parses its output into diagnostics, launches VICE
  Tedide.App/         The Terminal.Gui TUI shell (menu bar, solution explorer, editor, output pane, dialogs)
tests/
  Tedide.Core.Tests/
  Tedide.Build.Tests/
samples/
  HelloC64.tsln      A small multi-file sample solution/project, buildable for every Commodore cc65 target
  HelloC64/
    HelloC64.tproj     src/*.c plus one hand-written src/border.s, -I include for the headers
    src/               main.c, screen.c, animation.c, input.c, delay.c, border.s (ca65 assembly)
                       (each gets its own .lst next to it if listing generation is on) - gitignored
    include/           screen.h, animation.h, input.h, delay.h, border.h
    bin/               Build output (HelloC64.prg) - gitignored
  HelloPlus4.tsln    A Plus/4-only sample touring TED chip features the C64's VIC-II/SID don't have
  HelloPlus4/
    HelloPlus4.tproj   src/*.c, -I include for the headers - Target is Plus4, not cross-target
    src/               main.c, screen.c, palette.c, sound.c, speed.c, input.c, delay.c
                       (each gets its own .lst next to it if listing generation is on) - gitignored
    include/           screen.h, palette.h, sound.h, speed.h, input.h, delay.h
    bin/               Build output (HelloPlus4.prg) - gitignored
```

## Running

```bash
dotnet run --project src/Tedide.App
```

From the **File** menu:

- **New Project...** to scaffold a fresh cc65 project (name, target platform, destination folder) with a starter `main.c`.
- **Open Project...** and pick `samples/HelloC64.tsln` (or `samples/HelloC64/HelloC64.tproj`) for a
  working example - it's deliberately split across several `.c`/`.h`/`.s` files (see layout above) to
  show off the Solution Explorer's folder tree even though only one of them can be open for editing
  at a time (see "Editing" below), and its `border.s`/`animation.c` use per-target conditional
  compilation so the same sample builds correctly on every Commodore machine cc65 targets, not just
  the C64.
- **Open Project...** and pick `samples/HelloPlus4.tsln` for the opposite story - a Plus/4-only tour
  of TED-chip features the C64's VIC-II/SID can't do: `palette.c` cycles the border/background
  through TED's full 121-color palette (16 hues x 8 luminance levels, not the C64's fixed 16
  colors), `sound.c` pokes TED's sound registers directly for a voice-1 arpeggio and a burst from
  voice 2's dedicated noise generator, and `speed.c` benchmarks `fast()`/`slow()`, the C16/Plus4's
  CPU clock-doubling switch that the C64/128 doesn't have.
- **Recent Projects and Solutions** lists the 10 most-recently-opened `.tproj`/`.tsln` paths
  (persisted per-user, independent of any one project), numbered for Alt+1..9 accelerators like
  Visual Studio's own list. Selecting a stale entry (moved/deleted on disk) drops it from the list
  with an error instead of crashing.
- **Close Solution** clears the currently loaded project(s) from the session (closing the open file
  first, prompting to save if modified) without touching anything on disk.

The Solution Explorer recurses through a project's directory tree, showing every `.c`/`.h`/`.s`/
`.asm`/`.inc` file (not just the ones listed in `SourceFiles`, which is what's actually passed to
`cl65`) organized into the same folders they live in on disk - so `src/`, `include/`, etc. show up
as proper subfolders, and headers show up for browsing/editing even though they're never compiled
directly. Build-output folders (`bin/`, `obj/`, `.git/`, `.vs/`) are hidden from the tree, but a
project that has actually been built with an assembler listing enabled (see "Project Settings
dialog" below) gets its own **Generated Files** node listing each source file's own `.lst`.
Right-click (or Shift+F10) a project/folder/file node for **New File...**/**Delete File**. The
border between the Solution Explorer and the editor is a draggable splitter - drag it to resize
both panes.

Press **F5** (or **Build > Build Project**) to invoke `cl65` - once per source file (`-c`, compile
and assemble but don't link) so each gets its own assembler listing, then once more to link the
resulting object files into the output binary; output from every invocation streams live into the
**Output** pane as one build, and its success/failure (with an error count) is reported when it
finishes. A source file that fails to compile doesn't stop the rest from being compiled too - only
the link step is skipped, so a single build surfaces every file's errors at once. **Build > Clean
Project** deletes the project's build artifacts (each source file's object file and assembler
listing, plus the linked output binary) without rebuilding. **F6** (or **Build > Run Project**)
builds first, then launches the built output in the VICE emulator matching the project's target,
auto-starting it.

**Edit > Find in Files...** (Ctrl+Shift+F) searches every source/header/assembly file across the
loaded project(s) for a case-insensitive substring and lists every matching line; activating a
result opens that file and jumps the caret straight to the match.

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
an example, laid out the way GitHub's most common C project layout does (`src/`, `include/`,
`bin/`):

```json
{
  "Name": "HelloC64",
  "Target": "C64",
  "OptimizationLevel": "Standard",
  "GenerateAssemblyListing": true,
  "AddSourceAsComment": true,
  "SourceFiles": ["src/main.c", "src/screen.c"],
  "OutputFile": "bin/HelloC64.prg",
  "ExtraArguments": ["-I", "include"]
}
```

- `Target` is any of the platforms `cl65 -t` supports (`C64`, `Apple2`, `Nes`, `Atari`, ...; see
  `Cc65Target` in `Tedide.Core`) - the Project Settings dialog's dropdown restricts this to the
  nine Commodore 8-bit machines cc65 targets (`Cc65TargetExtensions.CommodoreTargets`), since
  that's Tedide's focus, but any value `cl65` accepts works if set by hand.
- `OptimizationLevel` is one of cc65's optimizer presets (`None`, `Standard` = `-O`, `Inline` =
  `-Oi`, `Register` = `-Or`, `InlineKnownFunctions` = `-Os`, `Extended` = `-Ox`, or `Maximum` =
  `-Oirs`, combining the last three) - see `Cc65OptimizationLevel`.
- `GenerateAssemblyListing` (`-l`) and `AddSourceAsComment` (`-T`) control the assembler listing
  cl65 can emit for each source file, alongside that file (e.g. `src/main.c` -> `src/main.lst`) -
  see "Project Settings dialog" below. Both default to `true`.
- `OutputFile` defaults to `<Name><platform-default-extension>` (e.g. `.prg` for C64, `.nes` for
  NES) in the project's own directory if not set; Tedide creates the output directory
  automatically if it doesn't exist yet (`ld65` itself won't).
- `ExtraArguments` are passed to `cl65` *before* `SourceFiles` - `-I <dir>` (used above to find
  `include/`) and similar flags only affect source files listed after them on the command line.

A `.tsln` file just lists the `.tproj` files that make up a solution.

## Project Settings dialog

**Project > Settings...** opens one dialog covering everything above, as three tabs sharing a
single Save/Cancel footer:

- **Settings** - display name, target platform, output file override, extra `cl65` arguments.
- **Optimizer** - the `OptimizationLevel` preset, with inline help text explaining what each of
  cc65's `-O`/`-Oi`/`-Or`/`-Os`/`-Ox`/`-Oirs` flags does.
- **Compiler** - two checkboxes: *Generate assembly listing file* (`-l`, written next to each
  source file, e.g. `src/Foo.c` -> `src/Foo.lst`, and surfaced in the Solution Explorer's
  Generated Files node once at least one exists) and *Include C source as comments in generated
  assembly* (`-T`, most useful together with the listing - it interleaves each C line as a comment
  above the 6502 instructions it compiled to). Tedide builds each source file in its own `cl65`
  invocation specifically so this is one listing per source file, not one covering the whole
  project - see "Running" above.

Saving writes every field from all three tabs to the `.tproj` in one go.

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
when `AddSourceAsComment` is on - is colored with the same rules as `.s`/`.asm`.

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

The **Theme** menu switches between nine color themes at runtime, with no restart needed:

- **VS2026 Dark** / **VS2026 Light** - modern true-color palettes similar to current Visual Studio/VS Code themes.
- **Borland Turbo C** - the classic navy-blue-background DOS IDE look, built from the 16-color ANSI palette for authenticity.
- **Monokai** / **Dracula** - the well-known Sublime/TextMate and Dracula Theme dark palettes.
- **Solarized Dark** / **Solarized Light** - Ethan Schoonover's low-contrast, accessibility-minded palette, both variants.
- **Commodore 64** - the C64's own boot-screen look (Pepto palette): blue background, light-blue text.
- **Amber Phosphor** - a monochrome amber-on-black CRT terminal look, evoking early 6502-era terminals.

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
the core IDE layout (resizable explorer / editor / output panes), a 6502/ca65 syntax highlighter,
Find in Files, a Recent Projects and Solutions list, nine runtime-switchable themes, and a
standalone-executable publish task are all in place and tested. Not yet implemented: a dedicated
error-list pane with jump-to-line (build diagnostics are parsed but only summarized in the Output
pane today), and true multi-project solution builds (a loaded solution's *first* project is always
the one Build/Clean/Run act on).

Note: this app is built against **prerelease** builds of
[Terminal.Gui v2](https://github.com/gui-cs/Terminal.Gui) (`2.4.17`) and
[Terminal.Gui.Editor](https://github.com/tui-cs/Editor) (`2.5.7`, pinned to that same
Terminal.Gui version), since that's the current state of the only mature C#/.NET TUI framework
with the widget set (docking panes, tree views, tabs, menus, a real code editor) this kind of
IDE needs. Expect some API churn if you bump either package version.
