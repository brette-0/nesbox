using NAudio.Wave;

namespace nesbox;

internal static class Audio {
    public sealed class SineWaveProvider16 : IWaveProvider
    {
        private readonly WaveFormat _format;
        private          double     _phase;
        private          double     _phaseStep; // radians per sample
        private volatile float      _volume;    // 0..1

        public SineWaveProvider16(int sampleRate = 48000, int channels = 1)
        {
            _format     = new WaveFormat(sampleRate, 16, channels);
            FrequencyHz = 440.0;
            Volume      = 0.2f;
        }

        public WaveFormat WaveFormat => _format;

        public double FrequencyHz
        {
            get => _phaseStep * _format.SampleRate / (2.0 * Math.PI);
            set => _phaseStep = 2.0                       * Math.PI * value / _format.SampleRate;
        }

        public float Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0f, 1f);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            // 16-bit PCM: 2 bytes per sample per channel
            int bytesPerFrame = 2     * _format.Channels;
            int frameCount    = count / bytesPerFrame;

            int   outIndex = offset;
            float vol      = _volume;

            for (int i = 0; i < frameCount; i++)
            {
                float sample = (float)Math.Sin(_phase) * vol;
                _phase += _phaseStep;
                if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;

                short s16 = (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);

                // write same sample to all channels (mono->stereo duplication if channels=2)
                for (int ch = 0; ch < _format.Channels; ch++)
                {
                    buffer[outIndex++] = (byte)(s16        & 0xFF);
                    buffer[outIndex++] = (byte)((s16 >> 8) & 0xFF);
                }
            }

            // return exact byte count produced (whole frames)
            return frameCount * bytesPerFrame;
        }
    }
}