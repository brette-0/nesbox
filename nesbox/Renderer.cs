using SDL3;
using System.Runtime.InteropServices;

namespace nesbox;

/// <summary>
/// Contains all members that concern the video render.
///
/// Threading model:
///   - The emu thread writes pixels into <see cref="BackBuffer"/> as the PPU
///     produces them.
///   - On <see cref="Present"/> (called at the VBlank-start boundary) the
///     emu thread swaps back/front buffers under <see cref="_swapLock"/> and
///     signals <see cref="_frameReady"/>.
///   - The renderer thread waits on <see cref="_frameReady"/>, acquires the
///     lock just long enough to memcpy the front buffer into the SDL
///     streaming texture, then presents outside the lock (so the vsync
///     wait does not stall the emu thread).
///
/// Two-buffer design is deliberate: <see cref="SDL.UpdateTexture"/> finishes
/// in well under a millisecond on this workload, and the emu thread has
/// ~16 ms of slack between publishes, so the lock contention is negligible.
/// If we ever observe the emu thread blocking on <see cref="_swapLock"/>,
/// promote to a third buffer.
/// </summary>
internal static class Renderer {
    internal const int Width  = 256;
    internal const int Height = 240;

    internal static void Initialize() {
        Program.Threads.Renderer = new Thread(__Initialize) {
            IsBackground = false
        };
        Program.Threads.Renderer.Start();
    }

    private static void __Initialize() {
        SDL.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Gamepad);

        _window = SDL.CreateWindow("RENDER OUT", Width * 4, Height * 3, 0);

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

        _texture = SDL.CreateTexture(_renderer,
            SDL.PixelFormat.ARGB8888,
            SDL.TextureAccess.Streaming,
            Width, Height);

        if (_texture is 0) {
            Console.WriteLine($"[SDL3] Create Texture Failed: {SDL.GetError()}");
            System.Quit = true;
            return;
        }

        // Build the 512-entry colour LUT from the user shader. Done here
        // (renderer thread, after SDL is up) so a shader may also create
        // SDL resources later if it wants to.
        System.PPU.Video.BuildLUT();

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

            // Short timeout so we keep pumping events even if the emu thread
            // never publishes (e.g. while paused in the debugger).
            if (_frameReady.WaitOne(2)) {
                UploadAndPresent();
            } else if (!_SDL3VSYNCSupported) {
                Thread.Sleep(1);
            }
        }

        Console.WriteLine("PPU OUT exit");

        if (_texture is not 0) SDL.DestroyTexture(_texture);
        SDL.DestroyRenderer(_renderer);
        SDL.DestroyWindow(_window);
        Audio.Shutdown();
        SDL.Quit();
        System.Quit = true;
    }

    private static void UploadAndPresent() {
        // Hold the lock only for the upload memcpy. The vsync-blocking
        // present runs outside the lock so the emu thread is never
        // forced to wait on display timing.
        lock (_swapLock) {
            var bytes = MemoryMarshal.AsBytes(_frontBuffer.AsSpan());
            SDL.UpdateTexture(_texture, IntPtr.Zero, bytes, Width * 4);
        }

        SDL.RenderClear(_renderer);
        SDL.RenderTexture(_renderer, _texture, IntPtr.Zero, IntPtr.Zero);
        SDL.RenderPresent(_renderer);
    }

    /// <summary>
    /// Called from the emu thread once per emulated frame (at VBlank start).
    /// Atomically swaps the back/front framebuffers and wakes the render thread.
    /// </summary>
    internal static void Present() {
        lock (_swapLock) {
            (BackBuffer, _frontBuffer) = (_frontBuffer, BackBuffer);
        }
        _frameReady.Set();
    }

    /// <summary>
    /// Framebuffer the PPU writes into. Pointer is swapped on
    /// <see cref="Present"/>. ARGB8888, row-major, 256 × 240.
    /// </summary>
    internal static uint[] BackBuffer = new uint[Width * Height];

    private static uint[] _frontBuffer = new uint[Width * Height];
    private static readonly object _swapLock = new();
    private static readonly AutoResetEvent _frameReady = new(false);

    internal static volatile bool RendererReady = false;

    private static bool _SDL3VSYNCSupported;
    private static nint _window;
    private static nint _renderer;
    private static nint _texture;
}
