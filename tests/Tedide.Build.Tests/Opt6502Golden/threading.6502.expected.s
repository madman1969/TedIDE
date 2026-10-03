; Tedide regression test: jump threading. Labels are reused per .proc, exactly as cc65 does.
	.setcpu		"6502"
	.importzp	sp
.segment	"CODE"

.proc	_a: near
L0001:	jmp     incsp2
L0002:
L0003:	jmp     incsp2		; imported helper: L0001's chain ends here
L0004:	rts	; lands on RTS -> becomes RTS
L0005:
	.dbg	line
	rts
L0006:	jmp     L0007		; L0007/L0008 jump to each other - an intentional loop, left alone
L0007:	jmp     L0008
L0008:	jmp     L0007
L0009:	jmp     L0009		; spins on itself - left alone
L000B:	beq     L0001		; a branch - never retargeted
	jmp     L000A		; lands in a #NOOPT region - left alone
L000C:	lda     #$04
;#NOOPT
L000A:	jmp     L0002
;#OPT
.endproc

.proc	_b: near
L0001:	rts
.endproc
