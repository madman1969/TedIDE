# opt6502 - Tedide's patched fork

A copy of [CTalkobt/opt6502](https://github.com/CTalkobt/opt6502) at commit `aa27874`
(2026-01-08), patched so it works on cc65's own output. Tedide runs it on each C file's generated
assembly between the `cl65 -S` and `cl65 -c` steps when a project turns it on (Project Settings >
opt6502 tab).

It stays under upstream's **GPL-3.0** licence (see `LICENSE`). Tedide itself is MIT; the two
are separate programs - Tedide only starts `opt6502.exe` as a separate process - so the GPL covers
this directory and the binary built from it, not Tedide.

## Building

```
tools\opt6502\build.cmd
```

This needs Visual Studio 2022 or later with the C++ tools, and writes `tools\opt6502\bin\opt6502.exe`.
On Linux/macOS, `make` works as upstream intended. Tedide's build copies the exe next to
`Tedide.App.exe` when it exists, and Tedide looks for it there before trying PATH.

Tests: `OPT6502=bin/opt6502.exe bash run_tests.sh` (Git Bash). It runs upstream's golden files plus
`tests/cc65_6502` and `tests/cc65_65816`, which cover real cc65 output.

## What was wrong upstream, and what changed

Upstream's `-asm ca65` output failed to assemble for every cc65 file tried (35 sample files), and
once that was fixed, several passes would have miscompiled cc65 code:

| Where | Upstream behaviour | Now |
|---|---|---|
| `ast/parser.c` | Any column-0 token was a label, so `.segment "CODE"` became `.segment: "CODE"` | ca65: a column-0 token is a label only if it ends in `:` |
| `ast/parser.c`, `output/output.c` | Blank lines written as `:`; every comment-only line (including cc65 `-T` source comments) dropped | Unchanged lines are written back verbatim |
| `ast/parser.c` | Label/opcode/operand cut off at 63/15/63 chars (damaged `.dbg file` lines) | No field limits; `MAX_LINE` is 4096, and a longer line is an error rather than being split |
| `ast/parser.c` | A `;` inside a quoted string started a comment | Comment search skips quoted text |
| every pass | Opcodes matched case-sensitively against `"LDA"` etc - cc65 writes lowercase, so almost nothing fired | Matched case-insensitively |
| `optimizations/jumps.c` | Deleted a `JMP` whenever the next line was *any* label (e.g. a loop's back-jump) | Only when the next label is the JMP's own target |
| `optimizations/regusage.c` | `TAX / TXA`: deleted **both**, losing X | Deletes only the `TXA` (and `TYA` of `TAY / TYA`) |
| `optimizations/constant.c` | Kept assuming A's value across `JSR`; ignored N/Z changes in between; compared 63 chars | Only across instructions that change neither the register nor N/Z; also handles `LDX`/`LDY` |
| `optimizations/deadcode.c` | Deleted directives after `JMP`/`RTS` (`.dbg`, `.byte` data) | Deletes only instructions; stops at any label or directive |
| `optimizations/cpu65c02.c` | `STA` -> `STZ` for every mode, including `(zp),y` and `abs,y`, which STZ doesn't have; deleted `LDA #0` even when a `JSR`/`RTS`/`BEQ` came after | Only STZ-legal modes, and only when A and N/Z are provably overwritten before any read; raises `.setcpu "6502"` to `"65C02"` |
| `optimizations/loadstore.c` | Exact duplicate of the peephole pass, so every match counted twice | Now empty |
| `output/output.c` | Added a 4-line header comment | Removed, so line numbers match cc65's output except for removed lines |
| `main.c` | Always exited 0, even if the output couldn't be written; accepted any `-cpu` value | Exits 1 on either |

**Additions:**
- `analysis/nodeinfo.c`: the rules every pass shares (what's removable, and what reads or
  overwrites A and N/Z). Unknown mnemonics and ca65 macros such as `jeq` count as reading
  everything and as ending straight-line code.
- `-quiet`: prints only errors and one final line, which Tedide parses for the Output panel:

  ```
  opt6502-stats: optimizations=6 removed=6 rewritten=1 bytes=17 cycles=17 reload=0 constant=0 transfer=0 jump=5 unreachable=0 stz=1
  ```

  `bytes`/`cycles` are estimates from each changed instruction's addressing mode. Zero-page is
  taken from literals below `$100` and `.importzp` names. Cycles count one execution and ignore
  page-crossing penalties.

The 65816 and 45GS02 passes weren't reviewed. Tedide only ever passes `-cpu 6502`, or `65816` for
SuperCPU projects, and `65816` enables just the 65C02 pass above.

## Expected gains

On the Tedide samples, most wins come from cc65 output built **without** optimization (a `jmp` to
the next line, repeated `ldx #$00`). With cc65's own `-O`/`-Oirs`, cc65 has already removed
nearly all of these, and opt6502 finds little beyond the SuperCPU `STZ` rewrite.
