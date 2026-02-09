using System.Runtime.CompilerServices;

namespace nesbox;

internal static class Link {
    /// <summary>
    /// Helper class to contain all subscription methods for components to be triggered on a specific event
    /// </summary>
    internal static class Subscribe {

        /// <summary>
        /// Subscribes the given component to be signalled to each PPU dot
        /// </summary>
        /// <param name="ctx">The component to be signalled</param>
        internal static void OnTick(API.IClockDriven ctx) {
            ClockDrivenImplementations.Add(ctx);
        }

        internal static void ControllerToPort<T>(byte port, ref T? device) where T : API.IIO, new() {
            if (device is not null) {
                Console.WriteLine("[EMU] [LINK] [IO] Device is already configured and not Subscribable");
                System.Quit = true;
                return;
            }
            
            device = new T();
            device.SetIndex(port);

            switch (port) {
                case 0:
                    Program.Controller1 = device;
                    break;
                
                case 1:
                    Program.Controller2 = device;
                    break;
                
                default:
                    Console.WriteLine("[EMU] [LINK] [IO] Port is unsupported");
                    System.Quit = true;
                    return;
            }
        }
}

    /// <summary>
    /// Should only ever be called by System, should not be used by anyone's implementation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void TriggerClockDrivenImplementations() {
        foreach (var clockDriven in ClockDrivenImplementations) {
            clockDriven.OnTick();
        }
    }
    
    /// <summary>
    /// Components that implement IClockDriven, these will be ticked per PPU dot
    /// using virtualTime it's up to the class to decide how it handles being ticked.
    /// </summary>
    private static readonly List<API.IClockDriven> ClockDrivenImplementations = [];
}