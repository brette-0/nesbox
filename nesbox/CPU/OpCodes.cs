using System.Reflection;
using System.Text.RegularExpressions;

namespace nesbox.CPU;
using static System;
using Memory = System.Memory;

// TODO: Add Branching, Implied Mode, Calling, Jumping instructions and verify all.

internal static class OpCodes {
    private static readonly Action[] OpCodeSolvers = [
        #region 0x00-0x10

        /* 00 brk #    */ () => {
            switch (cycle) {
                case 1:
                    ADL = (byte)(PC & 0xFF);
                    ADH = (byte)(PC >> 8);
                    DriveAddressPins();
                    Memory.Read();

                    PC++;
                    break;

                case 2:
                    Data = (byte)(PC >> 8);
                    Memory.Push();
                    break;

                case 3:
                    Data = (byte)(PC & 0xFF);
                    Memory.Push();
                    break;

                case 4:
                    var p =
                        (byte)((Register.c ? 1 : 0) << 0 |
                               (Register.z ? 1 : 0) << 1 |
                               (Register.i ? 1 : 0) << 2 |
                               (Register.d ? 1 : 0) << 3 |
                               (1 << 4)                  |
                               (1 << 5)                  |
                               (Register.v ? 1 : 0) << 6 |
                               (Register.n ? 1 : 0) << 7);
                    Data = p;
                    Memory.Push();

                    Register.i = true;
                    Register.b = true;
                    break;

                case 5:
                    ADL = 0xFE;
                    ADH = 0xFF;
                    DriveAddressPins();
                    Memory.Read();

                    DB = Data;
                    break;

                case 6:
                    ADL = 0xFF;
                    ADH = 0xFF;
                    DriveAddressPins();
                    Memory.Read();

                    PC    = (ushort)((Data << 8) | DB);
                    cycle = 0xff;
                    break;

                default:
                    Console.WriteLine("[CPU] Performed BRK on incorrect cycle");
                    Quit = true;
                    break;
            }
        },
        /* 01 ora *+x  */ () => IndexedIndirect(ORA),
        /* 02 jam      */ JAM,
        /* 03 slo *+x  */ () => IndexedIndirect(SLO),
        /* 04 nop d    */ () => DirectPage(() => { }),
        /* 05 ora d    */ () => DirectPage(ORA),
        /* 06 asl d    */ () => DirectPage(ASL),
        /* 07 slo d    */ () => DirectPage(SLO),
        /* 08 */ () => { },
        /* 09 ora #imm  */ () => Immediate(ORA),
        /* 0a asl       */ ASLA,
        /* 0b anc #imm  */ () => Immediate(ANC),
        /* 0c nop a     */ () => Absolute(() => { }),
        /* 0d ora a     */ () => Absolute(ORA),
        /* 0e asl a     */ () => Absolute(ASL),
        /* 0f slo a     */ () => Absolute(SLO),

        #endregion

        #region 0x10-0x20

        /* 10 bpl r   */ () => Branch(() => !Register.n),
        /* 11 ora *+y */ () => IndirectIndexed(ORA),
        /* 12 jam     */ JAM,
        /* 13 slo *+y */ () => IndirectIndexed(SLO),
        /* 14 nop d+x */ () => DirectPageIndexed(Register.X, () => { }),
        /* 15 ora d+x */ () => DirectPageIndexed(Register.X, ORA),
        /* 16 asl d+x */ () => DirectPageIndexed(Register.X, ASL),
        /* 17 slo d+x */ () => DirectPageIndexed(Register.X, SLO),
        /* 18 clc */ () => {
            Register.c = false;
            cycle      = 0xff;
        },
        /* 19 ora a+y */ () => AbsoluteIndexed(Register.Y, ORA),
        /* 1a nop     */ () => { },
        /* 1b slo a+y */ () => AbsoluteIndexed(Register.Y, SLO),
        /* 1c nop a+x */ () => AbsoluteIndexed(Register.X, () => { }),
        /* 1d ora a+x */ () => AbsoluteIndexed(Register.X, ORA),
        /* 1e asl a+x */ () => AbsoluteIndexed(Register.X, ASL),
        /* 1f slo a+x */ () => AbsoluteIndexed(Register.X, SLO),

        #endregion

        #region 0x20-0x30

        /* 20 jsr a   */ () => {
            switch (cycle) {
                case 1:
                    Address = PC;
                    Memory.Read();

                    DB = Data;
                    PC++;
                    break;

                case 2:
                    ADL = Register.S;
                    ADH = 0x01;
                    DriveAddressPins();
                    Memory.Read();
                    break;

                case 3:
                    Data = PCH;
                    Memory.Push();
                    break;

                case 4:
                    Data = PCL;
                    Memory.Push();
                    break;

                case 5:
                    Address = PC;
                    Memory.Read();

                    PCH   = Data;
                    PCL   = DB;
                    cycle = 0xff;
                    break;

                default:
                    Console.WriteLine("[CPU] Performed JSR absolute on incorrect cycle");
                    Quit = true;
                    break;

            }
        },
        /* 21 and *+x */ () => IndexedIndirect(AND),
        /* 22 jam     */ JAM,
        /* 23 slo *+x */ () => IndexedIndirect(SLO),
        /* 24 bit d   */ () => DirectPage(BIT),
        /* 25 and d   */ () => DirectPage(AND),
        /* 26 rol d   */ () => DirectPage(ROL),
        /* 27 rla d   */ () => DirectPage(RLA),
        /* 28 */ () => { },
        /* 29 and #   */ () => Immediate(AND),
        /* 2a rol     */ ROLA,
        /* 2b anc     */ () => Immediate(ANC),
        /* 2c bit a   */ () => Absolute(BIT),
        /* 2d and a   */ () => Absolute(AND),
        /* 2e rol a   */ () => Absolute(ROL),
        /* 2f rla a   */ () => Absolute(RLA),

        #endregion

        #region 0x30-0x40

        /* 30 bmi r   */ () => Branch(() => Register.n),
        /* 31 and *+y */ () => IndirectIndexed(AND),
        /* 32 jam     */ JAM,
        /* 33 rla *+d */ () => IndirectIndexed(RLA),
        /* 34 nop d+x */ () => DirectPageIndexed(Register.X, () => { }),
        /* 35 and d+x */ () => DirectPageIndexed(Register.X, AND),
        /* 36 rol d+x */ () => DirectPageIndexed(Register.X, ROL),
        /* 37 rla d+x */ () => DirectPageIndexed(Register.X, RLA),
        /* 38 sec */ () => {
            Register.c = true;
            cycle      = 0xff;
        },
        /* 39 and a+y */ () => AbsoluteIndexed(Register.Y, AND),
        /* 3a nop     */ () => { },
        /* 3b rla a+y */ () => AbsoluteIndexed(Register.Y, RLA),
        /* 3c nop a+x */ () => AbsoluteIndexed(Register.X, () => { }),
        /* 3d and a+x */ () => AbsoluteIndexed(Register.X, AND),
        /* 3e rol a+x */ () => AbsoluteIndexed(Register.X, ROL),
        /* 3f rla a+x */ () => AbsoluteIndexed(Register.X, RLA),

        #endregion

        #region 0x40-0x50

        /* 40 rti    */ () => {
            switch (cycle) {
                case 1:
                    Address = PC;
                    Memory.Read();
                    break;

                case 2:
                    ADH = 0x01;
                    ADL = ++Register.S;
                    
                    DriveAddressPins();
                    Memory.Read();

                    var p = Data;

                    Register.c = (p & 0x01) != 0;
                    Register.z = (p & 0x02) != 0;
                    Register.i = (p & 0x04) != 0;
                    Register.d = (p & 0x08) != 0;

                    Register.b = false;

                    Register.v = (p & 0x40) != 0;
                    Register.n = (p & 0x80) != 0;

                    break;

                case 3:
                    Register.S++;
                    ADL = Register.S;
                    ADH = 0x01;
                    DriveAddressPins();
                    Memory.Read();

                    DB = Data;
                    break;

                case 4:
                    Register.S++;
                    ADL = Register.S;
                    ADH = 0x01;
                    DriveAddressPins();
                    Memory.Read();
                    
                    PCH = Data;
                    PCL = DB;
                    break;

                case 5:
                    Address = PC;
                    Memory.Read();

                    cycle = 0xff;
                    break;

                default:
                    Console.WriteLine("[CPU] Performed RTI on incorrect cycle");
                    Quit = true;
                    break;
            }
        },
        /* 41 eor *+x */ () => IndexedIndirect(EOR),
        /* 42 jam     */ JAM,
        /* 43 sre *+x */ () => IndexedIndirect(SRE),
        /* 44 nop d   */ () => DirectPage(() => { }),
        /* 45 eor d   */ () => DirectPage(EOR),
        /* 46 lsr d   */ () => DirectPage(LSR),
        /* 47 sre d   */ () => DirectPage(SRE),
        /* 48 pha     */ () => {
            Data = Register.AC;
            Memory.Push();
            cycle = 0xff;
        },
        /* 49 eor #   */ () => Immediate(EOR),
        /* 4a lsr     */ LSRA,
        /* 4b alr #   */ () => Immediate(ALR),
        /* 4c jmp a   */ () => {
            switch (cycle) {
                case 1:
                    Address = PC;
                    Memory.Read();

                    ADL = Data;
                    PC++;
                    break;

                case 2:
                    Address = PC;
                    Memory.Read();

                    ADH = Data;
                    DriveAddressPins();
                    PC = Address;
                    
                    cycle = 0xff;
                    break;


                default:
                    Console.WriteLine("[CPU] Performed JMP absolute on incorrect cycle");
                    Quit = true;
                    break;
            }
        },
        /* 4d eor a   */ () => Absolute(EOR),
        /* 4e lsr a   */ () => Absolute(LSR),
        /* 4f sre a   */ () => Absolute(SRE),

        #endregion

        #region 0x50-0x60

        /* 50 bvc r   */ () => Branch(() => !Register.v),
        /* 51 eor *+y */ () => IndirectIndexed(EOR),
        /* 52 jam     */ JAM,
        /* 53 sre *+y */ () => IndirectIndexed(SRE),
        /* 54 nop d+x */ () => DirectPageIndexed(Register.X, () => { }),
        /* 55 eor d+x */ () => DirectPageIndexed(Register.X, EOR),
        /* 56 lsr d+x */ () => DirectPageIndexed(Register.X, LSR),
        /* 57 sre d+x */ () => DirectPageIndexed(Register.X, SRE),
        /* 58 cli */ () => {
            Register.i = false;
            cycle      = 0xff;
        },
        /* 59 eor a+y */ () => AbsoluteIndexed(Register.Y, EOR),
        /* 5a nop     */ () => { },
        /* 5b sre a+y */ () => AbsoluteIndexed(Register.Y, SRE),
        /* 5c nop a+x */ () => AbsoluteIndexed(Register.X, () => { }),
        /* 5d eor a+x */ () => AbsoluteIndexed(Register.X, EOR),
        /* 5e lsr a+x */ () => AbsoluteIndexed(Register.X, LSR),
        /* 5f sre a+x */ () => AbsoluteIndexed(Register.X, SRE),

        #endregion

        #region 0x60-0x70

        /* 60 rts     */ () => {
            switch (cycle) {
                case 1:
                    Address = PC;
                    Memory.Read();
                    break;

                case 2:
                    ADL = Register.S;
                    ADH = 0x01;
                    DriveAddressPins();
                    Memory.Read();
                    break;

                case 3:
                    Register.S++;
                    ADL = Register.S;
                    ADH = 0x01;
                    DriveAddressPins();
                    Memory.Read();

                    PC = (ushort)((Data << 8) | DB);
                    break;

                case 4:
                    Register.S++;
                    ADL = Register.S;
                    ADH = 0x01;
                    DriveAddressPins();
                    Memory.Read();
                    PCL = DB;
                    PCH = Data;
                    break;
                
                case 5:
                    PC++;
                    break;

                case 6:
                    Address = PC;
                    Memory.Read();
                    cycle = 0xff;
                    break;

                default:
                    Console.WriteLine("[CPU] Performed RTS on incorrect cycle");
                    Quit = true;
                    break;
            }
        },
        /* 61 ora *+x */ () => IndirectIndexed(ORA),
        /* 62 jam     */ JAM,
        /* 63 slo *+x */ () => IndirectIndexed(SLO),
        /* 64 nop d   */ () => DirectPage(() => { }),
        /* 65 adc d   */ () => DirectPage(ADC),
        /* 66 ror d   */ () => DirectPage(ROR),
        /* 67 rra d   */ () => DirectPage(RRA),
        /* 68 pla     */ () => {
            Memory.Pull();
            Register.AC = Data;
            cycle       = 0xff;
        },
        /* 69 adc #   */ () => Immediate(ADC),
        /* 6a ror     */ RORA,
        /* 6b arr #   */ () => Immediate(ARR),
        /* 6c jmp *   */ () => {
            switch (cycle) {
                case 1:
                    ADL = (byte)(PC & 0xFF);
                    ADH = (byte)(PC >> 8);
                    DriveAddressPins();
                    Memory.Read();

                    ADL = Data;
                    PC++;
                    break;

                case 2:
                    PCL = (byte)(PC & 0xFF);
                    PCH = (byte)(PC >> 8);

                    var ptrLow = ADL;

                    ADL = PCL;
                    ADH = PCH;
                    DriveAddressPins();
                    Memory.Read();

                    ADL = ptrLow;
                    ADH = Data;

                    PC++;
                    break;

                case 3:
                    DriveAddressPins();
                    Memory.Read();

                    DB = Data;
                    break;

                case 4:
                    ADL = (byte)(ADL + 1);

                    DriveAddressPins();
                    Memory.Read();

                    PC    = (ushort)((Data << 8) | DB);
                    cycle = 0xff;
                    break;

                default:
                    Console.WriteLine("[CPU] Performed JMP indirect on incorrect cycle");
                    Quit = true;
                    break;
            }
        },
        /* 6d adc a   */ () => Absolute(ADC),
        /* 6e ror a   */ () => Absolute(ROR),
        /* 6f rra a   */ () => Absolute(RRA),

        #endregion

        #region 0x70-0x80

        /* 70 bvs r   */ () => Branch(() => Register.v),
        /* 71 adc *+y */ () => IndirectIndexed(ADC),
        /* 72 jam     */ JAM,
        /* 73 sre *+y */ () => IndirectIndexed(SRE),
        /* 74 nop d+x */ () => DirectPageIndexed(Register.X, () => { }),
        /* 75 adc d+x */ () => DirectPageIndexed(Register.X, ADC),
        /* 76 ror d+x */ () => DirectPageIndexed(Register.X, ROR),
        /* 77 rra d+x */ () => DirectPageIndexed(Register.X, RRA),
        /* 78 sei     */ () => {
            Register.i = true;
            cycle      = 0xff;
        },
        /* 79 adc a+y */ () => AbsoluteIndexed(Register.Y, ADC),
        /* 7a nop     */ () => { },
        /* 7b rra a+y */ () => AbsoluteIndexed(Register.Y, RRA),
        /* 7c nop a+x */ () => AbsoluteIndexed(Register.X, () => { }),
        /* 7d abs a+x */ () => AbsoluteIndexed(Register.X, ADC),
        /* 7e ror a+x */ () => AbsoluteIndexed(Register.X, ROR),
        /* 7f rra a+x */ () => AbsoluteIndexed(Register.X, RRA),

        #endregion

        #region 0x80-0x90

        /* 80 nop #   */ () => Immediate(() => { }),
        /* 81 sta *+y */ () => IndirectIndexed(STA),
        /* 82 nop #   */ () => Immediate(() => { }),
        /* 83 sax *+x */ () => IndexedIndirect(SAX),
        /* 84 sty d   */ () => DirectPage(STY),
        /* 85 sta d   */ () => DirectPage(STA),
        /* 86 stx d   */ () => DirectPage(STX),
        /* 87 sax d   */ () => DirectPage(SAX),
        /* 88 dey     */ () => {
            Register.Y--;
            cycle = 0xff;
            NonArithmeticProcessorFlagSets(Register.Y);
        },
        /* 89 nop #   */ () => Immediate(() => { }),
        /* 8a txa     */ () => {
            Register.AC = Register.X;
            cycle       = 0xff;
            NonArithmeticProcessorFlagSets(Register.AC);
        },
        /* 8b xaa #   */ () => Immediate(XAA),
        /* 8c sty a   */ () => Absolute(STY),
        /* 8d sta a   */ () => Absolute(STA),
        /* 8e stx a   */ () => Absolute(STX),
        /* 8f sax a   */ () => Absolute(SAX),

        #endregion

        #region 0x90-0xa0

        /* 90 bcc     */ () => Branch(() => !Register.c),
        /* 91 sta *+y */ () => IndirectIndexed(STA),
        /* 92 jam     */ JAM,
        /* 93 sha *+y */ () => IndirectIndexed(SHA),
        /* 94 sty d+x */ () => DirectPageIndexed(Register.X, STY),
        /* 95 sta d+x */ () => DirectPageIndexed(Register.X, STA),
        /* 96 stx d+y */ () => DirectPageIndexed(Register.Y, STX),
        /* 97 sax d+y */ () => DirectPageIndexed(Register.Y, SAX),
        /* 98 tya     */ () => {
            Register.AC = Register.Y;
            cycle       = 0xff;
            NonArithmeticProcessorFlagSets(Register.AC);
        },
        /* 99 sta a+y */ () => AbsoluteIndexed(Register.Y, STA),
        /* 9a txs     */ () => {
            Register.S = Register.X;
            cycle      = 0xff;
        },
        /* 9b tas a+y */ () => AbsoluteIndexed(Register.Y, TAS),
        /* 9c shy a+x */ () => AbsoluteIndexed(Register.X, SHY),
        /* 9d sta a+x */ () => AbsoluteIndexed(Register.X, STA),
        /* 9e shx a+y */ () => AbsoluteIndexed(Register.Y, SHX),
        /* 9f sha a+y */ () => AbsoluteIndexed(Register.Y, SHA),

        #endregion

        #region 0xa0-0xb0

        /* a0 ldy #imm */ () => Immediate(LDY),
        /* a1 lda *+x  */ () => IndexedIndirect(LDA),
        /* a2 ldx #imm */ () => Immediate(LDX),
        /* a3 lax *+x  */ () => IndexedIndirect(LAX),
        /* a4 ldy d    */ () => DirectPage(LDY),
        /* a5 lda d    */ () => DirectPage(LDA),
        /* a6 ldx d    */ () => DirectPage(LDX),
        /* a7 lax d    */ () => DirectPage(LAX),
        /* a8 tay      */ () => {
            Register.Y = Register.AC;
            cycle      = 0xff;
            NonArithmeticProcessorFlagSets(Register.Y);
        },
        /* a9 lda #imm */ () => Immediate(LDA),
        /* aa tax      */ () => {
            Register.X = Register.AC;
            cycle      = 0xff;
            NonArithmeticProcessorFlagSets(Register.X);
        },
        /* ab lax #imm */ () => Immediate(LAXI),
        /* ac ldy a    */ () => Absolute(LDY),
        /* ad lda a    */ () => Absolute(LDA),
        /* ae ldx a    */ () => Absolute(LDX),
        /* af lax a    */ () => Absolute(LAX),

        #endregion

        #region 0xb0-0xc0

        /* b0 bcs r   */ () => Branch(() => Register.c),
        /* b1 lda *+y */ () => IndirectIndexed(LDA),
        /* b2 jam     */ JAM,
        /* b3 lax a+y */ () => IndirectIndexed(LAX),
        /* b4 ldy d+x */ () => DirectPageIndexed(Register.X, LDY),
        /* b5 lda d+x */ () => DirectPageIndexed(Register.X, LDA),
        /* b6 ldx d+y */ () => DirectPageIndexed(Register.Y, LDX),
        /* b7 lax d+y */ () => DirectPageIndexed(Register.Y, LAX),
        /* b8 clv     */ () => {
            Register.v = false;
            cycle      = 0xff;
        },
        /* b9 lda a,x */ () => AbsoluteIndexed(Register.X, LDA),
        /* ba tsx     */ () => {
            Register.X = Register.S;
            cycle      = 0xff;
            NonArithmeticProcessorFlagSets(Register.X);
        },
        /* bb las a,y */ () => AbsoluteIndexed(Register.Y, LAS),
        /* bc ldy a,x */ () => AbsoluteIndexed(Register.X, LDY),
        /* bd lda a,x */ () => AbsoluteIndexed(Register.X, LDA),
        /* be ldx a,y */ () => AbsoluteIndexed(Register.Y, LDX),
        /* bf lax a,y */ () => AbsoluteIndexed(Register.Y, LAX),

        #endregion

        #region 0xc0-0xd0

        /* c0 cpy #   */ () => Immediate(CPY),
        /* c1 cmp *+x */ () => IndexedIndirect(CMP),
        /* c2 nop #   */ () => Immediate(() => { }),
        /* c3 dcp *+x */ () => IndexedIndirect(DCP),
        /* c4 cpy d   */ () => DirectPage(CPY),
        /* c5 cmp d   */ () => DirectPage(CMP),
        /* c6 dec d   */ () => DirectPage(DEC),
        /* c7 dcp d   */ () => DirectPage(DCP),
        /* c8 iny     */ () => {
            Register.Y++;
            cycle = 0xff;
            NonArithmeticProcessorFlagSets(Register.Y);
        },
        /* c9 cmp #   */ () => Immediate(CMP),
        /* ca dex     */ () => {
            Register.X--;
            cycle = 0xff;
            NonArithmeticProcessorFlagSets(Register.X);
        },
        /* cb axs #   */ () => Immediate(AXS),
        /* cc cpy a   */ () => Absolute(CPY),
        /* cd cmp a   */ () => Absolute(CMP),
        /* ce dec a   */ () => Absolute(DEC),
        /* cf dcp a   */ () => Absolute(DCP),

        #endregion

        #region 0xd0-0xe0

        /* d0 bne r   */ () => Branch(() => !Register.z),
        /* d1 cmp *+y */ () => IndexedIndirect(CMP),
        /* d2 jam     */ JAM,
        /* d3 dcp *+y */ () => IndexedIndirect(DCP),
        /* d4 nop d+x */ () => DirectPageIndexed(Register.X, () => { }),
        /* d5 cmp d+x */ () => DirectPageIndexed(Register.X, CMP),
        /* d6 dec d+x */ () => DirectPageIndexed(Register.X, DEC),
        /* d7 dcp d+x */ () => DirectPageIndexed(Register.X, DCP),
        /* d8 cld     */ () => {
            Register.d = false;
            cycle      = 0xff;
        },
        /* d9 cmp a+y */ () => AbsoluteIndexed(Register.Y, CMP),
        /* da nop     */ () => { },
        /* db dcp a+y */ () => AbsoluteIndexed(Register.Y, DCP),
        /* dc nop a,x */ () => AbsoluteIndexed(Register.X, () => { }),
        /* dd cmp a+x */ () => AbsoluteIndexed(Register.X, CMP),
        /* de dec a+x */ () => AbsoluteIndexed(Register.X, DEC),
        /* df dcp a+x */ () => AbsoluteIndexed(Register.X, DCP),

        #endregion

        #region 0xe0-0xf0

        /* e0 cpx #   */ () => Immediate(CPX),
        /* e1 sbc *+x */ () => IndexedIndirect(SBC),
        /* e2 nop #   */ () => Immediate(() => { }),
        /* e3 isc *+x */ () => IndexedIndirect(ISC),
        /* e4 cpx d   */ () => DirectPage(CPX),
        /* e5 sbc d   */ () => DirectPage(SBC),
        /* e6 inc d   */ () => DirectPage(INC),
        /* e7 isc d   */ () => DirectPage(ISC),
        /* e8 inx     */ () => {
            Register.X++;
            cycle = 0xff;
            NonArithmeticProcessorFlagSets(Register.X);
        },
        /* e9 sbc #   */ () => Immediate(SBC),
        /* ea nop     */ () => { },
        /* eb sbc #   */ () => Immediate(SBC),
        /* ec cpx a   */ () => Absolute(CPX),
        /* ed sbc a   */ () => Absolute(SBC),
        /* ee inc a   */ () => Absolute(INC),
        /* ef isc a   */ () => Absolute(ISC),

        #endregion

        #region 0xf0-

        /* f0 beq r   */ () => Branch(() => Register.z),
        /* f1 sbc *+y */ () => IndirectIndexed(SBC),
        /* f2 jam     */ JAM,
        /* f3 isc *+y */ () => IndirectIndexed(ISC),
        /* f4 nop d+x */ () => DirectPageIndexed(Register.X, () => { }),
        /* f5 sbc d+x */ () => DirectPageIndexed(Register.X, SBC),
        /* f6 inc d+x */ () => DirectPageIndexed(Register.X, INC),
        /* f7 isc d+x */ () => DirectPageIndexed(Register.X, ISC),
        /* f8 sed     */ () => {
            Register.d = true;
            cycle      = 0xff;
        },
        /* f9 sbc a+y */ () => AbsoluteIndexed(Register.Y, SBC),
        /* fa nop     */ () => { },
        /* fb isc a+y */ () => AbsoluteIndexed(Register.Y, ISC),
        /* fc nop a,x */ () => AbsoluteIndexed(Register.X, () => { }),
        /* fd sbc a+x */ () => AbsoluteIndexed(Register.X, SBC),
        /* fe inc a+x */ () => AbsoluteIndexed(Register.X, INC),
        /* ff isc a+x */ () => AbsoluteIndexed(Register.X, ISC),

        #endregion
    ];

