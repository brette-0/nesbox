using System.Runtime.CompilerServices;

namespace nesbox.Emulator;

internal static partial class System {
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

        // ── Per-dot sprite evaluation state machine ─────────────────
        // State: 0=clearing secOAM, 1=evaluating, 2=overflow scan, 3=done
        private static byte sprEvalState;
        private static byte sprSecOamAddr;      // secondary OAM byte write pointer (0-31)
        private static int  sprEvalN;           // primary OAM sprite index (0-63)
        private static int  sprEvalStartN;      // starting sprite index (for wrap detection)
        private static byte sprEvalM;           // byte offset for overflow bug (0-3)
        private static byte sprEvalStep;        // logical copy phase: 0=Y check, 1-3=copy
        private static byte sprOamStartM;       // misalignment: OAMAddress & 3
        internal static byte sprEvalLatch;      // data latch (visible to R2004)
        private static bool sprZeroOnLine;      // first evaluation at dot 66 found in-range
        private static int  sprEvalNextLine;    // which scanline we're evaluating for
        private static bool sprEvalFirstDone;   // has the first sprite been checked at dot 66?
        private static int  sprEvalCount;       // sprites found during current evaluation (applied at dot 257)

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

        /// <summary>
        /// Initialize sprite evaluation state at dot 1 of visible/pre-render lines.
        /// </summary>
        private static void SprEvalInit(bool isPreRender, int line) {
            sprEvalState     = 0; // start with secondary OAM clear
            sprSecOamAddr    = 0;
            // DON'T read OAMAddress here — it may be written via $2003
            // during the clear phase (dots 1-64). We capture it at dot 65.
            sprEvalM         = 0;
            sprEvalStep      = 0;
            sprEvalLatch     = 0xFF;
            sprZeroOnLine    = false;
            sprEvalFirstDone = false;
            sprEvalCount     = 0;  // don't touch spriteCount — rendering needs it
            sprEvalNextLine  = isPreRender ? 0 : line + 1;
            // DO NOT clear spriteIsZero here — the current scanline's
            // rendering (dots 1-256) still uses the values loaded at
            // the previous dot 257. We update them at the next dot 257.
        }

