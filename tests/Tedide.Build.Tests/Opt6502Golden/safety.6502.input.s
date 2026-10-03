; Tedide regression test: cc65-shaped output the upstream passes miscompiled or corrupted.
	.setcpu		"6502"
	.importzp	sp, sreg, ptr1
	.dbg		file, "f.c", 100, 0

.segment	"CODE"

.proc	_f: near

	.dbg	line, "f.c", 1
	lda     #$00
	jsr     pusha
	lda     #$00		; A may have changed inside pusha - must stay
	tax
	txa			; redundant: A == X after TAX - removed, TAX stays
	jmp     L0002		; jumps over L0001, not to the next line - must stay
L0001:	ldx     #$01
L0002:	lda     _x
	sta     _y
	lda     _x		; redundant reload - removed
	lda     $D012
	sta     _y
	lda     $D012		; literal address, may be I/O - must stay
	jmp     L0003		; jumps to the next label past a .dbg line - removed
	.dbg	line, "f.c", 2
L0003:	rts
	ldx     #$02		; unreachable after RTS - removed
	.dbg	line
.endproc

.segment	"RODATA"

S0001:
	.byte	"a;b", $00, $48,$45,$4C,$4C,$4F,$20,$57,$4F,$52,$4C,$44,$21,$21,$21,$21,$21,$21,$00
