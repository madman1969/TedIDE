# Tedide's assembly optimizer (opt6502)

This optimizer runs on the assembly cc65 generates for each C file, between `cl65 -S` and the
assemble step, when a project turns it on (Project Settings > opt6502 tab). `Cc65Toolchain` runs it
in-process. It only reads ca65 source.

Its rule: every change must be one the instruction rules in `Instructions.cs` prove leaves the
program's behaviour unchanged. Anything inside a `;#NOOPT` ... `;#OPT` region is left alone.

## Files

| File | What it holds |
|---|---|
| `Opt6502Optimizer.cs` | Entry point `Optimize(source, options)`; runs the passes until a round finds nothing new (at most 10) |
| `AsmLine.cs` | One source line: label, opcode, operand, comment, parsed by ca65's rules |
| `ProgramText.cs` | The line list, imported zero-page symbols, cost estimates, counters, output |
| `Instructions.cs` | What each instruction reads and writes (A, N/Z), and what ends straight-line code |
| `Passes.cs` | The size passes |
| `RuntimeInliner.cs`, `RuntimeHelpers.cs` | Speed mode's runtime-call inlining and its helper table |

## Parsing and output

- A label starts in column 0 and ends with `:`. Any other column-0 token is an ordinary statement,
  such as `.segment`, `.proc` or a `name = value` assignment. `name:=` is an assignment, and `name::`
  is a global label.
- A comment starts at the first `;` outside a quoted string.
- Every line no pass changed is written back exactly as read, so formatting, comments and `.dbg`
  lines survive. A changed line is rebuilt as label, tab, opcode, five spaces, operand, tab, comment.
- No lines are added or reordered, except by speed mode's inlining. So line numbers match cc65's
  output apart from removed lines.

## Instruction rules

These are deliberately conservative:

- An unknown mnemonic counts as reading everything and as ending straight-line code. That covers
  65816-only instructions and ca65 macros such as cc65's `jeq`.
- Only live, unlabelled, optimizable machine instructions are ever deleted. Labels and directives
  always stay.
- Blank, comment and `.dbg` lines are looked straight through.
- Label lookups stay inside the current `.proc`, because cc65 reuses `L0001` and the like in every
  function.

## Size passes (both modes)

They only ever remove an instruction, or rewrite one without growing it:

| Pass | Change | Safe because |
|---|---|---|
| Redundant reload | `LDA x / STA y / LDA x`: second load removed | STA changes neither A nor the flags. Never for an indirect operand (the STA could rewrite the pointer) or a literal address (possibly I/O) |
| Repeated constant | A second `LDA/LDX/LDY #v` with the same value removed | Only across instructions that change neither that register nor N/Z |
| Redundant transfer | `TAX / TXA` (or `TAY / TYA`): the transfer back removed | The first stays: X/Y still need it |
| Unreachable code | Unlabelled instructions after `JMP`/`RTS`/`RTI` removed | Every way into code is a label. Stops at any label or directive |
| Jump to next line | A `JMP` to the very next label removed | |
| Jump threading | `JMP L1` landing on `JMP L2`/`BRA L2` becomes `JMP L2`; a `JMP` landing on `RTS` becomes `RTS` | Cycles and `#NOOPT` landings are left alone. Only `JMP` is rewritten, so no instruction grows and no branch can go out of range |
| STZ (65C02/65816 only) | `LDA #0 / STA a ...` becomes `STZ a ...`, LDA removed; `.setcpu "6502"` raised to `"65C02"` | Only STZ-legal modes (no indirect or Y-indexed stores), and only when A and N/Z are provably overwritten before anything reads them |

## Speed mode: runtime inlining

Speed mode replaces calls to 57 of cc65's short runtime helpers with the helpers' own code, but
only for calls *inside loops*. A loop is the code from a label to the last jump or branch back to
it in the same `.proc`.

cc65 always calls these helpers to keep code small. Each call costs a 12-cycle `JSR`/`RTS` on top
of the helper's work, and inlining saves that on every pass round the loop: 15 cycles for helpers
that were `ldy #n / jmp` stubs, 9 where an `RTS` in the middle became a `JMP`.

- **The helpers:**
  - Stack: `pushax` `pusha0` `push0` `pusha` `pushwysp` `pushw0sp`, `decsp1`-`8`, `incsp1`-`8`,
    `addysp` `addysp1`.
  - Stack variables: `ldaxysp` `ldax0sp` `staxysp` `stax0sp` `addeqysp` `addeq0sp` `subeqysp`
    `subeq0sp` `leaaxsp` `leaa0sp` `staxspidx`.
  - Arithmetic: `tosicmp` `tosicmp0`, `incax2`-`8` `incaxy`, `aslax1`/`shlax1`, `aslax2`/`shlax2`,
    `mulax3`/`5`/`9`, `laddeq` `laddeqa` `laddeq1`.
  - Pointers: `ldaxi` `ldaxidx`.
