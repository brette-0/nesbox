using System.Diagnostics;
using System.Runtime.CompilerServices;
using nesbox.CPU;
namespace nesbox;

// TODO: convert Memory module IO writes to set an internal state, revise IO response to R/W semantics and remove strobing

internal static class System {
    internal static class PPU {
        // TODO: Add OAM DMA for DMC DMA to interrupt it

        private const int DOTS_PER_LINE  = 341;
        private const int LINES_PER_FRAME = 262;

        // NTSC PPU suppresses VBlank for ~29658 CPU cycles after reset.
        // 29658 CPU cycles × 3 PPU dots/cycle = 88974 PPU dots.
        internal const ulong  WARMUP_DOTS = 29658 * 3; // 88974
        internal static ulong warmupEndDot;

        internal static int _dot;
        internal static int _line;
        private static bool _oddFrame;
        internal static bool FrameComplete;

        // ── Background shift registers & latches ──────────────────────
        private static ushort bgShiftLo;
        private static ushort bgShiftHi;
        private static ushort bgAttrShiftLo;
        private static ushort bgAttrShiftHi;
        private static byte   ntByte;
        private static byte   atByte;
        private static byte   ptLo;
        private static byte   ptHi;

        // ── Sprite evaluation & rendering state ──────────────────────
        private static byte[]  secondaryOAM     = new byte[32];  // 8 sprites × 4 bytes
        private static byte[]  sprShiftLo       = new byte[8];
        private static byte[]  sprShiftHi       = new byte[8];
        private static byte[]  sprAttr          = new byte[8];
        private static byte[]  sprXCounter      = new byte[8];
        private static int     spriteCount;
        private static bool[]  spriteIsZero     = new bool[8];

