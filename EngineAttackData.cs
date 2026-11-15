using System;

namespace RpmSynth
{
    public static class EngineAttackData
    {
        public const int SampleRate = 24000;
        public const int Samples = 336;
        public const int Slots = 2;
        public const int Stride = Slots * (Samples + 1);

        public static bool HasAttacks(float[] data, int rpmCount) =>
            data?.Length == rpmCount * Stride;

        internal static float[] ForSampleRate(float[] data, int sampleRate)
        {
            if (data == null || sampleRate >= SampleRate) return data;
            var filtered = (float[])data.Clone();
            // Band-limit the stored waveforms before reading them at a lower output rate.
            float factor = (float)(1 - Math.Exp(-2 * Math.PI * sampleRate * 0.18 / SampleRate));
            for (int offset = 0; offset < filtered.Length; offset += Samples + 1)
            {
                if (filtered[offset] < 0) continue;
                Filter(filtered, offset + 1, 1, factor);
                Filter(filtered, offset + Samples, -1, factor);
                for (int i = 0; i < 24; i++)
                {
                    float fade = i / 24f;
                    filtered[offset + 1 + i] *= fade;
                    filtered[offset + Samples - i] *= fade;
                }
            }
            return filtered;
        }

        private static void Filter(float[] signal, int start, int step, float factor)
        {
            float a = 0, b = 0;
            for (int i = 0; i < Samples; i++)
            {
                int index = start + i * step;
                a += factor * (signal[index] - a);
                b += factor * (a - b);
                signal[index] = b;
            }
        }

        internal static float Read(float[] data, int key, double phase, double frequency)
        {
            if (frequency <= 0) return 0;
            double samplesPerCycle = SampleRate / frequency;
            float result = 0;
            for (int slot = 0; slot < Slots; slot++)
            {
                int offset = key * Stride + slot * (Samples + 1);
                float startPhase = data[offset];
                if (startPhase < 0) continue;
                double elapsed = phase - startPhase;
                if (elapsed < 0) elapsed += 1;
                for (double position = elapsed * samplesPerCycle; position < Samples - 1; position += samplesPerCycle)
                {
                    int i = (int)position;
                    float a = data[offset + 1 + i], b = data[offset + 2 + i];
                    result += a + (b - a) * (float)(position - i);
                }
            }
            return result;
        }
    }
}
