; Tedide sample: border.s
;
; A tiny ca65 routine called from animation.c (see border.h) - advances the
; border/background color register by one step each call, so the border
; flickers in step with the bouncing character. Exists mainly to show a
; hand-written .s module sitting alongside the project's C ones, and to
; demonstrate per-target conditional assembly so the same source builds
; correctly for every Commodore machine cc65 targets.
;
; Register addresses come straight from cc65's own C headers (the structs
; behind the VIC/TED macros in c64.h, cbm510.h, cbm264.h, vic20.h) rather
; than guesswork:
;   C64/C128   - VIC-II  bordercolor          @ $D000 + $20 = $D020
;   CBM510     - VIC-II  bordercolor          @ $D800 + $20 = $D820
;   C16/Plus4  - TED     bordercolor          @ $FF00 + $19 = $FF19
;   VIC-20     - VIC-I   bg_border_color      @ $9000 + $0F = $900F
;   PET/CBM610 - monochrome, text-only CRTC video - no border concept.
;   GEOS       - a GEOS app shouldn't poke hardware registers behind the
;                OS/windowing system's back regardless of what's physically
;                at $D020, so this is a no-op there too (__C64__ isn't
;                defined for the geos-cbm target, so it falls out of every
;                branch below on its own).
; Any target without a border/background color register (or without one
; this routine knows about) leaves BORDER_REGISTER undefined, and
; border_flash() becomes a plain rts - safe on every cc65 target, not just
; the Commodore ones.
;
; cc65 prefixes C-visible symbols with an underscore, and a void, no-argument
; function needs no argument or return-value handling under its calling
; convention - just do the work and rts.

        .export _border_flash

.if .defined(__C64__) .or .defined(__C128__)
BORDER_REGISTER := $D020
.elseif .defined(__CBM510__)
BORDER_REGISTER := $D820
.elseif .defined(__C16__) .or .defined(__PLUS4__)
BORDER_REGISTER := $FF19
.elseif .defined(__VIC20__)
BORDER_REGISTER := $900F
.endif

.proc _border_flash: near
.ifdef BORDER_REGISTER
        inc     BORDER_REGISTER
.endif
        rts
.endproc
