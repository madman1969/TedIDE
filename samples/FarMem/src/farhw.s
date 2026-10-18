;
; farhw.s - the hardware end of farmem: REU DMA (C64, C128), SuperCPU block moves (C64), 6509
; indirect-bank copies (CBM 510, CBM 610) and Plus/4 RAM expansion bank switching (Hannes and
; Csory boards). farmem.c fills in the variables below, then calls one of these routines; each
; moves at most one 64K bank's worth in one go.
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

; ------------------------------------------------------------------------
; Plus/4 and C16 RAM expansions: the Hannes board (256K or 1MB, register $FD16) and the Csory
; board (256K, register $FD15). Both remap part of the CPU's own address space to a bank of
; expansion RAM, from $4000 up, so this code, its buffer and its bank table live in LOWCODE,
; which cc65 places straight after the startup code near $1000. Interrupts are off while a bank
; is switched in, since the handler and the rest of the program may be in the remapped part.
;
; farmem.c sets, before calling either routine:
;   far_hw_dst   the board's register
;   far_hw_ctrl  the register value that puts the machine's own RAM back
;   tedx_select  the register value that selects each expansion bank

.if .defined(__C16__) .or .defined(__PLUS4__)

        .export         _tedx_copy, _tedx_count, _tedx_select, _far_low_buf
        .importzp       ptr2, ptr3

TED_STATUS = $FF13                      ; bit 0 is 1 while the ROMs are mapped in
ROM_ON  = $FF3E                         ; a write maps the ROMs in...
RAM_ON  = $FF3F                         ; ...or out
PROBE   = $4000                         ; the first byte every bank switches

        .segment "LOWCODE"

_far_low_buf:   .res    256             ; the near side of every copy
_tedx_select:   .res    16
; Copies of far_hw_ctrl and far_hw_len, taken before a bank goes in: the far_hw_ variables are in
; the BSS, which may well be above $4000 and switched out with everything else.
restore:        .res    1
length:         .res    1
odd:            .res    1               ; length & 7: the bytes copied before the blocks of 8
stashing:       .res    1               ; tedx_copy's argument
rom:            .res    1
saved:          .res    1
count:          .res    1
limit:          .res    1

; Copies length bytes (0 meaning 256) from load's operand to store's, both indexed by Y, e.g.
; copy_bytes {lda (ptr2),y}, {sta _far_low_buf,y}. The length & 7 odd bytes go first, then
; blocks of 8 unrolled - about 13 cycles a byte rather than 19 for a plain loop, which matters on
; a Plus/4, where a read of a few hundred bytes takes much of a frame. It never touches a byte
; past the end: just past the end of a bank's window are the Plus/4's I/O registers.
.macro  copy_bytes load, store
        .local  odd_loop, block, done
        ldy     #0
        ldx     odd
        beq     block
odd_loop:
        load
        store
        iny
        dex
        bne     odd_loop
        cpy     length
        beq     done
block:
        .repeat 8
        load
        store
        iny
        .endrepeat
        cpy     length                  ; with 256 bytes (length 0), Y comes round to 0
        bne     block
done:
.endmacro

; void __fastcall__ tedx_copy (unsigned char stash)
; Copies far_hw_len bytes (1-256, with 0 meaning 256) between ordinary memory at far_hw_near and
; an expansion bank: to the bank if stash is non-zero, else from it. far_hw_src holds the address
; in the CPU's window in its low two bytes, and the register value that selects the bank in its
; third. The ROMs are switched out for the copy, since the Hannes window reaches past $8000.
; far_hw_near may be inside the window, so the bytes go through far_low_buf, below it - copied
; there or back with interrupts still on, so they're off only for the copy to or from the bank.

_tedx_copy:
        sta     stashing
        jsr     setreg
        lda     _far_hw_src
        sta     ptr2
        lda     _far_hw_src+1
        sta     ptr2+1
        lda     _far_hw_near
        sta     ptr3
        lda     _far_hw_near+1
        sta     ptr3+1
        lda     _far_hw_len
        sta     length
        and     #7
        sta     odd
        lda     stashing
        beq     bank_in
        copy_bytes {lda (ptr3),y}, {sta _far_low_buf,y}     ; stash: into far_low_buf first
bank_in:
        php
        sei
        lda     TED_STATUS
        sta     rom
        sta     RAM_ON
        ldy     #0
        lda     _far_hw_src+2
        sta     (ptr1),y                ; the bank in
        lda     stashing
        bne     stash
        copy_bytes {lda (ptr2),y}, {sta _far_low_buf,y}
        jmp     done
stash:  copy_bytes {lda _far_low_buf,y}, {sta (ptr2),y}
done:   ldy     #0
        lda     restore
        sta     (ptr1),y                ; the machine's own RAM back
        lda     rom
        lsr     a
        bcc     :+
        sta     ROM_ON
:       plp
        lda     stashing
        bne     :+
        copy_bytes {lda _far_low_buf,y}, {sta (ptr3),y}     ; fetch: out of far_low_buf last
:       rts

; unsigned char __fastcall__ tedx_count (unsigned char banks)
; How many of the first banks in tedx_select really exist and are separate: writes a different
; marker to PROBE in each, and checks after each one that every bank so far still holds its own
; and the machine's own RAM still holds what it did. With no board, the first marker lands in the
; machine's own RAM, which that check catches; with a smaller board than the table allows for,
; a bank past its end repeats an earlier one, which the first check catches. The machine's own
; byte at PROBE is put back either way.

_tedx_count:
        sta     limit
        jsr     setreg
        php
        sei
        lda     PROBE
        sta     saved
        lda     #0
        sta     count
        ldy     #0

next:   lda     count
        cmp     limit
        beq     counted
        tax
        lda     _tedx_select,x
        sta     (ptr1),y
        txa
        jsr     marker
        sta     PROBE

        ldx     #0                      ; every bank so far, this one included
check:  lda     _tedx_select,x
        sta     (ptr1),y
        txa
        jsr     marker
        cmp     PROBE
        bne     counted
        inx
        cpx     count
        beq     check
        bcc     check

        lda     restore                 ; and the machine's own RAM
        sta     (ptr1),y
        lda     PROBE
        cmp     saved
        bne     counted
        inc     count
        jmp     next

counted:
        lda     restore
        sta     (ptr1),y
        lda     saved
        sta     PROBE
        plp
        lda     count
        ldx     #0
        rts

; The marker for bank A: never the saved byte, and different for every bank.
marker: clc
        adc     #$5A
        eor     saved
        rts

; Points ptr1 at the board's register and takes a copy of the value that restores it, while the
; BSS they come from is still the machine's own.
setreg: lda     _far_hw_dst
        sta     ptr1
        lda     _far_hw_dst+1
        sta     ptr1+1
        lda     _far_hw_ctrl
        sta     restore
        rts

.endif
