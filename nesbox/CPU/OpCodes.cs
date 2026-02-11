using System.Reflection;
using System.Runtime.CompilerServices;

namespace nesbox.CPU;
using static System;
using Memory = System.Memory;

// TODO: Write PHP and PLP (How did you miss this?)

internal static class OpCodes {
    [Flags]
    private enum FlagChecks : byte {
        c = 1,
        z = 2,
        n = 4,
        v = 8,
        u = 16      // check unset
    }

    private unsafe struct Opcode {
        internal delegate*<void> ptr;
        internal RWKind          kind;

        internal Opcode(delegate*<void> ptr, RWKind kind) {
            this.ptr = ptr;
            this.kind = kind;
        }
    }
    
    private static readonly unsafe Action[] OpCodeSolvers = [
        #region 0x00-0x10

        /* 00 brk #    */ BRK,
        /* 01 ora *+x  */ () => IndexedIndirect(ORA),
        /* 02 jam      */ JAM,
        /* 03 slo *+x  */ () => IndexedIndirect(SLO),
        /* 04 nop d    */ () => DirectPage(NOP),
        /* 05 ora d    */ () => DirectPage(ORA),
        /* 06 asl d    */ () => DirectPage(ASL),
        /* 07 slo d    */ () => DirectPage(SLO),
        /* 08 php       */ () => {
            switch (cycle) {
                case 1:
                    AD = PC;
                    DriveAddressPins();
                    Memory.Read();
                    break;
                
                case 2:
                    Data = (byte)(
                        ((Register.c ? 1 : 0) << 0) |
                        ((Register.z ? 1 : 0) << 1) |
                        ((Register.i ? 1 : 0) << 2) |
                        ((Register.d ? 1 : 0) << 3) |
                        ((Register.b ? 1 : 0) << 4) |
                        (1                    << 5) |
                        ((Register.v ? 1 : 0) << 6) |
                        ((Register.n ? 1 : 0) << 7)
                    );
                    
                    Memory.Push();
                    cycle = 0xff;
                    break;
                
                default:
                    Console.WriteLine("[CPU] Performed PHP on incorrect cycle");
                    Quit = true;
                    break;
            }
        },
        /* 09 ora #imm  */ () => Immediate(ORA),
        /* 0a asl       */ ASLA,
        /* 0b anc #imm  */ () => Immediate(ANC),
        /* 0c nop a     */ () => Absolute(NOP),
        /* 0d ora a     */ () => Absolute(ORA),
        /* 0e asl a     */ () => Absolute(ASL),
        /* 0f slo a     */ () => Absolute(SLO),

        #endregion

        #region 0x10-0x20

        /* 10 bpl r   */ () => Branch(Register.n),
        /* 11 ora *+y */ () => IndirectIndexed(ORA),
        /* 12 jam     */ JAM,
        /* 13 slo *+y */ () => IndirectIndexed(SLO),
        /* 14 nop d+x */ () => DirectPageIndexed(Register.X, NOP),
        /* 15 ora d+x */ () => DirectPageIndexed(Register.X, ORA),
        /* 16 asl d+x */ () => DirectPageIndexed(Register.X, ASL),
        /* 17 slo d+x */ () => DirectPageIndexed(Register.X, SLO),
        /* 18 clc     */ CLC,
        /* 19 ora a+y */ () => AbsoluteIndexed(Register.Y, ORA),
        /* 1a nop     */ __NOP,
        /* 1b slo a+y */ () => AbsoluteIndexed(Register.Y, SLO),
        /* 1c nop a+x */ () => AbsoluteIndexed(Register.X, NOP),
        /* 1d ora a+x */ () => AbsoluteIndexed(Register.X, ORA),
        /* 1e asl a+x */ () => AbsoluteIndexed(Register.X, ASL),
        /* 1f slo a+x */ () => AbsoluteIndexed(Register.X, SLO),

        #endregion

        #region 0x20-0x30

        /* 20 jsr a   */ JSR,
        /* 21 and *+x */ () => IndexedIndirect(AND),
        /* 22 jam     */ JAM,
        /* 23 slo *+x */ () => IndexedIndirect(SLO),
        /* 24 bit d   */ () => DirectPage(BIT),
        /* 25 and d   */ () => DirectPage(AND),
        /* 26 rol d   */ () => DirectPage(ROL),
        /* 27 rla d   */ () => DirectPage(RLA),
        /* 28 plp     */ () => {
            switch (cycle) {
                case 1:
                    AD = PC;
                    DriveAddressPins();
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
                    
                    Register.c = (Data & 0x01) != 0;
                    Register.z = (Data & 0x02) != 0;
                    Register.i = (Data & 0x04) != 0;
                    Register.d = (Data & 0x08) != 0;

                    Register.b = false;

                    Register.v = (Data & 0x40) != 0;
                    Register.n = (Data & 0x80) != 0;
                    cycle      = 0xff;
                    break;
                
                default:
                    Console.WriteLine("[CPU] Performed PLP on incorrect cycle");
                    Quit = true;
                    break;
            }
        },
        /* 29 and #   */ () => Immediate(AND),
        /* 2a rol     */ ROLA,
        /* 2b anc     */ () => Immediate(ANC),
        /* 2c bit a   */ () => Absolute(BIT),
        /* 2d and a   */ () => Absolute(AND),
        /* 2e rol a   */ () => Absolute(ROL),
        /* 2f rla a   */ () => Absolute(RLA),

        #endregion

        #region 0x30-0x40

        /* 30 bmi r   */ () => Branch(Register.n),
        /* 31 and *+y */ () => IndirectIndexed(AND),
        /* 32 jam     */ JAM,
        /* 33 rla *+d */ () => IndirectIndexed(RLA),
        /* 34 nop d+x */ () => DirectPageIndexed(Register.X, NOP),
        /* 35 and d+x */ () => DirectPageIndexed(Register.X, AND),
        /* 36 rol d+x */ () => DirectPageIndexed(Register.X, ROL),
        /* 37 rla d+x */ () => DirectPageIndexed(Register.X, RLA),
        /* 38 sec     */ SEC,
        /* 39 and a+y */ () => AbsoluteIndexed(Register.Y, AND),
        /* 3a nop     */ __NOP,
        /* 3b rla a+y */ () => AbsoluteIndexed(Register.Y, RLA),
        /* 3c nop a+x */ () => AbsoluteIndexed(Register.X, NOP),
        /* 3d and a+x */ () => AbsoluteIndexed(Register.X, AND),
        /* 3e rol a+x */ () => AbsoluteIndexed(Register.X, ROL),
        /* 3f rla a+x */ () => AbsoluteIndexed(Register.X, RLA),

        #endregion

        #region 0x40-0x50

        /* 40 rti     */ RTI,
        /* 41 eor *+x */ () => IndexedIndirect(EOR),
        /* 42 jam     */ JAM,
        /* 43 sre *+x */ () => IndexedIndirect(SRE),
        /* 44 nop d   */ () => DirectPage(NOP),
        /* 45 eor d   */ () => DirectPage(EOR),
        /* 46 lsr d   */ () => DirectPage(LSR),
        /* 47 sre d   */ () => DirectPage(SRE),
        /* 48 pha     */ PHA,
        /* 49 eor #   */ () => Immediate(EOR),
        /* 4a lsr     */ LSRA,
        /* 4b alr #   */ () => Immediate(ALR),
        /* 4c jmp a   */ JMPA,
        /* 4d eor a   */ () => Absolute(EOR),
        /* 4e lsr a   */ () => Absolute(LSR),
        /* 4f sre a   */ () => Absolute(SRE),

        #endregion

        #region 0x50-0x60

        /* 50 bvc r   */ () => Branch(Register.v),
        /* 51 eor *+y */ () => IndirectIndexed(EOR),
        /* 52 jam     */ JAM,
        /* 53 sre *+y */ () => IndirectIndexed(SRE),
        /* 54 nop d+x */ () => DirectPageIndexed(Register.X, NOP),
        /* 55 eor d+x */ () => DirectPageIndexed(Register.X, EOR),
        /* 56 lsr d+x */ () => DirectPageIndexed(Register.X, LSR),
        /* 57 sre d+x */ () => DirectPageIndexed(Register.X, SRE),
        /* 58 cli     */ CLI,
        /* 59 eor a+y */ () => AbsoluteIndexed(Register.Y, EOR),
        /* 5a nop     */ __NOP,
        /* 5b sre a+y */ () => AbsoluteIndexed(Register.Y, SRE),
        /* 5c nop a+x */ () => AbsoluteIndexed(Register.X, NOP),
        /* 5d eor a+x */ () => AbsoluteIndexed(Register.X, EOR),
        /* 5e lsr a+x */ () => AbsoluteIndexed(Register.X, LSR),
        /* 5f sre a+x */ () => AbsoluteIndexed(Register.X, SRE),

        #endregion

        #region 0x60-0x70

        /* 60 rts     */ RTS,
        /* 61 ora *+x */ () => IndirectIndexed(ORA),
        /* 62 jam     */ JAM,
        /* 63 slo *+x */ () => IndirectIndexed(SLO),
        /* 64 nop d   */ () => DirectPage(NOP),
        /* 65 adc d   */ () => DirectPage(ADC),
        /* 66 ror d   */ () => DirectPage(ROR),
        /* 67 rra d   */ () => DirectPage(RRA),
        /* 68 pla     */ PLA,
        /* 69 adc #   */ () => Immediate(ADC),
        /* 6a ror     */ RORA,
        /* 6b arr #   */ () => Immediate(ARR),
        /* 6c jmp *   */ JMPI,
        /* 6d adc a   */ () => Absolute(ADC),
        /* 6e ror a   */ () => Absolute(ROR),
        /* 6f rra a   */ () => Absolute(RRA),

        #endregion

        #region 0x70-0x80

        /* 70 bvs r   */ () => Branch(Register.v),
        /* 71 adc *+y */ () => IndirectIndexed(ADC),
        /* 72 jam     */ JAM,
        /* 73 sre *+y */ () => IndirectIndexed(SRE),
        /* 74 nop d+x */ () => DirectPageIndexed(Register.X, NOP),
        /* 75 adc d+x */ () => DirectPageIndexed(Register.X, ADC),
        /* 76 ror d+x */ () => DirectPageIndexed(Register.X, ROR),
        /* 77 rra d+x */ () => DirectPageIndexed(Register.X, RRA),
        /* 78 sei     */ SEI,
        /* 79 adc a+y */ () => AbsoluteIndexed(Register.Y, ADC),
        /* 7a nop     */ __NOP,
        /* 7b rra a+y */ () => AbsoluteIndexed(Register.Y, RRA),
        /* 7c nop a+x */ () => AbsoluteIndexed(Register.X, NOP),
        /* 7d abs a+x */ () => AbsoluteIndexed(Register.X, ADC),
        /* 7e ror a+x */ () => AbsoluteIndexed(Register.X, ROR),
        /* 7f rra a+x */ () => AbsoluteIndexed(Register.X, RRA),

        #endregion

        #region 0x80-0x90

        /* 80 nop #   */ () => Immediate(NOP),
        /* 81 sta *+y */ () => IndirectIndexed(STA),
        /* 82 nop #   */ () => Immediate(NOP),
        /* 83 sax *+x */ () => IndexedIndirect(SAX),
        /* 84 sty d   */ () => DirectPage(STY),
        /* 85 sta d   */ () => DirectPage(STA),
        /* 86 stx d   */ () => DirectPage(STX),
        /* 87 sax d   */ () => DirectPage(SAX),
        /* 88 dey     */ DEY,
        /* 89 nop #   */ () => Immediate(NOP),
        /* 8a txa     */ TXA,
        /* 8b xaa #   */ () => Immediate(XAA),
        /* 8c sty a   */ () => Absolute(STY),
        /* 8d sta a   */ () => Absolute(STA),
        /* 8e stx a   */ () => Absolute(STX),
        /* 8f sax a   */ () => Absolute(SAX),

        #endregion

        #region 0x90-0xa0

        /* 90 bcc     */ () => Branch(Register.c),
        /* 91 sta *+y */ () => IndirectIndexed(STA),
        /* 92 jam     */ JAM,
        /* 93 sha *+y */ () => IndirectIndexed(SHA),
        /* 94 sty d+x */ () => DirectPageIndexed(Register.X, STY),
        /* 95 sta d+x */ () => DirectPageIndexed(Register.X, STA),
        /* 96 stx d+y */ () => DirectPageIndexed(Register.Y, STX),
        /* 97 sax d+y */ () => DirectPageIndexed(Register.Y, SAX),
        /* 98 tya     */ TYA,
        /* 99 sta a+y */ () => AbsoluteIndexed(Register.Y, STA),
        /* 9a txs     */ TXS,
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
        /* a8 tay      */ TAY,
        /* a9 lda #imm */ () => Immediate(LDA),
        /* aa tax      */ TAX,
        /* ab lax #imm */ () => Immediate(LAXI),
        /* ac ldy a    */ () => Absolute(LDY),
        /* ad lda a    */ () => Absolute(LDA),
        /* ae ldx a    */ () => Absolute(LDX),
        /* af lax a    */ () => Absolute(LAX),

        #endregion

        #region 0xb0-0xc0

        /* b0 bcs r   */ () => Branch(Register.c),
        /* b1 lda *+y */ () => IndirectIndexed(LDA),
        /* b2 jam     */ JAM,
        /* b3 lax a+y */ () => IndirectIndexed(LAX),
        /* b4 ldy d+x */ () => DirectPageIndexed(Register.X, LDY),
        /* b5 lda d+x */ () => DirectPageIndexed(Register.X, LDA),
        /* b6 ldx d+y */ () => DirectPageIndexed(Register.Y, LDX),
        /* b7 lax d+y */ () => DirectPageIndexed(Register.Y, LAX),
        /* b8 clv     */ CLV,
        /* b9 lda a,x */ () => AbsoluteIndexed(Register.X, LDA),
        /* ba tsx     */ TSX,
        /* bb las a,y */ () => AbsoluteIndexed(Register.Y, LAS),
        /* bc ldy a,x */ () => AbsoluteIndexed(Register.X, LDY),
        /* bd lda a,x */ () => AbsoluteIndexed(Register.X, LDA),
        /* be ldx a,y */ () => AbsoluteIndexed(Register.Y, LDX),
        /* bf lax a,y */ () => AbsoluteIndexed(Register.Y, LAX),

        #endregion

        #region 0xc0-0xd0

        /* c0 cpy #   */ () => Immediate(CPY),
        /* c1 cmp *+x */ () => IndexedIndirect(CMP),
        /* c2 nop #   */ () => Immediate(NOP),
        /* c3 dcp *+x */ () => IndexedIndirect(DCP),
        /* c4 cpy d   */ () => DirectPage(CPY),
        /* c5 cmp d   */ () => DirectPage(CMP),
        /* c6 dec d   */ () => DirectPage(DEC),
        /* c7 dcp d   */ () => DirectPage(DCP),
        /* c8 iny     */ INY,
        /* c9 cmp #   */ () => Immediate(CMP),
        /* ca dex     */ DEX,
        /* cb axs #   */ () => Immediate(AXS),
        /* cc cpy a   */ () => Absolute(CPY),
        /* cd cmp a   */ () => Absolute(CMP),
        /* ce dec a   */ () => Absolute(DEC),
        /* cf dcp a   */ () => Absolute(DCP),

        #endregion

        #region 0xd0-0xe0

        /* d0 bne r   */ () => Branch(Register.z),
        /* d1 cmp *+y */ () => IndexedIndirect(CMP),
        /* d2 jam     */ JAM,
        /* d3 dcp *+y */ () => IndexedIndirect(DCP),
        /* d4 nop d+x */ () => DirectPageIndexed(Register.X, NOP),
        /* d5 cmp d+x */ () => DirectPageIndexed(Register.X, CMP),
        /* d6 dec d+x */ () => DirectPageIndexed(Register.X, DEC),
        /* d7 dcp d+x */ () => DirectPageIndexed(Register.X, DCP),
        /* d8 cld     */ CLD,
        /* d9 cmp a+y */ () => AbsoluteIndexed(Register.Y, CMP),
        /* da nop     */ __NOP,
        /* db dcp a+y */ () => AbsoluteIndexed(Register.Y, DCP),
        /* dc nop a,x */ () => AbsoluteIndexed(Register.X, NOP),
        /* dd cmp a+x */ () => AbsoluteIndexed(Register.X, CMP),
        /* de dec a+x */ () => AbsoluteIndexed(Register.X, DEC),
        /* df dcp a+x */ () => AbsoluteIndexed(Register.X, DCP),

        #endregion

        #region 0xe0-0xf0

        /* e0 cpx #   */ () => Immediate(CPX),
        /* e1 sbc *+x */ () => IndexedIndirect(SBC),
        /* e2 nop #   */ () => Immediate(NOP),
        /* e3 isc *+x */ () => IndexedIndirect(ISC),
        /* e4 cpx d   */ () => DirectPage(CPX),
        /* e5 sbc d   */ () => DirectPage(SBC),
        /* e6 inc d   */ () => DirectPage(INC),
        /* e7 isc d   */ () => DirectPage(ISC),
        /* e8 inx     */ INX,
        /* e9 sbc #   */ () => Immediate(SBC),
        /* ea nop     */ __NOP,
        /* eb sbc #   */ () => Immediate(SBC),
        /* ec cpx a   */ () => Absolute(CPX),
        /* ed sbc a   */ () => Absolute(SBC),
        /* ee inc a   */ () => Absolute(INC),
        /* ef isc a   */ () => Absolute(ISC),

        #endregion

        #region 0xf0-

        /* f0 beq r   */ () => Branch(Register.z),
        /* f1 sbc *+y */ () => IndirectIndexed(SBC),
        /* f2 jam     */ JAM,
        /* f3 isc *+y */ () => IndirectIndexed(ISC),
        /* f4 nop d+x */ () => DirectPageIndexed(Register.X, NOP),
        /* f5 sbc d+x */ () => DirectPageIndexed(Register.X, SBC),
        /* f6 inc d+x */ () => DirectPageIndexed(Register.X, INC),
        /* f7 isc d+x */ () => DirectPageIndexed(Register.X, ISC),
        /* f8 sed     */ SED,
        /* f9 sbc a+y */ () => AbsoluteIndexed(Register.Y, SBC),
        /* fa nop     */ __NOP,
        /* fb isc a+y */ () => AbsoluteIndexed(Register.Y, ISC),
        /* fc nop a,x */ () => AbsoluteIndexed(Register.X, NOP),
        /* fd sbc a+x */ () => AbsoluteIndexed(Register.X, SBC),
        /* fe inc a+x */ () => AbsoluteIndexed(Register.X, INC),
        /* ff isc a+x */ () => AbsoluteIndexed(Register.X, ISC),

        #endregion
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Action GetOpcodeSolver(byte opcode) => OpCodeSolvers[opcode];

    #region Instruction Interfaces

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __BIT() {
        Register.z = (byte)(Register.AC & Data) == 0;
        Register.n = (Data & 0x80)              != 0;
        Register.v = (Data & 0x40)              != 0;
    }

    private static readonly unsafe Opcode BIT = new Opcode(&__BIT, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BRK() {
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CLC() {
        Register.c = false;
        cycle      = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void JSR() {
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SEC() {
        Register.c = true;
        cycle      = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RTI() {
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PHA() {
        Data = Register.AC;
        Memory.Push();
        cycle = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void JMPA() {
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CLI() {
        Register.i = false;
        cycle      = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RTS() {
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PLA() {
        Memory.Pull();
        Register.AC = Data;
        cycle       = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void JMPI() {
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SEI() {
        Register.i = true;
        cycle      = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DEY() {
        Register.Y--;
        cycle = 0xff;
        NonArithmeticProcessorFlagSets(Register.Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TXA() {
        Register.AC = Register.X;
        cycle       = 0xff;
        NonArithmeticProcessorFlagSets(Register.AC);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TYA() {
        Register.AC = Register.Y;
        cycle       = 0xff;
        NonArithmeticProcessorFlagSets(Register.AC);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TXS() {
        Register.S = Register.X;
        cycle      = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TAY() {
        Register.Y = Register.AC;
        cycle      = 0xff;
        NonArithmeticProcessorFlagSets(Register.Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TAX() {
        Register.X = Register.AC;
        cycle      = 0xff;
        NonArithmeticProcessorFlagSets(Register.X);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CLV() {
        Register.v = false;
        cycle      = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TSX() {
        Register.X = Register.S;
        cycle      = 0xff;
        NonArithmeticProcessorFlagSets(Register.X);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void INY() {
        Register.Y++;
        cycle = 0xff;
        NonArithmeticProcessorFlagSets(Register.Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DEX() {
        Register.X--;
        cycle = 0xff;
        NonArithmeticProcessorFlagSets(Register.X);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CLD() {
        Register.d = false;
        cycle      = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void INX() {
        Register.X++;
        cycle = 0xff;
        NonArithmeticProcessorFlagSets(Register.X);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SED() {
        Register.d = true;
        cycle      = 0xff;
    }
    
    // interrupts only occur after an instruction has finished, so we can evaluate the condition immediately
    private static void Branch(bool condition) {
        switch (cycle) {
            case 1:
                ADL = (byte)(PC & 0xFF);
                ADH = (byte)(PC >> 8);
                DriveAddressPins();
                Memory.Read();

                DB = Data;
                PC++;
                
                

                if (!condition) cycle = 0xff;
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

    private static                 void   __NOP() { }
    private static readonly unsafe Opcode NOP = new Opcode(&__NOP, RWKind.Read);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __CMP() {
        var diff = (short)(Register.AC - Data);
        Register.c   = diff               >= 0;
        NonArithmeticProcessorFlagSets(Register.AC);
    } 
    
    private static readonly unsafe Opcode CMP = new Opcode(&__CMP, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __CPX() {
        var diff = (short)(Register.X - Data);
        Register.c   = diff               >= 0;
        NonArithmeticProcessorFlagSets(Register.X);
    }
    private static readonly unsafe Opcode CPX = new Opcode(&__CPX, RWKind.Read);
    

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __CPY() {
        var diff = (short)(Register.Y - Data);
        Register.c   = diff               >= 0;
        NonArithmeticProcessorFlagSets(Register.Y);
    }
    
    private static readonly unsafe Opcode CPY = new Opcode(&__CPY, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __DEC() {
        Data--;
        NonArithmeticProcessorFlagSets(Data);
    }
    private static readonly unsafe Opcode DEC = new Opcode(&__DEC, RWKind.RMW);
    

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __INC() {
        Data++;
        NonArithmeticProcessorFlagSets(Data);
    }
    
    private static readonly unsafe Opcode INC = new Opcode(&__INC, RWKind.RMW);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __STA() => Data = Register.AC;
    private static readonly unsafe Opcode STA = new Opcode(&__STA, RWKind.Write);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __STX() => Data = Register.X;
    private static readonly unsafe Opcode STX = new Opcode(&__STX, RWKind.Write);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __STY() => Data = Register.Y;
    private static readonly unsafe Opcode STY = new Opcode(&__STY, RWKind.Write);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __SLO() {
        __ASL();
        __ORA();
    }
    private static readonly unsafe Opcode SLO = new Opcode(&__SLO, RWKind.RMW);
    
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __RLA() {
        __ROL();
        __AND();
    }
    private static readonly unsafe Opcode RLA = new Opcode(&__RLA, RWKind.RMW);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __SRE() {
        __LSR();
        __EOR();
    }
    private static readonly unsafe Opcode SRE = new Opcode(&__SRE, RWKind.RMW);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __RRA() {
        __ROR();
        __ADC();
    }
    private static readonly unsafe Opcode RRA = new Opcode(&__RRA, RWKind.RMW);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __SAX() => Data = (byte)(Register.AC & Register.X);
    private static readonly unsafe Opcode SAX = new Opcode(&__SAX, RWKind.Write);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __DCP() {
        __DEC();
        __CMP();
    }
    private static readonly unsafe Opcode DCP = new Opcode(&__DCP, RWKind.RMW);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __ISC() {
        __INC();
        __SBC();
    }
    private static readonly unsafe Opcode ISC = new Opcode(&__ISC, RWKind.RMW);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __SHA() => Data = (byte)(Register.AC & Register.X & (1 + (Address >> 8)));
    private static readonly unsafe Opcode SHA = new Opcode(&__SHA, RWKind.Write);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __SHX() => Data = (byte)(Register.X  & (1              + (Address >> 8)));
    private static readonly unsafe Opcode SHX = new Opcode(&__SHX, RWKind.Write);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __SHY() => Data = (byte)(Register.Y  & (1              + (Address >> 8)));
    private static readonly unsafe Opcode SHY = new Opcode(&__SHY, RWKind.Write);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __TAS() {
        Data       = (byte)(Register.AC & Register.X & (1 + (Address >> 8)));
        Register.S = (byte)(Register.AC & Register.X);
    }
    private static readonly unsafe Opcode TAS = new Opcode(&__TAS, RWKind.Write);
   
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __ROR() {
        var c      = (byte)(Data & 1);
        Data       = (byte)((Register.c ? 0x80 : 0x00) | (Data >> 1));
        Register.c = c is 1;
        NonArithmeticProcessorFlagSets(Data);
    }
    private static readonly unsafe Opcode ROR = new Opcode(&__ROR, RWKind.RMW);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __ROL() {
        var c      = (byte)(Data                         >> 7);
        Data       = (byte)((Register.c ? 1 : 0) | (Data << 1));
        Register.c = c is 1;
        NonArithmeticProcessorFlagSets(Data);
    }
    private static readonly unsafe Opcode ROL = new Opcode(&__ROL, RWKind.RMW);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __LSR() {
        Register.c =   (Data & 1) is 1;
        Data       >>= 1;
        NonArithmeticProcessorFlagSets(Data);
    }
    
    private static readonly unsafe Opcode LSR = new Opcode(&__LSR, RWKind.RMW);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __ASL() {
        Register.c =   (Data & 0x80) is 0x80;
        Data       <<= 1;
        NonArithmeticProcessorFlagSets(Data);
    }
    
    private static readonly unsafe Opcode ASL = new Opcode(&__ASL, RWKind.RMW);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __ADC() {
        var c   = Register.c ? 1 : 0;
        var sum = (ushort)(Register.AC + Data + c);

        var (sa, sc) = (Register.AC > 0x7f, Data > 0x7f);
        
        Register.c  = sum > 0xff;
        Register.AC = (byte)sum;
        Register.v  = Register.AC > 0x7f == sa == sc;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    private static readonly unsafe Opcode ADC = new Opcode(&__ADC, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __SBC() {
        var diff = (short)(Register.AC - Data - (Register.c ? 0 : 1));
        var (sa, sc) = (Register.AC > 0x7f, Data > 0x7f);
        Register.AC  = (byte)diff;
        Register.c   = diff               >= 0;
        Register.v   = Register.AC > 0x7f == sa == sc;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    private static readonly unsafe Opcode SBC = new Opcode(&__SBC, RWKind.Read);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __XAA() {
        NonArithmeticProcessorFlagSets(Register.AC);
        Register.AC = (byte)((Register.AC | Random.Shared.Next()) & Register.X & Data);
    }
    
    private static readonly unsafe Opcode XAA = new Opcode(&__XAA, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __LAXI() {
        NonArithmeticProcessorFlagSets(Register.AC);
        Register.AC = Register.X = (byte)((Register.AC | Random.Shared.Next()) & Data);
    }
    
    private static readonly unsafe Opcode LAXI = new Opcode(&__LAXI, RWKind.Read);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void JAM() {
        Console.WriteLine("[CPU] Encountered CPU Jam.");
        Quit = true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __ANC() {
        __AND();
        Register.c = Register.n;
    }
    
    private static readonly unsafe Opcode ANC = new Opcode(&__ANC, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __ALR() {
        __AND();
        LSRA();
    }
    
    private static readonly unsafe Opcode ALR = new Opcode(&__ALR, RWKind.Read);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LSRA() {
        Register.c  =   (Register.AC & 1) is 1;
        Register.AC >>= 1;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ASLA() {
        Register.c  =   (Register.AC & 0x80) is 0x80;
        Register.AC <<= 1;
        NonArithmeticProcessorFlagSets(Register.AC);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __ARR() {
        __AND();
        Register.c = Data >> 7 is 1;
        Register.v = ((Data >> 7) ^ (Data >> 6) & 1) is 1;
        RORA();
    }
    private static readonly unsafe Opcode ARR = new Opcode(&__ARR, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __AXS() {
        Register.X ^= Register.AC;
        Register.X -= Data;
        NonArithmeticProcessorFlagSets(Register.X);
    }
    
    private static readonly unsafe Opcode AXS = new Opcode(&__AXS, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __LAS() {
        Register.AC = Register.X = Register.S = (byte)(Data & Register.S);
        NonArithmeticProcessorFlagSets(Data);
    }
    private static readonly unsafe Opcode LAS = new Opcode(&__LAS, RWKind.Write);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RORA() {
        var c      = (byte)(Register.AC & 1);
        Register.AC = (byte)((Register.c ? 0x80 : 0x00) | (Register.AC >> 1));
        Register.c  = c is 1;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ROLA() {
        var c      = (byte)(Register.AC                          >> 7);
        Register.AC = (byte)((Register.c ? 1 : 0) | (Register.AC << 1));
        Register.c  = c is 1;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __ORA() {
        Register.AC |= Data;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    private static readonly unsafe Opcode ORA = new Opcode(&__ORA, RWKind.Read);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __AND() {
        Register.AC &= Data;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    private static readonly unsafe Opcode AND = new Opcode(&__AND, RWKind.Read);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __EOR() {
        Register.AC &= Data;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    private static readonly unsafe Opcode EOR = new Opcode(&__EOR, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __LDA() {
        Register.AC = Data;
        NonArithmeticProcessorFlagSets(Register.AC);
    }
    
    private static readonly unsafe Opcode LDA = new Opcode(&__LDA, RWKind.Read);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __LDX() {
        Register.X = Data;
        NonArithmeticProcessorFlagSets(Register.X);
    }
    
    private static readonly unsafe Opcode LDX = new Opcode(&__LDX, RWKind.Read);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __LDY() {
        Register.Y = Data;
        NonArithmeticProcessorFlagSets(Register.Y);
    }
    
    private static readonly unsafe Opcode LDY = new Opcode(&__LDY, RWKind.Read);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void __LAX() {
        Register.AC = Register.X = Data;
        NonArithmeticProcessorFlagSets(Register.X);
    }
    
    private static readonly unsafe Opcode LAX = new Opcode(&__LAX, RWKind.Read);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NonArithmeticProcessorFlagSets(byte ctx) {
        Register.z = ctx is 0;
        Register.n = ctx > 0x79;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AbsoluteIndexed(byte reg, Opcode op) {
        
        Action post = op.kind is RWKind.Read ? EndRead : EndRest;

        
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
                
                switch (op.kind) {
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

                switch (op.kind) {
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
                if (op.kind is RWKind.RMW) goto complete;
                DriveAddressPins();
                Memory.Write();
                break;
                
            case 6: 
                if (op.kind is RWKind.RMW) goto complete;
                goto default;
            
            default:
                Console.WriteLine("[CPU] Performed Absolute Indexed on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                op.ptr();
                post();
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void DirectPageIndexed(byte reg, Opcode op) {
        
        Action post = op.kind is RWKind.Read ? EndRead : EndRest;

        
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
                switch (op.kind) {
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
                if (op.kind is not RWKind.RMW) goto default;
                DriveAddressPins();
                Memory.Write();
                break;
            
            case 5:
                if (op.kind is not RWKind.RMW) goto default;
                goto complete;
            
            
            default:
                Console.WriteLine("[CPU] Performed Direct Page Indexed on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                op.ptr();
                post();
                break;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Absolute(Opcode op) {
        
        Action post = op.kind is RWKind.Read ? EndRead : EndRest;
        
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

                if (op.kind is RWKind.RMW) break;
                goto complete;
            
            case 4:
                if (op.kind is not RWKind.RMW) goto default;
                DriveAddressPins();
                Memory.Write();
                break;
            
            case 5:
                if (op.kind is not RWKind.RMW) goto default;
                goto complete;
            
            default:
                Console.WriteLine("[CPU] Performed Absolute read on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                op.ptr();
                post();
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EndRead()  => cycle = 0xff;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EndRest() {
        DriveAddressPins();
        Memory.Write();
        cycle = 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void DirectPage(Opcode op) {
        
        Action post = op.kind is RWKind.Read ? EndRead : EndRest;
        
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
                
                switch (op.kind) {
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
                op.ptr();
                post();
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Immediate(Opcode op) {
        switch (cycle) {
            case 1:
                ADL = PCL;
                ADH = PCH;

                DriveAddressPins();
                Memory.Read();
                PC++;
                op.ptr();
                cycle = 0xff;
                break;
            
            default:
                Console.WriteLine($"[CPU] Performed Immediate read on incorrect cycle {cycle}");
                Quit = true;
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void IndirectIndexed(Opcode op) {
        
        Action post = op.kind is RWKind.Read ? EndRead : EndRest;
        
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

                switch (op.kind) {
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

                switch (op.kind) {
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
                 if (op.kind is not RWKind.RMW) goto default;
                 DriveAddressPins();
                 Memory.Write();
                 break;
             
             case 7:
                 if (op.kind is not RWKind.RMW) goto default;
                 goto complete;
                 
            default:
                Console.WriteLine("[CPU] Performed Indirect Indexed read on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                op.ptr();
                post();
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void IndexedIndirect(Opcode op) {
        Action post = op.kind is RWKind.Read ? EndRead : EndRest;
        
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
                switch (op.kind) {
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
                if (op.kind is not RWKind.RMW) goto default;
                DriveAddressPins();
                Memory.Write();
                break;
            
            case 7:
                if (op.kind is not RWKind.RMW) goto default;
                goto complete;
            
            default:
                Console.WriteLine("[CPU] Performed Indexed Indirect read on incorrect cycle");
                Quit = true;
                break;
            
            complete:
                op.ptr();
                post();
                break;
        }
    }
    
    #endregion
}