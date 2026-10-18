;
; blit.s - draws the map view's rows of tiles onto the screen as characters and colours (see
; view.c), and the machine-specific timing around it.
;
; C64: the view is drawn straight onto the screen, without tearing. Tearing happens when the
; screen is changed while the video chip's raster beam is drawing it, so the top of a frame shows
; the new view and the bottom the old. This waits until the beam has just finished the first text
; row, then draws each row after the beam has passed it. Drawing a row takes longer than the beam
; takes to draw one, so the beam stays ahead: every row on screen is either all old or all new,
; and the next frame shows the whole new view.
;
; Plus/4: the view is drawn, a row at a time, into whichever of two screens the TED isn't showing
; (screen_plus4.c) - often ahead of time, in the spare time between steps - and an interrupt
; handler here switches the TED to it between frames. The same handler scrolls the map smoothly,
; changing the TED's scroll registers partway down every frame.

        .import         _view_tile_char, _view_tile_colour
        .importzp       ptr1

COLS    = 40

.if .defined(__C64__)

        .export         _view_blit
        .import         _view_row_lo, _view_row_hi

ROWS    = 23                            ; SCREEN_MAP_HEIGHT in screen.h
SCREEN  = $E000                         ; SCREEN_CHARS
COLOUR  = $D800                         ; SCREEN_COLOURS
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

.else

ROWS    = 22                            ; SCREEN_MAP_HEIGHT in screen.h
MAP_TOP = 3                             ; SCREEN_MAP_TOP
TED_LINE_HI = $FF1C                     ; bit 0: bit 8 of the raster line the beam is on
TED_LINE_LO = $FF1D                     ; its low 8 bits

CACHE_ROWS = ROWS + 2                   ; view.c's tile cache: rows of 512 tiles, one per slot

        .export         _view_blit_rows, _blit_chars, _blit_colours, _blit_end, _blit_stop_line
        .export         _blit_tiles, _blit_slot
        .importzp       ptr2, ptr3, tmp1, tmp2

        .bss

_blit_chars:    .res    2               ; the screen to draw into: its characters...
_blit_colours:  .res    2               ; ...and its colours
_blit_end:      .res    1               ; the row to stop before
_blit_stop_line: .res   1               ; stop rather than start a row on this line or later (up to 204)
_blit_tiles:    .res    2               ; the first view row's tiles in cache slot 0
_blit_slot:     .res    1               ; the cache slot of the first view row

        .rodata

; Each map row's offset into the screen.
row_offset_lo:
        .repeat ROWS, row
        .byte   <((MAP_TOP+row)*COLS)
        .endrepeat
row_offset_hi:
        .repeat ROWS, row
        .byte   >((MAP_TOP+row)*COLS)
        .endrepeat

        .code

; unsigned char __fastcall__ view_blit_rows (unsigned char first)
; As on the C64, but into the screen at blit_chars/blit_colours - the Plus/4's hidden one - and only
; rows first to blit_end - 1. Before each row it checks the raster line, and stops if the frame is
; nearly over (blit_stop_line up to line 204), so that view.c can draw ahead in the time left over
; from a frame without making the next step late: a check here takes a few instructions, where in C
; it took as long as the row. Returns the row it got to. There's no beam to race, so interrupts
; stay on.
; Each row's tiles are found here too, as working out 22 pointers in C took over a third of a
; frame: view row r is in cache slot blit_slot + r (wrapping round), and the slots are 512 bytes
; apart, so its tiles are at blit_tiles + slot * 512 - the same low byte, and the high byte
; plus slot * 2.

_view_blit_rows:
        sta     tmp1                    ; the row
        clc                             ; its cache slot
        adc     _blit_slot
        cmp     #CACHE_ROWS
        bcc     :+
        sbc     #CACHE_ROWS
:       sta     tmp2
@row:   lda     tmp1
        cmp     _blit_end
        bcs     @done
        lda     TED_LINE_HI             ; lines 256-311: the frame has only just started
        lsr     a
        bcs     @go
        lda     TED_LINE_LO
        cmp     #205                    ; so have lines 205-255
        bcs     @go
        cmp     _blit_stop_line
        bcs     @done                   ; too near the frame's end for another row