    internal static Action GetOpcodeSolver(byte opcode) => OpCodeSolvers[opcode];



    #region Instruction Interfaces

    private static void BIT() {
        Register.z = (byte)(Register.AC & Data) == 0;
        Register.n = (Data & 0x80)              != 0;
        Register.v = (Data & 0x40)              != 0;
    }

    private static void Branch(Func<bool> condition) {
        switch (cycle) {
            case 1:
                ADL = (byte)(PC & 0xFF);
                ADH = (byte)(PC >> 8);
                DriveAddressPins();
                Memory.Read();

                DB = Data;
                PC++;

                if (!condition()) cycle = 0xff;
                break;

            case 2:
                // Taken → dummy read at current PC
                ADL = (byte)(PC & 0xFF);
                ADH = (byte)(PC >> 8);
                DriveAddressPins();
                Memory.Read();
                break;

            case 3:
                var rel = (sbyte)DB;
                var sum = (ushort)(PCL + rel);

                ADL = (byte)sum;
                ADH = PCH;

                DriveAddressPins();
                Memory.Read();

                PC = (ushort)((PC & 0xFF00) | ADL);

                if ((sum & 0x100) == 0) cycle = 0xff;
                break;

            case 4:
                ADH++;
                DriveAddressPins();
                Memory.Read();

                PC    = (ushort)((ADH << 8) | ADL);
                cycle = 0xff;
                break;

            default:
                Console.WriteLine("[CPU] Performed Branch on incorrect cycle");
                Quit = true;
                break;
        }
    }

