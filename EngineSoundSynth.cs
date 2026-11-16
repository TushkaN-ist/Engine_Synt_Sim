using System;

namespace RpmSynth
{
    public sealed class EngineSoundData
    {
        public const int MechanicalPhaseBins = 128;
        public const int NoisePhaseBins = 128;
        public readonly int RevolutionsPerCycle;
        public readonly int Harmonics;
        public readonly float[] Rpm, Cosine, Sine, Residual;
        public readonly float[] MechanicalEnvelope;
        public readonly float[] ResidualSpectrum;
        public readonly float[] ResonantAttacks;
        public readonly float[] ResidualModulation;
        public readonly float[] FocusedResidualModulation;
        public readonly float[] BandResidualModulation;

        public EngineSoundData(int revolutionsPerCycle, int harmonics, float[] rpm, float[] cosine,
            float[] sine, float[] residual, float[] mechanicalEnvelope = null, float[] residualSpectrum = null,
            float[] resonantAttacks = null, float[] residualModulation = null, float[] focusedResidualModulation = null,
            float[] bandResidualModulation = null)
        {
            RevolutionsPerCycle = revolutionsPerCycle;
            Harmonics = harmonics;
            Rpm = rpm;
            Cosine = cosine;
            Sine = sine;
            Residual = residual;
            MechanicalEnvelope = mechanicalEnvelope;
            ResidualSpectrum = residualSpectrum;
            ResonantAttacks = resonantAttacks;
            ResidualModulation = residualModulation;
            FocusedResidualModulation = focusedResidualModulation;
            BandResidualModulation = bandResidualModulation;
        }

