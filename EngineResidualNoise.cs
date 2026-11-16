using System;

namespace RpmSynth
{
    public sealed class EngineResidualNoise
    {
        private static readonly int[] Edges = { 40, 90, 180, 355, 710, 1100, 1600, 2400, 3500, 5000, 7000, 10000, 14000, 20000 };
        public const int BandCount = 13;
        public const int BodyModulationStartHz = 710;
        public const int HighModulationStartHz = 2400;
        public const float MaximumBackgroundTrimDb = 18f;
        private static readonly float[] FocusWeights = BuildFocusWeights();
        private readonly Band[] bands;
        private readonly int sampleRate;
        private readonly float trimSmoothing;
        private Shelf lowerShelf, upperShelf;
        private float trimDb;
        private bool trimActive;

        private static float[] BuildFocusWeights()
        {
            var weights = new float[BandCount];
            for (int i = 0; i < weights.Length; i++)
            {
                int low = Math.Max(3000, Edges[i]), high = Math.Min(8000, Edges[i + 1]);
                if (high > low)
                    weights[i] = (float)(Math.Log((double)high / low) /
                        Math.Log((double)Edges[i + 1] / Edges[i]));
            }
            return weights;
        }

        private struct Shelf
        {
            private float b0, b1, b2, a1, a2, z1, z2;
            private float targetB0, targetB1, targetB2, targetA1, targetA2;

            public void Set(double hz, int rate, float gainDb, bool immediate)
            {
                double a = Math.Pow(10, gainDb / 40.0);
                double omega = 2 * Math.PI * hz / rate;
                double cos = Math.Cos(omega);
                double alpha = Math.Sin(omega) * Math.Sqrt(2 * a);
                double a0 = (a + 1) - (a - 1) * cos + alpha;
                targetB0 = (float)(a * ((a + 1) + (a - 1) * cos + alpha) / a0);
                targetB1 = (float)(-2 * a * ((a - 1) + (a + 1) * cos) / a0);
                targetB2 = (float)(a * ((a + 1) + (a - 1) * cos - alpha) / a0);
                targetA1 = (float)(2 * ((a - 1) - (a + 1) * cos) / a0);
                targetA2 = (float)(((a + 1) - (a - 1) * cos - alpha) / a0);
                if (immediate)
                {
                    b0 = targetB0;
                    b1 = targetB1;
                    b2 = targetB2;
                    a1 = targetA1;
                    a2 = targetA2;
                }
            }

            public float Process(float input, float smooth)
            {
                b0 += (targetB0 - b0) * smooth;
                b1 += (targetB1 - b1) * smooth;
                b2 += (targetB2 - b2) * smooth;
                a1 += (targetA1 - a1) * smooth;
                a2 += (targetA2 - a2) * smooth;
                float result = b0 * input + z1;
                z1 = b1 * input - a1 * result + z2;
                z2 = b2 * input - a2 * result;
                return result;
            }
        }

        internal void SetBackgroundTrim(float db, bool immediate = false)
        {
            db = ClampBackgroundTrim(db);
            if (db == trimDb) return;
            trimDb = db;
            trimActive = true;
            lowerShelf.Set(1900, sampleRate, -db, immediate);
            if (sampleRate > 19000) upperShelf.Set(8400, sampleRate, db, immediate);
        }

        internal static float ClampBackgroundTrim(float db) =>
            float.IsNaN(db) ? 0 : Math.Max(0, Math.Min(MaximumBackgroundTrimDb, db));

        private struct Section
        {
            private float z1, z2;

            public float Process(float input, float b, float a1, float a2)
            {
                float output = b * input + z1;
                z1 = z2 - a1 * output;
                z2 = -b * input - a2 * output;
                return output;
            }
        }

        private struct Band
        {
            public float B, A1, A2, Gain;
            private Section first, second, third;

            public float Process(float input)
            {
                float value = first.Process(input, B, A1, A2);
                value = second.Process(value, B, A1, A2);
                return third.Process(value, B, A1, A2);
            }
        }