@go:    lda     _blit_tiles             ; the row's tiles
        sta     ptr1
        lda     tmp2
        asl     a
        clc
        adc     _blit_tiles+1
        sta     ptr1+1
        ldx     tmp1                    ; where they go
        clc
        lda     _blit_chars
        adc     row_offset_lo,x
        sta     ptr2
        lda     _blit_chars+1
        adc     row_offset_hi,x
        sta     ptr2+1
        clc
        lda     _blit_colours
        adc     row_offset_lo,x
        sta     ptr3
        lda     _blit_colours+1
        adc     row_offset_hi,x
        sta     ptr3+1
        ldy     #COLS-1
@cell:  lda     (ptr1),y                ; the tile
        tax
        lda     _view_tile_char,x
        sta     (ptr2),y
        lda     _view_tile_colour,x
        sta     (ptr3),y
        dey
        bpl     @cell
        inc     tmp1                    ; the next row, and its slot
        inc     tmp2
        lda     tmp2
        cmp     #CACHE_ROWS
        bcc     :+
        lda     #0
        sta     tmp2
:       jmp     @row

@done:  lda     tmp1
        ldx     #0
        rts

; ------------------------------------------------------------------------
; The TED interrupt handler. cc65's Plus/4 runtime calls every .interruptor on each interrupt,
; with RAM paged in, before passing the interrupt on to the KERNAL.
;
; The KERNAL has the TED interrupt at raster line 204 every frame, just below the screen, for its
; keyboard scan and clock (and at line 161, which this ignores); this uses the line-204 one too,
; rather than move it. There, it:
;   - switches to a newly drawn screen, if screen_present asked for one (ted_flip);
;   - sets the scroll registers for the top of the next frame: the status bar, unscrolled;
;   - starts TED timer 2 to interrupt again at line 22, in the blank row under the status bar.
; At timer 2's interrupt, it sets the scroll registers for the map below. The blank row hides
; the few lines the TED garbles when its vertical scroll changes mid-frame.
;
; The timer counts single clock cycles, 57 a raster line on a PAL machine.

        .export         _ted_top06, _ted_top07, _ted_map06, _ted_map07
        .export         _ted_want_x, _ted_want_y, _ted_flip, _ted_split, _ted_frames
        .interruptor    ted_irq

TED_IRQ_FLAGS   = $FF09                 ; which interrupts happened; writing 1s clears them
TED_TIMER2_LO   = $FF02                 ; timer 2: writing the high byte starts it counting down
TED_TIMER2_HI   = $FF03
TED_SCROLL_Y    = $FF06                 ; vertical scroll, 24/25 rows, display on
TED_SCROLL_X    = $FF07                 ; horizontal scroll, 38/40 columns, multicolour
TED_VIDEO       = $FF14                 ; the screen's address, top 5 bits
TED_RASTER_CMP  = $FF0B                 ; the raster line that raises the raster interrupt
TED_RASTER_HI   = $FF1C                 ; bit 0: bit 8 of the line the beam is on
TED_RASTER_LO   = $FF1D                 ; its low 8 bits
TED_CHAR_LINE   = $FF1F                 ; bits 0-2: which line of a character row it's drawing
RASTER_FLAG     = $02
TIMER2_FLAG     = $10

LINES_PER_FRAME = 312                   ; PAL; each line is 57 ticks of the timers
SPLIT_LINE      = 22                    ; inside the blank row, screen row 2 (lines 20-27)
IRQ_LINE        = 204                   ; the KERNAL's

        .bss

_ted_top06:     .res    1               ; register values for the status bar
_ted_top07:     .res    1
_ted_map06:     .res    1               ; ...and the map, before its scroll is added
_ted_map07:     .res    1
_ted_want_x:    .res    1               ; the map's scroll, 0-7, for the next frame
_ted_want_y:    .res    1
_ted_flip:      .res    1               ; $FF14 for a screen to switch to, or 0
_ted_split:     .res    1               ; non-zero while the map is on show
_ted_frames:    .res    1               ; counts frames, for waiting for the next one
shown_x:        .res    1               ; the scroll of the frame being shown
shown_y:        .res    1
lines:          .res    2               ; working out the timer's count: not zero page, which
ticks:          .res    2               ; belongs to whatever the interrupt interrupted

        .code

