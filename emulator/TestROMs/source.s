; vim: ft=asm_ca65

.include "famistudio_ca65.s"
.include "The Moon.s"

.segment "Program"

PPUCTRL      = $2000
PPUMASK      = $2001
PPUSTATUS    = $2002
DMC_MODE     = $4010
FRAMECOUNTER = $4017

.proc RESET
    sei
    ldx #$40
    stx FRAMECOUNTER
    ldx #$ff
    txs
    inx
    stx PPUCTRL
    ;stx PPUMASK
    stx DMC_MODE
    
    ;bit PPUSTATUS
    ;@VBlankWait1:
    ;    bit PPUSTATUS
    ;    bpl @VBlanmkWait1

;    txa
;    @ClearMemory:
;        sta $00,  x
;        sta $100, x
;        sta $200, x
;        sta $300, x
;        sta $400, x
;        sta $500, x
;        sta $600, x
;        sta $700, x
;        inx
;        bne @ClearMemory

    ldx #<music_data_duck_tales
    lda #1
    ldy #>music_data_duck_tales
    jsr famistudio_init
    lda #$00
    jsr famistudio_music_play
    ;@VBlankWait2:
        ;    bit PPUSTATUS
        ;    bpl @VBlankWait2

    lda #$80
    sta PPUCTRL
    jmp main
.endproc

.proc NMI
    jsr famistudio_update
    rti
.endproc

.proc main

    @loop:
        jmp @loop
.endproc

.segment "Vectors"
    .word NMI
    .word RESET
    .word $0000