    internal enum RWKind : byte {
        Read,
        Write,
        RMW
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    internal sealed class RWKindAttribute : Attribute {
        public RWKind Kind { get; }
        public RWKindAttribute(RWKind kind) => Kind = kind;
    }

    [RWKind(RWKind.Read)]
    private static void CMP() {
        var diff = (short)(Register.AC - Data);
        Register.c   = diff               >= 0;
        NonArithmeticProcessorFlagSets(Register.AC);
    }

    [RWKind(RWKind.Read)]
    private static void CPX() {
        var diff = (short)(Register.X - Data);
        Register.c   = diff               >= 0;
        NonArithmeticProcessorFlagSets(Register.X);
    }

    [RWKind(RWKind.Read)]
    private static void CPY() {
        var diff = (short)(Register.Y - Data);
        Register.c   = diff               >= 0;
        NonArithmeticProcessorFlagSets(Register.Y);
    }

    [RWKind(RWKind.RMW)]
    private static void DEC() {
        Data--;
        NonArithmeticProcessorFlagSets(Data);
    }

    [RWKind(RWKind.RMW)]
    private static void INC() {
        Data++;
        NonArithmeticProcessorFlagSets(Data);
    }

    [RWKind(RWKind.Read)] private static void STA() => Data = Register.AC;
    [RWKind(RWKind.Read)] private static void STX() => Data = Register.X;
    [RWKind(RWKind.Read)] private static void STY() => Data = Register.Y;

