; Tedide regression test: inlining a call that carries a loop's label, right after a JMP. The
; label must survive on its own line ahead of the inlined body - an earlier version left it on
; the dead JSR, where the dead-code pass couldn't see it and deleted the start of the body as
; unreachable (found by tests/sim65/helpers.c: speed builds crashed).
	.fopt		compiler,"cc65 v 2.19 - Git b75f872"
	.setcpu		"6502"
	.importzp	sp, sreg
	.macpack	longbranch
.segment	"CODE"
.proc	_f: near
L0002:	dex
	jne     L0005
	rts
L0005:
	lda sp
	sec
	sbc #5
	sta sp
	bcc @opt6502_1_1
	jmp @opt6502_1_E
@opt6502_1_1: dec sp+1
@opt6502_1_E:
	ldy     #$01
	jmp     L0002
L0003:	rts
.endproc
