ClearScreen:
    LDA #$00
    STA $D020
    STA $D021
    TAX
@loop:
    STA $0400,X
    STA $0500,X
    INX
    BNE @loop
    RTS