        public EngineSoundData RemapRpm(float minimum, float maximum)
        {
            if (!(minimum >= 150 && maximum >= minimum + 150 && maximum <= EngineSoundSynth.MaximumSupportedRpm))
                throw new ArgumentException("Set the saved RPM range between 150 and 30,000 RPM, with at least 150 RPM between the limits.");

            if (Rpm == null || Rpm.Length < 2 || !(Rpm[Rpm.Length - 1] > Rpm[0]))
                throw new InvalidOperationException("The profile needs at least two distinct RPM points.");

            var mapped = new float[Rpm.Length];
            mapped[0] = minimum;
            mapped[mapped.Length - 1] = maximum;
            double originalSpan = (double)Rpm[Rpm.Length - 1] - Rpm[0];
            for (int i = 1; i < mapped.Length - 1; i++)
            {
                double position = ((double)Rpm[i] - Rpm[0]) / originalSpan;
                mapped[i] = (float)(minimum + position * ((double)maximum - minimum));
                if (mapped[i] <= mapped[i - 1] || mapped[i] >= maximum)
                    throw new ArgumentException("The saved RPM range is too narrow for this profile.");
            }
            return new EngineSoundData(RevolutionsPerCycle, Harmonics, mapped, Cosine, Sine, Residual,
                MechanicalEnvelope, ResidualSpectrum, ResonantAttacks, ResidualModulation, FocusedResidualModulation,
                BandResidualModulation);
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
        private readonly float[] mechanicalEnvelope;
        private readonly float[] residualSpectrum;
        private readonly float[] residualModulation;
        private readonly float[] focusedResidualModulation;
        private readonly float[] bandResidualModulation;
        private readonly float[] resonantAttacks;
        private readonly EngineResidualNoise coloredNoise;
        private readonly float defaultBackgroundTrimDb;
        private readonly int lastRecordedKey;
        private readonly double cyclesPerRpmSample, smoothing, rpmSmoothing;
        private readonly double cyclesPerRpm;
        private readonly float lowFilter, midFilter;
        private int key;
        private double phase, currentRpm;
        private float currentLoad = 1f, currentGain, currentTexture, lowNoise, midNoise, variation;
        private float currentMechanical, mechanicalNoise;
        private float currentAttacks;
        private readonly float mechanicalFilter;
        private uint random = 0xA341316Cu;

        public EngineSoundSynth(EngineSoundData profile, int sampleRate, float initialRpm, bool interactive = false,
            float defaultBackgroundTrimDb = 0f)
        {
            rpmKeys = ExtendKeys(profile.Rpm);
            mechanicalEnvelope = GetMechanicalEnvelope(profile);
            residualSpectrum = GetResidualSpectrum(profile);
            residualModulation = GetResidualModulation(profile);
            focusedResidualModulation = GetFocusedModulation(profile);
            bandResidualModulation = GetBandModulation(profile);
            resonantAttacks = EngineAttackData.ForSampleRate(GetResonantAttacks(profile), sampleRate);
            this.defaultBackgroundTrimDb = defaultBackgroundTrimDb;
            if (residualSpectrum != null)
            {
                coloredNoise = new EngineResidualNoise(sampleRate);
                coloredNoise.SetBackgroundTrim(defaultBackgroundTrimDb, immediate: true);
            }
            lastRecordedKey = profile.Rpm.Length - 1;
            tables = BuildTables(profile, rpmKeys, sampleRate, out residual);
            fallbackRpm = profile.Rpm[0];
            currentRpm = PlaybackRpm(fallbackRpm, initialRpm);
            cyclesPerRpm = 1.0 / (60.0 * profile.RevolutionsPerCycle);
            cyclesPerRpmSample = cyclesPerRpm / sampleRate;
            smoothing = 1.0 - Math.Exp(-1.0 / (0.035 * sampleRate));
            rpmSmoothing = interactive ? 1.0 - Math.Exp(-1.0 / (0.012 * sampleRate)) : smoothing;
            lowFilter = (float)(1.0 - Math.Exp(-2.0 * Math.PI * 250 / sampleRate));
            midFilter = (float)(1.0 - Math.Exp(-2.0 * Math.PI * 1400 / sampleRate));
            mechanicalFilter = MechanicalFilter(sampleRate);
        }

        public void Render(float[] mono, float rpm, float load, float gain, float texture, float mechanicalDetail = 1f,
            float resonantDetail = 1f, float? backgroundTrimDb = null)
        {
            double targetRpm = PlaybackRpm(fallbackRpm, rpm);
            load = Unit(load);
            gain = Unit(gain);
            texture = Unit(texture);
            mechanicalDetail = DetailGain(mechanicalDetail);
            resonantDetail = DetailGain(resonantDetail);
            coloredNoise?.SetBackgroundTrim(backgroundTrimDb ?? defaultBackgroundTrimDb);

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
                int a = Math.Max(0, Math.Min(lastRecordedKey, key - 4));
                int b = Math.Min(lastRecordedKey, Math.Max(0, key - 3));
                float noise;
                float shapedAttack = 0;
                if (coloredNoise != null)
                {
                    // Texture 0.5 reproduces the measured residual level.
                    noise = 2 * RenderResidual(coloredNoise, residualSpectrum, residualModulation, focusedResidualModulation,
                        bandResidualModulation, a, b, mix, phase, random, out shapedAttack);
                }
                else
                {
                    int band = key * 3;
                    float low = Lerp(residual[band], residual[band + 3], mix);
                    float mid = Lerp(residual[band + 1], residual[band + 4], mix);
                    float high = Lerp(residual[band + 2], residual[band + 5], mix);
                    noise = 11f * low * lowNoise + 6f * mid * (midNoise - lowNoise)
                        + 1.9f * high * (white - midNoise);
                }
                float detail = 0;
                if (mechanicalEnvelope != null)
                {
                    currentMechanical += (mechanicalDetail - currentMechanical) * (float)smoothing;
                    mechanicalNoise += mechanicalFilter * (white - mechanicalNoise);
                    float envelope = ReadEnvelope(mechanicalEnvelope, a, b, mix, phase);
                    float excitation = coloredNoise != null ? shapedAttack : (white - mechanicalNoise) * 2f;
                    detail = envelope * excitation * currentMechanical;
                }
                if (resonantAttacks != null)
                {
                    currentAttacks += (resonantDetail - currentAttacks) * (float)smoothing;
                    double frequency = currentRpm * cyclesPerRpm;
                    float attacks = ReadAttacks(resonantAttacks, a, b, mix, phase, frequency);
                    detail += attacks * currentAttacks;
                }
                float sample = (tone * (1f + currentTexture * variation * 1.5f)
                    + noise * currentTexture * (0.35f + 0.65f * currentLoad) + detail)
                    * currentGain * (0.45f + 0.55f * currentLoad) * Unit((float)currentRpm / 100f);
                mono[i] = Math.Max(-1f, Math.Min(1f, sample));
                phase += currentRpm * cyclesPerRpmSample * (1.0 + currentTexture * 0.035 * variation);
                phase -= Math.Floor(phase);
            }
        }

