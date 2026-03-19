using SDL3;
using System.Runtime.InteropServices;

namespace nesbox;

internal static class Audio {
    private static nint _stream;

    // 2 frames of float32 mono at 48 kHz: 800 samples × 4 bytes × 2
    private const int TargetQueueBytes = 6_400;

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

        if (SDL.GetAudioStreamQueued(_stream) < TargetQueueBytes) {
            var bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(buffer)).ToArray();
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
