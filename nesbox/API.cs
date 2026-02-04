namespace nesbox;

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

    internal interface ICartridge {
        
        /// <summary>
        /// Invoked on emulator load
        /// </summary>
        public void Initialize();
        
        /// <summary>
        /// Expects information from Address, is contextualized as a read
        /// </summary>
        public void ReadMemory();

        /// <summary>
        /// Expects information from Address, is contextualized as a write
        /// </summary>
        public void WriteMemory();
    }
}