        /// <summary>
        /// Per-dot sprite evaluation tick. Called once per dot during dots 1-256
        /// on visible and pre-render scanlines when rendering is enabled.
        /// </summary>
        private static void SprEvalTick(int dot) {
            // ─── Phase 1: Secondary OAM clear (dots 1-64) ───
            if (dot <= 64) {
                if ((dot & 1) == 0) {
                    // Even dot: write $FF to secondary OAM
                    secondaryOAM[sprSecOamAddr & 31] = 0xFF;
                    sprSecOamAddr++;
                }
                sprEvalLatch = 0xFF; // $2004 reads see $FF during clear
                return;
            }

            // ─── Transition to evaluation at dot 65 ───
            if (dot == 65) {
                sprEvalState  = 1; // evaluating
                sprSecOamAddr = 0;
                // Capture OAMAddress NOW — writes to $2003 during clear phase take effect
                sprEvalN      = OAMAddress >> 2;
                sprEvalStartN = sprEvalN;
                sprOamStartM  = (byte)(OAMAddress & 3);
            }

            if (sprEvalState == 3) return; // evaluation complete

            var spriteHeight = (PPUCTRL & 0x20) != 0 ? 16 : 8;

            // ─── Phase 2 & 3: Evaluation / overflow (dots 65-256) ───
            if ((dot & 1) == 1) {
                // Odd dot: read from primary OAM into latch
                int addr;
                if (sprEvalState == 2) // overflow mode uses N*4 + M
                    addr = (sprEvalN * 4 + sprEvalM) & 0xFF;
                else // normal mode: N*4 + misalignment + logical step
                    addr = (sprEvalN * 4 + sprOamStartM + sprEvalStep) & 0xFF;
                sprEvalLatch = OAMBuffer[addr];
            } else {
                // Even dot: process the read
                if (sprEvalState == 1) {
                    // ── Normal evaluation ──
                    if (sprEvalStep == 0) {
                        // Y-position range check
                        var top  = sprEvalLatch + 1;
                        var diff = sprEvalNextLine - top;
                        if (diff >= 0 && diff < spriteHeight) {
                            // Sprite is in range — copy Y to secondary OAM
                            secondaryOAM[sprSecOamAddr & 31] = sprEvalLatch;
                            sprSecOamAddr++;
                            sprEvalStep = 1;
                            // First evaluation: this sprite is "sprite zero"
                            if (!sprEvalFirstDone) {
                                sprZeroOnLine = true;
                            }
                            sprEvalFirstDone = true;
                        } else {
                            // Not in range — advance to next sprite
                            sprEvalFirstDone = true;
                            sprEvalN = (sprEvalN + 1) & 63;
                            if (sprEvalN == sprEvalStartN)
                                sprEvalState = 3; // wrapped through all 64
                        }
                    } else {
                        // Steps 1-3: copy remaining bytes to secondary OAM
                        secondaryOAM[sprSecOamAddr & 31] = sprEvalLatch;
                        sprSecOamAddr++;
                        sprEvalStep++;
                        if (sprEvalStep >= 4) {
                            sprEvalStep = 0;
                            sprEvalCount++;
                            sprEvalN = (sprEvalN + 1) & 63;
                            if (sprEvalCount >= 8)
                                sprEvalState = 2; // switch to overflow scan
                            if (sprEvalN == sprEvalStartN)
                                sprEvalState = 3; // wrapped around all 64
                        }
                    }
                } else if (sprEvalState == 2) {
                    // ── Overflow scan (with hardware sprite overflow bug) ──
                    // Reads byte at N*4+M; on miss, increments BOTH N and M
                    var top  = sprEvalLatch + 1;
                    var diff = sprEvalNextLine - top;
                    if (diff >= 0 && diff < spriteHeight) {
                        spriteOverflow = true;
                        sprEvalState = 3; // done
                    } else {
                        // The bug: both N and M increment on miss
                        sprEvalN = (sprEvalN + 1) & 63;
                        sprEvalM = (byte)((sprEvalM + 1) & 3);
                        if (sprEvalN == sprEvalStartN)
                            sprEvalState = 3; // wrapped
                    }
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

            vblankJustSet     = false;
            vblankJustCleared = false;

            if (dot == 0 && line == 0) Video.ResetStream();

            var isVisible   = line < 240;
            var isPreRender = line == 261;
            var isFetchLine = isVisible || isPreRender;

            // ── Pre-render clear ─────────────────────────────────────
            if (isPreRender && dot == 1) {
                vblankJustCleared = inVblank; // remember if VBlank was set
                inVblank         = false;
                nmiLine          = false;
                spriteZeroHit    = false;
                spriteOverflow   = false;
                vblankSuppressed = false;
            }

            // ── VBlank set ───────────────────────────────────────────
            if (line == 241 && dot == 1 && virtualTime >= warmupEndDot) {
                vblankJustSet       = true;
                if (!vblankSuppressed) {
                    inVblank = true;
                    EdgeDetectNMI();
                }
                vblankSuppressed = false;
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

                // ─── Per-dot sprite evaluation (dots 1-256) ───
                if ((isVisible || isPreRender) && dot >= 1 && dot <= 256) {
                    if (dot == 1) SprEvalInit(isPreRender, line);
                    SprEvalTick(dot);
                }

                // ─── Horizontal scroll reload + sprite shift register load ───
                if (dot == 257) {
                    CopyHorizontalBits();
                    if (isVisible || isPreRender) {
                        // Real PPU clears OAMADDR at dot 257
                        OAMAddress = 0;
                        // Apply evaluation results for the NEXT scanline's rendering
                        spriteCount = sprEvalCount;
                        for (int i = 0; i < 8; i++) spriteIsZero[i] = false;
                        if (sprZeroOnLine && spriteCount > 0)
                            spriteIsZero[0] = true;
                        LoadSpriteShifters(sprEvalNextLine);
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
            else if (!newLine && nmiLine && !_nmiPending)
                // Falling edge before the CPU latched the NMI:
                // the /NMI line went low (e.g. W2000 disabled NMI)
                // before the CPU's edge-detector committed the
                // interrupt.  Cancel the assertion.
                NMIAsserted = false;
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
            // they return.
            //
            // Decay: the PPU data bus is capacitive — bits that haven't been
            // refreshed eventually decay to 0.  We track the PPU cycle at
            // which each bit was last driven and clear it after a fixed
            // number of PPU cycles (~600 000 ≈ 6.7 frames).
            // ----------------------------------------------------------------
            internal static byte ppuLatch;

            /// PPU-cycle timestamp of last refresh, per bit.
            private static ulong[] _latchBitTime = new ulong[8];
            private const ulong LatchDecayCycles = 600_000; // ~6.7 frames

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void RefreshLatch(byte value) {
                var now = virtualTime;
                for (int b = 0; b < 8; b++) {
                    if (((value >> b) & 1) != 0)
                        _latchBitTime[b] = now;
                }
                ppuLatch = value;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static byte ReadLatch() {
                var now = virtualTime;
                byte result = ppuLatch;
                for (int b = 0; b < 8; b++) {
                    if (now - _latchBitTime[b] >= LatchDecayCycles)
                        result &= (byte)~(1 << b);
                }
                ppuLatch = result;   // write back decayed value
                return result;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2000_PPUCRTL() {
                RefreshLatch(Data);
                PPUCTRL    = Data;
                NMIEnabled = (Data & 0x80) is 0x80;
                // Nametable select bits drop into t bits 10-11.
                tempVRAMAddr = (ushort)((tempVRAMAddr & 0xF3FF) | ((Data & 0x03) << 10));
                EdgeDetectNMI();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2001_PPUMASK() {
                RefreshLatch(Data);
                PPUMASK  = Data;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void R2002_PPUSTATUS() {
                // ── VBlank suppression (pre-emptive + retroactive) ──
                // Reading $2002 on the same CPU cycle that VBlank is
                // set suppresses the flag (bit 7 returns 0).
                //
                // Pre-emptive: VBlank hasn't fired yet but will on the
                // next PPU step.  Guard so PPU.Step() won't set it.
                if (_line == 241 && _dot == 1 && virtualTime >= warmupEndDot) {
                    vblankSuppressed = true;
                }
                // Retroactive: VBlank was the last PPU step before
                // this CPU step.  Suppress the flag but preserve
                // NMIAsserted — on real hardware the NMI edge has
                // already been captured even though the flag is
                // suppressed.
                if (vblankJustSet) {
                    inVblank = false;
                    nmiLine  = false;
                }

                // Low 5 bits come from the PPU's internal latch (open bus on
                // the unused bits). Bits 7-5 are the live status flags.
                // Reading $2002 only refreshes bits 7-5 of the latch.
                //
                // vblankJustCleared: if the pre-render clear just ran this
                // tick, the CPU should still see VBlank as set (the clear
                // hasn't "happened yet" from the CPU's perspective within
                // the same cycle — matching real hardware behavior).
                Data        = (byte)(ReadLatch() & 0x1f);
                Data       |= (byte)((inVblank || vblankJustCleared) ? 0x80 : 0x00);
                Data       |= (byte)(spriteZeroHit  ? 0x40 : 0x00);
                Data       |= (byte)(spriteOverflow ? 0x20 : 0x00);
                RefreshLatch((byte)((ppuLatch & 0x1F) | (Data & 0xE0)));
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
                RefreshLatch(Data);
                OAMAddress = Data;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2004_OAMDATA() {
                RefreshLatch(Data);
                // During rendering, writes to $2004 don't modify OAM.
                // Instead, OAMAddress gets a glitchy increment: +4 with
                // the low 2 bits cleared (only the high 6 bits bump).
                if (RenderingEnabled && (_line < 240 || _line == 261)) {
                    OAMAddress = (byte)((OAMAddress + 4) & 0xFC);
                } else {
                    OAMBuffer[OAMAddress++] = Data;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2005_PPUSCROLL() {
                RefreshLatch(Data);
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
                RefreshLatch(Data);
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
                RefreshLatch(Data);
                Address  = v;
                PPUBusWrite(v, Data);
                if (RenderingEnabled && (_line < 240 || _line == 261)) {
                    IncrementCoarseX();
                    IncrementFineY();
                } else {
                    v = (ushort)((v + (((PPUCTRL & 0x04) is 0) ? 1 : 32)) & 0x3FFF);
                }
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
                    // When greyscale is enabled (PPUMASK bit 0), the lower 4 bits
                    // of the palette value read back as zero (masked with $30).
                    var raw       = (byte)(PPUBusRead(v) & 0x3F);
                    if ((PPUMASK & 0x01) is not 0) raw &= 0x30;
                    Data          = (byte)(raw | (ppuLatch & 0xC0));
                    ppuDataBuffer = PPUBusRead((ushort)(v - 0x1000));
                }
                RefreshLatch(Data);        // latch refreshes with the byte just returned

                // v increment: during rendering, a $2007 read triggers
                // both the coarse-X and fine-Y increments simultaneously
                // (the same pair that normally fires at dots 256/257).
                // Outside of rendering, the normal +1 or +32 applies.
                if (RenderingEnabled && (_line < 240 || _line == 261)) {
                    IncrementCoarseX();
                    IncrementFineY();
                } else {
                    v = (ushort)((v + (((PPUCTRL & 0x04) is 0) ? 1 : 32)) & 0x3FFF);
                }
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
                // During rendering, $2004 reads come from the evaluation pipeline
                if (RenderingEnabled && (_line < 240 || _line == 261)) {
                    if (_dot >= 1 && _dot <= 64) {
                        // Secondary OAM clear phase: always returns $FF
                        Data = 0xFF;
                    } else if (_dot >= 65 && _dot <= 256) {
                        // Evaluation phase: returns the current evaluation latch
                        Data = sprEvalLatch;
                    } else {
                        Data = OAMBuffer[OAMAddress];
                        // Attribute byte masking only applies to normal OAM reads
                        if ((OAMAddress & 0x03) == 2) Data &= 0xE3;
                    }
                } else {
                    Data = OAMBuffer[OAMAddress];
                    // Attribute bytes (byte 2 of each 4-byte entry) have bits 2-4
                    // unimplemented — they always read as 0.
                    if ((OAMAddress & 0x03) == 2) Data &= 0xE3;
                }
                Registers.RefreshLatch(Data);
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
        /// <summary>
        /// Set during the PPU dot that VBlank flag is raised (line 241, dot 1).
        /// If the CPU reads $2002 on that same tick, VBlank is "suppressed":
        /// the read returns $00 and the flag is never actually set.
        /// Cleared at the start of every PPU.Step().
        /// </summary>
        private static bool    vblankJustSet;
        private static bool    vblankJustCleared;
        private static bool    vblankSuppressed;
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
}