- **Where the bodies come from:** they're cc65's own code from `libsrc/runtime` at git `b75f872`
  (cc65 2.19, zlib licence), changed only mechanically. Every path leaves A, X, Y, the flags and
  memory exactly as the real helper does. That holds whether the program links the 6502 or the
  65C02 build of the library.
- **Left out:** `popax`, `tosaddax` and `pusheax`, whose two library builds leave Y different;
  `incax1`, whose builds leave carry different; and multiply/divide/modulo, which run 100+ cycles,
  too long for 12 saved cycles to justify.
- **When it runs:** only on ca65 source that says it came from cc65 2.19
  (`.fopt compiler,"cc65 v 2.19`), imports `sp` (later cc65 renamed it `c_sp`), and includes
  `.macpack longbranch`. A call is only inlined where the file imports every zero-page symbol the
  body uses.
- **Labels:** a label on an inlined call, usually the loop's own, moves to a line of its own ahead
  of the body. Left on the deleted `JSR`, the other passes, which look through deleted lines, would
  remove the start of the body as unreachable after a preceding `JMP`.
- **Branch ranges:** inlining makes loops longer. Any short branch whose span now contains inlined
  code, and whose worst-case distance could exceed ±127 bytes, becomes cc65's longbranch macro
  (`bne` -> `jne`). A `bra` becomes a `jmp`.
- **Measured gain** on the stack-heavy test program (`stress.c`): 5.0% fewer cycles on top of
  `-Oirs` (5.7% with the 65C02 library), and 10% without cc65 optimization. The cost is a few
  hundred bytes. Size mode never inlines.

## Statistics

`Opt6502Result.Stats` reports counts per kind of optimization, plus estimated bytes and cycles
saved. Cc65Toolchain turns them into the Output panel's per-file and per-build lines. Bytes go
negative when inlining added code.

The estimates come from each changed instruction's addressing mode. A literal below `$100`, or an
`.importzp` name, counts as zero page. Cycles count one run of each changed line and ignore
page-crossing penalties.

## Testing

- **`tests/Tedide.Build.Tests/Opt6502OptimizerTests.cs`:** every rule above, including the cases
  that must *not* change.
- **`Opt6502GoldenTests.cs`:** before/after pairs in `Opt6502Golden/`.
- **`tools/Opt6502Cli/run_sim65_tests.sh`:** the behaviour check. Run it after any change here, and
  always after touching inlining.
  - It needs cc65 on PATH, Git Bash, and a Release build of `tools/Opt6502Cli`.
  - It builds each program in `tools/Opt6502Cli/sim65/` for cc65's simulator against both the 6502
    and 65C02 runtime libraries. It runs each unoptimized, with `-size` and with `-speed`, and fails
    unless all three give the same output and exit code.
  - `stress.c` and `helpers.c` are stack-heavy C programs, built at each cc65 `-O` level.
  - `regs.s`, generated by `gen_regs.py`, calls every inlined helper 256 times with varied A/X/Y,
    carry, `sp` and memory. It checksums the registers, flags and memory after each call: the
    direct check that inlined copies leave exactly the state the real helpers do.
  - A test's checksum must never include the address of anything static. Inlining changes code
    size, which moves the data segment.
- **Finding a bad inline site:** set `OPT6502_INLINE_LIMIT=n` for the CLI, or
  `Opt6502Options.InlineLimit`, to inline only the first n call sites, then bisect.

## Command line

`tools/Opt6502Cli` builds `opt6502.exe`, a front end with the original program's arguments:

```text
opt6502 [-size|-speed] [-asm ca65] [-cpu 6502|65c02|65816] [-quiet] input.s [output.s]
```

It prints one `opt6502-stats: ...` line (`Opt6502Stats.ToStatsLine`) and exits 1 on error. As in
the original, the default mode is `-speed`.

## History

Tedide first integrated [CTalkobt/opt6502](https://github.com/CTalkobt/opt6502), a GPL-3 C program,
as a patched fork. As released, its ca65 output didn't assemble for any cc65 file tried. Once it
did, several passes miscompiled cc65 code:

- it removed a `JMP` before any label, including a loop's back-jump;
- it removed both halves of `TAX/TXA`;
- it assumed A survived a `JSR`;
- it emitted `STZ (zp),y`, which doesn't exist;
- it matched opcodes case-sensitively, so almost nothing fired on cc65's lowercase output.

Tedide's fork rewrote the passes. Speed-mode inlining and jump threading were added there.

This C# implementation reimplements that design. Tedide is MIT-licensed, and this keeps GPL code
out of it. Before the fork was removed, both were run on every sample file (with and without
`-Oirs`, 6502 and 65816, size and speed), the golden inputs, and the simulator programs: 210
combinations, all byte-for-byte identical. The simulator suite also gave identical results and
cycle counts.
