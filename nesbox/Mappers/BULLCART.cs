using EList;
using nesbox;
namespace nesbox.Mappers;

internal enum NameTableArrangements : byte {
    Vertical,
    Horizontal
}

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
                
                case "--vertical":
                    NameTableArrangement = NameTableArrangements.Vertical;
                    break;
                
                case "--horizontal":
                    NameTableArrangement = NameTableArrangements.Horizontal;
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

        if (ProgramROM.Length < 0x8000) CPUReadByteTask = (self)          => self.SmallProgramCPUReadByte();
        if (ProgramROM.Length < 0x8000) ReadByteTask    = (self, address) => self.SmallProgramReadByte(address);

        args = next;
    }
    
    public void ProgramRead(ushort address) { }
    public void CPUWrite() { }
    
    public void PPURead() {
        throw new NotImplementedException();
    }
    public void PPUWrite() {
        throw new NotImplementedException();
    }

    public byte ReadByte(ushort address) => ReadByteTask(this, address);
    public byte CPUReadByte()            => CPUReadByteTask(this);

    public byte[] ProgramROM                    { get => __ProgramROM;   set => __ProgramROM = value; }
    public byte[] CharacterROM                  { get => __CharacterROM; set => __CharacterROM = value ; }
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
    private Func<BULLCART, byte> CPUReadByteTask      = (self)          => self.StandardProgramCPUReadByte();
    private Func<BULLCART, ushort, byte> ReadByteTask = (self, address) => self.StandardProgramReadByte(address);
    
    private byte SmallProgramCPUReadByte() => System.Address switch {
        < 0x8000                           => (byte)(System.Address >> 8),
        _                                  => ProgramROM[System.Address & (ProgramROM.Length - 1)]
    };
    
    private byte StandardProgramCPUReadByte() => System.Address switch {
        < 0x8000 => (byte)(System.Address >> 8),
        _        => ProgramROM[System.Address]
    };
    
    private byte SmallProgramReadByte(ushort address) => address switch {
        < 0x8000 => (byte)(address >> 8),
        _        => ProgramROM[address & (ProgramROM.Length - 1)]
    };
    
    private byte StandardProgramReadByte(ushort address) => address switch {
        < 0x8000 => (byte)(address >> 8),
        _        => ProgramROM[address]
    };
    #endregion CPUReadByte
    
    private NameTableArrangements NameTableArrangement;
}