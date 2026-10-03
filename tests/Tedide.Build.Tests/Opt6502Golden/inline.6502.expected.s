; Tedide regression test: -speed inlines cc65 runtime calls inside loops only, and turns a short
; branch that inlining pushes out of range into a longbranch macro.
	.fopt		compiler,"cc65 v 2.19 - Git b75f872"
	.setcpu		"6502"
	.importzp	sp, sreg
	.macpack	longbranch
.segment	"CODE"
.proc	_f: near
	jsr     pushax		; not in a loop - stays a call
L0002:
	ldy sp
	beq @opt6502_1_1
	dec sp
	ldy #0
	sta (sp),y
	jmp @opt6502_1_E
@opt6502_1_1: dec sp+1
	dec sp
	sta (sp),y
@opt6502_1_E:
	jsr     _g		; not a runtime helper - stays a call
	pha
	lda sp
	sec
	sbc #2
	sta sp
	bcs @opt6502_2_1
	dec sp+1
@opt6502_2_1: ldy #1
	txa
	sta (sp),y
	pla
	dey
	sta (sp),y
	pha
	lda sp
	sec
	sbc #2
	sta sp
	bcs @opt6502_3_1
	dec sp+1
@opt6502_3_1: ldy #1
	txa
	sta (sp),y
	pla
	dey
	sta (sp),y
	pha
	lda sp
	sec
	sbc #2
	sta sp
	bcs @opt6502_4_1
	dec sp+1
@opt6502_4_1: ldy #1
	txa
	sta (sp),y
	pla
	dey
	sta (sp),y
	pha
	lda sp
	sec
	sbc #2
	sta sp
	bcs @opt6502_5_1
	dec sp+1
@opt6502_5_1: ldy #1
	txa
	sta (sp),y
	pla
	dey
	sta (sp),y
	pha
	lda sp
	sec
	sbc #2
	sta sp
	bcs @opt6502_6_1
	dec sp+1
@opt6502_6_1: ldy #1
	txa
	sta (sp),y
	pla
	dey
	sta (sp),y
	pha
	lda sp
	sec
	sbc #2
	sta sp
	bcs @opt6502_7_1
	dec sp+1
@opt6502_7_1: ldy #1
	txa
	sta (sp),y
	pla
	dey
	sta (sp),y
	pha
	lda sp
	sec
	sbc #2
	sta sp
	bcs @opt6502_8_1
	dec sp+1
@opt6502_8_1: ldy #1
	txa
	sta (sp),y
	pla
	dey
	sta (sp),y
	pha
	lda sp
	sec
	sbc #2
	sta sp
	bcs @opt6502_9_1
	dec sp+1
@opt6502_9_1: ldy #1
	txa
	sta (sp),y
	pla
	dey
	sta (sp),y
	pha
	lda sp
	sec
	sbc #2
	sta sp
	bcs @opt6502_10_1
	dec sp+1
@opt6502_10_1: ldy #1
	txa
	sta (sp),y
	pla
	dey
	sta (sp),y
	dex
	jne     L0002	; 9 inlined pushax = 144+ bytes back: must become jne
L0003:
	inc sp
	beq @opt6502_11_1
	inc sp
	beq @opt6502_11_2
	jmp @opt6502_11_E
@opt6502_11_1: inc sp
@opt6502_11_2: inc sp+1
@opt6502_11_E:
	dey
	bne     L0003		; still close after inlining - stays bne
	rts
.endproc