    [RWKind(RWKind.RMW)]
    private static void SLO() {
        ASL();
        ORA();
    }
    
    [RWKind(RWKind.RMW)]
    private static void RLA() {
        ROL();
        AND();
    }

    [RWKind(RWKind.RMW)]
    private static void SRE() {
        LSR();
        EOR();
    }

    [RWKind(RWKind.RMW)]
    private static void RRA() {
        ROR();
        ADC();
    }

    [RWKind(RWKind.Write)]
    private static void SAX() => Data = (byte)(Register.AC & Register.X);

    [RWKind(RWKind.RMW)]
    private static void DCP() {
        DEC();
        CMP();
    }

    [RWKind(RWKind.RMW)]
    private static void ISC() {
        INC();
        SBC();
    }

    [RWKind(RWKind.Write)] private static void SHA() => Data = (byte)(Register.AC & Register.X & (1 + (Address >> 8)));
    [RWKind(RWKind.Write)] private static void SHX() => Data = (byte)(Register.X  & (1              + (Address >> 8)));
    [RWKind(RWKind.Write)] private static void SHY() => Data = (byte)(Register.Y  & (1              + (Address >> 8)));

    [RWKind(RWKind.Write)] private static void TAS() {
        Data       = (byte)(Register.AC & Register.X & (1 + (Address >> 8)));
        Register.S = (byte)(Register.AC & Register.X);
    }
   
