; Tedide regression test: 65C02 STZ on SuperCPU (65816) - only where the zero in A is provably dead.
	.setcpu		"6502"
	.importzp	sp, sreg
.segment	"CODE"
.proc	_g: near
	lda     #$00		; removed: both stores become STZ, then N/Z and A are overwritten
	sta     sreg+1
	sta     _z,x
	ldx     #$04
	lda     #$01
	sta     sreg
	lda     #$00
	sta     (sp),y		; STZ has no (zp),y mode - must stay STA
	jsr     _h		; A handed to _h
	lda     #$00
	sta     _w,y		; STZ has no abs,y mode - must stay STA
	lda     #$01
	lda     #$00
	sta     _v
	beq     L0001		; tests the Z flag the LDA set - must stay
L0001:	rts
.endproc
