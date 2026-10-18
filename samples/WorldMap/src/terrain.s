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

; ------------------------------------------------------------------------
; void terrain_walk (void), void terrain_mark (void)
; Rivers' and roads' cells, a step at a time, counted or listed by row - see terrain.h. world.c's
; C did this at about 1,000 cycles a cell; the longest roads have thousands, walked twice.

        .export         _terrain_walk, _terrain_mark
        .import         _terrain_walk_x, _terrain_walk_y, _terrain_walk_to_x, _terrain_walk_to_y
        .import         _terrain_list_end, _terrain_list_lo, _terrain_list_hi, _terrain_listing
        .import         _terrain_wander, _terrain_width, _terrain_height

        .bss
adx:    .res    2                       ; the steps still to take across...
ady:    .res    2                       ; ...and down
sx:     .res    1                       ; which way across: 1 or $FF (-1)...
sy:     .res    1                       ; ...and down

        .rodata
cell_bit:
        .byte   $01, $02, $04, $08, $10, $20, $40, $80   ; as world.c's bit_of

        .code

_terrain_walk:
        ; adx = |to_x - x|, sx its sign; the same down.
        sec
        lda     _terrain_walk_to_x
        sbc     _terrain_walk_x
        sta     adx
        lda     _terrain_walk_to_x+1
        sbc     _terrain_walk_x+1
        sta     adx+1
        ldx     #1                      ; the difference's sign: coordinates can be a little
        bit     adx+1                   ; below 0 (-2 to 513), so not the borrow
        bpl     @x_pos
        jsr     negate_adx
        ldx     #$FF
@x_pos: stx     sx
        sec
        lda     _terrain_walk_to_y
        sbc     _terrain_walk_y
        sta     ady
        lda     _terrain_walk_to_y+1
        sbc     _terrain_walk_y+1
        sta     ady+1
        ldx     #1
        bit     ady+1
        bpl     @y_pos
        jsr     negate_ady
        ldx     #$FF
@y_pos: stx     sy

@step:  lda     adx                     ; there yet?
        ora     adx+1
        ora     ady
        ora     ady+1
        bne     :+
        rts                             ; there
:       jsr     _terrain_mark
        lda     ady                     ; nothing left down: across
        ora     ady+1
        beq     @across
        lda     adx                     ; nothing left across: down
        ora     adx+1
        beq     @down
        ; Both: along the longer way (adx >= ady: across), but the other way when the random
        ; number's low 2 bits are 0 - (adx >= ady) ^ ((next_wander() & 3) == 0), as in C.
        jsr     next_wander
        and     #3
        beq     @flip
        lda     adx
        cmp     ady
        lda     adx+1
        sbc     ady+1
        bcs     @across
        bcc     @down
@flip:  lda     adx
        cmp     ady
        lda     adx+1
        sbc     ady+1
        bcs     @down

@across:
        lda     sx                      ; x += sx
        bmi     @x_dec
        inc     _terrain_walk_x
        bne     @adx_dec
        inc     _terrain_walk_x+1
        jmp     @adx_dec
@x_dec: lda     _terrain_walk_x
        bne     :+
        dec     _terrain_walk_x+1
:       dec     _terrain_walk_x
@adx_dec:
        lda     adx                     ; --adx
        bne     :+
        dec     adx+1
:       dec     adx
        jmp     @step

@down:  lda     sy                      ; y += sy
        bmi     @y_dec
        inc     _terrain_walk_y
        bne     @ady_dec
        inc     _terrain_walk_y+1
        jmp     @ady_dec
@y_dec: lda     _terrain_walk_y
        bne     :+
        dec     _terrain_walk_y+1
:       dec     _terrain_walk_y
@ady_dec:
        lda     ady                     ; --ady
        bne     :+
        dec     ady+1
:       dec     ady
        jmp     @step

negate_adx:
        sec
        lda     #0
        sbc     adx
        sta     adx
        lda     #0
        sbc     adx+1
        sta     adx+1
        rts