    [RWKind(RWKind.RMW)] 
    private static void ROR() {
        var c      = (byte)(Data & 1);
        Data       = (byte)((Register.c ? 0x80 : 0x00) | (Data >> 1));
        Register.c = c is 1;
        NonArithmeticProcessorFlagSets(Data);
    }
    
    [RWKind(RWKind.RMW)] 
    private static void ROL() {
        var c      = (byte)(Data                         >> 7);
        Data       = (byte)((Register.c ? 1 : 0) | (Data << 1));
        Register.c = c is 1;
        NonArithmeticProcessorFlagSets(Data);
    }

    [RWKind(RWKind.RMW)] 
    private static void LSR() {
        Register.c =   (Data & 1) is 1;
        Data       >>= 1;
        NonArithmeticProcessorFlagSets(Data);
    }
    
    [RWKind(RWKind.RMW)] 
    private static void ASL() {
        Register.c =   (Data & 0x80) is 0x80;
        Data       <<= 1;
        NonArithmeticProcessorFlagSets(Data);
    }

    [RWKind(RWKind.Read)] 
    private static void ADC() {
        var c   = Register.c ? 1 : 0;
        var sum = (ushort)(Register.AC + Data + c);

        var (sa, sc) = (Register.AC > 0x7f, Data > 0x7f);
        
        Register.c  = sum > 0xff;
        Register.AC = (byte)sum;
        Register.v  = Register.AC > 0x7f == sa == sc;
        NonArithmeticProcessorFlagSets(Register.AC);
    }

