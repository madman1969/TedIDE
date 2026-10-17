;
; blit.s - draws the 40x23 map view into screen and colour RAM (see view.c), fast enough to do
; in about a frame and a half, and without tearing.
;
; Tearing happens when the screen is changed while the video chip's raster beam is drawing it,
; so the top of a frame shows the new view and the bottom the old. This waits until the beam has
; just finished the first text row, then draws each row after the beam has passed it. Drawing a
; row takes longer than the beam takes to draw one, so the beam stays ahead: every row on screen
; is either all old or all new, and the next frame shows the whole new view.
;

        .export         _view_blit
        .import         _view_row_lo, _view_row_hi, _view_tile_char, _view_tile_colour
        .importzp       ptr1

SCREEN  = $E000                         ; CHARSET_SCREEN in charset.h
COLOUR  = $D800
ROWS    = 23
COLS    = 40
RASTER  = $D012                         ; the raster line the beam is on (its low 8 bits)
ROW0_DONE = 51 + 8                      ; the first line after text row 0 (lines 51-58)

        .code

; void view_blit (void)
; Draws each view row from the tiles at view_row_lo/hi[row], 40 of them, as characters and
; colours from view_tile_char and view_tile_colour.

_view_blit:
        ; Interrupts off while it draws, so the KERNAL's interrupt handler can't hold us up long
        ; enough for the beam to catch up with the last rows on the next frame. The keyboard is
        ; read a little late; nothing is lost.
        php
        sei
@wait:  lda     RASTER
        cmp     #ROW0_DONE
        bne     @wait

        .repeat ROWS, row
        lda     _view_row_lo+row
        sta     ptr1
        lda     _view_row_hi+row
        sta     ptr1+1
        ldy     #COLS-1
:       lda     (ptr1),y                ; the tile
        tax
        lda     _view_tile_char,x
        sta     SCREEN+row*COLS,y
        lda     _view_tile_colour,x
        sta     COLOUR+row*COLS,y
        dey
        bpl     :-
        .endrepeat

        plp
        rts
