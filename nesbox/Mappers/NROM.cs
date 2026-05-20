using System.Runtime.CompilerServices;
using EList;
using nesbox.Emulator;
namespace nesbox.Mappers;

internal sealed class NROM : API.INESCartridge {
    public NROM(ref EList<string> args) {
        var next = new EList<string>();

        while (args.MoveNext()) {
            switch (args.Current) {
                case "--program":
                    if (!args.MoveNext()) {
                        Console.WriteLine("[CART] No Program ROM file path specified");
                        Emulator.System.Quit = true;
                        return;
                    }

                    #if DEBUG
                    Console.WriteLine("Fetching Program ROM...");
                    #endif

                    API.GetProgramROM(args.Current, ref __ProgramROM);
                    if (ProgramROM.Length is 0) {
                        Emulator.System.Quit = true;
                        return;
                    }
                    break;

                case "--character":
                    if (!args.MoveNext()) {
                        Console.WriteLine("[CART] No Character ROM file path specified");
                        Emulator.System.Quit = true;
                        return;
                    }

                    API.GetCharacterROM(args.Current, ref __CharacterROM);
                    if (CharacterROM.Length is 0) {
                        Emulator.System.Quit = true;
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
            Emulator.System.Quit = true;
            return;
        }


        if (ProgramROM.Length > 0x8000) {
            Console.WriteLine($"[CART] Program ROM is too large");
            Emulator.System.Quit = true;
            return;
        }

        if ((ProgramROM.Length & (ProgramROM.Length - 1)) is not 0) {
            Console.WriteLine($"[CART] Program ROM is illegal size");
            Emulator.System.Quit = true;
            return;
        }

        if (CharacterROM.Length > 0x2000) {
            Console.WriteLine($"[CART] Character ROM is too large");
            Emulator.System.Quit = true;
            return;
        }

        if ((CharacterROM.Length & (CharacterROM.Length - 1)) is not 0) {
            Console.WriteLine($"[CART] Character ROM is illegal size");
            Emulator.System.Quit = true;
            return;
        }

        args = next;
    }

    public void ProgramRead(ushort address) { }
    public void CPUWrite() { }

    public void PPURead() { }

    public void PPUWrite() {
        // CHR-ROM: writes are ignored (read-only)
    }

    public int GetROMLocation(ushort address) => address;

    /// <summary>
    /// CPU-side PRG read. Below $8000 the cart's not selected — return open
    /// bus (high byte of the address). At/above $8000, mirror into PRG-ROM
    /// by masking with (Length - 1). Length is power-of-two by construction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte CPUReadByte() {
        var addr = Emulator.System.Address;
        return addr < 0x8000
            ? Emulator.System.OpenBus
            : ProgramROM[(addr - 0x8000) & (ProgramROM.Length - 1)];
    }

    /// <summary>
    /// Same mapping as <see cref="CPUReadByte"/> but for explicit-address
    /// callers (DMC sample fetch, debugger peeks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte(ushort address) =>
        address < 0x8000
            ? Emulator.System.OpenBus
            : ProgramROM[(address - 0x8000) & (ProgramROM.Length - 1)];

    /// <summary>
    /// PPU-side CHR read. Caller is expected to drive the bus with a pattern
    /// table address (&lt; $2000); anything higher is the PPU's own VRAM /
    /// palette and never reaches the cart in normal operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte PPUReadByte() {
        var addr = Emulator.System.PPU.Registers.Address;
        if (CharacterROM.Length is 0) return 0;   // no CHR-ROM, bus floats — open-bus stub
        return CharacterROM[addr & (CharacterROM.Length - 1)];
    }

    public byte[] ProgramROM   { get => __ProgramROM;   set => __ProgramROM = value; }
    public byte[] CharacterROM { get => __CharacterROM; set => __CharacterROM = value; }
    public bool   PPUA10_11(bool a10, bool _) => a10;

    public void F_NT()    { }
    public void F_AT()    { }
    public void F_TA()    { }
    public void F_TB()    { }
    public void OBJ_NT()  { }
    public void OBJ_TA()  { }
    public void OBJ_TB()  { }
    public void A12_Rise(){ }
    public void A12_Fall(){ }

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
}