    [RWKind(RWKind.Read)] 
    private static void SBC() {
        var diff = (short)(Register.AC - Data - (Register.c ? 0 : 1));
        var (sa, sc) = (Register.AC > 0x7f, Data > 0x7f);
        Register.AC  = (byte)diff;
        Register.c   = diff               >= 0;
        Register.v   = Register.AC > 0x7f == sa == sc;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    [RWKind(RWKind.Read)] 
    private static void XAA() {
        NonArithmeticProcessorFlagSets(Register.AC);
        Register.AC = (byte)((Register.AC | Random.Shared.Next()) & Register.X & Data);
    }

    [RWKind(RWKind.Read)] 
    private static void LAXI() {
        NonArithmeticProcessorFlagSets(Register.AC);
        Register.AC = Register.X = (byte)((Register.AC | Random.Shared.Next()) & Data);
    }

    // does not need method attribute, would never be accessed
    private static void JAM() {
        Console.WriteLine("[CPU] Encountered CPU Jam.");
        Quit = true;
    }
    
    [RWKind(RWKind.Read)] 
    private static void ANC() {
        AND();
        Register.c = Register.n;
    }

    [RWKind(RWKind.Read)] 
    private static void ALR() {
        AND();
        LSRA();
    }
    
    // does not need method attribute, would never be accessed
    private static void LSRA() {
        Register.c  =   (Register.AC & 1) is 1;
        Register.AC >>= 1;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    // does not need method attribute, would never be accessed
    private static void ASLA() {
        Register.c  =   (Register.AC & 0x80) is 0x80;
        Register.AC <<= 1;
        NonArithmeticProcessorFlagSets(Register.AC);
    }

    [RWKind(RWKind.Read)]
    private static void ARR() {
        AND();
        Register.c = Data >> 7 is 1;
        Register.v = ((Data >> 7) ^ (Data >> 6) & 1) is 1;
        RORA();
    }

    [RWKind(RWKind.Read)]
    private static void AXS() {
        Register.X ^= Register.AC;
        Register.X -= Data;
        NonArithmeticProcessorFlagSets(Register.X);
    }

    [RWKind(RWKind.Read)]
    private static void LAS() {
        Register.AC = Register.X = Register.S = (byte)(Data & Register.S);
        NonArithmeticProcessorFlagSets(Data);
    }

    // does not need method attribute, would never be accessed
    private static void RORA() {
        var c      = (byte)(Register.AC & 1);
        Register.AC = (byte)((Register.c ? 0x80 : 0x00) | (Register.AC >> 1));
        Register.c  = c is 1;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    // does not need method attribute, would never be accessed
    private static void ROLA() {
        var c      = (byte)(Register.AC                          >> 7);
        Register.AC = (byte)((Register.c ? 1 : 0) | (Register.AC << 1));
        Register.c  = c is 1;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    [RWKind(RWKind.Read)]
    private static void ORA() {
        Register.AC |= Data;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    [RWKind(RWKind.Read)]
    private static void AND() {
        Register.AC &= Data;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    [RWKind(RWKind.Read)]
    private static void EOR() {
        Register.AC &= Data;
        NonArithmeticProcessorFlagSets(Register.AC);
    }

    [RWKind(RWKind.Read)]
    private static void LDA() {
        Register.AC = Data;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    [RWKind(RWKind.Read)]
    private static void LDX() {
        Register.X = Data;
        NonArithmeticProcessorFlagSets(Register.X);
    }
    
    [RWKind(RWKind.Read)]
    private static void LDY() {
        Register.Y = Data;
        NonArithmeticProcessorFlagSets(Register.Y);
    }

    [RWKind(RWKind.Read)]
    private static void LAX() {
        Register.AC = Register.X = Data;
        NonArithmeticProcessorFlagSets(Register.X);
    }
    
    private static void NonArithmeticProcessorFlagSets(byte ctx) {
        Register.z = ctx is 0;
        Register.n = ctx > 0x79;
    }

    private static void AbsoluteIndexed(byte reg, Action ctx) {
        var    kind = GetOpType(ref ctx);
        Action post = kind is RWKind.Read ? EndRead : EndRest;

        
        switch (cycle) {
            case 1:
                Address = PC;
                Memory.Read();
                
                ADL  = Data;
                PC++;
                break;
            
            case 2:
                Address = PC;
                Memory.Read();
                ADH = Data;
                PC++;
                break;
            
            case 3:
                var sum = ADL + reg;
                ADL  = (byte)sum;

                DriveAddressPins();
                Memory.Read();
                
                switch (kind) {
                    case RWKind.Write:
                    case RWKind.RMW:
                        break;
                    
                    case RWKind.Read:
                        if (sum < 0x100) goto complete;
                        break;
                    
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                
                break;

            case 4:
                ADH++;

                switch (kind) {
                    case RWKind.Write: goto complete;
                    case RWKind.Read:
                        DriveAddressPins();
                        Memory.Read();
                        goto complete;
                    
                    case RWKind.RMW:
                        DriveAddressPins();
                        Memory.Read();
                        break;
                    
                    default:
                        throw new ArgumentException();
                }
                
                break;
            
            case 5: 
                if (kind is RWKind.RMW) goto complete;
                DriveAddressPins();
                Memory.Write();
                break;
                
            case 6: 
                if (kind is RWKind.RMW) goto complete;
                goto default;
            
            default:
                Console.WriteLine("[CPU] Performed Absolute Indexed on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                ctx();
                post();
                break;
        }
    }

    private static void DirectPageIndexed(byte reg, Action ctx) {
        var    kind = GetOpType(ref ctx);
        Action post = kind is RWKind.Read ? EndRead : EndRest;

        
        switch (cycle) {
            case 1:
                Address = PC;
                DriveAddressPins();
                Memory.Read();
                
                DB   = Data;
                PC++;
                break;
            
            case 2:
                ADL  = DB;
                ADH  = 0x00;
                DriveAddressPins();
                Memory.Read();


                ADL = (byte)(DB + reg);
                break;
            
            case 3:
                switch (kind) {
                    case RWKind.Read:
                        DriveAddressPins();
                        Memory.Read();
                        goto complete;
                        
                    case RWKind.Write: goto complete;
                        
                    case RWKind.RMW:
                        DriveAddressPins();
                        Memory.Read();
                        break;
                    
                    default:
                        throw new ArgumentOutOfRangeException(
                            $"Instruction Type {nameof(RWKind)} is invalid.");
                }
                break;
            
            case 4:
                if (kind is not RWKind.RMW) goto default;
                DriveAddressPins();
                Memory.Write();
                break;
            
            case 5:
                if (kind is not RWKind.RMW) goto default;
                goto complete;
            
            
            default:
                Console.WriteLine("[CPU] Performed Direct Page Indexed on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                ctx();
                post();
                break;
        }
    }

    private static RWKind GetOpType(ref Action ctx) => 
        ctx.Method.GetCustomAttribute<RWKindAttribute>()?.Kind ?? RWKind.Read;
    
    private static void Absolute(Action ctx) {
        var    kind = GetOpType(ref ctx);
        Action post = kind is RWKind.Read ? EndRead : EndRest;
        
        switch (cycle) {
            case 1:
                Address = PC;
                DriveAddressPins();
                Memory.Read();
                
                ADL  = Data;
                PC++;
                break;
            
            case 2:
                Address = ++PC;
                Memory.Read();
                
                ADH  = Data;
                PC++;
                break;
            
            case 3:
                DriveAddressPins();
                Memory.Read();

                if (kind is RWKind.RMW) break;
                goto complete;
            
            case 4:
                if (kind is not RWKind.RMW) goto default;
                DriveAddressPins();
                Memory.Write();
                break;
            
            case 5:
                if (kind is not RWKind.RMW) goto default;
                goto complete;
            
            default:
                Console.WriteLine("[CPU] Performed Absolute read on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                ctx();
                post();
                break;
        }
    }

    private static void EndRead()  => cycle = 0xff;

    private static void EndRest() {
        DriveAddressPins();
        Memory.Write();
        cycle = 0xff;
    }

    private static void DirectPage(Action ctx) {
        var    kind = GetOpType(ref ctx);
        Action post = kind is RWKind.Read ? EndRead : EndRest;
        
        switch (cycle) {
            case 1:
                Address = PC;
                Memory.Read();
                
                ADL = Data;
                ADH = 0x00;
                PC++;
                break;
            
            case 2:
                DriveAddressPins();
                Memory.Read();
                
                switch (kind) {
                    case RWKind.Read:  goto complete;
                    case RWKind.Write: goto complete;
                    case RWKind.RMW:   break;

                    default:
                        throw new ArgumentOutOfRangeException($"Instruction Type {nameof(RWKind)} is invalid.");
                }

                goto complete;
            
            default:
                Console.WriteLine("[CPU] Performed Direct Page read on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                ctx();
                post();
                break;
        }
    }

    private static void Immediate(Action ctx) {
        #if DEBUG
        if (GetOpType(ref ctx) is not RWKind.Read) {
            throw new ArgumentException($"Action {nameof(ctx)} does not support Immediate Memory Addressing");
        }
        #endif
        switch (cycle) {
            case 1:
                ADL = PCL;
                ADH = PCH;

                DriveAddressPins();
                Memory.Read();
                PC++;
                ctx();
                cycle = 0xff;
                break;
            
            default:
                Console.WriteLine($"[CPU] Performed Immediate read on incorrect cycle {cycle}");
                Quit = true;
                break;
        }
    }

    
    private static void IndirectIndexed(Action ctx) {
        var    kind = GetOpType(ref ctx);
        Action post = kind is RWKind.Read ? EndRead : EndRest;
        
        switch (cycle){
            case 1:
                Address = PC;
                DriveAddressPins();
                Memory.Read();
                DB = Data;                
                PC++;
                break;

            case 2:
                Address = DB;
                Memory.Read();
                
                ADL     = Data;
                break;

            case 3:
                ADL = (byte)(DB + 1);
                ADH = Data;

                DriveAddressPins();
                Memory.Read();
                ADH  = Data;
                break;

            case 4:
                var sum = ADL + Register.Y;
                ADL         = (byte)sum;

                DriveAddressPins();
                Memory.Read();

                switch (kind) {
                    case RWKind.Write:
                    case RWKind.RMW:
                        break;
                    
                    case RWKind.Read:
                        if (sum > 0xff) break;
                        goto complete;
                        
                    default: throw new ArgumentException();    
                }
                break;
            
            case 5:
                ADH++;

                switch (kind) {
                    case RWKind.Write: goto complete;
                    case RWKind.RMW:
                        DriveAddressPins();
                        Memory.Read();
                        break;
                    
                    case RWKind.Read:
                        DriveAddressPins();
                        Memory.Read();
                        goto complete;
                        
                    default: throw new ArgumentException();    
                }
                
                break;

             case 6:
                 if (kind is not RWKind.RMW) goto default;
                 DriveAddressPins();
                 Memory.Write();
                 break;
             
             case 7:
                 if (kind is not RWKind.RMW) goto default;
                 goto complete;
                 
            default:
                Console.WriteLine("[CPU] Performed Indirect Indexed read on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                ctx();
                post();
                break;
        }
    }

    private static void IndexedIndirect(Action ctx) {
        var kind = GetOpType(ref ctx);
        Action post = kind is RWKind.Read ? EndRead : EndRest;
        
        switch (cycle) {
            case 1:
                Address = PC;
                DriveAddressPins();
                Memory.Read();
                
                DB   = Data;
                PC++;
                break;
                
            case 2:
                Address = DB;
                Memory.Read();
                break;
                
            case 3:
                Address = (byte)(DB + Register.X);
                Memory.Read();

                ADH = Data;
                break;
                
            case 4:
                Address  = (byte)(DB + Register.X + 1);
                Memory.Read();

                ADH  = Data;
                break;
                
            case 5:
                switch (kind) {
                    case RWKind.RMW:
                        DriveAddressPins();
                        Memory.Read();
                        break;
                    
                    case RWKind.Write:
                        goto complete;
                        
                    case RWKind.Read:
                        DriveAddressPins();
                        Memory.Read();
                        goto complete;
                        
                    default: throw new ArgumentOutOfRangeException();
                }
                
                break;
            
            case 6:
                if (kind is not RWKind.RMW) goto default;
                DriveAddressPins();
                Memory.Write();
                break;
            
            case 7:
                if (kind is not RWKind.RMW) goto default;
                goto complete;
            
            default:
                Console.WriteLine("[CPU] Performed Indexed Indirect read on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                ctx();
                post();
                break;
        }
    }
    
    #endregion
}