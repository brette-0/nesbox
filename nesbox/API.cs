using System.Diagnostics.Contracts;

namespace nesbox;

using EList;

/// <summary>
/// Contains all methods that may interface components designed by user with the emulator
/// </summary>
internal static class API {
    
    /// <summary>
    /// Type of component that is signalled per PPU dot.
    /// </summary>
    internal interface IClockDriven {
        void OnTick();
    }

    private static void GetFile(string fp, ref byte[] fileObject, string taskName) {
        try {
            fileObject   = File.ReadAllBytes(fp);;
        } catch (Exception e) {
            switch (e) {
                case FileNotFoundException:
                    Console.WriteLine($"[EMU] File for {taskName} not found");
                    break;
                
                default:
                    Console.WriteLine("[EMU] Unknown File IO error");
                    break;
            }
        }
    }
    
    internal static void GetProgramROM(string fp, ref byte[] ProgramROM) => GetFile(fp, ref ProgramROM, "Program ROM");
    internal static void GetCharacterROM(string fp, ref byte[] CharacterROM) => GetFile(fp, ref CharacterROM, "Character ROM");

    internal interface IIO {
        public void OnWrite();
        public byte OnRead();
        public void SetIndex(byte Index);
    }

    public class HasIRQLine {
        public void SetIRQLine(bool assertion) => System.CPU.IRQPending = assertion;
        public void DeassertIRQ()              => System.CPU.IRQPending = false;
        public void AssertIRQ()                => System.CPU.IRQPending = true;
    }

    internal interface ICartridge {
        /// <summary>
        /// Expects information from CPU Address, is contextualized as a read
        /// </summary>
        public void CPURead();

        /// <summary>
        /// Expects information from CPU Address, is contextualized as a write
        /// </summary>
        public void CPUWrite();
        
        /// <summary>
        /// Expects information from PPU Address, is contextualized as a read
        /// </summary>
        public void PPURead();

        /// <summary>
        /// Expects information from PPU Address, is contextualized as a write
        /// </summary>
        public void PPUWrite();
        
        /// <summary>
        /// This should not trigger internal hardware for on-reads, but should only return the information at the location
        /// CPURead will always be invoked immediately after
        /// </summary>
        /// <returns></returns>
        [Pure] public byte CPUReadByte();
        
        public byte[] ProgramROM   { get; set; }
        public byte[] CharacterROM { get; set; }
    }
}