ted_irq:
        lda     TED_IRQ_FLAGS
        tax                             ; keep them for the timer check
        and     #RASTER_FLAG
        beq     not_bottom
        ; The KERNAL also raises a raster interrupt at line 161 ($A1), partway down the screen;
        ; only its line-204 one is the bottom of the frame. RASTER_CMP still holds the line that
        ; raised this one - the KERNAL only moves it on after us.
        lda     TED_RASTER_CMP
        cmp     #<IRQ_LINE
        beq     bottom
not_bottom:
        jmp     timer                   ; too far for a branch
bottom:

        ; The bottom of the frame. The timer first, timed from the raster line the beam has
        ; actually reached - the interrupt takes a varying while to get here, more if it waited
        ; out a FarMem copy with interrupts off - so the split lands on the same line each frame:
        ; lines to go = LINES_PER_FRAME + SPLIT_LINE - this line, and ticks = lines * 57, worked
        ; out as lines * (1 + 8 + 16 + 32).
        lda     _ted_split
        beq     flip
        sec
        lda     #<(LINES_PER_FRAME + SPLIT_LINE)
        sbc     TED_RASTER_LO
        sta     lines
        lda     TED_RASTER_HI
        and     #1                      ; bit 8 of the line
        sta     ticks+1
        lda     #>(LINES_PER_FRAME + SPLIT_LINE)
        sbc     ticks+1
        sta     lines+1
        lda     lines                   ; ticks = lines
        sta     ticks
        lda     lines+1
        sta     ticks+1
        ldy     #3                      ; + lines * 8, * 16 and * 32
        asl     lines
        rol     lines+1
        asl     lines
        rol     lines+1
@times: asl     lines
        rol     lines+1
        clc
        lda     ticks
        adc     lines
        sta     ticks
        lda     ticks+1
        adc     lines+1
        sta     ticks+1
        dey
        bne     @times
        lda     ticks
        sta     TED_TIMER2_LO
        lda     ticks+1
        sta     TED_TIMER2_HI

flip:   inc     _ted_frames
        lda     _ted_flip               ; a new screen to show?
        beq     :+
        sta     TED_VIDEO
        lda     #0
        sta     _ted_flip
:       lda     _ted_want_x             ; the next frame's map scroll, taken now so it changes
        sta     shown_x                 ; with the screen
        lda     _ted_want_y
        sta     shown_y

        lda     _ted_top06              ; the status bar (or whole text screen) unscrolled
        sta     TED_SCROLL_Y
        lda     _ted_top07
        sta     TED_SCROLL_X
        ; The raster flag is the KERNAL's to clear.

timer:  txa
        and     #TIMER2_FLAG
        beq     done
        sta     TED_IRQ_FLAGS           ; clear it: this interrupt is ours, not the KERNAL's
        lda     _ted_split
        beq     done
        lda     _ted_map06              ; the map's scroll, below the blank row
        ora     shown_y
        sta     TED_SCROLL_Y
        ; Changing the vertical scroll moves where the TED fetches each character row's colours
        ; and characters, but not its count of which line of the character it's on, so the
        ; characters would be drawn from the wrong line - and out of step with their colours.
        ; Set the count to match: a row's colours arrive where (line & 7) is the scroll + 1, and
        ; the count written takes effect from the next line, so it's (line - scroll) & 7.
        lda     TED_RASTER_LO
        sec
        sbc     shown_y
        and     #7
        sta     ticks                   ; (free here)
        lda     TED_CHAR_LINE
        and     #$F8                    ; the flash counter above it stays
        ora     ticks
        sta     TED_CHAR_LINE
        lda     _ted_map07
        ora     shown_x
        sta     TED_SCROLL_X

done:   clc
        rts

; ------------------------------------------------------------------------
; void __fastcall__ screen_copy_font (unsigned char* dst)
; Copies the ROM's 1K uppercase font, at $D000, to dst. cc65's Plus/4 programs run with the ROMs
; paged out, and the C runtime's stack is in the RAM under them, so C code can't run while
; they're paged in to read it.

        .export         _screen_copy_font

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