        internal EngineResidualNoise(int sampleRate)
        {
            this.sampleRate = sampleRate;
            trimSmoothing = (float)(1 - Math.Exp(-1.0 / (0.035 * sampleRate)));
            lowerShelf.Set(1900, sampleRate, 0, true);
            if (sampleRate > 19000) upperShelf.Set(8400, sampleRate, 0, true);
            bands = new Band[BandCount];
            for (int i = 0; i < bands.Length && Edges[i] < sampleRate * 0.47; i++)
            {
                double lo = Math.Tan(Math.PI * Edges[i] / sampleRate);
                double hi = Math.Tan(Math.PI * Math.Min(Edges[i + 1], sampleRate * 0.48) / sampleRate);
                double center = Math.Sqrt(lo * hi);
                double q = center / (hi - lo) * Math.Sqrt(Math.Pow(2, 1.0 / 3) - 1);
                double omega = 2 * Math.Atan(center);
                double alpha = Math.Sin(omega) / (2 * q);
                var filter = new Band
                {
                    B = (float)(alpha / (1 + alpha)),
                    A1 = (float)(-2 * Math.Cos(omega) / (1 + alpha)),
                    A2 = (float)((1 - alpha) / (1 + alpha))
                };
                var impulse = filter;
                double sum = 0;
                for (int n = 0; n < sampleRate / 6; n++)
                {
                    double amplitude = impulse.Process(n == 0 ? 1 : 0);
                    sum += amplitude * amplitude;
                }
                // Convert a unit RMS target into gain for a uniform [-1, 1] excitation.
                filter.Gain = (float)Math.Sqrt(3 / sum);
                bands[i] = filter;
            }
        }

        public static int GetBand(double hz)
        {
            if (hz < Edges[0] || hz >= Edges[BandCount]) return -1;
            int i = 0;
            while (hz >= Edges[i + 1]) i++;
            return i;
        }

        public static int LowerEdge(int band) => Edges[band];
        public static int UpperEdge(int band) => Edges[band + 1];

        internal float Render(float[] levels, int key, int nextKey, float mix, uint seed, out float attack,
            float bodyModulation = 1, float highModulation = 1, float focusedPower = -1,
            float[] bandPower = null, double phase = 0)
        {
            float sum = 0;
            float high = 0, highPower = 0;
            int left = key * BandCount, right = nextKey * BandCount;
            const int phaseBins = EngineSoundData.NoisePhaseBins;
            double position = phase * phaseBins;
            int phaseIndex = (int)position, nextPhase = (phaseIndex + 1) % phaseBins;
            float phaseMix = (float)(position - phaseIndex);
            unchecked
            {
                seed = (seed ^ (seed >> 16)) * 0x21F0AAADu;
                seed = (seed ^ (seed >> 15)) * 0x735A2D97u;
            }
            for (int i = 0; i < bands.Length && bands[i].Gain != 0; i++)
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                float white = (seed >> 8) * (2f / 16777216f) - 1f;
                ref Band band = ref bands[i];
                float level = levels[left + i];
                level += (levels[right + i] - level) * mix;
                float value = band.Process(white) * band.Gain * level;
                float modulation = Edges[i] >= HighModulationStartHz ? highModulation
                    : Edges[i] >= BodyModulationStartHz ? bodyModulation : 1f;
                if (bandPower != null)
                {
                    int a = (left + i) * phaseBins, b = (right + i) * phaseBins;
                    float aPower = bandPower[a + phaseIndex];
                    float bPower = bandPower[b + phaseIndex];
                    aPower += (bandPower[a + nextPhase] - aPower) * phaseMix;
                    bPower += (bandPower[b + nextPhase] - bPower) * phaseMix;
                    modulation = (float)Math.Sqrt(aPower + (bPower - aPower) * mix);
                }
                if (focusedPower >= 0 && FocusWeights[i] > 0)
                {
                    float highEnergy = modulation * modulation;
                    modulation = (float)Math.Sqrt(highEnergy + (focusedPower - highEnergy) * FocusWeights[i]);
                }
                sum += value * modulation;
                if (Edges[i] >= 1600)
                {
                    high += value;
                    highPower += level * level;
                }
            }
            // Reuse the measured upper spectrum for phase-gated attacks so they cannot add a white hiss.
            attack = high / Math.Max(0.005f, (float)Math.Sqrt(highPower));
            if (trimActive)
            {
                sum = lowerShelf.Process(sum, trimSmoothing);
                if (sampleRate > 19000) sum = upperShelf.Process(sum, trimSmoothing);
            }
            return sum;
        }
    }
}
