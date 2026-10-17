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
; The same for the C64 (VIC-II) and the Plus/4 (TED); the addresses come from screen.h.

        .export         _view_blit
        .import         _view_row_lo, _view_row_hi, _view_tile_char, _view_tile_colour
        .importzp       ptr1

ROWS    = 23
COLS    = 40

.if .defined(__C64__)
SCREEN  = $E000                         ; SCREEN_CHARS in screen.h
COLOUR  = $D800                         ; SCREEN_COLOURS
RASTER  = $D012                         ; the raster line the beam is on (its low 8 bits)
ROW0_DONE = 51 + 8                      ; the first line after text row 0 (lines 51-58)
.else
SCREEN  = $0C00
COLOUR  = $0800
RASTER_HI = $FF1C                       ; bit 0: bit 8 of the raster line
RASTER  = $FF1D                         ; its low 8 bits
ROW0_DONE = 4 + 8                       ; the first line after text row 0 (lines 4-11)
.endif

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
@wait:
.if .not .defined(__C64__)
        lda     RASTER_HI               ; on the TED, line 268 has the same low byte as 12
        lsr     a
        bcs     @wait
.endif
        lda     RASTER
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

.if .not .defined(__C64__)

; ------------------------------------------------------------------------
; void __fastcall__ screen_copy_font (unsigned char* dst)
; Copies the ROM's 1K uppercase font, at $D000, to dst. cc65's Plus/4 programs run with the ROMs
; paged out, and the C runtime's stack is in the RAM under them, so C code can't run while
; they're paged in to read it.

        .export         _screen_copy_font
        .importzp       ptr2, tmp1

TED_STATUS = $FF13                      ; bit 0 is 1 while the ROMs are paged in
ROM_ON  = $FF3E
RAM_ON  = $FF3F

_screen_copy_font:
        sta     ptr1
        stx     ptr1+1
        lda     #<$D000
        sta     ptr2
        lda     #>$D000
        sta     ptr2+1
        php
        sei
        lda     TED_STATUS
        sta     tmp1
        sta     ROM_ON
        ldx     #4                      ; 4 pages
        ldy     #0
:       lda     (ptr2),y
        sta     (ptr1),y
        iny
        bne     :-
        inc     ptr1+1
        inc     ptr2+1
        dex
        bne     :-
        lda     tmp1
        lsr     a
        bcs     :+                      ; they were already in
        sta     RAM_ON
:       plp
        rts

.endif
