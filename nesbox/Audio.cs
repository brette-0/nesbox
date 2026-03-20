using SDL3;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace nesbox;

internal static class Audio {
    private static nint _stream;

    // Allow up to 4 frames of latency (800 samples × 4 bytes × 4)
    private const int MaxQueueBytes = 12_800;

    // First-order high-pass filter (AC coupling), ~20 Hz cutoff at 48 kHz.
    // Removes DC offset just like the capacitor on real NES hardware.
    // y[n] = alpha * (y[n-1] + x[n] - x[n-1])
    private static float _hpfPrevIn;
    private static float _hpfPrevOut;
    private const  float HpfAlpha = 0.9974f; // RC/(RC+dt), RC=1/(2*pi*20), dt=1/48000

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float HighPass(float input) {
        var output = HpfAlpha * (_hpfPrevOut + input - _hpfPrevIn);
        _hpfPrevIn  = input;
        _hpfPrevOut = output;
        return output;
    }

    internal static void Initialize() {
        SDL.Init(SDL.InitFlags.Audio);

        var spec = new SDL.AudioSpec {
            Format   = SDL.AudioFormat.AudioF32LE,
            Channels = 1,
            Freq     = (int)System.SamplingFrequency
        };

        _stream = SDL.OpenAudioDeviceStream(
            SDL.AudioDeviceDefaultPlayback, ref spec, null, IntPtr.Zero);

        if (_stream is 0) {
            Console.WriteLine($"[Audio] Failed to open audio device: {SDL.GetError()}");
            System.Quit = true;
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

        // Apply high-pass filter in-place to remove DC offset
        var span = CollectionsMarshal.AsSpan(buffer);
        for (int i = 0; i < span.Length; i++)
            span[i] = HighPass(span[i]);

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
