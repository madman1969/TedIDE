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
        .import         _terrain_tile_for_height, _terrain_row_out
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
        lda     _terrain_row_out
        sta     ptr1
        lda     _terrain_row_out+1
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
        .import         _terrain_wide, _terrain_list_wide1, _terrain_list_wide2

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
        jmp     @wide
@clear: lda     cell_bit,x
        eor     #$FF
        and     (ptr2),y
        sta     (ptr2),y

        ; terrain_wide's bits 0 and 1 set the cell's bits in terrain_list_wide1 and _wide2 (the
        ; byte tmp2:tmp1 and bit x again); with neither, they're left alone (roads have none).
@wide:  lda     _terrain_wide
        lsr     a
        bcc     @wide2
        clc
        lda     _terrain_list_wide1
        adc     tmp1
        sta     ptr2
        lda     _terrain_list_wide1+1
        adc     tmp2
        sta     ptr2+1
        lda     (ptr2),y
        ora     cell_bit,x
        sta     (ptr2),y
@wide2: lda     _terrain_wide
        and     #2
        beq     @done
        clc
        lda     _terrain_list_wide2
        adc     tmp1
        sta     ptr2
        lda     _terrain_list_wide2+1
        adc     tmp2
        sta     ptr2+1
        lda     (ptr2),y
        ora     cell_bit,x
        sta     (ptr2),y
@done:  rts

