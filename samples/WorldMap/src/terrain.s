;
; terrain.s - the two loops world.c runs for every row of the map (see terrain.h): building the
; row's tiles, and stepping each grid column's height down to the next row. world.c explains
; the method; this is the same arithmetic, with the heights in zero page and byte arrays.
;

        .export         _terrain_row, _terrain_next_row
        .import         _terrain_col_a_lo, _terrain_col_a_hi, _terrain_step_a_lo, _terrain_step_a_hi
        .import         _terrain_col_b_lo, _terrain_col_b_hi, _terrain_step_b_lo, _terrain_step_b_hi
        .import         _terrain_columns_a, _terrain_columns_b
        .import         _terrain_roughness, _terrain_roughness_start
        .import         _terrain_tile_for_height, _terrain_row_buf
        .importzp       ptr1, ptr2, sreg, tmp1, tmp2, tmp3

SPAN_B  = 8                             ; cells per fine span; a coarse span is 4 of them

        .bss

spans:  .res    1                       ; fine spans in the row
a_lo:   .res    1                       ; the coarse layer's height at the current cell, 8.8
a_hi:   .res    1
sa_lo:  .res    1                       ; ...and its change per cell
sa_hi:  .res    1

        .code

; ------------------------------------------------------------------------
; void terrain_row (void)
;
; Zero page while it runs: ptr1 = where the next cell goes, sreg = the cell's total height
; (8.8), ptr2 = its change per cell across this fine span, tmp1 = the fine span number,
; tmp2 = the position in the roughness table.

_terrain_row:
        ldx     _terrain_columns_b
        dex
        stx     spans
        lda     #<_terrain_row_buf
        sta     ptr1
        lda     #>_terrain_row_buf
        sta     ptr1+1
        lda     _terrain_roughness_start
        sta     tmp2
        lda     #0
        sta     tmp1

span:   lda     tmp1
        and     #3                      ; a new coarse span every 4 fine ones
        bne     fine
        lda     tmp1
        lsr     a
        lsr     a
        tax                             ; the coarse column
        lda     _terrain_col_a_lo,x
        sta     a_lo
        lda     _terrain_col_a_hi,x
        sta     a_hi
        sec                             ; sa = (next coarse column - this one) / 32
        lda     _terrain_col_a_lo+1,x
        sbc     a_lo
        sta     sa_lo
        lda     _terrain_col_a_hi+1,x
        sbc     a_hi
        ldy     #5
@sa:    cmp     #$80                    ; shift right keeping the sign: copy bit 7 into carry
        ror     a
        ror     sa_lo
        dey
        bne     @sa
        sta     sa_hi

fine:   ldx     tmp1                    ; the fine column
        clc                             ; height = coarse + fine layer
        lda     a_lo
        adc     _terrain_col_b_lo,x
        sta     sreg
        lda     a_hi
        adc     _terrain_col_b_hi,x
        sta     sreg+1
        sec                             ; step = (next fine column - this one) / 8 + sa
        lda     _terrain_col_b_lo+1,x
        sbc     _terrain_col_b_lo,x
        sta     ptr2
        lda     _terrain_col_b_hi+1,x
        sbc     _terrain_col_b_hi,x
        ldy     #3
@sb:    cmp     #$80
        ror     a
        ror     ptr2
        dey
        bne     @sb
        sta     ptr2+1
        clc
        lda     ptr2
        adc     sa_lo
        sta     ptr2
        lda     ptr2+1
        adc     sa_hi
        sta     ptr2+1

        lda     sa_hi                   ; the coarse layer moves on 8 cells: a += sa * 8
        sta     tmp3
        lda     sa_lo
        asl     a
        rol     tmp3
        asl     a
        rol     tmp3
        asl     a
        rol     tmp3
        clc
        adc     a_lo
        sta     a_lo
        lda     tmp3
        adc     a_hi
        sta     a_hi

        ldy     #0
cell:   ldx     tmp2                    ; tile = tile_for_height[height + roughness]
        inc     tmp2
        lda     sreg+1
        clc
        adc     _terrain_roughness,x
        tax
        lda     _terrain_tile_for_height,x
        sta     (ptr1),y
        clc                             ; height += step
        lda     sreg
        adc     ptr2
        sta     sreg
        lda     sreg+1
        adc     ptr2+1
        sta     sreg+1
        iny
        cpy     #SPAN_B
        bne     cell

        tya                             ; ptr1 += 8
        clc
        adc     ptr1
        sta     ptr1
        bcc     :+
        inc     ptr1+1
:       inc     tmp1
        lda     tmp1
        cmp     spans
        beq     done
        jmp     span                    ; too far back for a branch
done:   rts

; ------------------------------------------------------------------------
; void terrain_next_row (void)

_terrain_next_row:
        ldx     _terrain_columns_a
        dex
@a:     clc
        lda     _terrain_col_a_lo,x
        adc     _terrain_step_a_lo,x
        sta     _terrain_col_a_lo,x
        lda     _terrain_col_a_hi,x
        adc     _terrain_step_a_hi,x
        sta     _terrain_col_a_hi,x
        dex
        bpl     @a

        ldx     _terrain_columns_b
        dex
@b:     clc
        lda     _terrain_col_b_lo,x
        adc     _terrain_step_b_lo,x
        sta     _terrain_col_b_lo,x
        lda     _terrain_col_b_hi,x
        adc     _terrain_step_b_hi,x
        sta     _terrain_col_b_hi,x
        dex
        bpl     @b
        rts