        private static bool RenderingEnabled =>
            (PPUMASK & 0x18) is not 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void IncrementCoarseX() {
            if ((v & 0x001F) == 31) {
                v &= unchecked((ushort)~0x001F);
                v ^= 0x0400;
            } else {
                v++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void IncrementFineY() {
            if ((v & 0x7000) != 0x7000) {
                v += 0x1000;
            } else {
                v &= unchecked((ushort)~0x7000);
                var coarseY = (v & 0x03E0) >> 5;
                if (coarseY == 29) {
                    coarseY = 0;
                    v ^= 0x0800;
                } else if (coarseY == 31) {
                    coarseY = 0;
                } else {
                    coarseY++;
                }
                v = (ushort)((v & ~0x03E0) | (coarseY << 5));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyHorizontalBits() {
            v = (ushort)((v & ~0x041F) | (tempVRAMAddr & 0x041F));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyVerticalBits() {
            v = (ushort)((v & ~0x7BE0) | (tempVRAMAddr & 0x7BE0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LoadBGShifters() {
            bgShiftLo = (ushort)((bgShiftLo & 0xFF00) | ptLo);
            bgShiftHi = (ushort)((bgShiftHi & 0xFF00) | ptHi);

            bgAttrShiftLo = (ushort)((bgAttrShiftLo & 0xFF00) | ((atByte & 0x01) != 0 ? 0xFF : 0x00));
            bgAttrShiftHi = (ushort)((bgAttrShiftHi & 0xFF00) | ((atByte & 0x02) != 0 ? 0xFF : 0x00));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FetchNT() {
            var addr = (ushort)(0x2000 | (v & 0x0FFF));
            ntByte = ReadVRAM(addr);
            // fetch NT diag disabled
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FetchAT() {
            var atAddr = (ushort)(0x23C0 | (v & 0x0C00)
                         | ((v >> 4) & 0x38)
                         | ((v >> 2) & 0x07));
            var raw = ReadVRAM(atAddr);
            var shift = ((v >> 4) & 0x04) | (v & 0x02);
            atByte = (byte)((raw >> shift) & 0x03);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FetchPTLo() {
            var baseAddr = (PPUCTRL & 0x10) != 0 ? 0x1000 : 0x0000;
            var fineY    = (v >> 12) & 0x07;
            var addr     = (ushort)(baseAddr + ntByte * 16 + fineY);
            Registers.Address = addr;
            ptLo = Program.Cartridge.PPUReadByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FetchPTHi() {
            var baseAddr = (PPUCTRL & 0x10) != 0 ? 0x1000 : 0x0000;
            var fineY    = (v >> 12) & 0x07;
            var addr     = (ushort)(baseAddr + ntByte * 16 + fineY + 8);
            Registers.Address = addr;
            ptHi = Program.Cartridge.PPUReadByte();
        }

        private static void EvaluateSprites(int nextLine) {
            spriteCount = 0;
            for (int i = 0; i < 8; i++) spriteIsZero[i] = false;

            var spriteHeight = (PPUCTRL & 0x20) != 0 ? 16 : 8;

            for (int n = 0; n < 64; n++) {
                var yPos = OAMBuffer[n * 4];
                var top  = yPos + 1;          // no 8-bit mask: Y=$FF → top=256 → always out of range
                var diff = nextLine - top;
                if (diff < 0 || diff >= spriteHeight) continue;

                if (spriteCount < 8) {
                    secondaryOAM[spriteCount * 4 + 0] = OAMBuffer[n * 4 + 0];
                    secondaryOAM[spriteCount * 4 + 1] = OAMBuffer[n * 4 + 1];
                    secondaryOAM[spriteCount * 4 + 2] = OAMBuffer[n * 4 + 2];
                    secondaryOAM[spriteCount * 4 + 3] = OAMBuffer[n * 4 + 3];
                    if (n == 0) spriteIsZero[spriteCount] = true;
                    spriteCount++;
                } else {
                    spriteOverflow = true;
                    break;
                }
            }
        }

        private static void LoadSpriteShifters(int nextLine) {
            var spriteHeight = (PPUCTRL & 0x20) != 0 ? 16 : 8;

            for (int i = 0; i < 8; i++) {
                if (i >= spriteCount) {
                    sprShiftLo[i] = 0;
                    sprShiftHi[i] = 0;
                    sprAttr[i]    = 0;
                    sprXCounter[i] = 0xFF;
                    continue;
                }

                var yPos    = secondaryOAM[i * 4 + 0];
                var tileIdx = secondaryOAM[i * 4 + 1];
                var attr    = secondaryOAM[i * 4 + 2];
                var xPos    = secondaryOAM[i * 4 + 3];

                sprAttr[i]    = attr;
                sprXCounter[i] = xPos;

                var flipV = (attr & 0x80) != 0;
                var row   = nextLine - (yPos + 1);

                ushort patAddr;
                if (spriteHeight == 16) {
                    var bank = (tileIdx & 0x01) != 0 ? 0x1000 : 0x0000;
                    var tile = tileIdx & 0xFE;
                    if (flipV) row = 15 - row;
                    if (row >= 8) { tile++; row -= 8; }
                    patAddr = (ushort)(bank + tile * 16 + row);
                } else {
                    var bank = (PPUCTRL & 0x08) != 0 ? 0x1000 : 0x0000;
                    if (flipV) row = 7 - row;
                    patAddr = (ushort)(bank + tileIdx * 16 + row);
                }

                Registers.Address = patAddr;
                var lo = Program.Cartridge.PPUReadByte();
                Registers.Address = (ushort)(patAddr + 8);
                var hi = Program.Cartridge.PPUReadByte();

                if ((attr & 0x40) != 0) {
                    lo = ReverseByte(lo);
                    hi = ReverseByte(hi);
                }

                sprShiftLo[i] = lo;
                sprShiftHi[i] = hi;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ReverseByte(byte b) {
            b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
            b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
            b = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
            return b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Step() {
            var dot  = _dot;
            var line = _line;

            if (dot == 0 && line == 0) Video.ResetStream();

            var isVisible   = line < 240;
            var isPreRender = line == 261;
            var isFetchLine = isVisible || isPreRender;

            // ── Pre-render clear ─────────────────────────────────────
            if (isPreRender && dot == 1) {
                inVblank       = false;
                nmiLine        = false;
                spriteZeroHit  = false;
                spriteOverflow = false;
            }

            // ── VBlank set ───────────────────────────────────────────
            if (line == 241 && dot == 1 && virtualTime >= warmupEndDot) {
                inVblank = true;
                EdgeDetectNMI();
            }

            // ── Rendering ────────────────────────────────────────────
            if (RenderingEnabled && isFetchLine) {

                // ─── Visible pixel output (dots 1-256, visible lines) ───
                if (isVisible && dot >= 1 && dot <= 256) {

                    // ── Background pixel ──
                    byte bgPixel = 0;
                    byte bgPal   = 0;

                    if ((PPUMASK & 0x08) != 0) {
                        if ((PPUMASK & 0x02) != 0 || dot > 8) {
                            var mux    = (ushort)(0x8000 >> fineX);
                            bgPixel = (byte)(
                                ((bgShiftLo & mux) != 0 ? 1 : 0) |
                                ((bgShiftHi & mux) != 0 ? 2 : 0));
                            bgPal = (byte)(
                                ((bgAttrShiftLo & mux) != 0 ? 1 : 0) |
                                ((bgAttrShiftHi & mux) != 0 ? 2 : 0));
                        }
                    }

                    // ── Sprite pixel (screen-relative, never affected by scroll) ──
                    byte sprPixel  = 0;
                    byte sprPal    = 0;
                    byte sprPri    = 0;
                    bool isSprZero = false;

                    if ((PPUMASK & 0x10) != 0) {
                        var screenX = dot - 1;
                        for (int i = 0; i < spriteCount; i++) {
                            int offset = screenX - sprXCounter[i];
                            if ((uint)offset > 7) continue;

                            if ((PPUMASK & 0x04) == 0 && dot <= 8) continue;

                            var px = (byte)(
                                ((sprShiftLo[i] >> (7 - offset)) & 1) |
                                (((sprShiftHi[i] >> (7 - offset)) & 1) << 1));
                            if (px == 0) continue;

                            sprPixel   = px;
                            sprPal     = (byte)((sprAttr[i] & 0x03) + 4);
                            sprPri     = (byte)((sprAttr[i] >> 5) & 1);
                            isSprZero  = spriteIsZero[i];
                            break;
                        }
                    }

                    // ── Sprite zero hit ──
                    if (isSprZero && bgPixel != 0 && sprPixel != 0
                        && dot >= 1 && dot <= 255) {
                        spriteZeroHit = true;
                    }

                    // ── Priority multiplexer ──
                    byte muxOutput;
                    if (bgPixel == 0 && sprPixel == 0)
                        muxOutput = 0;
                    else if (bgPixel == 0)
                        muxOutput = (byte)((sprPal << 2) | sprPixel);
                    else if (sprPixel == 0)
                        muxOutput = (byte)((bgPal << 2) | bgPixel);
                    else
                        muxOutput = sprPri != 0
                            ? (byte)((bgPal << 2) | bgPixel)
                            : (byte)((sprPal << 2) | sprPixel);

                    // Diagnostic: dump scanline with content
                    // pixel diag disabled for AT analysis
                    Video.Emit(muxOutput);
                }

                // ─── BG shift register clock (dots 1-256 and 321-336) ───
                if ((dot >= 1 && dot <= 256) || (dot >= 321 && dot <= 336)) {
                    bgShiftLo     <<= 1;
                    bgShiftHi     = (ushort)((bgShiftHi << 1) | 1); // serial-in is 1 for high plane
                    bgAttrShiftLo <<= 1;
                    bgAttrShiftHi <<= 1;

                    switch ((dot - 1) & 0x07) {
                        case 1: FetchNT(); break;
                        case 3: FetchAT(); break;
                        case 5: FetchPTLo(); break;
                        case 7:
                            FetchPTHi();
                            LoadBGShifters();
                            IncrementCoarseX();
                            break;
                    }
                }

                // ─── Y increment at end of visible portion ───
                if (dot == 256) IncrementFineY();

                // ─── Horizontal scroll reload + sprite eval ───
                if (dot == 257) {
                    CopyHorizontalBits();
                    if (isVisible || isPreRender) {
                        // Real PPU clears OAMADDR during sprite evaluation
                        OAMAddress = 0;
                        var nextLine = isPreRender ? 0 : line + 1;
                        EvaluateSprites(nextLine);
                        LoadSpriteShifters(nextLine);
                    }
                }

                // ─── Vertical scroll reload (pre-render only) ───
                if (isPreRender && dot >= 280 && dot <= 304) {
                    CopyVerticalBits();
                }

            } else if (isVisible && dot >= 1 && dot <= 256) {
                // Rendering disabled — emit backdrop
                Video.Emit(0);
            }

            // ── Advance dot/line counters ────────────────────────────
            // Odd-frame skip: when rendering is enabled on odd frames,
            // the pre-render line is 340 dots (0-339) instead of 341
            // (0-340). This keeps even+odd = 178683 dots, perfectly
            // divisible by the 3:1 PPU:CPU ratio.
            var lineLen = (isPreRender && _oddFrame && RenderingEnabled)
                          ? 340 : DOTS_PER_LINE;
            if (++_dot >= lineLen) {
                _dot = 0;
                if (++_line >= LINES_PER_FRAME) {
                    _line = 0;
                    _oddFrame = !_oddFrame;
                    FrameComplete = true;
                }
            }
        }

        // ====================================================================
        // PPU bus helpers (for $2007 access).
        // CHR pattern fetches bypass this — they go directly through the
        // cartridge so mapper bank-switching snoops fire on the right address.
        // ====================================================================
        private static byte ReadVRAM(ushort addr) {
            // Nametable address: $2000-$2FFF (caller already masked the upper bits).
            var rel  = (addr - 0x2000) & 0x0FFF;
            var nt   = (rel >> 10) & 0x03;
            var line = 
                Program.Cartridge.PPUA10_11((nt & 0x01) is not 0, (nt & 0x02) is not 0) ? 0x400 : 0;
            return VRAM[(line | (rel & 0x3FF)) & 0x7FF];
        }

        private static byte PPUBusRead(ushort addr) {
            addr &= 0x3FFF;
            if (addr < 0x2000) {
                Registers.Address = addr;
                return Program.Cartridge.PPUReadByte();
            }
            if (addr < 0x3F00) {
                return ReadVRAM((ushort)(0x2000 | (addr & 0x0FFF)));
            }
            return PaletteRAM[NormalisePaletteAddr(addr)];
        }

        private static void PPUBusWrite(ushort addr, byte data) {
            addr &= 0x3FFF;
            if (addr < 0x2000) {
                Registers.Address = addr;
                Data = data;
                Program.Cartridge.PPUWrite();
                return;
            }
            if (addr < 0x3F00) {
                var rel  = (addr - 0x2000) & 0x0FFF;
                var nt   = (rel >> 10) & 0x03;
                var line = Program.Cartridge.PPUA10_11((nt & 0x01) is not 0, (nt & 0x02) is not 0) ? 0x400 : 0;
                VRAM[(line | (rel & 0x3FF)) & 0x7FF] = data;
                return;
            }
            PaletteRAM[NormalisePaletteAddr(addr)] = data;
        }

        /// <summary>
        /// Reduce a $3F00-$3F1F address to its canonical 5-bit palette RAM
        /// index. Only $3F10/$14/$18/$1C are real mirrors of $3F00/$04/$08/$0C;
        /// the rest are separate cells.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int NormalisePaletteAddr(ushort addr) {
            var p = addr & 0x1F;
            if ((p & 0x13) is 0x10) p &= 0x0F;
            return p;
        }

        // NMI is edge-triggered on real hardware.
        // /NMI output = inVblank AND NMIEnabled.
        // CPU latches NMI only on the 0→1 transition of that signal.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EdgeDetectNMI() {
            var newLine = inVblank && NMIEnabled;
            if (newLine && !nmiLine)
                NMIAsserted = true;
            nmiLine = newLine;
        }

        internal static void PowerOn() {
            Reset();

            Array.Fill<byte>(OAMBuffer, 0xFE);
        }

        /// <summary>
        /// Executed on 'Reset'
        /// </summary>
        internal static void Reset() {
            
        }

        internal static byte[]   VRAM              = new byte[0x800];
        internal static ushort   tempVRAMAddr      = 0;
        internal static bool     latch             = false;
        /// <summary>
        /// Palette RAM, flat 32 cells matching the chip's $3F00-$3F1F.
        /// CPU writes to $3F10/$14/$18/$1C are normalised on the way in
        /// to $3F00/$04/$08/$0C — those are the only true mirror pairs.
        /// The "universal backdrop" used for any transparent output pixel
        /// is always read from index 0 ($3F00).
        /// </summary>
        internal static byte[]   PaletteRAM        = new byte[32];
        private const   int      PPUFRAMELENGTH    = 89342;

        internal static class Registers {
            /// <summary>
            /// Live PPU bus address. Set transiently by the renderer's fetch
            /// helpers (NT/AT/PT addresses) and by $2007 accesses (= v).
            /// Cartridge snoops read this to know what's on the bus right now.
            /// </summary>
            internal static ushort Address = 0;
            internal static byte   PPUDATA = 0;

            // ----------------------------------------------------------------
            // PPU internal data bus latch ("PPUGenLatch" / "io_db").
            //
            // Per nesdev: every PPU port access (read OR write) fills this
            // latch with whatever was on the data bus. Reads of nominally
            // write-only registers ($2000/$2001/$2003/$2005/$2006) return the
            // latch unchanged. The unused low 5 bits of $2002 reads come from
            // here too. $2004 and $2007 reads refill the latch with the byte
            // they return. We don't model the analog decay (3-30 ms) because
            // games that rely on decay are vanishingly rare.
            //
            // Before this implementation, the emulator used the shared `Data`
            // bus field as a proxy — which is wrong: `Data` reflects the LAST
            // CPU cycle's bus value (often an operand byte), not the PPU's
            // own latch. Games that read open-bus PPU bits would see a
            // different value here than on real hardware.
            // ----------------------------------------------------------------
            internal static byte ppuLatch;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2000_PPUCRTL() {
                ppuLatch   = Data;
                PPUCTRL    = Data;
                NMIEnabled = (Data & 0x80) is 0x80;
                // Nametable select bits drop into t bits 10-11.
                tempVRAMAddr = (ushort)((tempVRAMAddr & 0xF3FF) | ((Data & 0x03) << 10));
                EdgeDetectNMI();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2001_PPUMASK() {
                ppuLatch = Data;
                PPUMASK  = Data;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void R2002_PPUSTATUS() {
                // Low 5 bits come from the PPU's internal latch (open bus on
                // the unused bits). Bits 7-5 are the live status flags.
                // Reading $2002 only refreshes bits 7-5 of the latch.
                Data        = (byte)(ppuLatch & 0x1f);
                Data       |= (byte)(inVblank       ? 0x80 : 0x00);
                Data       |= (byte)(spriteZeroHit  ? 0x40 : 0x00);
                Data       |= (byte)(spriteOverflow ? 0x20 : 0x00);
                ppuLatch    = (byte)((ppuLatch & 0x1F) | (Data & 0xE0));
                inVblank    = false;
                nmiLine     = false;
                // Do NOT clear NMIAsserted here. NMIAsserted is the latched
                // 0→1 edge of the /NMI signal; once the CPU has captured it,
                // dropping the level (which is what R2002 does, by clearing
                // inVblank) must not unqueue the pending interrupt. Only the
                // CPU's interrupt dispatcher should clear NMIAsserted.
                latch       = false;    // reading $2002 clears the $2005/$2006 write toggle
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2003_OAMADDR() {
                ppuLatch   = Data;
                OAMAddress = Data;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2004_OAMDATA() {
                ppuLatch                = Data;
                OAMBuffer[OAMAddress++] = Data;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2005_PPUSCROLL() {
                ppuLatch = Data;
                if (!latch) {
                    // First write: fine X + coarse X
                    fineX        = (byte)(Data & 0x07);
                    tempVRAMAddr = (ushort)((tempVRAMAddr & 0x7FE0) | (Data >> 3));
                    latch        = true;
                } else {
                    // Second write: fine Y + coarse Y
                    tempVRAMAddr = (ushort)(
                        (tempVRAMAddr & 0x0C1F) |
                        ((Data & 0x07) << 12)   |   // fine Y -> bits 12-14
                        ((Data & 0xF8) << 2));      // coarse Y -> bits 5-9
                    latch = false;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2006_PPUADDR() {
                ppuLatch = Data;
                if (!latch) {
                    // First write: high 6 bits of t. Bit 14 cleared.
                    tempVRAMAddr = (ushort)((tempVRAMAddr & 0x00FF) | ((Data & 0x3F) << 8));
                    latch        = true;
                } else {
                    // Second write: low 8 bits of t, then copy t -> v.
                    tempVRAMAddr = (ushort)((tempVRAMAddr & 0x7F00) | Data);
                    v            = tempVRAMAddr;
                    Address      = v;       // drive bus
                    latch        = false;
                }
            }

            internal static void W2007_PPUDATA() {
                ppuLatch = Data;
                Address  = v;
                PPUBusWrite(v, Data);
                v = (ushort)((v + (((PPUCTRL & 0x04) is 0) ? 1 : 32)) & 0x3FFF);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void R2007_PPUDATA() {
                Address = v;
                if ((v & 0x3FFF) < 0x3F00) {
                    // Buffered read: yield previous buffer, then refill.
                    Data          = ppuDataBuffer;
                    ppuDataBuffer = PPUBusRead(v);
                } else {
                    // Palette: direct read; bits 6-7 are open bus from PPU latch.
                    Data          = (byte)((PPUBusRead(v) & 0x3F) | (ppuLatch & 0xC0));
                    ppuDataBuffer = PPUBusRead((ushort)(v - 0x1000));
                }
                ppuLatch = Data;        // latch refreshes with the byte just returned
                v        = (ushort)((v + (((PPUCTRL & 0x04) is 0) ? 1 : 32)) & 0x3FFF);
            }
        }

        internal static class OAM {
            internal static void W4014_OAMDMA() {
                inDMA        = true;
                oamHaltCycle = true;
                dmaPage      = Data;
                dmaIndex     = 0;
                dmaLatch     = 0;
                dmaGetPhase  = true;

                dmaAlign    = virtualTime % 6 is 0;     // aligned if on an even cpu cycle
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void R2004_OAMDATA() {
                Data                 = OAMBuffer[OAMAddress];
                // Attribute bytes (byte 2 of each 4-byte entry) have bits 2-4
                // unimplemented — they always read as 0.
                if ((OAMAddress & 0x03) == 2)
                    Data &= 0xE3;
                Registers.ppuLatch   = Data;   // PPU latch refreshes with the byte returned
            }

            internal static void DMA() {
                if (!inDMA || APU.PCM.inDMA) return;
                if (oamHaltCycle) {
                    oamHaltCycle = false;
                    RDY          = true;
                    return;
                }
                
                if (dmaAlign) {
                    dmaAlign = false;
                    return;
                }
        
                if (dmaGetPhase) {
                    var addr = (ushort)((dmaPage << 8) | dmaIndex);
                    // The 2A03 has three internal address buses (6502, DMC, OAM).
                    // APU registers ($4000-$401F) only respond when the *6502*
                    // address bus is in that range.  After STA $4014, the 6502
                    // would next fetch from PC (in ROM), so its bus is NOT in
                    // the APU range — OAM DMA reads open bus instead.
                    if (addr is >= 0x4000 and <= 0x401F
                        && PC is not (>= 0x4000 and <= 0x401F)) {
                        dmaLatch = OpenBus;
                    } else {
                        Memory.Read(addr, out dmaLatch);
                    }
                } else {
                    OAMBuffer[OAMAddress++] = dmaLatch;
                    dmaIndex++;

                    if (dmaIndex is 0) {
                        inDMA = false;
                        RDY   = false;
                        return;
                    }
                }
        
                dmaGetPhase ^= true;
            }
        }
        
        /// <summary>
        /// Video output stage. Owns the NES base palette and the 512-entry
        /// final-color lookup table (64 hues × 8 PPUMASK emphasis combos).
        /// </summary>
        internal static class Video {
            // 2C02 NTSC palette, 0x00RRGGBB. Source: nesdev "NTSC video" reference.
            private static readonly uint[] BasePalette = {
                0x545454, 0x001E74, 0x081090, 0x300088, 0x440064, 0x5C0030, 0x540400, 0x3C1800,
                0x202A00, 0x083A00, 0x004000, 0x003C00, 0x00323C, 0x000000, 0x000000, 0x000000,
                0x989698, 0x084CC4, 0x3032EC, 0x5C1EE4, 0x8814B0, 0xA01464, 0x982220, 0x783C00,
                0x545A00, 0x287200, 0x087C00, 0x007628, 0x006678, 0x000000, 0x000000, 0x000000,
                0xECEEEC, 0x4C9AEC, 0x787CEC, 0xB062EC, 0xE454EC, 0xEC58B4, 0xEC6A64, 0xD48820,
                0xA0AA00, 0x74C400, 0x4CD020, 0x38CC6C, 0x38B4CC, 0x3C3C3C, 0x000000, 0x000000,
                0xECEEEC, 0xA8CCEC, 0xBCBCEC, 0xD4B2EC, 0xECAEEC, 0xECAED4, 0xECB4B0, 0xE4C490,
                0xCCD278, 0xB4DE78, 0xA8E290, 0x98E2B4, 0xA0D6E4, 0xA0A2A0, 0x000000, 0x000000,
            };

            /// <summary>
            /// 512-entry LUT in ARGB8888 (alpha=0xFF). Index = (emphasis &lt;&lt; 6) | paletteIdx.
            /// Built once from BasePalette × emphasis attenuation × user shader.
            /// </summary>
            internal static readonly uint[] LUT = new uint[512];

            /// <summary>
            /// Build the final-color LUT. Call after a shader has been installed.
            /// Safe to call again to rebuild (e.g. if the shader is swapped).
            /// </summary>
            internal static void BuildLUT() {
                for (var emph = 0; emph < 8; emph++) {
                    var attenR = (emph & 0b010) is not 0 || (emph & 0b100) is not 0 ? 0.75f : 1f;
                    var attenG = (emph & 0b001) is not 0 || (emph & 0b100) is not 0 ? 0.75f : 1f;
                    var attenB = (emph & 0b001) is not 0 || (emph & 0b010) is not 0 ? 0.75f : 1f;

                    for (var hue = 0; hue < 64; hue++) {
                        var rgb = BasePalette[hue];
                        var r = (byte)((rgb >> 16) & 0xff);
                        var g = (byte)((rgb >>  8) & 0xff);
                        var b = (byte)( rgb        & 0xff);

                        r = (byte)(r * attenR);
                        g = (byte)(g * attenG);
                        b = (byte)(b * attenB);

                        var shaded = Program.Shader.Recolour(new SDL3.SDL.Color { R = r, G = g, B = b, A = 0xff });
                        LUT[(emph << 6) | hue] = (uint)(0xff << 24 | shaded.R << 16 | shaded.G << 8 | shaded.B);
                    }
                }
            }

            /// <summary>
            /// Stream cursor — index of the next pixel slot in
            /// <see cref="Renderer.BackBuffer"/> to fill. Resets to 0 at frame
            /// start. There is no horizontal/vertical addressing because the
            /// PPU itself has none: its output stage drives a serial video
            /// signal and the framebuffer is whatever happens to be latched
            /// from that stream over one frame's worth of /VIS-active dots.
            /// </summary>
            private static int _cursor;

            /// <summary>
            /// Called once at frame start. Returns the stream to the
            /// top-left of the next frame so subsequent <see cref="Emit"/>
            /// calls fill the buffer from the beginning.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void ResetStream() => _cursor = 0;

            /// <summary>
            /// Emit the next pixel in the video stream.
            ///
            /// This mirrors the PPU output stage: at every /VIS-active dot
            /// the pixel composer multiplexes BG and the 8 sprite candidates
            /// down to a single 5-bit address, indexes palette RAM (with
            /// universal-backdrop substitution for any "transparent" pixel),
            /// applies the PPUMASK colour controls, and clocks the result
            /// out the video pin. Downstream — that is, us — latches it
            /// into the next slot in the framebuffer.
            ///
            /// Call once per /VIS-active dot in raster order. Real hardware
            /// emits every /VIS dot regardless of PPUMASK state — when
            /// rendering is off, the mux output is just 0 and you get the
            /// backdrop colour. Do the same here: keep emitting, do not
            /// skip dots, or the buffer retains stale pixels.
            /// </summary>
            /// <param name="muxOutput">
            /// 5-bit pixel-composer mux output:
            /// <list type="bullet">
            /// <item>bit 4 — source (0 = background, 1 = sprite)</item>
            /// <item>bits 3:2 — palette select within the BG or sprite half</item>
            /// <item>bits 1:0 — colour within the palette (0 = transparent → universal backdrop)</item>
            /// </list>
            /// </param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Emit(byte muxOutput) {
                if ((uint)_cursor >= (uint)(Renderer.Width * Renderer.Height)) return;

                // Transparent (colour-in-palette == 0) always reads $3F00
                // regardless of which sub-palette the mux selected — that's
                // the chip's universal backdrop substitution. Otherwise the
                // 5-bit mux output IS the palette RAM index.
                var pIdx      = (muxOutput & 0x03) is 0 ? 0 : (muxOutput & 0x1F);
                var nesColour = PaletteRAM[pIdx];

                var idx = nesColour & 0x3f;
                if ((PPUMASK & 0x01) is not 0) idx &= 0x30;      // grayscale
                var emph = (PPUMASK >> 5) & 0x07;

                Renderer.BackBuffer[_cursor++] = LUT[(emph << 6) | idx];
            }
        }

        // =============================================================
        // Scroll / address registers
        //
        // v: the live PPU address used by rendering (15 bits).
        //    yyy NN YYYYY XXXXX  — fine Y, NT select, coarse Y, coarse X.
        // t: temporary VRAM address. Loaded by $2000/$2005/$2006 writes;
        //    copied to v at specific render boundaries (dot 257 horiz,
        //    dots 280..304 vert) and on the second $2006 write.
        // fineX: 3-bit per-pixel horizontal offset into the BG shifters.
        // latch: shared write toggle for $2005 and $2006 (the "w" flag).
        // =============================================================
        internal static ushort v;
        internal static byte   fineX;

        /// <summary>
        /// PPUCTRL latched byte.
        /// bit 0-1: nametable select (also written into t bits 10-11 by W2000)
        /// bit 2:   $2007 increment (0 = +1, 1 = +32)
        /// bit 3:   sprite pattern table base (8×8 mode)
        /// bit 4:   BG pattern table base
        /// bit 5:   8×16 sprite mode
        /// bit 7:   NMI enable
        /// </summary>
        internal static byte   PPUCTRL;

        /// <summary>$2007 read buffer (returns previous byte, then refreshes).</summary>
        internal static byte   ppuDataBuffer;

        // ---- PPUSTATUS bits ----
        internal static bool   spriteZeroHit;
        internal static bool   spriteOverflow;

        private static bool dmaAlign;
        private static byte dmaLatch;
        private static byte dmaPage;
        private static byte dmaIndex;
        private static bool dmaGetPhase;

        internal static bool   NMIEnabled;
        internal static bool   inDMA;
        internal static bool   inVblank;
        internal static bool   nmiLine;
        internal static bool   oamHaltCycle = false;
        internal static byte   OAMAddress;
        internal static byte   OAMData;
        internal static byte[] OAMBuffer = new byte[256];

        /// <summary>
        /// PPUMASK register state. Bit 0 = greyscale, bits 1/2 = show
        /// BG/sprites in leftmost 8 px, bits 3/4 = show BG/sprites at all,
        /// bits 7:5 = R/G/B emphasis. Read live each output dot so mid-line
        /// writes affect mid-line pixels.
        /// </summary>
        internal static byte   PPUMASK;
    }
    
    internal static class APU {
        internal static /* pcm */ float GetPCMSample() {
            var p1 = Program.AudioProcessor.ProcessPulse1Level(Pulse1.GetLevel());
            var p2 = Program.AudioProcessor.ProcessPulse2Level(Pulse2.GetLevel());
            var t  = Program.AudioProcessor.ProcessTriangleLevel(Triangle.GetLevel());
            var n  = Program.AudioProcessor.ProcessNoiseLevel(Noise.GetLevel());
            var p  = Program.AudioProcessor.ProcessPCMLevel(PCM.GetLevel());

            var pulseSum = p1 + p2;
            var pulseOut = pulseSum > 0 ? 95.88f          / (8128f / pulseSum                              + 100f) : 0f;
            var tnpOut   = (t | n | p) is not 0 ? 159.79f / (1f    / (t / 8227f + n / 12241f + p / 22638f) + 100f) : 0f;

            return (Program.isFamicom
                ? ((API.IFamicomCartridge)Program.Cartridge).ModifyAPUSignal(pulseOut + tnpOut)
                : pulseOut + tnpOut) * 2f - 1f;
        }
        
        
        internal static void Step() {
            _clockFlipFlop ^= true;

            if (_pendingFrameIRQClear && !_clockFlipFlop) {
                _pendingFrameIRQClear = false;
                FrameIRQAsserted = false;
            }

            if (_irqWindow > 0 && --_irqWindow is > 0 and <= 3 && !IRQInhibit)
                FrameIRQAsserted = true;

            if (_irqFlagWindow > 0) _irqFlagWindow--;


            switch (_resetFrameCounter) {
                case > 4:
                    break;
                
                case 0:
                    _resetFrameCounter = 0xff;
                    _frameCounter      = 0;
                    if (UsingFiveStep) {
                        Pulse1.QuarterFrame();
                        Pulse2.QuarterFrame();
                        Pulse1.HalfFrame();
                        Pulse2.HalfFrame();
                        Triangle.QuarterFrame();
                        Triangle.HalfFrame();
                        Noise.QuarterFrame();
                        Noise.HalfFrame();
                    }
                    break;
                
                default:
                    _resetFrameCounter--;
                    break;
            }

            switch (++_frameCounter) {
                case S1:
                case S3:
                    Pulse1.QuarterFrame();
                    Pulse2.QuarterFrame();
                    Triangle.QuarterFrame();
                    Noise.QuarterFrame();
                    break;

                case S2:
                    Pulse1.HalfFrame();
                    Pulse2.HalfFrame();
                    Triangle.HalfFrame();
                    Noise.HalfFrame();
                    goto case S1;

                case S4 - 1:
                    if (UsingFiveStep) break;
                    if (!IRQInhibit) {
                        if (_frameCounterOddReset) {
                            _irqWindow = 5;
                        } else {
                            FrameIRQAsserted = true;
                            _irqWindow = 3;
                        }
                    } else {
                        _irqFlagWindow = 4;
                    }
                    break;

                case S4:
                    if (UsingFiveStep) break;
                    _frameCounter = unchecked((ushort)(-1));
                    goto case S2;

               case S5:
                   if (!UsingFiveStep) break;
                   _frameCounter = 0;
                   goto case S2;
            }
            
            if (IOAssertion && _clockFlipFlop) {
                Program.Controller1?.OnWrite();
                Program.Controller2?.OnWrite();
            }

            Pulse1.Step();
            Pulse2.Step();
            Triangle.Step();
            Noise.Step();
            PCM.Step();
        }

        internal static class PCM {
            private static readonly ushort[] rateTable = {
                0x01AC, 0x017C, 0x0154, 0x0140,
                0x011E, 0x00FE, 0x00E2, 0x00D6,
                0x00BE, 0x00A0, 0x008E, 0x0080,
                0x006A, 0x0054, 0x0048, 0x0036
            };

            internal static byte GetLevel() => enabled ? (byte)(outputLevel & 0x7f) : (byte)0;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Step() {
                if (enableDelay > 0 && --enableDelay is 0) enabled = true;

                if (timerCounter is not 0) {
                    timerCounter--;
                    return;
                }

                timerCounter = (ushort)(rateTable[rateIndex] - 1);

                if (!Silence) {
                    switch ((shiftReg & 1) is not 0, outputLevel) {
                        case (true,  < 126): outputLevel += 2; break;
                        case (false, > 1):   outputLevel -= 2; break;
                    }
                }

                shiftReg >>= 1;

                if (bitsRemaining is 0) {
                    bitsRemaining = 7;

                    if (bufferEmpty) {
                        Silence = true;
                    } else {
                        Silence     = false;
                        shiftReg    = sampleBuffer;
                        bufferEmpty = true;
                    }
                } else bitsRemaining--;

                if (!bufferEmpty || bytesRemaining is 0 || dmaRequested || inDMA) return;
                dmaRequested = true;
                dmaDelayCount = 1;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4010_DMC() {
                DMC_IRQ_Enabled = (Data       & 0x80) is 0x80;
                Loop            = (Data       & 0x40) is 0x40;
                rateIndex       = (byte)(Data & 0x0f);
                if (!DMC_IRQ_Enabled) IRQFlag = false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4011_DMC() {
                outputLevel = (byte)(Data & 0x7f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4012_DMC() {
                SampleAddress = (ushort)(0xc000 | (Data << 6));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4013_DMC() {
                SampleLength = (ushort)((Data << 4) | 1);
            }

            internal static bool   IRQFlag;
            internal static ushort currentAddress;
            internal static byte   sampleBuffer;
            internal static ushort timerCounter;
            internal static byte   outputLevel;
            internal static byte   shiftReg;
            internal static byte   rateIndex;
            internal static byte   bitsRemaining;
            internal static bool   bufferEmpty;
            internal static ushort bytesRemaining;
            internal static bool   Silence;
            internal static ushort SampleAddress;
            internal static ushort SampleLength;
            internal static bool   DMC_IRQ_Enabled;
            internal static bool   Loop;
            internal static bool   enabled;
            internal static byte   enableDelay;

            internal static bool   inDMA;
            internal static bool   dmaRequested;
            private  static byte   dmaCycleCount;
            internal static byte   dmaDelayCount;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void DMA_Step() {
                if (!dmaRequested && !inDMA) return;

                if (dmaRequested && !inDMA) {
                    inDMA = true;
                    dmaRequested = false;
                    dmaCycleCount = dmaDelayCount > 0 ? (byte)4 : (byte)3;
                    dmaDelayCount = 0;
                    RDY = true;
                    return;
                }

                if (--dmaCycleCount is not 0) return;

                var sample = Program.Cartridge.ReadByte(currentAddress);
                OpenBus = sample;
                sampleBuffer = sample;
                bufferEmpty = false;

                currentAddress = (ushort)((currentAddress + 1) | 0x8000);
                bytesRemaining--;

                if (bytesRemaining is 0) {
                    if (Loop) {
                        currentAddress = SampleAddress;
                        bytesRemaining = SampleLength;
                    } else if (DMC_IRQ_Enabled) {
                        IRQFlag = true;
                    }
                }

                inDMA = false;
                if (!PPU.inDMA) RDY = false;
            }
        }

        internal static class Noise {
            private static readonly ushort[] PeriodTable = {
                4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068
            };
            
            private static readonly byte[] lengthTable = {
                10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,
                12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30
            };

            internal static byte GetLevel() =>
                !enabled || Length is 0 || (lfsr & 1) is not 0
                    ? (byte)0
                    : (byte)((constantVolume ? Volume : envDecay) & 0x0f);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Step() {
                if (!_clockFlipFlop || !enabled) return;

                if (timerCounter is 0) {
                    timerCounter = PeriodTable[periodIndex];

                    var bit0     = (ushort)( lfsr                    & 1);
                    var tap      = (ushort)((lfsr >> (mode ? 6 : 1)) & 1);
                    var feedback = (ushort)(bit0 ^ tap);

                    lfsr >>= 1;
                    lfsr |= (ushort)(feedback << 14);
                } else timerCounter--;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void QuarterFrame() {
                if (envStart) {
                    envStart   = false;
                    envDecay   = 15;
                    envDivider = Volume;
                    return;
                }

                if (envDivider is 0) {
                    envDivider = Volume;
                    if (envDecay > 0) envDecay--;
                    else if (Halt) envDecay = 15;
                } else envDivider--;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void HalfFrame() {
                if (Length > 0 && !Halt) Length--;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400C_Noise() {
                Halt           = (Data       & 0x20) is 0x20;
                constantVolume = (Data       & 0x10) is 0x10;
                Volume         = (byte)(Data & 0x0f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400E_Noise() {
                mode        = (Data       & 0x80) is 0x80;
                periodIndex = (byte)(Data & 0x0f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400F_Noise() {
                lengthIndex = (byte)((Data >> 3) & 0x1F);
                Length      = enabled ? lengthTable[lengthIndex] : (byte)0;
                envStart    = true;
            }
            
            private static bool   envStart;
            private static byte   envDivider;
            private static byte   envDecay;
            private static bool   mode;
            private static ushort lfsr = 1;
            private static byte   periodIndex;
            private static ushort timerCounter;
            private static bool   constantVolume;
            private static byte   lengthIndex;
            
            internal static bool enabled;
            internal static bool Halt;
            internal static byte Length;
            internal static byte Volume;
        }

        internal static class Triangle {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Step() {
                if (!enabled) return;

                if (timerCounter is 0) {
                    timerCounter = Timer;

                    if (Length is not 0 && linearCounter is not 0) {
                        sequencer = (byte)((sequencer + 1) & 31);
                    }
                } else {
                    timerCounter--;
                }
            }

            internal static byte GetLevel() {
                if (!enabled || Length == 0 || linearCounter == 0) return 0;
                var s  = sequencer & 0x1f;
                return (byte)(s < 0x10 ? 0x0f - s : s - 0x10);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void QuarterFrame() {
                if (linearReloadFlag) linearCounter = reloadValue;
                else if (linearCounter is not 0) linearCounter--;

                if (!control) linearReloadFlag = false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void HalfFrame() {
                if (Length > 0 && !control) Length--;
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4008_Triangle() {
                control     = (Data       & 0x80) is 0x80;
                reloadValue = (byte)(Data & 0x7f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400A_Triangle() {
                Timer &= 0xff00;
                Timer |= Data;
            }
            
            private static readonly byte[] LengthTable =
            {
                10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,
                12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30
            };

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400B_Triangle() {
                Length =  enabled ? LengthTable[(Data >> 3) & 0x1f] : (byte)0;
                Timer  &= 0x00ff;
                Timer  |= (ushort)((Data & 0x07) << 8);

                linearReloadFlag = true;
            }


            private static ushort timerCounter;
            private static byte   sequencer;
            private static byte   linearCounter;
            private static bool   linearReloadFlag;
            private static bool   control;
            private static byte   reloadValue;
            
            internal static byte   Length;
            internal static ushort Timer;
            internal static bool   enabled;
        }
        
        internal class PulseChannel {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Step() {
                if (!_clockFlipFlop || !enabled) return;

                if (timerCounter is 0) {
                    timerCounter = Timer;
                    seq          = (byte)((seq + 1) & 7);
                    return;
                }

                timerCounter--;
            }

            private static readonly byte[] DutyTable = [0b01000000, 0b01100000, 0b01111000, 0b10011111];
            
            internal byte GetLevel() {
                if (!enabled || Length is 0 || Timer < 8) return 0;

                if (Shift is not 0) {
                    var change = Timer >> Shift;
                    var target = Negate
                        ? this == Pulse1 ? (Timer - change - 1) : Timer - change
                        : Timer + change;

                    if (target > 0x7ff) return 0;
                }

                var dutyBit = (DutyTable[Duty] >> seq) & 1;
                if (dutyBit is 0) return 0;
                return ConstantVolume ? Volume : envDecay;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void QuarterFrame() {
                if (envStart) {
                    envStart   = false;
                    envDecay   = 15;
                    envDivider = Volume;
                    return;
                }

                if (envDivider is 0) {
                    envDivider = Volume;
                    if (envDecay > 0) envDecay--;
                    else if (Halt) envDecay = 15;
                } else {
                    envDivider--;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void HalfFrame() {
                if (Length > 0 && !Halt) Length--;

                var divZero = sweepDivider is 0;
                if (divZero) {
                    if (SweepEnable && Shift is not 0) {
                        var target = (ushort)(
                            Negate
                                ? this == Pulse1
                                    ? Timer - (Timer >> Shift) - 1
                                    : Timer - (Timer >> Shift)
                                : Timer + (Timer >> Shift)
                        );

                        if (Timer < 8 || target > 0x7FF) goto sweepReloadCheck;
                        Timer = target;
                    }
                }
                
                sweepReloadCheck:
                if (sweepReload || divZero) {
                    sweepDivider = Period;
                    sweepReload  = false;
                } else {
                    sweepDivider--;
                }
            }
            
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void W4000_PulseX() {
                Duty           = (byte)(Data >> 6);
                Halt           = (Data       & 0x20) is 0x20;
                ConstantVolume = (Data       & 0x10) is 0x10;
                Volume         = (byte)(Data & 0x0f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void W4001_PulseX() {
                SweepEnable = (Data & 0x80) is 0x80;
                Period      = (byte)((Data & 0x70) >> 4);
                Negate      = (Data       & 0x08) is 0x08;
                Shift       = (byte)(Data & 0x07);
                sweepReload = true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void W4002_PulseX() {
                Timer &= 0xff00;
                Timer |= Data;
            }

            private static readonly byte[] LengthTable =
            {
                10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,
                12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30
            };

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void W4003_PulseX() {
                var lengthIndex = (Data >> 3) & 0x1F;
                Length = enabled ? LengthTable[lengthIndex] : (byte)0;

                Timer &= 0x00FF;
                Timer |= (ushort)((Data & 0x07) << 8);

                envStart = true;
                seq      = 0;
            }
            
            internal bool  enabled;
            
            private ushort timerCounter;
            private byte   seq;

            private bool   envStart;
            private byte   envDivider;
            private byte   envDecay;

            private bool   sweepReload;
            private byte   sweepDivider;

            private  byte   Duty;
            private  bool   Halt;
            private  bool   ConstantVolume;
            private  byte   Volume;
            private  bool   SweepEnable;
            private  byte   Period;
            private  bool   Negate;
            private  byte   Shift;
            internal  ushort Timer;
            internal byte   Length;
        }
        
        internal static class Registers {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4000_Pulse1() => Pulse1.W4000_PulseX();
                
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4004_Pulse2() => Pulse2.W4000_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4001_Pulse1() => Pulse1.W4001_PulseX();
                
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4005_Pulse2() => Pulse2.W4001_PulseX();
                
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4002_Pulse1() => Pulse1.W4002_PulseX();
                
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4006_Pulse2() => Pulse2.W4002_PulseX();
                
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4003_Pulse1() => Pulse1.W4003_PulseX();
                
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4007_Pulse2() => Pulse2.W4003_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4008_Triangle() => Triangle.W4008_Triangle();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400A_Triangle() => Triangle.W400A_Triangle();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400B_Triangle() => Triangle.W400B_Triangle();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400C_Noise() => Noise.W400C_Noise();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400E_Noise() => Noise.W400E_Noise();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400F_Noise() => Noise.W400F_Noise();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4010_DMC() => PCM.W4010_DMC();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4011_DMC() => PCM.W4011_DMC();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4012_DMC() => PCM.W4012_DMC();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4013_DMC() => PCM.W4013_DMC();
            

            
            internal static void W4015_Status() {
                var pcmEnable    = (Data & 0x10) is 0x10;
                Noise.enabled    = (Data & 0x08) is 0x08;
                Triangle.enabled = (Data & 0x04) is 0x04;
                Pulse2.enabled   = (Data & 0x02) is 0x02;
                Pulse1.enabled   = (Data & 0x01) is 0x01;

                if (!Pulse1.enabled)   Pulse1.Length = 0;
                if (!Pulse2.enabled)   Pulse2.Length = 0;
                if (!Triangle.enabled) Triangle.Length = 0;
                if (!Noise.enabled)    Noise.Length = 0;
                PCM.IRQFlag = false;
                if (pcmEnable) {
                    PCM.enableDelay = 4;
                    if (PCM.bytesRemaining is 0) {
                        PCM.currentAddress = PCM.SampleAddress;
                        PCM.bytesRemaining = PCM.SampleLength;
                    }

                    if (!PCM.bufferEmpty || PCM.bytesRemaining is 0 || PCM.dmaRequested || PCM.inDMA) return;
                    PCM.dmaRequested = true;
                    PCM.dmaDelayCount = 1;
                } else {
                    PCM.enabled = false;
                    PCM.enableDelay = 0;
                    PCM.dmaDelayCount = 0;
                    PCM.bytesRemaining  = 0;
                    PCM.bufferEmpty     = true;
                    PCM.Silence         = true;
                    if (PCM.inDMA || PCM.dmaRequested) {
                        PCM.inDMA = false;
                        PCM.dmaRequested = false;
                        if (!PPU.inDMA) RDY = false;
                    }
                }
            }

            internal static void R4015_Status() {
                var resp = (byte)(Data & 0x20); // bit 5 is open bus (internal data bus, not external)
                
                resp |= (byte)(Pulse1.Length      is not 0 ? 0x01 : 0);
                resp |= (byte)(Pulse2.Length      is not 0 ? 0x02 : 0);
                resp |= (byte)(Triangle.Length    is not 0 ? 0x04 : 0);
                resp |= (byte)(Noise.Length       is not 0 ? 0x08 : 0);
                resp |= (byte)(PCM.bytesRemaining      > 0 ? 0x10 : 0);
                resp |= (byte)(FrameIRQAsserted || _irqFlagWindow is > 0 and <= 2 ? 0x40 : 0); // bit 6: frame IRQ pending
                resp |= (byte)(PCM.IRQFlag                 ? 0x80 : 0); // bit 7: DMC IRQ pending
                Data =  resp;

                _pendingFrameIRQClear = true;
            }

            internal static void W4017_FrameCounter() {
                UsingFiveStep = (Data & 0x80) is 0x80;
                IRQInhibit    = (Data & 0x40) is 0x40;
                if (IRQInhibit) FrameIRQAsserted = false;

                _frameCounterOddReset = !_clockFlipFlop;
                _resetFrameCounter = (byte)(_clockFlipFlop ? 3 : 2);
            }
        }

        internal static bool FrameIRQAsserted;
        internal static bool _pendingFrameIRQClear;
        internal static byte _irqWindow;
        internal static byte _irqFlagWindow;
        internal static bool _frameCounterOddReset;
        internal static byte _resetFrameCounter;
        internal static bool UsingFiveStep;
        internal static bool IRQInhibit;

        private const ushort S1 = 7457;
        private const ushort S2 = 14913;
        private const ushort S3 = 22371;
        private const ushort S4 = 29829;
        private const ushort S5 = 37281;
        

        
        internal static bool   _clockFlipFlop;
        private  static ushort _frameCounter;
    }
    
    internal static class Memory {
        private const ushort PPUCTRL   = 0x2000;
        private const ushort PPUMASK   = 0x2001;
        private const ushort PPUSTATUS = 0x2002;
        private const ushort OAMADDR   = 0x2003;
        private const ushort OAMDATA   = 0x2004;
        private const ushort PPUSCROLL = 0x2005;
        private const ushort PPUADDR   = 0x2006;
        private const ushort PPUDATA   = 0x2007;

        private const ushort PULSE1_ENVELOPE  = 0x4000;
        private const ushort PULSE1_SWEEP     = 0x4001;
        private const ushort PULSE1_TIMER     = 0x4002;
        private const ushort PULSE1_COUNTER   = 0x4003;
        private const ushort PULSE2_ENVELOPE  = 0x4004;
        private const ushort PULSE2_SWEEP     = 0x4005;
        private const ushort PULSE2_TIMER     = 0x4006;
        private const ushort PULSE2_COUNTER   = 0x4007;
        private const ushort TRIANGLE_COUNTER = 0x4008;
        private const ushort TRIANGLE_TIMER   = 0x400a;
        private const ushort TRIANGLE_LINEAR  = 0x400b;
        private const ushort NOISE_ENVELOPE   = 0x400c;
        private const ushort NOISE_MODE       = 0x400e;
        private const ushort NOISE_COUNTER    = 0x400f;
        private const ushort DMC_MODE         = 0x4010;
        private const ushort DMC_LOAD         = 0x4011;
        private const ushort DMC_ASAMPLE      = 0x4012;
        private const ushort DMC_LSAMPLE      = 0x4013;

        private const ushort OAMDMA           = 0x4014;
        private const ushort CHANNELSTATUS    = 0x4015;
        private const ushort IODEVICE1        = 0x4016;
        private const ushort IODEVICE2        = 0x4017;
        private const ushort FRAMECOUNTER     = 0x4017;

        internal static void Initialize(Func<byte> sharedBehavior) {
            for (var l = 0; l < SystemRAM.Length; l++) {
                SystemRAM[l] = sharedBehavior();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CPU_Read() => Read(Address, out Data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte DMC_Read(ushort address) {
            
            #if RELEASE         // if you break anything, this should stop hardware acting impossibly
            address |= 0xc000;
            #endif

            return Program.Cartridge.ReadByte(address);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Read(ushort address, out byte data) {
            if (address < 0x2000) {
                data = SystemRAM[address & 0x7ff];
                OpenBus = data;
                goto SendReadToCart;
            }

            if (address < 0x4000) {
                // Reads of nominally write-only registers ($2000/$2001/$2003/$2005/$2006)
                // return the PPU's internal data bus latch ("PPUGenLatch"), NOT the CPU
                // data bus. The latch reflects the last byte the PPU put on its bus —
                // any write to any PPU register, or any byte returned by $2002/$2004/$2007.
                switch (address & 0x2007) {
                    case PPUCTRL:   data = PPU.Registers.ppuLatch; OpenBus = data; goto SendReadToCart;
                    case PPUMASK:   data = PPU.Registers.ppuLatch; OpenBus = data; goto SendReadToCart;
                    case PPUSTATUS: PPU.Registers.R2002_PPUSTATUS(); data = Data; OpenBus = data; goto SendReadToCart;
                    case OAMADDR:   data = PPU.Registers.ppuLatch;            OpenBus = data; goto SendReadToCart; // write-only, returns PPU latch
                    case OAMDATA:   PPU.OAM.R2004_OAMDATA();         data = Data; OpenBus = data; goto SendReadToCart;
                    case PPUSCROLL: data = PPU.Registers.ppuLatch;            OpenBus = data; goto SendReadToCart; // write-only, returns PPU latch
                    case PPUADDR:   data = PPU.Registers.ppuLatch;            OpenBus = data; goto SendReadToCart; // write-only, returns PPU latch
                    case PPUDATA:   PPU.Registers.R2007_PPUDATA(); data = Data; OpenBus = data; goto SendReadToCart;
                    default:
                        Console.WriteLine("[CPU] [Memory] [PPU] Your programmer does not know how to use a mask");
                        Quit = true;
                        data = 0x00;
                        return;
                }
            }

            if (address > 0x4020) {
                data = Program.Cartridge.ReadByte(address);
                OpenBus = data;
                goto SendReadToCart;
            }

            switch (address) {
                case CHANNELSTATUS:
                    APU.Registers.R4015_Status();
                    data = Data;
                    goto SendReadToCart;
                case IODEVICE1:
                    data = (byte)((Program.Controller1?.OnRead() ?? 0) | (OpenBus & 0xE0));
                    goto SendReadToCart;
                case IODEVICE2:
                    data = (byte)((Program.Controller2?.OnRead() ?? 0) | (OpenBus & 0xE0));
                    goto SendReadToCart;

                default: data = OpenBus; goto SendReadToCart;
            }

            SendReadToCart:
            Program.Cartridge.ProgramRead(address);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CPU_Write() {
            OpenBus = Data;
            switch (Address) {
                case < 0x2000:
                    SystemRAM[Address & 0x7ff] = Data;
                    break;
                
                case < 0x4000:
                    switch (Address & 0x2007) {
                        case PPUCTRL:   PPU.Registers.W2000_PPUCRTL();   goto SendAddressToCart;
                        case PPUMASK:   PPU.Registers.W2001_PPUMASK();   goto SendAddressToCart;
                        case PPUSTATUS: PPU.Registers.ppuLatch = Data; goto SendAddressToCart;
                        case OAMADDR:   PPU.Registers.W2003_OAMADDR();   goto SendAddressToCart;
                        case OAMDATA:   PPU.Registers.W2004_OAMDATA();   goto SendAddressToCart;
                        case PPUSCROLL: PPU.Registers.W2005_PPUSCROLL(); goto SendAddressToCart;
                        case PPUADDR:   PPU.Registers.W2006_PPUADDR();   goto SendAddressToCart;
                        case PPUDATA:   PPU.Registers.W2007_PPUDATA();   goto SendAddressToCart;
                    }
                    break;
                
                case PULSE1_ENVELOPE : APU.Registers.W4000_Pulse1();   goto SendAddressToCart;
                case PULSE1_SWEEP    : APU.Registers.W4001_Pulse1();   goto SendAddressToCart;
                case PULSE1_TIMER    : APU.Registers.W4002_Pulse1();   goto SendAddressToCart;
                case PULSE1_COUNTER  : APU.Registers.W4003_Pulse1();   goto SendAddressToCart;
                case PULSE2_ENVELOPE : APU.Registers.W4004_Pulse2();   goto SendAddressToCart;
                case PULSE2_SWEEP    : APU.Registers.W4005_Pulse2();   goto SendAddressToCart;
                case PULSE2_TIMER    : APU.Registers.W4006_Pulse2();   goto SendAddressToCart;
                case PULSE2_COUNTER  : APU.Registers.W4007_Pulse2();   goto SendAddressToCart;
                case TRIANGLE_COUNTER: APU.Registers.W4008_Triangle(); goto SendAddressToCart;
                case TRIANGLE_TIMER  : APU.Registers.W400A_Triangle(); goto SendAddressToCart;
                case TRIANGLE_LINEAR : APU.Registers.W400B_Triangle(); goto SendAddressToCart;
                case NOISE_ENVELOPE  : APU.Registers.W400C_Noise();    goto SendAddressToCart;
                case NOISE_MODE      : APU.Registers.W400E_Noise();    goto SendAddressToCart;
                case NOISE_COUNTER   : APU.Registers.W400F_Noise();    goto SendAddressToCart;
                case DMC_MODE        : APU.Registers.W4010_DMC();      goto SendAddressToCart;
                case DMC_LOAD        : APU.Registers.W4011_DMC();      goto SendAddressToCart;
                case DMC_ASAMPLE     : APU.Registers.W4012_DMC();      goto SendAddressToCart;
                case DMC_LSAMPLE     : APU.Registers.W4013_DMC();      goto SendAddressToCart;
                case OAMDMA          : PPU.OAM.W4014_OAMDMA();         goto SendAddressToCart;;
                case CHANNELSTATUS   : APU.Registers.W4015_Status();   goto SendAddressToCart;;
                case IODEVICE1:
                    IOAssertion = (Data & 1) is 1;
                    break;
                case FRAMECOUNTER:     APU.Registers.W4017_FrameCounter(); goto SendAddressToCart;
                    
                case > 0x4020:
                    Program.Cartridge.CPUWrite();
                    break;
                
                default:
                    // TODO: open bus, nothing to write to "Actually, remember how we write to PPUSTATUS to precharge"
                    goto SendAddressToCart;
                    break;
            }
            SendAddressToCart:
            Program.Cartridge.ProgramRead(Address);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Push() {
            ref var s = ref Register.S;
            ADL = s;
            ADH = 0x01;
            DriveAddressPins();
            CPU_Write();
            s--;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Pull() {
            ref var s = ref Register.S;
            s++;
            ADL = s;
            ADH = 0x01;
            DriveAddressPins();
            CPU_Read();
        }

        internal static byte[] SystemRAM = new byte[0x800];
    }
    
    internal const ulong DOTS_PER_FRAME = 89_342;
    
    /// <summary>
    /// Begin CPU Emulation
    /// </summary>
    internal static void Initialize() {
        Program.Threads.System = new Thread(RunSystem){IsBackground = false};
        Program.Threads.System.Start();
    }
    
    private static void RunSystem() {
        Console.WriteLine("[CPU] System is Running");

        Register.AC = (byte)Random.Shared.Next();
        Register.X  = (byte)Random.Shared.Next();
        Register.Y  = (byte)Random.Shared.Next();
        Register.S  = 0xfd;
        Register.i  = true;

        Reset    = true;
        OpHandle = StepReset;
        PPU.warmupEndDot = virtualTime + PPU.WARMUP_DOTS;

        const double fps = 60.0988d;
        var frameTimeSeconds = 1.0 / fps;

        var freq = Stopwatch.Frequency;

        long frameDeadlineTick = 0;
        var lastThrottle = float.NaN;

        ulong frames = 0, lateFrames = 0;
        long nextPrint = 0;
        double worstLateMs = 0;

        var untilNextSample = 1d / SamplingFrequency;
        var secondsPerDot   = 1d / dotsPerSecond;
        int cpuDiv = 2;

        DoNotProgress:
        while (!Quit) {
            if (Debug.Debugger.debugging) {
                Debug.Debugger.ResumeEvent.Wait(1000);
                Debug.Debugger.ResumeEvent.Reset();
                goto DoNotProgress;
            } else {
                PPU.Step();
                Link.TriggerClockDrivenImplementations();
                if (++cpuDiv >= 3) {
                    cpuDiv = 0;
                    Step();
                    APU.Step();
                    APU.PCM.DMA_Step();
                    PPU.OAM.DMA();
                    if (Quit) return;
                }

                if ((untilNextSample -= secondsPerDot) <= 0d) {
                    untilNextSample  += 1d / SamplingFrequency;
                    SampleBuffer.Add(Program.AudioVolume *
                                     Program.AudioProcessor.PostProcessSample(APU.GetPCMSample()));
                }
            }

            if (PPU.FrameComplete) {
                PPU.FrameComplete = false;
                // Publish the just-finished frame to the renderer thread and
                // drain audio. These are independent of pacing — they must
                // happen every frame regardless of Throttle, otherwise we
                // produce no video and silence.
                Renderer.Present();

                if (Throttle == 1f)
                    Audio.Drain(SampleBuffer);
                else
                    SampleBuffer.Clear();

                frames++;

                // Pacing wait — only when the user requested throttling.
                // Without it we run flat-out and the CPU will pin a core.
                if (Throttle > 0f) {
                    var frameStartTick = Stopwatch.GetTimestamp();

                    var effectiveFrameSeconds = frameTimeSeconds / Throttle;
                    var frameTicks = (long)(effectiveFrameSeconds * freq);
                    if (frameTicks < 1) frameTicks = 1;

                    if (frameDeadlineTick == 0 || Throttle != lastThrottle) {
                        lastThrottle      = Throttle;
                        frameDeadlineTick = frameStartTick + frameTicks;
                    }

                    var workEndTick = Stopwatch.GetTimestamp();

                    if (workEndTick > frameDeadlineTick) {
                        lateFrames++;

                        var lateMs = (workEndTick - frameDeadlineTick) * 1000.0 / freq;
                        if (lateMs > worstLateMs) worstLateMs = lateMs;

                        if (Program.Config.Strict) {
                            Console.WriteLine("[CPU] Unable to compute in time");
                            Quit = true;
                            return;
                        }

                        var behindTicks = workEndTick              - frameDeadlineTick;
                        var missed      = behindTicks / frameTicks + 1;
                        frameDeadlineTick += missed * frameTicks;
                    } else {
                        while (Stopwatch.GetTimestamp() < frameDeadlineTick) {
                            Thread.Yield();
                        }

                        frameDeadlineTick += frameTicks;

                        var afterWait = Stopwatch.GetTimestamp();
                        if (afterWait > frameDeadlineTick) {
                            frameDeadlineTick = afterWait + frameTicks;
                        }
                    }
                }

                var now = Stopwatch.GetTimestamp();
                if (nextPrint == 0) nextPrint = now + freq;
                if (now >= nextPrint) {
                    #if DEBUG
                    Console.WriteLine($"[CPU] thr={Throttle:0.###} fps={frames:0} late={lateFrames} worstLateMs={worstLateMs:0.###}");
                    #endif
                    frames      = 0;
                    lateFrames  = 0;
                    worstLateMs = 0;
                    do nextPrint += freq; while (nextPrint <= now);
                }
            }

            ++virtualTime;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Step() {
        if (RDY) return;
        if (cycle is 0) {
            if (Reset) {
                Console.WriteLine("[CPU] Resetting CPU");
                OpHandle = StepReset;
                goto HandleInstruction;
            }
            
            if (NMIAsserted) {
                NMIAsserted = false;
                Vector      = Vectors.NMI;
                OpHandle    = Interrupt;
                goto HandleInstruction;
            }

            if (_irqDetected) {
                _irqDetected = false;
                if (!prevInterruptInhibit) {
                    Vector     = Vectors.IRQ;
                    OpHandle   = Interrupt;
                    goto HandleInstruction;
                }
            }

            FetchInstruction:
            _irqDetected = CPU_IRQ || APU.FrameIRQAsserted || APU.PCM.IRQFlag;
            prevInterruptInhibit = Register.i;
            AD          = PC;
            DriveAddressPins();

            Memory.CPU_Read();
            PC++;
            Register.IR = Data;
            OpHandle    = OpCodes.GetOpcodeSolver(Register.IR);

            // Check for breakpoints on the instruction we just fetched.
            // PC has already been incremented so the instruction address is PC-1.
            // This is the only moment the address is stable and mappable to a source line.
            if (Debug.Debugger.CheckBreakpoint((ushort)(PC - 1))) {
                Debug.Debugger.debugging = true;
                Debug.Debugger.ResumeEvent.Set();
            }

            cycle++;
            return;
        }
        
        HandleInstruction:
            OpHandle();
            #if DEBUG
            if (cycle is 0xff && OpHandle != Interrupt && OpHandle != StepReset) {
                //Console.WriteLine($"{PC:x4}: {OpCodes.Mnemonics[Register.IR]} {Address:x4} {Data:x2}");
            }
            #endif
            cycle++;
    }

    private static void StepReset() {
        switch (cycle) {
            case 0:
                AD = PC;
                DriveAddressPins();
                Memory.CPU_Read();
                break;
            
            case 1:
                ADH = 0x01;
                ADL = Register.S;
                DriveAddressPins();
                Memory.CPU_Read();
                Register.S--;
                break;
            
            case 2:
                ADH = 0x01;
                ADL = Register.S;
                DriveAddressPins();
                Memory.CPU_Read();
                Register.S--;
                break;
            
            case 3:
                ADH = 0x01;
                ADL = Register.S;
                DriveAddressPins();
                Memory.CPU_Read();
                Register.S--;
                Register.i = true;
                break;
            
            case 4:
                AD = 0xfffc;
                DriveAddressPins();
                Memory.CPU_Read();
                DB = Data;
                break;
            
            case 5:
                AD = 0xfffd;
                DriveAddressPins();
                Memory.CPU_Read();
                PCL   = DB;
                PCH   = Data;
                cycle = 0xff;
                Reset = false;
                prevInterruptInhibit = true;
                break;

            default:
                Console.WriteLine("[CPU] StepReset on incorrect cycle");
                Quit = true;
                break;
        }
    }

    private static void Interrupt() {
        switch (cycle) {
            case 0:
                AD        = PC;
                DriveAddressPins();
                Memory.CPU_Read();
                return;

            case 1:
                AD        = PC;
                DriveAddressPins();
                Memory.CPU_Read();
                break;

            case 2:
                Data = PCH;
                Memory.Push();
                break;

            case 3:
                Data = PCL;
                Memory.Push();
                break;

            case 4:
                Data =
                    (byte)((Register.c ? 1 : 0) << 0 |
                           (Register.z ? 1 : 0) << 1 |
                           (Register.i ? 1 : 0) << 2 |
                           (Register.d ? 1 : 0) << 3 |
                           0                         |
                           32                        |
                           (Register.v ? 1 : 0) << 6 |
                           (Register.n ? 1 : 0) << 7);
                Memory.Push();
                Register.i = true;
                break;

            case 5:
                if (NMIAsserted) {
                    NMIAsserted = false;
                    Vector      = Vectors.NMI;
                }
                AD = Vector;
                DriveAddressPins();
                Memory.CPU_Read();
                DB  = Data;
                PCL = DB;
                break;

            case 6:
                AD = (ushort)(Vector + 1);
                DriveAddressPins();
                Memory.CPU_Read();
                PCH   = Data;
                cycle = 0xff;
                prevInterruptInhibit = true;
                break;

            default:
                Console.WriteLine("[CPU] StepIRQ on incorrect cycle");
                Quit = true;
                break;
        }
    }


    internal static ushort Vector;
    internal static bool   CPU_IRQ;
    internal static bool   NMIAsserted;
    private static  bool   Reset;
    internal static bool   prevInterruptInhibit;
    internal static bool   _irqDetected;


    internal static Action OpHandle;
    internal static byte   cycle;

    internal static ushort Address;
    internal static byte   Data;
    internal static byte   OpenBus;
    internal static byte   DB;
    internal static byte   PCL;
    internal static byte   PCH;
    internal static byte   ADL;
    internal static byte   ADH;

    internal static ushort AD {
        get => (ushort)((ADH << 8) | ADL);
        set {
            ADH = (byte)(value >> 8);
            ADL = (byte)(value & 0xff);
        }
    }
    
    internal static ushort PC {
        get => (ushort)((PCH << 8) | PCL);
        set {
            PCH = (byte)(value >> 8);
            PCL = (byte)(value & 0xff);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DriveAddressPins()
    {
        Address = (ushort)((ADH << 8) | ADL);
    }
    
    internal static class Register {
        // Registers
        internal static byte IR = 0x00; // instruction register (stores the current operation code)
        internal static byte X  = 0x00;
        internal static byte Y  = 0x00; // index registers
        internal static byte S  = 0x00; // stack pointer
        internal static byte AC = 0x00; // accumulator
        
        // flags
        internal static bool c = false;
        internal static bool z = false;
        internal static bool i = false;
        internal static bool d = false;
        internal static bool b = false;
        internal static bool v = false;
        internal static bool n = false;
    }

    internal static class Vectors {
        internal const ushort NMI   = 0xfffa;
        internal const ushort Reset = 0xfffc;
        internal const ushort IRQ   = 0xfffe;
    }

    private const ulong  dotsPerSecond  = 5_369_318ul;

    internal static bool   RDY;
    private  const  double SECONDS_PER_FRAME    = 0d;
    internal static float  Throttle             = float.NegativeInfinity;
    internal static ulong  virtualTime          = 0;
    internal static bool   Quit                 = false;
    internal static uint   SamplingFrequency    = 48_000;
    internal static double SamplingCoefficiient = 0f;
    internal static bool   fetchOnNext;
    internal static List<float> SampleBuffer = [];
    internal static APU.PulseChannel Pulse1 = new();
    internal static APU.PulseChannel Pulse2 = new();

    internal static bool IOAssertion;
    // Debug variables
    private static string _mnemonic = string.Empty;
}