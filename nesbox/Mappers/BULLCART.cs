using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using EList;
using nesbox;
namespace nesbox.Mappers;

internal sealed class BULLCART : API.IFamicomCartridge {
    public BULLCART(ref EList<string> args) {
        var next = new EList<string>();

        
        
        while (args.MoveNext()) {
            switch (args.Current) {
                case "--program":
                    if (!args.MoveNext()) {
                        Console.WriteLine("[CART] No Program ROM file path specified");
                        System.Quit = true;
                        return;
                    }
                    
                    #if DEBUG
                    Console.WriteLine("Fetching Program ROM...");
                    #endif
                    
                    API.GetProgramROM(args.Current, ref __ProgramROM);
                    if (ProgramROM.Length is 0) {
                        System.Quit = true;
                        return;
                    }
                    break;
                
                case "--character":
                    if (!args.MoveNext()) {
                        Console.WriteLine("[CART] No Character ROM file path specified");
                        System.Quit = true;
                        return;
                    }
                    
                    API.GetCharacterROM(args.Current, ref __CharacterROM);
                    if (CharacterROM.Length is 0) {
                        System.Quit = true;
                        return;
                    }
                    break;
                
                default:
                    next.Add(args.Current);
                    break;
            }
        }

        if (__ProgramROM.Length is 0) {
            Console.WriteLine("[CART] No Program ROM file path specified");
            System.Quit = true;
            return;
        }
        
        
        if (ProgramROM.Length > 0x8000) {
            Console.WriteLine($"[CART] Program ROM is too large");
            System.Quit = true;
            return;
        }

        if ((ProgramROM.Length & ~0xc000) != ProgramROM.Length) {
            Console.WriteLine($"[CART] Program ROM is illegal size");
            System.Quit = true;
            return;
        }

        if (CharacterROM.Length > 0x2000) {
            Console.WriteLine($"[CART] Character ROM is too large");
            System.Quit = true;
            return;
        }

        if ((CharacterROM.Length & ~0x2000) != CharacterROM.Length) {
            Console.WriteLine($"[CART] Character ROM is illegal size");
            System.Quit = true;
            return;
        }

        if (ProgramROM.Length < 0x8000) {
            CPUProgramReadByteTask = self            => self.SmallProgramCPUReadByte();
            ProgramReadByteTask    = (self, address) => self.SmallProgramReadByte(address);
        } 
        
        if (CharacterROM.Length < 0x8000) {
            PPUCharacterReadByteTask = self            => self.SmallProgramCPUReadByte();
            CharacterReadByteTask    = (self, address) => self.SmallProgramReadByte(address);
        }

        args = next;
    }
    
    public void ProgramRead(ushort address) { }
    public void CPUWrite() { }
    
    public void PPURead() { }

    public void PPUWrite() {
        throw new NotImplementedException();
    }

    public byte ReadByte(ushort       address) => ProgramReadByteTask(this, address);
    public int  GetROMLocation(ushort address) => address;
    public byte CPUReadByte()                  => CPUProgramReadByteTask(this);
    public byte PPUReadByte()                  => PPUCharacterReadByteTask(this);

    public byte[] ProgramROM                           { get => __ProgramROM;   set => __ProgramROM = value; }
    public byte[] CharacterROM                         { get => __CharacterROM; set => __CharacterROM = value ; }
    public bool   PPUA10_11(bool a10, bool _) => a10;
    public float  ModifyAPUSignal(float signal) => signal;

    public bool  EXPO                          { get; set; }
    public bool  EXP1                          { get; set; }
    public bool  EXP2                          { get; set; }
    public bool  EXP3                          { get; set; }
    public bool  EXP4                          { get; set; }
    public bool  EXP5                          { get; set; }
    public bool  EXP6                          { get; set; }
    public bool  EXP7                          { get; set; }
    public bool  EXP8                          { get; set; }
    public bool  EXP9                          { get; set; }

    private byte[] __ProgramROM   = [];
    private byte[] __CharacterROM = [];

    #region CPUReadByte
    private Func<BULLCART, byte>         CPUProgramReadByteTask   = self            => self.StandardProgramCPUReadByte();
    private Func<BULLCART, ushort, byte> ProgramReadByteTask      = (self, address) => self.StandardProgramReadByte(address);
    private Func<BULLCART, byte>         PPUCharacterReadByteTask = self            => self.StandardCharacterPPUReadByte();
    private Func<BULLCART, ushort, byte> CharacterReadByteTask    = (self, address) => self.StandardCharacterReadByte(address);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte SmallProgramCPUReadByte() => System.Address switch {
        < 0x8000                           => (byte)(System.Address >> 8),
        _                                  => ProgramROM[System.Address & (ProgramROM.Length - 1)]
    };
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte StandardProgramCPUReadByte() => System.Address switch {
        < 0x8000 => (byte)(System.Address >> 8),
        _        => ProgramROM[System.Address - 0x8000]
    };
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte SmallCharacterPPUReadByte() => System.PPU.Registers.Address switch {
        < 0x8000 => CharacterROM[System.PPU.Registers.Address & (CharacterROM.Length - 1)],
        _        => throw new ArgumentOutOfRangeException()
    };
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte StandardCharacterPPUReadByte() => System.PPU.Registers.Address switch {
        < 0x8000 => CharacterROM[System.PPU.Registers.Address],
        _        => throw new ArgumentOutOfRangeException()
    };
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte SmallProgramReadByte(ushort address) => address switch {
        < 0x8000 => (byte)(address >> 8),
        _        => ProgramROM[address & (ProgramROM.Length - 1)]
    };
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte StandardProgramReadByte(ushort address) => address switch {
        < 0x8000 => (byte)(address >> 8),
        _        => ProgramROM[address - 0x8000]
    };
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte SmallCharacterReadByte(ushort address) => address switch {
        < 0x8000 => (byte)(address >> 8),
        _        => CharacterROM[address & (CharacterROM.Length - 1)]
    };
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte StandardCharacterReadByte(ushort address) => address switch {
        < 0x8000 => (byte)(address >> 8),
        _        => CharacterROM[address - 0x8000]
    };
    #endregion CPUReadByte
}