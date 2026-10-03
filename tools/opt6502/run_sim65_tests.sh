#!/bin/bash
# Tedide: semantic check for -speed runtime inlining (src/optimizations/inline_runtime.c).
# Builds each test for the sim6502 and sim65c02 targets, runs the unoptimized, -size and -speed
# versions in cc65's simulator (sim65), and fails unless all three print the same output and exit
# code. Also reports how many calls -speed inlined and the cycles it saved.
#   tests/sim65/*.c - C programs, compiled by cc65 at each -O level
#   tests/sim65/*.s - assembly written in cc65's output style (regs.s, from gen_regs.py), used
#                     as-is; on sim65c02 it links the 65C02 runtime library, which checks the
#                     inlined 6502 bodies leave the same state as that library's own helpers
# Needs cc65 2.19 (cl65, sim65) on PATH. OPT6502 overrides the binary, e.g. OPT6502=bin/opt6502.exe
set -u
OPT="${OPT6502:-./opt6502}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failed=0
for src in tests/sim65/*.c tests/sim65/*.s; do
    [ -f "$src" ] || continue
    case "$src" in
        *.c) levels=("" -O -Oirs) ;;
        *)   levels=(asm) ;;
    esac
    for target in sim6502:6502 sim65c02:65c02; do
        t=${target%:*}; cpu=${target#*:}
        for level in "${levels[@]}"; do
            name="$(basename "$src") $t ${level:-none}"
            if [ "$level" = asm ]; then
                cp "$src" "$work/base.s"
            else
                cl65 -t "$t" $level -S -o "$work/base.s" "$src" || { echo "✗ $name: cc65 failed"; failed=1; continue; }
            fi
            "$OPT" -quiet -size -asm ca65 -cpu "$cpu" "$work/base.s" "$work/size.s" > /dev/null || { echo "✗ $name: opt6502 -size failed"; failed=1; continue; }
            stats=$("$OPT" -quiet -speed -asm ca65 -cpu "$cpu" "$work/base.s" "$work/speed.s") || { echo "✗ $name: opt6502 -speed failed"; failed=1; continue; }
            inlined=$(echo "$stats" | grep -o 'inline=[0-9]*' | grep -o '[0-9]*')
            declare -A result=() cycles=()
            for v in base size speed; do
                cl65 -t "$t" -o "$work/$v.prg" "$work/$v.s" || { echo "✗ $name: $v failed to assemble/link"; failed=1; continue 2; }
                out=$(sim65 -c "$work/$v.prg" 2>&1); rc=$?
                cycles[$v]=$(echo "$out" | grep -o '[0-9]* cycles' | grep -o '[0-9]*')
                result[$v]="rc=$rc $(echo "$out" | grep -v cycles)"
            done
            if [ "${result[base]}" = "${result[size]}" ] && [ "${result[base]}" = "${result[speed]}" ]; then
                saved=$(( ${cycles[base]} - ${cycles[speed]} ))
                echo "✓ $name: identical results; -speed inlined $inlined calls, saved $saved of ${cycles[base]} cycles"
            else
                echo "✗ $name: results differ - base '${result[base]}', size '${result[size]}', speed '${result[speed]}'"
                failed=1
            fi
            unset result cycles
        done
    done
done
exit $failed
