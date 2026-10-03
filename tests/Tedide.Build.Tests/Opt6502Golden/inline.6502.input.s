; Tedide regression test: -speed inlines cc65 runtime calls inside loops only, and turns a short
; branch that inlining pushes out of range into a longbranch macro.
	.fopt		compiler,"cc65 v 2.19 - Git b75f872"
	.setcpu		"6502"
	.importzp	sp, sreg
	.macpack	longbranch
.segment	"CODE"
.proc	_f: near
	jsr     pushax		; not in a loop - stays a call
L0002:	jsr     pusha
	jsr     _g		; not a runtime helper - stays a call
	jsr     pushax
	jsr     pushax
	jsr     pushax
	jsr     pushax
	jsr     pushax
	jsr     pushax
	jsr     pushax
	jsr     pushax
	jsr     pushax
	dex
	bne     L0002		; 9 inlined pushax = 144+ bytes back: must become jne
L0003:	jsr     incsp2
	dey
	bne     L0003		; still close after inlining - stays bne
	rts
.endproc
