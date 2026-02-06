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
            SDL.Init(SDL.InitFlags.Video);
            
            _window   = SDL.CreateWindow("RENDER OUT", 256, 240, 0);

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
            Lifetime();
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
            SDL.Quit();
            System.Quit = true;
        }

        internal static void Present() {
            
        }

        private static bool    _SDL3VSYNCSupported;
        private static nint    _window;
        private static nint    _renderer;
}