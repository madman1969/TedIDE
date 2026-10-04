;
; farhw.s - the hardware end of farmem: REU DMA (C64, C128), SuperCPU block moves (C64) and
; 6509 indirect-bank copies (CBM 510, CBM 610). farmem.c fills in the variables below, then calls
; one of these routines; each moves at most one 64K bank's worth in one go.
;

        .export         _far_hw_near, _far_hw_src, _far_hw_dst, _far_hw_len, _far_hw_ctrl
        .importzp       ptr1

        .bss

_far_hw_near:   .res    2               ; address in ordinary memory
_far_hw_src:    .res    4               ; far side (REU address / 24-bit SuperCPU address / CBM bank+address)
_far_hw_dst:    .res    4               ; SuperCPU destination
_far_hw_len:    .res    2               ; bytes to move, 1-65535
_far_hw_ctrl:   .res    1               ; REU address control ($80: hold the C64 address, for fills)

; ------------------------------------------------------------------------
; REU: void __fastcall__ reu_go (unsigned char command)
; command: $80 stash (to the REU), $81 fetch (from the REU). The transfer starts on the write to
; $FF00 after the ROMs and I/O are switched out, as cc65's own REU drivers do it.

.if .defined(__C64__) .or .defined(__C128__)

        .export         _reu_go

REU_COMMAND     = $DF01
REU_C64ADDR     = $DF02
REU_REUADDR     = $DF04
REU_COUNT       = $DF07
REU_CONTROL     = $DF0A
REU_TRIGGER     = $FF00

        .code

_reu_go:
        tay                             ; command
        ; Interrupts off before the REU is armed, not just around the trigger: the C128's IRQ
        ; handler writes $FF00 (its MMU configuration) on entry, and that write would start an
        ; armed transfer early, with the ROMs and I/O still in.
        php
        sei
        lda     _far_hw_near
        sta     REU_C64ADDR
        lda     _far_hw_near+1
        sta     REU_C64ADDR+1
        lda     _far_hw_src
        sta     REU_REUADDR
        lda     _far_hw_src+1
        sta     REU_REUADDR+1
        lda     _far_hw_src+2
        sta     REU_REUADDR+2
        lda     _far_hw_len
        sta     REU_COUNT
        lda     _far_hw_len+1
        sta     REU_COUNT+1
        lda     _far_hw_ctrl
        sta     REU_CONTROL
        sty     REU_COMMAND             ; armed: waits for the $FF00 write

.ifdef __C128__
        ldy     $FF00                   ; MMU configuration
        lda     #$3F                    ; all RAM, bank 0 - this write starts the transfer
        sta     $FF00
        sty     $FF00
.else
        ldy     $01
        tya
        and     #$F8                    ; all RAM, no I/O
        sta     $01
        lda     REU_TRIGGER             ; RAM under the KERNAL: write back what's there
        sta     REU_TRIGGER
        sty     $01
.endif
        lda     #$00
        sta     REU_CONTROL
        plp
        rts

.endif

; ------------------------------------------------------------------------
; SuperCPU: void __fastcall__ scpu_move (unsigned char backward)
; Moves _far_hw_len bytes from _far_hw_src to _far_hw_dst (both 24-bit, neither crossing a bank
; boundary). Forward (MVN) from the first byte, or backward (MVP) from the last, when the
; addresses passed are each range's last byte. Interrupts are off while the CPU is in native mode.

.ifdef __C64__

        .export         _scpu_move

        .code

_scpu_move:
        tax                             ; direction
        lda     _far_hw_dst+2           ; MVN/MVP operands: destination bank, then source bank
        sta     mvn_op+1
        sta     mvp_op+1
        lda     _far_hw_src+2
        sta     mvn_op+2
        sta     mvp_op+2
        php
        sei
        clc
.p816
        xce                             ; native mode (A and index registers still 8-bit)
        phb
        cpx     #0
        bne     backward

        rep     #$30
.a16
.i16
        lda     _far_hw_len
        dec     a
        ldx     _far_hw_src
        ldy     _far_hw_dst
mvn_op: .byte   $54, $00, $00           ; MVN
        bra     done

backward:
        rep     #$30
        lda     _far_hw_len
        dec     a
        ldx     _far_hw_src
        ldy     _far_hw_dst
mvp_op: .byte   $44, $00, $00           ; MVP

done:
        sep     #$30
.a8
.i8
        plb                             ; MVN/MVP left the data bank on the destination
        sec
        xce                             ; back to emulation mode
.p02
        plp
        rts

.endif

; ------------------------------------------------------------------------
; CBM-II: void cbm_fetch (void), void cbm_stash (void)
; Copy _far_hw_len bytes between ordinary memory (_far_hw_near, in the program's bank) and the
; bank in _far_hw_src+2 at the address in _far_hw_src. Only LDA/STA (zp),Y reach another bank -
; through the 6509's indirect bank register at $0001 - so the ordinary-memory side uses absolute
; indexed addressing, patched in place.

.if .defined(__CBM510__) .or .defined(__CBM610__)

        .export         _cbm_fetch, _cbm_stash

IndReg  = $01

        .code

_cbm_fetch:
        jsr     setup
        lda     _far_hw_near
        sta     fst1+1
        sta     fst2+1
        lda     _far_hw_near+1
        sta     fst1+2
        sta     fst2+2
        ldy     #0
        ldx     _far_hw_len+1           ; whole pages
        beq     ftail
fpage:  lda     (ptr1),y
fst1:   sta     $FFFF,y
        iny
        bne     fpage
        inc     ptr1+1
        inc     fst1+2
        inc     fst2+2
        dex
        bne     fpage
ftail:  ldx     _far_hw_len             ; then the rest
        beq     finish
fbyte:  lda     (ptr1),y
fst2:   sta     $FFFF,y
        iny
        dex
        bne     fbyte
        beq     finish

_cbm_stash:
        jsr     setup
        lda     _far_hw_near
        sta     sld1+1
        sta     sld2+1
        lda     _far_hw_near+1
        sta     sld1+2
        sta     sld2+2
        ldy     #0
        ldx     _far_hw_len+1
        beq     stail
spage:
sld1:   lda     $FFFF,y
        sta     (ptr1),y
        iny
        bne     spage
        inc     ptr1+1
        inc     sld1+2
        inc     sld2+2
        dex
        bne     spage
stail:  ldx     _far_hw_len
        beq     finish
sbyte:
sld2:   lda     $FFFF,y
        sta     (ptr1),y
        iny
        dex
        bne     sbyte

finish: pla                             ; the caller's indirect bank
        sta     IndReg
        plp
        rts

; Points ptr1 at the far address and switches the indirect bank, leaving the old bank and the
; flags on the stack under this routine's return address for finish to restore.
setup:  pla
        sta     ret
        pla
        sta     ret+1
        php
        sei
        lda     IndReg
        pha
        lda     _far_hw_src
        sta     ptr1
        lda     _far_hw_src+1
        sta     ptr1+1
        lda     _far_hw_src+2
        sta     IndReg
        lda     ret+1
        pha
        lda     ret
        pha
        rts

        .bss
ret:    .res    2

.endif