        public static float[] RenderLoop(EngineSoundData data, float rpm, float seconds, int sampleRate,
            float mechanicalDetail = 1f, float texture = 0.5f, float resonantDetail = 1f,
            float backgroundTrimDb = 0f)
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
            var envelope = GetMechanicalEnvelope(data);
            float attackGain = DetailGain(resonantDetail);
            var attacks = attackGain > 0 ? EngineAttackData.ForSampleRate(GetResonantAttacks(data), sampleRate) : null;
            double loopFrequency = (double)cycles * sampleRate / length;
            var spectrum = GetResidualSpectrum(data);
            var modulation = GetResidualModulation(data);
            var focused = GetFocusedModulation(data);
            var bandPower = GetBandModulation(data);
            float noiseGain = Unit(texture);
            float detailGain = DetailGain(mechanicalDetail);
            var coloredNoise = spectrum != null && (noiseGain > 0 || (envelope != null && detailGain > 0))
                ? new EngineResidualNoise(sampleRate) : null;
            coloredNoise?.SetBackgroundTrim(backgroundTrimDb, immediate: true);
            float filter = MechanicalFilter(sampleRate), filtered = 0;
            // Warm the filter with the end of the same noise sequence so the WAV wraps cleanly.
            if (envelope != null && detailGain > 0 && coloredNoise == null)
                for (int i = -128; i < 0; i++)
                    filtered += filter * (LoopNoise((i % length + length) % length) - filtered);
            if (coloredNoise != null)
                for (int i = -sampleRate / 4; i < 0; i++)
                {
                    double p = (double)(((long)i * cycles % length + length) % length) / length;
                    RenderResidual(coloredNoise, spectrum, modulation, focused, bandPower, key, key + 1, mix, p,
                        LoopSeed((i % length + length) % length), out _);
                }
            for (int i = 0; i < length; i++)
            {
                double cyclePhase = (double)((long)i * cycles % length) / length;
                float sample = Read(table, cyclePhase);
                float shapedAttack = 0;
                if (coloredNoise != null)
                    sample += 2 * noiseGain * RenderResidual(coloredNoise, spectrum, modulation, focused, bandPower, key, key + 1,
                        mix, cyclePhase, LoopSeed(i), out shapedAttack);
                if (envelope != null && detailGain > 0)
                {
                    if (coloredNoise == null)
                    {
                        float white = LoopNoise(i);
                        filtered += filter * (white - filtered);
                        shapedAttack = (white - filtered) * 2f;
                    }
                    sample += ReadEnvelope(envelope, key, key + 1, mix, cyclePhase) * shapedAttack * detailGain;
                }
                if (attacks != null)
                    sample += ReadAttacks(attacks, key, key + 1, mix, cyclePhase, loopFrequency) * attackGain;
                audio[i] = sample;
            }
            return audio;
        }

        private static float[] GetMechanicalEnvelope(EngineSoundData data) =>
            data.MechanicalEnvelope?.Length == data.Rpm.Length * EngineSoundData.MechanicalPhaseBins
                ? data.MechanicalEnvelope : null;

        private static float[] GetResidualSpectrum(EngineSoundData data) =>
            data.ResidualSpectrum?.Length == data.Rpm.Length * EngineResidualNoise.BandCount
                ? data.ResidualSpectrum : null;