; ------------------------------------------------------------------------
; void terrain_marsh (void)
; Turns the beach cells of the row at terrain_marsh_row with no sea in the 8 cells around them
; into marsh. Sea is a tile up to TILE_SHALLOWS, a lake tile, or a bridge: the row above has its
; roads and lakes in by then, and a bridge may stand on sea. terrain_marsh_above and _below are the rows above and below
; (0 for none, at the map's top and bottom). The cells in the first and last column stay beach.
; It looks at every cell of every row, so it's in assembly; only beaches take long.

        .export         _terrain_marsh
        .import         _terrain_marsh_row, _terrain_marsh_above, _terrain_marsh_below

TILE_SHALLOWS   = 4                     ; world.h's tiles
TILE_BEACH      = 5
TILE_BRIDGE     = 18
TILE_MARSH      = 19
TILE_LAKE       = 20                    ; ...to 24, the lake tiles, the last of all

        .bss
remain:         .res    2               ; cells of the row still to look at
limit:          .res    1               ; where this page's look stops (0: all 256)
page_x:         .res    2               ; the column of this page's first cell (a multiple of 256)
cell_y:         .res    1               ; the beach's place in the page
near_base:      .res    2               ; the row near_sea looks at

        .code

_terrain_marsh:
        lda     _terrain_marsh_row
        sta     ptr1
        lda     _terrain_marsh_row+1
        sta     ptr1+1
        lda     _terrain_width
        sta     remain
        lda     _terrain_width+1
        sta     remain+1
        lda     #0
        sta     page_x
        sta     page_x+1

@page:  lda     remain                  ; a page of up to 256 cells at a time
        ora     remain+1
        beq     @done
        lda     remain+1
        beq     @part
        lda     #0                      ; a whole page
        sta     limit
        dec     remain+1
        jmp     @scan_start
@part:  lda     remain                  ; the rest
        sta     limit
        lda     #0
        sta     remain
@scan_start:
        ldy     #0
@scan:  lda     (ptr1),y
        cmp     #TILE_BEACH
        beq     @beach
@next:  iny
        cpy     limit
        bne     @scan
        inc     ptr1+1
        inc     page_x+1
        jmp     @page
@done:  rts

@beach: sty     cell_y
        tya                             ; not in the first column...
        ora     page_x+1
        beq     @keep
        tya                             ; ...nor the last: x + 1 == width
        clc
        adc     #1
        tax
        lda     page_x+1
        adc     #0
        cmp     _terrain_width+1
        bne     @inner
        cpx     _terrain_width
        beq     @keep
@inner: lda     _terrain_marsh_above    ; the row above, if any
        ora     _terrain_marsh_above+1
        beq     @no_above
        lda     _terrain_marsh_above
        sta     near_base
        lda     _terrain_marsh_above+1
        sta     near_base+1
        jsr     near_sea
        bcs     @keep
@no_above:
        lda     _terrain_marsh_row      ; this row
        sta     near_base
        lda     _terrain_marsh_row+1
        sta     near_base+1
        jsr     near_sea
        bcs     @keep
        lda     _terrain_marsh_below    ; the row below, if any
        ora     _terrain_marsh_below+1
        beq     @marsh
        lda     _terrain_marsh_below
        sta     near_base
        lda     _terrain_marsh_below+1
        sta     near_base+1
        jsr     near_sea
        bcs     @keep
@marsh: ldy     cell_y
        lda     #TILE_MARSH
        sta     (ptr1),y
@keep:  ldy     cell_y
        jmp     @next

; Whether the row at near_base has sea in the beach's column or either side: carry set if so.
near_sea:
        clc                             ; ptr2 = near_base + x - 1
        lda     near_base
        adc     cell_y
        sta     ptr2
        lda     near_base+1
        adc     page_x+1
        sta     ptr2+1
        lda     ptr2
        bne     :+
        dec     ptr2+1
:       dec     ptr2
        ldy     #2
@cell:  lda     (ptr2),y
        cmp     #TILE_SHALLOWS + 1
        bcc     @sea
        cmp     #TILE_BRIDGE
        beq     @sea
        cmp     #TILE_LAKE              ; lake: the row above has its lakes marked by then
        bcs     @sea
        dey
        bpl     @cell
        clc
        rts
@sea:   sec
        rts

; ------------------------------------------------------------------------
; Putting a row's road cells in - see world.c's add_roads and terrain.h. In C this took over 1K
; of code; here it's a few hundred bytes, and quicker.
;
; void __fastcall__ terrain_road_bits (unsigned char below)
; Sets terrain_road_below (if below) or the row's own map to a bit for each listed road cell from
; terrain_road_from to terrain_road_to - 1 (in terrain_road_lo and _hi, as terrain_list_lo/_hi).
;
; void terrain_add_roads (void)
; Puts the listed road cells, terrain_road_from to terrain_road_to - 1, in the row at
; terrain_road_tiles: road, or bridge over a river or the sea; towns, and cells already road or
; bridge, stay. With a finished row above (terrain_road_above, 0 for none), a cell that would make
; a 2x2 square of road with a road cell n beside it and the two above them is left out if nothing
; else joins it: no road or town on its other side, and nothing below it (terrain_road_below:
; roads and towns) unless n has something below it too. The row's own map, filled by
; terrain_road_bits(0), keeps up with what's left out.

        .export         _terrain_road_bits, _terrain_add_roads, _terrain_road_below
        .import         _terrain_road_lo, _terrain_road_hi, _terrain_road_from, _terrain_road_to
        .import         _terrain_road_tiles, _terrain_road_above
        .importzp       ptr3, ptr4

TILE_TOWN       = 15                    ; world.h's tiles (TILE_SHALLOWS, _BRIDGE: above)
TILE_RIVER      = 16
TILE_ROAD       = 17

        .bss
road_here:      .res    64              ; a bit for each road cell in the row being finished...
_terrain_road_below:
                .res    64              ; ...and in the row below it, with its towns
road_i:         .res    2               ; the list index
road_x:         .res    2               ; its cell's column
road_n:         .res    2               ; a column beside it
road_o:         .res    2               ; ...and the one on the other side
road_side:      .res    1

        .code

; The column of list entry road_i, into road_x.
list_column:
        clc
        lda     _terrain_road_lo
        adc     road_i
        sta     ptr2
        lda     _terrain_road_lo+1
        adc     road_i+1
        sta     ptr2+1
        ldy     #0
        lda     (ptr2),y
        sta     road_x
        lda     road_i                  ; the 9th bit: bit (i & 7) of terrain_road_hi[i >> 3]
        and     #7
        tax
        lda     road_i+1
        sta     tmp2
        lda     road_i
        lsr     tmp2
        ror     a
        lsr     tmp2
        ror     a
        lsr     tmp2
        ror     a
        clc
        adc     _terrain_road_hi
        sta     ptr2
        lda     tmp2
        adc     _terrain_road_hi+1
        sta     ptr2+1
        lda     (ptr2),y
        and     cell_bit,x
        beq     :+
        lda     #1
:       sta     road_x+1
        rts

; Bit X (of the column in A:tmp1 - low byte in tmp1, high in A) of a map: points ptr2 at the map's
; byte (map in ptr3) and leaves the bit's mask in A.
map_byte:
        lsr     a                       ; byte = column >> 3 (columns are under 512)
        lda     tmp1
        ror     a
        lsr     a
        lsr     a
        clc
        adc     ptr3
        sta     ptr2
        lda     ptr3+1
        adc     #0
        sta     ptr2+1
        lda     tmp1
        and     #7
        tax
        lda     cell_bit,x
        rts

; Whether column road_n (Z clear if so) has its bit in the map at ptr3.
bit_n:  lda     road_n
        sta     tmp1
        lda     road_n+1
        jmp     bit_test
bit_o:  lda     road_o
        sta     tmp1
        lda     road_o+1
        jmp     bit_test
bit_x:  lda     road_x
        sta     tmp1
        lda     road_x+1
bit_test:
        jsr     map_byte
        ldy     #0
        and     (ptr2),y
        rts

; Sets (A = 1) or clears (A = 0) road_x's bit in road_here.
set_here:
        pha
        lda     #<road_here
        sta     ptr3
        lda     #>road_here
        sta     ptr3+1
        lda     road_x
        sta     tmp1
        lda     road_x+1
        jsr     map_byte
        ldy     #0
        sta     tmp3
        pla
        beq     @clear
        lda     tmp3
        ora     (ptr2),y
        sta     (ptr2),y
        rts
@clear: lda     tmp3
        eor     #$FF
        and     (ptr2),y
        sta     (ptr2),y
        rts

; Points ptr3 at road_here or terrain_road_below.
use_here:
        lda     #<road_here
        sta     ptr3
        lda     #>road_here
        sta     ptr3+1
        rts
use_below:
        lda     #<_terrain_road_below
        sta     ptr3
        lda     #>_terrain_road_below
        sta     ptr3+1
        rts

; Loads tile road_n / road_o / road_x of a row: the row in ptr4, the tile in A.
tile_of:
        clc
        adc     ptr4
        sta     ptr2
        txa
        adc     ptr4+1
        sta     ptr2+1
        ldy     #0
        lda     (ptr2),y
        rts

; Carry set if A is road or bridge (TILE_ROAD and TILE_BRIDGE are next to each other).
is_road:
        cmp     #TILE_ROAD
        bcc     @no
        cmp     #TILE_BRIDGE + 1
        bcs     @no
        sec
        rts
@no:    clc
        rts

_terrain_road_bits:
        tax
        beq     :+
        jsr     use_below
        jmp     @clear
:       jsr     use_here
@clear: lda     #0
        ldy     #63
:       sta     (ptr3),y
        dey
        bpl     :-
        lda     _terrain_road_from
        sta     road_i
        lda     _terrain_road_from+1
        sta     road_i+1
@next:  lda     road_i
        cmp     _terrain_road_to
        lda     road_i+1
        sbc     _terrain_road_to+1
        bcs     @done
        jsr     list_column
        lda     road_x                  ; set road_x's bit
        sta     tmp1
        lda     road_x+1
        jsr     map_byte
        ldy     #0
        ora     (ptr2),y
        sta     (ptr2),y
        inc     road_i
        bne     @next
        inc     road_i+1
        jmp     @next
@done:  rts

_terrain_add_roads:
        lda     _terrain_road_from
        sta     road_i
        lda     _terrain_road_from+1
        sta     road_i+1
@next:  lda     road_i
        cmp     _terrain_road_to
        lda     road_i+1
        sbc     _terrain_road_to+1
        bcc     :+
        rts
:       jsr     list_column
        lda     _terrain_road_tiles     ; the cell's tile
        sta     ptr4
        lda     _terrain_road_tiles+1
        sta     ptr4+1
        lda     road_x
        ldx     road_x+1
        jsr     tile_of
        cmp     #TILE_TOWN              ; towns, and cells already in, stay
        beq     @skip
        jsr     is_road
        bcs     @skip
        lda     _terrain_road_above
        ora     _terrain_road_above+1
        beq     @put
        jsr     spare
        bcc     @put
        lda     #0                      ; left out
        jsr     set_here
        jmp     @skip
@put:   lda     #1
        jsr     set_here
        lda     _terrain_road_tiles
        sta     ptr4
        lda     _terrain_road_tiles+1
        sta     ptr4+1
        lda     road_x
        ldx     road_x+1
        jsr     tile_of                 ; ptr2: the cell
        cmp     #TILE_RIVER             ; bridge over a river or the sea, else road
        beq     @bridge
        cmp     #TILE_SHALLOWS + 1
        bcc     @bridge
        lda     #TILE_ROAD
        bne     @store
@bridge:
        lda     #TILE_BRIDGE
@store: sta     (ptr2),y
@skip:  inc     road_i
        bne     @next
        inc     road_i+1
        jmp     @next

; Whether cell road_x can be left out: carry set if so.
spare:
        lda     _terrain_road_above     ; the cell above must be road
        sta     ptr4
        lda     _terrain_road_above+1
        sta     ptr4+1
        lda     road_x
        ldx     road_x+1
        jsr     tile_of
        jsr     is_road
        bcs     :+
        rts                             ; carry clear: not spare
:       lda     #0
        sta     road_side
@side:  ; n = x - 1 (side 0) or x + 1 (side 1); o the other way.
        lda     road_side
        bne     @right
        lda     road_x                  ; x == 0: no cell to the left
        ora     road_x+1
        bne     :+
        jmp     @next_side
:
        sec
        lda     road_x
        sbc     #1
        sta     road_n
        lda     road_x+1
        sbc     #0
        sta     road_n+1
        clc
        lda     road_x
        adc     #1
        sta     road_o
        lda     road_x+1
        adc     #0
        sta     road_o+1
        jmp     @check
@right: clc
        lda     road_x
        adc     #1
        sta     road_n
        lda     road_x+1
        adc     #0
        sta     road_n+1
        lda     road_n                  ; n == width: no cell to the right
        cmp     _terrain_width
        lda     road_n+1
        sbc     _terrain_width+1
        bcc     :+
        jmp     @next_side
:
        sec
        lda     road_x
        sbc     #1
        sta     road_o
        lda     road_x+1
        sbc     #0
        sta     road_o+1                ; $FFFF when x == 0: past the edge
@check: jsr     use_here                ; n road in this row...
        jsr     bit_n
        bne     :+
        jmp     @next_side
:
        lda     _terrain_road_above     ; ...and above it
        sta     ptr4
        lda     _terrain_road_above+1
        sta     ptr4+1
        lda     road_n
        ldx     road_n+1
        jsr     tile_of
        jsr     is_road
        bcs     :+
        jmp     @next_side
:
        ; A square with n. Not spare if the other side has road or a town.
        lda     road_o
        cmp     _terrain_width
        lda     road_o+1
        sbc     _terrain_width+1
        bcs     @below                  ; past the edge
        jsr     bit_o
        bne     @not
        lda     _terrain_road_tiles
        sta     ptr4
        lda     _terrain_road_tiles+1
        sta     ptr4+1
        lda     road_o
        ldx     road_o+1
        jsr     tile_of
        cmp     #TILE_TOWN
        beq     @not
@below: jsr     use_below               ; nothing below x, or something below n too
        jsr     bit_x
        beq     @yes
        jsr     bit_n
        bne     @yes
@not:   clc
        rts
@yes:   sec
        rts
@next_side:
        inc     road_side
        lda     road_side
        cmp     #2
        beq     @not
        jmp     @side

; ------------------------------------------------------------------------
; void terrain_route (void)
;
; The cheapest way from one point of a window of grid points to every other (world.c's
; plan_road): a shortest-path search, queue-based - a point is queued again whenever a cheaper
; way to it is found, until none is. The window is terrain_route_w points across (at most 126),
; numbered across then down, terrain_route_points of them; stepping onto a point, in any of the 8
; directions, costs its terrain_route_cost, and points costing $FF are walls, never stepped onto.
; The window's edge points must all be walls: then a point's neighbours are always in the window,
; at fixed offsets, and need no checking.
;
; The search starts from point terrain_route_start. world.c sets every point's
; terrain_route_dist_hi to $FF - no cost adds up to that - and its terrain_route_parent to 0
; first; terrain_route_queue has 2 bytes an entry, room for points + 1. Afterwards each point's
; terrain_route_dist is the cheapest way there, and the low 3 bits of its terrain_route_parent the
; direction of the last step of it (world.c's next_x/next_y order); bit 7 is set while it's
; queued. terrain_route_off then holds each direction's neighbour's offset from w + 1 points
; before a point.
;
; Zero page while it runs: ptr1, ptr2, ptr3 and ptr4 point into the costs, parents and costs so
; far (low bytes, high bytes) at base, w + 1 points before the point taken from the queue, so
; that its 8 neighbours are at fixed offsets from them (route_off); sreg is the queue entry being
; read or written; tmp1/tmp2 a cost through the point taken.

        .export         _terrain_route, _terrain_route_off
        .import         _terrain_route_w, _terrain_route_points, _terrain_route_start
        .import         _terrain_route_cost, _terrain_route_parent
        .import         _terrain_route_dist_lo, _terrain_route_dist_hi, _terrain_route_queue

        .bss
_terrain_route_off:                     ; each direction's neighbour's offset from base
route_off:      .res    8
route_w1:       .res    1               ; w + 1: the point taken's offset from base
route_base:     .res    2               ; the point taken's number - (w + 1)
route_head:     .res    2               ; the next queue entry to take...
route_tail:     .res    2               ; ...and to fill
route_end:      .res    2               ; the end of the queue, where it goes back round
route_lo:       .res    1               ; the cost of getting to the point taken
route_hi:       .res    1

        .code

_terrain_route:
        ; The neighbours' offsets, in next_x/next_y's order: up-left, up, up-right, left, right,
        ; down-left, down, down-right - 0, 1, 2, w, w + 2, 2w, 2w + 1, 2w + 2.
        ldx     #0
        stx     route_off
        inx
        stx     route_off+1
        inx
        stx     route_off+2
        lda     _terrain_route_w
        sta     route_off+3
        tax
        inx
        stx     route_w1
        inx
        stx     route_off+4
        asl     a                       ; w < 128: no carry
        sta     route_off+5
        adc     #1
        sta     route_off+6
        adc     #1
        sta     route_off+7

        ; The start: nothing to get there, and queued - the queue's first entry.
        clc
        lda     _terrain_route_dist_lo
        adc     _terrain_route_start
        sta     ptr1
        lda     _terrain_route_dist_lo+1
        adc     _terrain_route_start+1
        sta     ptr1+1
        clc
        lda     _terrain_route_dist_hi
        adc     _terrain_route_start
        sta     ptr2
        lda     _terrain_route_dist_hi+1
        adc     _terrain_route_start+1
        sta     ptr2+1
        clc
        lda     _terrain_route_parent
        adc     _terrain_route_start
        sta     ptr3
        lda     _terrain_route_parent+1
        adc     _terrain_route_start+1
        sta     ptr3+1
        ldy     #0
        tya
        sta     (ptr1),y
        sta     (ptr2),y
        lda     #$80
        sta     (ptr3),y
        lda     _terrain_route_queue
        sta     ptr1
        lda     _terrain_route_queue+1
        sta     ptr1+1
        lda     _terrain_route_start
        sta     (ptr1),y
        iny
        lda     _terrain_route_start+1
        sta     (ptr1),y

        ; The queue's end, (points + 1) * 2 bytes on; the start's entry is in, so the next to
        ; take is the first, and the next to fill the second.
        lda     _terrain_route_points
        clc
        adc     #1
        sta     tmp1
        lda     _terrain_route_points+1
        adc     #0
        asl     tmp1
        rol     a
        sta     tmp2
        clc
        lda     _terrain_route_queue
        sta     route_head
        adc     tmp1
        sta     route_end
        lda     _terrain_route_queue+1
        sta     route_head+1
        adc     tmp2
        sta     route_end+1
        clc
        lda     route_head
        adc     #2
        sta     route_tail
        lda     route_head+1
        adc     #0
        sta     route_tail+1

@take:  lda     route_head              ; nothing queued: done
        cmp     route_tail
        bne     @pop
        lda     route_head+1
        cmp     route_tail+1
        bne     @pop
        rts

@pop:   lda     route_head              ; take a point: base = its number - (w + 1)
        sta     sreg
        lda     route_head+1
        sta     sreg+1
        ldy     #0
        lda     (sreg),y
        sec
        sbc     route_w1
        sta     route_base
        iny
        lda     (sreg),y
        sbc     #0
        sta     route_base+1
        clc                             ; on to the next entry, back to the first after the last
        lda     route_head
        adc     #2
        sta     route_head
        bcc     :+
        inc     route_head+1
:       cmp     route_end
        bne     :+
        lda     route_head+1
        cmp     route_end+1
        bne     :+
        lda     _terrain_route_queue
        sta     route_head
        lda     _terrain_route_queue+1
        sta     route_head+1

:       clc                             ; the four pointers at base
        lda     _terrain_route_cost
        adc     route_base
        sta     ptr1
        lda     _terrain_route_cost+1
        adc     route_base+1
        sta     ptr1+1
        clc
        lda     _terrain_route_parent
        adc     route_base
        sta     ptr2
        lda     _terrain_route_parent+1
        adc     route_base+1
        sta     ptr2+1
        clc
        lda     _terrain_route_dist_lo
        adc     route_base
        sta     ptr3
        lda     _terrain_route_dist_lo+1
        adc     route_base+1
        sta     ptr3+1
        clc
        lda     _terrain_route_dist_hi
        adc     route_base
        sta     ptr4
        lda     _terrain_route_dist_hi+1
        adc     route_base+1
        sta     ptr4+1

        ldy     route_w1                ; the point taken: no longer queued; its cost so far
        lda     (ptr2),y
        and     #$7F
        sta     (ptr2),y
        lda     (ptr3),y
        sta     route_lo
        lda     (ptr4),y
        sta     route_hi

        ldx     #7                      ; each neighbour, direction x
@neighbour:
        ldy     route_off,x
        lda     (ptr1),y                ; a wall: never stepped onto
        cmp     #$FF
        beq     @next
        clc                             ; the cost of getting there through the point taken
        adc     route_lo
        sta     tmp1
        lda     route_hi
        adc     #0
        sta     tmp2
        cmp     (ptr4),y                ; cheaper than the cheapest so far?
        bcc     @cheaper
        bne     @next
        lda     tmp1
        cmp     (ptr3),y
        bcs     @next
@cheaper:
        lda     tmp1                    ; yes: the new cheapest, and its last step's direction
        sta     (ptr3),y
        lda     tmp2
        sta     (ptr4),y
        lda     (ptr2),y                ; already queued: just the new direction
        bmi     @queued
        txa
        ora     #$80
        sta     (ptr2),y
        lda     route_tail              ; else queued too: its number, base + y, at the tail
        sta     sreg
        lda     route_tail+1
        sta     sreg+1
        tya
        clc
        adc     route_base
        ldy     #0
        sta     (sreg),y
        lda     route_base+1
        adc     #0
        iny
        sta     (sreg),y
        clc                             ; on to the next entry, back to the first after the last
        lda     route_tail
        adc     #2
        sta     route_tail
        bcc     :+
        inc     route_tail+1
:       cmp     route_end
        bne     @next
        lda     route_tail+1
        cmp     route_end+1
        bne     @next
        lda     _terrain_route_queue
        sta     route_tail
        lda     _terrain_route_queue+1
        sta     route_tail+1
        jmp     @next
@queued:
        txa
        ora     #$80
        sta     (ptr2),y
@next:  dex
        bmi     :+
        jmp     @neighbour
:       jmp     @take

; ------------------------------------------------------------------------
; void terrain_water_runs (void)
;
; Lists the runs of water - tiles up to TILE_SHALLOWS - in the row at terrain_runs_row,
; terrain_width cells long, for finding lakes (world.c's lake_row): each run's first and last
; column, in the 16-bit words of terrain_run_a and terrain_run_b, terrain_run_count of them, in
; order across. There's room for RUNS_MAX (world.c's LAKE_RUNS); past that, the last run is stretched
; to take in the rest of the row's water - and the land between, which only ever makes world.c
; join bodies of water, never split them. It looks at every cell of every row, so it's in
; assembly: a tight loop over land, another over water.
;
; Zero page while it runs: ptr1 = the row's current page, tmp1 = where the cells end in it
; (0 for all 256).

        .export         _terrain_water_runs, _terrain_run_count
        .export         _terrain_run_a, _terrain_run_b, _terrain_run_mark, _terrain_lake_mark
        .import         _terrain_runs_row

RUNS_MAX        = 40

        .bss
_terrain_run_a:         .res    2 * RUNS_MAX
_terrain_run_b:         .res    2 * RUNS_MAX
_terrain_run_mark:      .res    RUNS_MAX        ; world.c: which runs terrain_lake_mark makes lake
_terrain_run_count:     .res    1
run_page:       .res    1               ; the current column's high byte
run_pages:      .res    1               ; whole pages of cells still to come after this one
in_water:       .res    1               ; non-zero while in a run

        .code

_terrain_water_runs:
        lda     _terrain_runs_row
        sta     ptr1
        lda     _terrain_runs_row+1
        sta     ptr1+1
        lda     #0
        sta     _terrain_run_count
        sta     run_page
        sta     in_water
        lda     _terrain_width+1
        sta     run_pages

        ; Each page of the row: a whole one while there are more, then what's left of the last.
        ; A whole page's loops just stop where y comes back round to 0; a part page's compare y
        ; with where it ends, tmp1.
@page:  ldy     #0
        lda     run_pages
        beq     @last
        lda     in_water
        bne     @w_water

@w_land:
        lda     (ptr1),y                ; over land, until water starts a run
        cmp     #TILE_SHALLOWS + 1
        bcc     @w_start
@w_land_next:
        iny
        bne     @w_land
        beq     @next_page
@w_start:
        jsr     start_run
@w_water:
        lda     (ptr1),y                ; over water, until land ends the run
        cmp     #TILE_SHALLOWS + 1
        bcs     @w_stop
        iny
        bne     @w_water
        beq     @next_page
@w_stop:
        jsr     end_run
        jmp     @w_land_next

@last:  lda     _terrain_width
        beq     @end                    ; no part page: done
        sta     tmp1
        lda     in_water
        bne     @water
@land:  lda     (ptr1),y
        cmp     #TILE_SHALLOWS + 1
        bcc     @start
@land_next:
        iny
        cpy     tmp1
        bne     @land
        beq     @end
@start: jsr     start_run
@water: lda     (ptr1),y
        cmp     #TILE_SHALLOWS + 1
        bcs     @stop
        iny
        cpy     tmp1
        bne     @water
        beq     @end
@stop:  jsr     end_run
        jmp     @land_next

@next_page:
        dec     run_pages
        inc     ptr1+1
        inc     run_page
        jmp     @page

@end:   lda     in_water                ; a run going on to the row's end ends with it
        beq     :+
        jsr     end_run
:       rts

; Starts a run at run_page:y - or, with no room for another, has the last one go on instead.
start_run:
        ldx     _terrain_run_count
        cpx     #RUNS_MAX
        bcc     :+
        dex
        stx     _terrain_run_count
        bcs     @in                     ; (the carry is still set)
:       txa                             ; its word
        asl     a
        tax
        tya
        sta     _terrain_run_a,x
        lda     run_page
        sta     _terrain_run_a+1,x
@in:    lda     #1
        sta     in_water
        rts

; Ends the current run: its last column is the one before run_page:y.
end_run:
        lda     _terrain_run_count      ; its word
        asl     a
        tax
        tya
        sec
        sbc     #1
        sta     _terrain_run_b,x
        lda     run_page
        sbc     #0
        sta     _terrain_run_b+1,x
        inc     _terrain_run_count
        lda     #0
        sta     in_water
        rts

; ------------------------------------------------------------------------
; void terrain_lake_mark (void)
;
; Makes lake the water of each run terrain_water_runs listed in the row at terrain_runs_row that
; world.c has marked in terrain_run_mark: tiles up to TILE_SHALLOWS become TILE_LAKE more, keeping
; their shade; anything else (a bridge) stays.
;
; Zero page while it runs: ptr1 = the run's first cell, then each page of it; tmp1:tmp2 = its
; cells, less those done.

_terrain_lake_mark:
        ldx     #0
@run:   cpx     _terrain_run_count
        bne     :+
        rts
:       lda     _terrain_run_mark,x
        beq     @next
        stx     tmp3
        txa                             ; the run's words
        asl     a
        tax
        clc                             ; ptr1 = the row + its first column
        lda     _terrain_runs_row
        adc     _terrain_run_a,x
        sta     ptr1
        lda     _terrain_runs_row+1
        adc     _terrain_run_a+1,x
        sta     ptr1+1
        sec                             ; cells: last - first + 1
        lda     _terrain_run_b,x
        sbc     _terrain_run_a,x
        sta     tmp1
        lda     _terrain_run_b+1,x
        sbc     _terrain_run_a+1,x
        sta     tmp2
        inc     tmp1
        bne     :+
        inc     tmp2
:       ldy     #0
@cell:  lda     (ptr1),y
        cmp     #TILE_SHALLOWS + 1
        bcs     :+
        adc     #TILE_LAKE              ; (the carry is clear)
        sta     (ptr1),y
:       iny
        bne     :+
        inc     ptr1+1                  ; on to the next page
:       lda     tmp1                    ; one fewer to do
        bne     :+
        dec     tmp2
:       dec     tmp1
        bne     @cell
        lda     tmp2
        bne     @cell
        ldx     tmp3
@next:  inx
        bne     @run                    ; (always: at most RUNS_MAX)

; ------------------------------------------------------------------------
; void terrain_lake_label (void)
;
; Labels the runs terrain_water_runs just listed (world.c's lake_row explains the method): each
; run gets the label of a run it touches in the row above - across, or diagonally, so the runs
; above from a cell before it to a cell after it - or a new one, and where it touches more than
; one, their bodies are joined. Labels are 16-bit, up to LAKE_LABELS (world.c), parents in the
; words at terrain_lake_parent; terrain_lake_labels counts those given out, and once there are
; LAKE_LABELS a new run is counted as ocean, label 0, which never makes a lake by mistake.
; terrain_lake_edge non-zero (the map's first or last row), or a run at either end of the row,
; joins the ocean. Leaves each run's label in terrain_run_l, sets terrain_run_mark for those not
; joined to the ocean, and keeps the runs as the row above for the next row.
;
; Zero page while it runs: ptr1 (find's parent pointers), tmp1-tmp3 (work).

        .export         _terrain_lake_label, _terrain_run_l, _terrain_lake_prev_n
        .import         _terrain_lake_parent, _terrain_lake_labels, _terrain_lake_edge

LAKE_LABELS     = 512

        .bss
_terrain_run_l:         .res    2 * RUNS_MAX    ; this row's labels
prev_a:         .res    2 * RUNS_MAX            ; the row above's runs: first column...
prev_b:         .res    2 * RUNS_MAX            ; ...last column...
prev_l:         .res    2 * RUNS_MAX            ; ...and label
_terrain_lake_prev_n:   .res    1               ; how many (world.c sets 0 to start)
lab_i:          .res    1                       ; the run being labelled, x 2
lab_j:          .res    1                       ; the first run above not yet passed, x 2
lab_k:          .res    1
lab_n2:         .res    1                       ; runs x 2...
lab_p2:         .res    1                       ; ...and runs above x 2
lab_l:          .res    2                       ; the label so far ($FFFF: none)
lab_x:          .res    2                       ; find and join's labels
lab_y:          .res    2
lab_end:        .res    2                       ; the row's last column
lab_a1:         .res    2                       ; the run's first column - 1...
lab_b1:         .res    2                       ; ...and last + 1

        .code

; ptr1 = &terrain_lake_parent[A/X] (low, high).
parent_of:
        stx     tmp1
        asl     a
        rol     tmp1
        clc
        adc     _terrain_lake_parent
        sta     ptr1
        lda     tmp1
        adc     _terrain_lake_parent+1
        sta     ptr1+1
        rts

; A/X = the root of label A/X, halving the path on the way: each label passed gets its
; grandparent as its parent.
find:   sta     lab_x
        stx     lab_x+1
@up:    lda     lab_x
        ldx     lab_x+1
        jsr     parent_of               ; ptr1 = &parent[l]
        ldy     #0
        lda     (ptr1),y                ; parent[l] == l: the root
        cmp     lab_x
        bne     @move
        iny
        lda     (ptr1),y
        cmp     lab_x+1
        bne     @move
        lda     lab_x
        ldx     lab_x+1
        rts
@move:  lda     ptr1                    ; keep &parent[l]
        sta     tmp2
        lda     ptr1+1
        sta     tmp3
        ldy     #0                      ; the grandparent: parent[parent[l]]
        lda     (ptr1),y
        pha
        iny
        lda     (ptr1),y
        tax
        pla
        jsr     parent_of
        ldy     #0
        lda     (ptr1),y
        sta     lab_x                   ; l = it...
        iny
        lda     (ptr1),y
        sta     lab_x+1
        lda     tmp2                    ; ...and parent[l] = it too
        sta     ptr1
        lda     tmp3
        sta     ptr1+1
        lda     lab_x+1
        sta     (ptr1),y
        dey
        lda     lab_x
        sta     (ptr1),y
        jmp     @up

; A/X = lab_l joined with label A/X: their roots, the larger made a child of the smaller (so the
; ocean stays 0), which is returned - and left in lab_l.
join:   jsr     find
        sta     lab_y                   ; the other's root
        stx     lab_y+1
        lda     lab_l
        ldx     lab_l+1
        jsr     find                    ; lab_l's root, in A/X (and lab_x)
        cpx     lab_y+1                 ; which is smaller?
        bcc     @x_lower
        bne     @y_lower
        cmp     lab_y
        bcc     @x_lower
        beq     @same
@y_lower:
        jsr     parent_of               ; parent[x root] = y root
        ldy     #0
        lda     lab_y
        sta     (ptr1),y
        iny
        lda     lab_y+1
        sta     (ptr1),y
        lda     lab_y
        ldx     lab_y+1
        jmp     @done
@x_lower:
        lda     lab_y                   ; parent[y root] = x root
        ldx     lab_y+1
        jsr     parent_of
        ldy     #0
        lda     lab_x
        sta     (ptr1),y
        iny
        lda     lab_x+1
        sta     (ptr1),y
@same:  lda     lab_x
        ldx     lab_x+1
@done:  sta     lab_l
        stx     lab_l+1
        rts

_terrain_lake_label:
        lda     _terrain_width          ; the last column
        sec
        sbc     #1
        sta     lab_end
        lda     _terrain_width+1
        sbc     #0
        sta     lab_end+1
        lda     _terrain_run_count
        asl     a
        sta     lab_n2
        lda     _terrain_lake_prev_n
        asl     a
        sta     lab_p2
        lda     #0
        sta     lab_i
        sta     lab_j

@run:   ldx     lab_i
        cpx     lab_n2
        bne     :+
        jmp     @keep
:       sec                             ; its first column - 1 and last + 1 (wrapping to $FFFF
        lda     _terrain_run_a,x        ; for a run from column 0 is harmless: nothing ends
        sbc     #1                      ; before it then)
        sta     lab_a1
        lda     _terrain_run_a+1,x
        sbc     #0
        sta     lab_a1+1
        clc
        lda     _terrain_run_b,x
        adc     #1
        sta     lab_b1
        lda     _terrain_run_b+1,x
        adc     #0
        sta     lab_b1+1

        ; The runs above ending before its first column - 1 are past for good: both rows' runs are
        ; in order. (A run from column 0 has nothing before it.)
        lda     _terrain_run_a,x
        ora     _terrain_run_a+1,x
        beq     @touch
@pass:  ldy     lab_j
        cpy     lab_p2
        beq     @touch
        lda     prev_b,y                ; prev_b < a - 1?
        cmp     lab_a1
        lda     prev_b+1,y
        sbc     lab_a1+1
        bcs     @touch
        iny
        iny
        sty     lab_j
        bne     @pass                   ; (always)

        ; Those it touches: starting up to its last column + 1.
@touch: lda     #$FF                    ; no label yet
        sta     lab_l
        sta     lab_l+1
        lda     lab_j
        sta     lab_k
@above: ldy     lab_k
        cpy     lab_p2
        beq     @labelled
        lda     lab_b1                  ; prev_a <= b + 1?
        cmp     prev_a,y
        lda     lab_b1+1
        sbc     prev_a+1,y
        bcc     @labelled
        lda     lab_l+1                 ; the first: its label
        and     lab_l
        cmp     #$FF
        bne     @join
        lda     prev_l,y
        sta     lab_l
        lda     prev_l+1,y
        sta     lab_l+1
        jmp     @next_above
@join:  lda     prev_l,y                ; more: joined
        ldx     prev_l+1,y
        jsr     join
@next_above:
        inc     lab_k
        inc     lab_k
        bne     @above                  ; (always)

@labelled:
        lda     lab_l+1                 ; none above: a new label, if there's one left
        and     lab_l
        cmp     #$FF
        bne     @edge
        lda     _terrain_lake_labels+1
        cmp     #>LAKE_LABELS
        bcc     @new
        lda     #0                      ; none left: the ocean
        sta     lab_l
        sta     lab_l+1
        beq     @edge
@new:   lda     _terrain_lake_labels    ; the next, its own parent
        sta     lab_l
        ldx     _terrain_lake_labels+1
        stx     lab_l+1
        jsr     parent_of
        ldy     #0
        lda     lab_l
        sta     (ptr1),y
        iny
        lda     lab_l+1
        sta     (ptr1),y
        inc     _terrain_lake_labels
        bne     @edge
        inc     _terrain_lake_labels+1

        ; At the map's edge: the ocean.
@edge:  ldx     lab_i
        lda     _terrain_lake_edge
        bne     @ocean
        lda     _terrain_run_a,x
        ora     _terrain_run_a+1,x
        beq     @ocean
        lda     _terrain_run_b,x
        cmp     lab_end
        bne     @store
        lda     _terrain_run_b+1,x
        cmp     lab_end+1
        bne     @store
@ocean: lda     #0
        tax
        jsr     join
@store: ldx     lab_i
        lda     lab_l
        sta     _terrain_run_l,x
        lda     lab_l+1
        sta     _terrain_run_l+1,x
        inx
        inx
        stx     lab_i
        jmp     @run

        ; Each run marked if its body isn't the ocean (after all of them, as a later run can join
        ; an earlier one to it), and kept as the row above.
@keep:  lda     #0
        sta     lab_i
@mark:  ldx     lab_i
        cpx     lab_n2
        beq     @kept
        lda     _terrain_run_a,x
        sta     prev_a,x
        lda     _terrain_run_a+1,x
        sta     prev_a+1,x
        lda     _terrain_run_b,x
        sta     prev_b,x
        lda     _terrain_run_b+1,x
        sta     prev_b+1,x
        lda     _terrain_run_l+1,x
        sta     prev_l+1,x
        tay
        lda     _terrain_run_l,x
        sta     prev_l,x
        tax
        tya
        stx     tmp1                    ; find(label): A/X = low, high
        tax
        lda     tmp1
        jsr     find
        stx     tmp1
        ora     tmp1                    ; root 0: the ocean
        beq     :+
        lda     #1
:       pha
        lda     lab_i
        lsr     a
        tax
        pla
        sta     _terrain_run_mark,x
        inc     lab_i
        inc     lab_i
        bne     @mark                   ; (always)
@kept:  lda     _terrain_run_count
        sta     _terrain_lake_prev_n
        rts
