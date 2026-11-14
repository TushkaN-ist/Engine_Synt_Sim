using System;

namespace RpmSynth
{
    public sealed class EngineSoundData
    {
        public readonly int RevolutionsPerCycle;
        public readonly int Harmonics;
        public readonly float[] Rpm, Cosine, Sine, Residual;

        public EngineSoundData(int revolutionsPerCycle, int harmonics, float[] rpm, float[] cosine,
            float[] sine, float[] residual)
        {
            RevolutionsPerCycle = revolutionsPerCycle;
            Harmonics = harmonics;
            Rpm = rpm;
            Cosine = cosine;
            Sine = sine;
            Residual = residual;
        }
    }

    public sealed class EngineSoundSynth
    {
        internal const int TableLength = 512;
        public const float MinimumLoopRpm = 100;
        public const float MaximumSupportedRpm = 30000;
        private readonly float[] rpmKeys, residual;
        private readonly float fallbackRpm;
        private readonly float[][] tables;
        private readonly double cyclesPerRpmSample, smoothing, rpmSmoothing;
        private readonly float lowFilter, midFilter;
        private int key;
        private double phase, currentRpm;
        private float currentLoad = 1f, currentGain, currentTexture, lowNoise, midNoise, variation;
        private uint random = 0xA341316Cu;

        public EngineSoundSynth(EngineSoundData profile, int sampleRate, float initialRpm, bool interactive = false)
        {
            rpmKeys = ExtendKeys(profile.Rpm);
            tables = BuildTables(profile, rpmKeys, sampleRate, out residual);
            fallbackRpm = profile.Rpm[0];
            currentRpm = PlaybackRpm(fallbackRpm, initialRpm);
            cyclesPerRpmSample = 1.0 / (60.0 * profile.RevolutionsPerCycle * sampleRate);
            smoothing = 1.0 - Math.Exp(-1.0 / (0.035 * sampleRate));
            rpmSmoothing = interactive ? 1.0 - Math.Exp(-1.0 / (0.012 * sampleRate)) : smoothing;
            lowFilter = (float)(1.0 - Math.Exp(-2.0 * Math.PI * 250 / sampleRate));
            midFilter = (float)(1.0 - Math.Exp(-2.0 * Math.PI * 1400 / sampleRate));
        }

        public void Render(float[] mono, float rpm, float load, float gain, float texture)
        {
            double targetRpm = PlaybackRpm(fallbackRpm, rpm);
            load = Unit(load);
            gain = Unit(gain);
            texture = Unit(texture);

            for (int i = 0; i < mono.Length; i++)
            {
                currentRpm += (targetRpm - currentRpm) * rpmSmoothing;
                currentLoad += (load - currentLoad) * (float)smoothing;
                currentGain += (gain - currentGain) * (float)smoothing;
                currentTexture += (texture - currentTexture) * (float)smoothing;
                while (key < rpmKeys.Length - 2 && currentRpm > rpmKeys[key + 1]) key++;
                while (key > 0 && currentRpm < rpmKeys[key]) key--;
                float mix = (float)((currentRpm - rpmKeys[key]) / (rpmKeys[key + 1] - rpmKeys[key]));
                float tone = Lerp(Read(tables[key], phase), Read(tables[key + 1], phase), mix);

                // Filter one white-noise source into the same bands the analyzer measures.
                random ^= random << 13;
                random ^= random >> 17;
                random ^= random << 5;
                float white = (random >> 8) * (2f / 16777216f) - 1f;
                lowNoise += lowFilter * (white - lowNoise);
                midNoise += midFilter * (white - midNoise);
                variation += 0.001f * (white - variation);
                int band = key * 3;
                float low = Lerp(residual[band], residual[band + 3], mix);
                float mid = Lerp(residual[band + 1], residual[band + 4], mix);
                float high = Lerp(residual[band + 2], residual[band + 5], mix);
                float noise = 11f * low * lowNoise + 6f * mid * (midNoise - lowNoise)
                    + 1.9f * high * (white - midNoise);
                float sample = (tone * (1f + currentTexture * variation * 1.5f)
                    + noise * currentTexture * (0.35f + 0.65f * currentLoad))
                    * currentGain * (0.45f + 0.55f * currentLoad) * Unit((float)currentRpm / 100f);
                mono[i] = Math.Max(-1f, Math.Min(1f, sample));
                phase += currentRpm * cyclesPerRpmSample * (1.0 + currentTexture * 0.035 * variation);
                phase -= Math.Floor(phase);
            }
        }