        private static float[] GetResidualModulation(EngineSoundData data) =>
            data.ResidualModulation?.Length == data.Rpm.Length * 2 * EngineSoundData.NoisePhaseBins
                ? data.ResidualModulation : null;

        private static float[] GetFocusedModulation(EngineSoundData data) =>
            data.FocusedResidualModulation?.Length == data.Rpm.Length * EngineSoundData.NoisePhaseBins
                ? data.FocusedResidualModulation : null;

        private static float[] GetBandModulation(EngineSoundData data) =>
            data.BandResidualModulation?.Length == data.Rpm.Length * EngineResidualNoise.BandCount * EngineSoundData.NoisePhaseBins
                ? data.BandResidualModulation : null;

        private static float RenderResidual(EngineResidualNoise noise, float[] levels, float[] modulation,
            float[] focused, float[] bandPower, int a, int b, float mix, double phase, uint seed, out float attack)
        {
            const int bins = EngineSoundData.NoisePhaseBins;
            float focusedPower = focused == null ? -1 : ReadEnvelope(focused, a, b, mix, phase, bins);
            if (bandPower != null)
                return noise.Render(levels, a, b, mix, seed, out attack, focusedPower: focusedPower,
                    bandPower: bandPower, phase: phase);
            if (modulation == null)
                return noise.Render(levels, a, b, mix, seed, out attack);
            float body = ReadEnvelope(modulation, a * 2, b * 2, mix, phase, bins);
            float high = ReadEnvelope(modulation, a * 2 + 1, b * 2 + 1, mix, phase, bins);
            return noise.Render(levels, a, b, mix, seed, out attack,
                (float)Math.Sqrt(body), (float)Math.Sqrt(high), focusedPower);
        }

        private static float[] GetResonantAttacks(EngineSoundData data) =>
            EngineAttackData.HasAttacks(data.ResonantAttacks, data.Rpm.Length) ? data.ResonantAttacks : null;

        private static float ReadAttacks(float[] attacks, int a, int b, float mix, double phase, double frequency)
        {
            float first = EngineAttackData.Read(attacks, a, phase, frequency);
            return a == b ? first : Lerp(first, EngineAttackData.Read(attacks, b, phase, frequency), mix);
        }

        private static float DetailGain(float gain) => float.IsNaN(gain) ? 0 : Math.Max(0, Math.Min(2, gain));

        private static float MechanicalFilter(int rate) => (float)(1 - Math.Exp(-2 * Math.PI * 1600 / rate));

        private static float ReadEnvelope(float[] data, int a, int b, float mix, double phase,
            int bins = EngineSoundData.MechanicalPhaseBins)
        {
            double position = phase * bins;
            int i = (int)position, j = (i + 1) % bins;
            float fraction = (float)(position - i);
            return Lerp(Lerp(data[a * bins + i], data[a * bins + j], fraction),
                Lerp(data[b * bins + i], data[b * bins + j], fraction), mix);
        }

        private static uint LoopSeed(int index)
        {
            unchecked
            {
                uint value = (uint)index + 0x9E3779B9u;
                value = (value ^ (value >> 16)) * 0x21F0AAADu;
                value = (value ^ (value >> 15)) * 0x735A2D97u;
                return value ^ (value >> 15);
            }
        }

        private static float LoopNoise(int index) => (LoopSeed(index) >> 8) * (2f / 16777216f) - 1f;

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
            int size = TableLength;
            while (size < data.Harmonics * 4) size <<= 1;
            var table = new float[size + 1];
            var real = new double[size];
            var imag = new double[size];
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
                real[h] = a * taper * 0.5;
                real[size - h] = real[h];
                imag[h] = -b * taper * 0.5;
                imag[size - h] = -imag[h];
            }
            EngineSoundFft.Transform(real, imag, inverse: true);
            for (int i = 0; i < size; i++) table[i] = (float)real[i];
            table[size] = table[0];
            return table;
        }

        private static float Read(float[] table, double phase)
        {
            double position = phase * (table.Length - 1);
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
