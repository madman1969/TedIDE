; Tedide sample: border.s
;
; A tiny ca65 routine called from animation.c (see border.h) - advances the
; VIC-II border color register by one step each call, so the border flickers
; in step with the bouncing character. Exists mainly to show a hand-written
; .s module sitting alongside the project's C ones in the Solution Explorer.
;
; cc65 prefixes C-visible symbols with an underscore, and a void, no-argument
; function needs no argument or return-value handling under its calling
; convention - just do the work and rts.

        .export _border_flash

VIC_BORDERCOLOR := $D020

.proc _border_flash: near
        inc     VIC_BORDERCOLOR
        rts
.endproc