        public static float[] RenderLoop(EngineSoundData data, float rpm, float seconds, int sampleRate)
        {
            if (sampleRate < 8000 || sampleRate > 192000 || !(seconds > 0 && seconds <= 30))
                throw new ArgumentOutOfRangeException(nameof(seconds), "Use 8–192 kHz and a duration up to 30 seconds.");
            rpm = PlaybackRpm(data.Rpm[0], rpm);
            if (rpm < MinimumLoopRpm)
                throw new ArgumentOutOfRangeException(nameof(rpm), "WAV loops require at least 100 RPM.");
            double frequency = rpm / (60.0 * data.RevolutionsPerCycle);
            int cycles = Math.Max(1, (int)Math.Round(seconds * frequency));
            int length = (int)Math.Round(cycles * sampleRate / frequency);
            float timbreRpm = Math.Max(data.Rpm[0], Math.Min(data.Rpm[data.Rpm.Length - 1], rpm));
            int key = Array.BinarySearch(data.Rpm, timbreRpm);
            key = key < 0 ? ~key - 1 : Math.Min(key, data.Rpm.Length - 2);
            float mix = (timbreRpm - data.Rpm[key]) / (data.Rpm[key + 1] - data.Rpm[key]);
            GetExtension(data.Rpm, rpm, out float below, out float above);
            float[] table = BuildTable(data, key, mix, (double)cycles * sampleRate / length, sampleRate, below, above);
            var audio = new float[length];
            for (int i = 0; i < length; i++)
                audio[i] = Read(table, (double)((long)i * cycles % length) / length);
            return audio;
        }

        private static float[] ExtendKeys(float[] recorded)
        {
            float high = recorded[recorded.Length - 1];
            int extra = Math.Max(0, (int)Math.Ceiling(Math.Log((double)MaximumSupportedRpm / high) / Math.Log(1.125)));
            var keys = new float[recorded.Length + 4 + extra];
            for (int i = 0; i < 4; i++) keys[i] = recorded[0] * i / 4;
            Array.Copy(recorded, 0, keys, 4, recorded.Length);
            for (int i = recorded.Length + 4; i < keys.Length; i++)
                keys[i] = i == keys.Length - 1 ? MaximumSupportedRpm : keys[i - 1] * 1.125f;
            return keys;
        }

        private static float[][] BuildTables(EngineSoundData data, float[] keys, int sampleRate, out float[] noise)
        {
            var result = new float[keys.Length][];
            noise = new float[keys.Length * 3];
            for (int i = 0; i < result.Length; i++)
            {
                int recorded = Math.Max(0, Math.Min(data.Rpm.Length - 1, i - 4));
                double highestFrequency = keys[Math.Min(i + 1, result.Length - 1)]
                    / (60.0 * data.RevolutionsPerCycle);
                GetExtension(data.Rpm, keys[i], out float below, out float above);
                result[i] = BuildTable(data, recorded, 0, highestFrequency * 1.002, sampleRate, below, above);
                noise[i * 3] = data.Residual[recorded * 3] * (1f + 0.10f * below);
                noise[i * 3 + 1] = data.Residual[recorded * 3 + 1] * (1f - 0.15f * below + 0.10f * above);
                noise[i * 3 + 2] = data.Residual[recorded * 3 + 2] * (1f - 0.35f * below + 0.20f * above);
            }
            return result;
        }

        private static void GetExtension(float[] recorded, float rpm, out float below, out float above)
        {
            float low = recorded[0], high = recorded[recorded.Length - 1];
            below = SmoothStep((low - rpm) / (0.75f * low));
            above = SmoothStep((rpm - high) / (0.65f * high));
        }

        private static float SmoothStep(float x)
        {
            x = Unit(x);
            return x * x * (3 - 2 * x);
        }

        private static float[] BuildTable(EngineSoundData data, int key, float mix, double frequency, int rate,
            float below, float above)
        {
            var table = new float[TableLength + 1];
            for (int h = 1; h <= data.Harmonics && h * frequency < rate * 0.48; h++)
            {
                int index = key * data.Harmonics + h - 1;
                float a = data.Cosine[index], b = data.Sine[index];
                if (mix > 0)
                {
                    a = Lerp(a, data.Cosine[index + data.Harmonics], mix);
                    b = Lerp(b, data.Sine[index + data.Harmonics], mix);
                }
                float taper = Unit((float)((rate * 0.48 - h * frequency) / (rate * 0.08)));
                // Retain the pulse at low RPM and soften the upper orders at high RPM.
                float upperOrders = h / (h + 12f);
                taper *= (1f - 0.30f * below) * (1f + 0.24f * below * upperOrders)
                    * (1f - 0.30f * above * upperOrders);
                double angle = 2 * Math.PI * h / TableLength;
                double stepCos = Math.Cos(angle), stepSin = Math.Sin(angle), cos = 1, sin = 0;
                for (int i = 0; i < TableLength; i++)
                {
                    table[i] += (float)(a * cos + b * sin) * taper;
                    double nextCos = cos * stepCos - sin * stepSin;
                    sin = sin * stepCos + cos * stepSin;
                    cos = nextCos;
                }
            }
            table[TableLength] = table[0];
            return table;
        }

        private static float Read(float[] table, double phase)
        {
            double position = phase * TableLength;
            int index = (int)position;
            return Lerp(table[index], table[index + 1], (float)(position - index));
        }

        private static float PlaybackRpm(float fallback, float rpm)
        {
            if (float.IsNaN(rpm) || float.IsInfinity(rpm)) return fallback;
            return Math.Max(0, Math.Min(MaximumSupportedRpm, rpm));
        }

        private static float Unit(float x) => float.IsNaN(x) ? 0 : Math.Max(0, Math.Min(1, x));
        private static float Lerp(float a, float b, float mix) => a + (b - a) * mix;
    }
}
