#!/bin/bash
# Tedide: semantic check for -speed runtime inlining (src/optimizations/inline_runtime.c).
# Builds tests/sim65/*.c with cc65 for the sim6502 and sim65c02 targets at each -O level, runs the
# unoptimized, -size and -speed versions in cc65's simulator (sim65), and fails unless all three
# print the same output and exit code. Also reports the cycles -speed saved.
# Needs cc65 2.19 (cl65, sim65) on PATH. OPT6502 overrides the binary, e.g. OPT6502=bin/opt6502.exe
set -u
OPT="${OPT6502:-./opt6502}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failed=0
for src in tests/sim65/*.c; do
    for target in sim6502:6502 sim65c02:65c02; do
        t=${target%:*}; cpu=${target#*:}
        for level in "" -O -Oirs; do
            name="$(basename "$src" .c) $t ${level:-none}"
            cl65 -t "$t" $level -S -o "$work/base.s" "$src" || { echo "✗ $name: cc65 failed"; failed=1; continue; }
            "$OPT" -quiet -size -asm ca65 -cpu "$cpu" "$work/base.s" "$work/size.s" > /dev/null || { echo "✗ $name: opt6502 -size failed"; failed=1; continue; }
            "$OPT" -quiet -speed -asm ca65 -cpu "$cpu" "$work/base.s" "$work/speed.s" > /dev/null || { echo "✗ $name: opt6502 -speed failed"; failed=1; continue; }
            declare -A result=() cycles=()
            for v in base size speed; do
                cl65 -t "$t" -o "$work/$v.prg" "$work/$v.s" || { echo "✗ $name: $v failed to assemble/link"; failed=1; continue 2; }
                out=$(sim65 -c "$work/$v.prg" 2>&1); rc=$?
                cycles[$v]=$(echo "$out" | grep -o '[0-9]* cycles' | grep -o '[0-9]*')
                result[$v]="rc=$rc $(echo "$out" | grep -v cycles)"
            done
            if [ "${result[base]}" = "${result[size]}" ] && [ "${result[base]}" = "${result[speed]}" ]; then
                saved=$(( ${cycles[base]} - ${cycles[speed]} ))
                echo "✓ $name: identical results; -speed saved $saved of ${cycles[base]} cycles"
            else
                echo "✗ $name: results differ - base '${result[base]}', size '${result[size]}', speed '${result[speed]}'"
                failed=1
            fi
            unset result cycles
        done
    done
done
exit $failed
