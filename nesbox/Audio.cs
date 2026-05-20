using SDL3;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace nesbox;

internal static class Audio {
    private static nint _stream;

    // Allow up to 4 frames of latency (800 samples × 4 bytes × 4)
    private const int MaxQueueBytes = 12_800;

    // NES output filter chain matching real hardware RC circuits:
    // 1) High-pass ~37 Hz  (DAC coupling capacitor)
    // 2) High-pass ~440 Hz (output coupling capacitor)
    // 3) Low-pass  ~14 kHz (output RC filter — tames noise and square wave harmonics)

    // HPF: y[n] = alpha * (y[n-1] + x[n] - x[n-1]),  alpha = RC/(RC+dt)
    private static float _hpf1PrevIn, _hpf1PrevOut;
    private static float _hpf2PrevIn, _hpf2PrevOut;
    private const  float Hpf1Alpha = 0.99881f; // ~37 Hz cutoff at 48 kHz
    private const  float Hpf2Alpha = 0.99429f; // ~440 Hz cutoff at 48 kHz

    // LPF: y[n] = alpha * x[n] + (1-alpha) * y[n-1],  alpha = dt/(RC+dt)
    private static float _lpfPrev;
    private const  float LpfAlpha = 0.64774f; // ~14 kHz cutoff at 48 kHz

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Filter(float input) {
        // HPF 37 Hz
        var hp1 = Hpf1Alpha * (_hpf1PrevOut + input - _hpf1PrevIn);
        _hpf1PrevIn  = input;
        _hpf1PrevOut = hp1;

        // HPF 440 Hz
        var hp2 = Hpf2Alpha * (_hpf2PrevOut + hp1 - _hpf2PrevIn);
        _hpf2PrevIn  = hp1;
        _hpf2PrevOut = hp2;

        // LPF 14 kHz
        _lpfPrev = LpfAlpha * hp2 + (1f - LpfAlpha) * _lpfPrev;
        return _lpfPrev;
    }

    internal static void Initialize() {
        SDL.Init(SDL.InitFlags.Audio);

        var spec = new SDL.AudioSpec {
            Format   = SDL.AudioFormat.AudioF32LE,
            Channels = 1,
            Freq     = (int)Emulator.System.SamplingFrequency
        };

        _stream = SDL.OpenAudioDeviceStream(
            SDL.AudioDeviceDefaultPlayback, ref spec, null, IntPtr.Zero);

        if (_stream is 0) {
            Console.WriteLine($"[Audio] Failed to open audio device: {SDL.GetError()}");
            Emulator.System.Quit = true;
            return;
        }

        SDL.ResumeAudioStreamDevice(_stream);
        Console.WriteLine("[Audio] Initialized");
    }

    internal static void Drain(List<float> buffer) {
        if (_stream is 0 || buffer.Count is 0) {
            buffer.Clear();
            return;
        }

        // Apply NES output filter chain in-place
        var span = CollectionsMarshal.AsSpan(buffer);
        for (int i = 0; i < span.Length; i++)
            span[i] = Filter(span[i]);

        // Always send audio unless queue is severely backed up
        if (SDL.GetAudioStreamQueued(_stream) < MaxQueueBytes) {
            var bytes = MemoryMarshal.AsBytes(span).ToArray();
            SDL.PutAudioStreamData(_stream, bytes, bytes.Length);
        }

        buffer.Clear();
    }

    internal static void Shutdown() {
        if (_stream is 0) return;
        SDL.DestroyAudioStream(_stream);
        _stream = 0;
    }
}