negate_ady:
        sec
        lda     #0
        sbc     ady
        sta     ady
        lda     #0
        sbc     ady+1
        sta     ady+1
        rts

; The next random number for the wandering: terrain_wander's 16-bit xorshift, as world.c's
; next_wander - s ^= s << 7; s ^= s >> 9; s ^= s << 8 - returning the low byte.
next_wander:
        lda     _terrain_wander+1       ; s << 7: high byte (s_hi bit 0, s_lo >> 1)...
        lsr     a
        lda     _terrain_wander
        ror     a
        tax
        lda     #0                      ; ...and low byte (s_lo bit 0, in bit 7)
        ror     a
        eor     _terrain_wander
        sta     _terrain_wander
        txa
        eor     _terrain_wander+1
        sta     _terrain_wander+1
        lsr     a                       ; s ^= s >> 9: the low byte ^= s_hi >> 1
        eor     _terrain_wander
        sta     _terrain_wander
        eor     _terrain_wander+1       ; s ^= s << 8: the high byte ^= the low byte
        sta     _terrain_wander+1
        lda     _terrain_wander
        rts

; Counts or lists (terrain_walk_x, terrain_walk_y), if it's on the map.
_terrain_mark:
        lda     _terrain_walk_y+1       ; y: 0 to height - 1, so its high byte is 0...
        bne     @off
        lda     _terrain_height+1       ; ...and, unless height is 256, its low byte < height
        bne     @y_ok
        lda     _terrain_walk_y
        cmp     _terrain_height
        bcs     @off
@y_ok:  lda     _terrain_walk_x+1       ; x: not negative, and below width
        bmi     @off
        lda     _terrain_walk_x
        cmp     _terrain_width
        lda     _terrain_walk_x+1
        sbc     _terrain_width+1
        bcs     @off

        lda     _terrain_walk_y         ; ptr1 = &terrain_list_end[y]
        asl     a
        sta     ptr1
        lda     #0
        rol     a
        sta     ptr1+1
        clc
        lda     ptr1
        adc     _terrain_list_end
        sta     ptr1
        lda     ptr1+1
        adc     _terrain_list_end+1
        sta     ptr1+1
        ldy     #0
        lda     _terrain_listing
        bne     @list

        lda     (ptr1),y                ; counting: ++terrain_list_end[y]
        clc
        adc     #1
        sta     (ptr1),y
        iny
        lda     (ptr1),y
        adc     #0
        sta     (ptr1),y
@off:   rts

@list:  lda     (ptr1),y                ; listing: i = --terrain_list_end[y], in tmp2:tmp1
        sec
        sbc     #1
        sta     (ptr1),y
        sta     tmp1
        iny
        lda     (ptr1),y
        sbc     #0
        sta     (ptr1),y
        sta     tmp2
        clc                             ; terrain_list_lo[i] = x's low 8 bits
        lda     _terrain_list_lo
        adc     tmp1
        sta     ptr2
        lda     _terrain_list_lo+1
        adc     tmp2
        sta     ptr2+1
        ldy     #0
        lda     _terrain_walk_x
        sta     (ptr2),y
        lda     tmp1                    ; bit i of terrain_list_hi = x's 9th bit:
        and     #7                      ; the bit within the byte...
        tax
        lsr     tmp2                    ; ...and the byte, i >> 3
        ror     tmp1
        lsr     tmp2
        ror     tmp1
        lsr     tmp2
        ror     tmp1
        clc
        lda     _terrain_list_hi
        adc     tmp1
        sta     ptr2
        lda     _terrain_list_hi+1
        adc     tmp2
        sta     ptr2+1
        lda     _terrain_walk_x+1
        lsr     a
        bcc     @clear
        lda     (ptr2),y
        ora     cell_bit,x
        sta     (ptr2),y
        rts
@clear: lda     cell_bit,x
        eor     #$FF
        and     (ptr2),y
        sta     (ptr2),y
        rts
