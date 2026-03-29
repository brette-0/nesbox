namespace nesbox;
using SDL3;

/// <summary>
/// Contains all members that concern the video render
/// </summary>
internal static class Renderer {
    internal static void Initialize() {
            Program.Threads.Renderer = new Thread(__Initialize) {
                IsBackground = false
            };
            Program.Threads.Renderer.Start();
        }

        private static void __Initialize() {
            SDL.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Gamepad);
            
            _window   = SDL.CreateWindow("RENDER OUT", 256 * 4, 240 * 3, 0);

            if (_window is 0) {
                Console.WriteLine($"[SDL3] Create Window Failed: {SDL.GetError()}");
                System.Quit = true;
                return;
            }
            
            _renderer = SDL.CreateRenderer(_window, null);
            if (_renderer is 0) {
                Console.WriteLine($"[SDL3] Create Renderer Failed: {SDL.GetError()}");
                System.Quit = true;
                return;
            }
            
            if (!(_SDL3VSYNCSupported = SDL.SetRenderVSync(_renderer, 1))) {
                Console.WriteLine($"[SDL3] VSync not supported: {SDL.GetError()}");
            }
            
            
            Console.WriteLine("RENDER OUT init");
            RendererReady = true;
            Lifetime();
        }

        // Gamepad handles opened on this (renderer/event) thread.
        // IO code on the emu thread reads button state from these — thread-safe per SDL3 docs.
        internal static nint Gamepad0;
        internal static nint Gamepad1;

        private static void OnGamepadAdded(uint which) {
            var gp = SDL.OpenGamepad(which);
            if (gp is 0) return;
            var name = SDL.GetGamepadName(gp) ?? "Unknown";

            if (Gamepad0 is 0) {
                Gamepad0 = gp;
                Console.WriteLine($"[IO] Gamepad connected to port 0: {name}");
            } else if (Gamepad1 is 0) {
                Gamepad1 = gp;
                Console.WriteLine($"[IO] Gamepad connected to port 1: {name}");
            } else {
                SDL.CloseGamepad(gp);
            }
        }

        private static void OnGamepadRemoved(uint which) {
            // SDL gives us the joystick instance ID of the removed device.
            // Check which slot it belongs to.
            if (Gamepad0 is not 0 && SDL.GetGamepadID(Gamepad0) == which) {
                Console.WriteLine("[IO] Gamepad disconnected from port 0");
                SDL.CloseGamepad(Gamepad0);
                Gamepad0 = 0;
            } else if (Gamepad1 is not 0 && SDL.GetGamepadID(Gamepad1) == which) {
                Console.WriteLine("[IO] Gamepad disconnected from port 1");
                SDL.CloseGamepad(Gamepad1);
                Gamepad1 = 0;
            }
        }

        private static void Lifetime() {
            SDL.Event ev;
            var       running = true;
            
            while (running) {
                while (SDL.PollEvent(out ev)) {
                    switch ((SDL.EventType)ev.Type) {
                        case SDL.EventType.Quit:
                            running = false;
                            break;
                        case SDL.EventType.GamepadAdded:
                            OnGamepadAdded(ev.GDevice.Which);
                            break;
                        case SDL.EventType.GamepadRemoved:
                            OnGamepadRemoved(ev.GDevice.Which);
                            break;
                    }
                }
                
                SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 255);
                SDL.RenderClear(_renderer);
                SDL.RenderPresent(_renderer);
                
                if (!_SDL3VSYNCSupported) {
                    Thread.Sleep(1);
                }
            }
            
            Console.WriteLine("PPU OUT exit");
            
            SDL.DestroyRenderer(_renderer);
            SDL.DestroyWindow(_window);
            Audio.Shutdown();
            SDL.Quit();
            System.Quit = true;
        }

        internal static void Present() {
            
        }

        internal static volatile bool RendererReady = false;
        
        private static bool    _SDL3VSYNCSupported;
        private static nint    _window;
        private static nint    _renderer;
}