using System.Runtime.CompilerServices;

namespace nesbox.Emulator;

internal static partial class System {
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
                    case PPUCTRL:   data = PPU.Registers.ReadLatch(); OpenBus = data; goto SendReadToCart;
                    case PPUMASK:   data = PPU.Registers.ReadLatch(); OpenBus = data; goto SendReadToCart;
                    case PPUSTATUS: PPU.Registers.R2002_PPUSTATUS(); data = Data; OpenBus = data; goto SendReadToCart;
                    case OAMADDR:   data = PPU.Registers.ReadLatch();            OpenBus = data; goto SendReadToCart; // write-only, returns PPU latch
                    case OAMDATA:   PPU.OAM.R2004_OAMDATA();         data = Data; OpenBus = data; goto SendReadToCart;
                    case PPUSCROLL: data = PPU.Registers.ReadLatch();            OpenBus = data; goto SendReadToCart; // write-only, returns PPU latch
                    case PPUADDR:   data = PPU.Registers.ReadLatch();            OpenBus = data; goto SendReadToCart; // write-only, returns PPU latch
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
                        case PPUSTATUS: PPU.Registers.RefreshLatch(Data); goto SendAddressToCart;
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
}