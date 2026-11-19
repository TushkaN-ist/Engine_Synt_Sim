using System;
using System.Threading;

namespace RpmSynth.Editor
{
    internal sealed class RecordingSpectrum
    {
        private const int FftSize = 16384;
        private const int FrequencyRows = 768;
        private const double FrequencyKnee = 40;
        public readonly int Width;
        public int Height => FrequencyRows;
        public readonly float MaxHz;
        public readonly byte[] Intensity;
        private readonly double logSpan;

        private RecordingSpectrum(int width, float maxHz)
        {
            Width = width;
            MaxHz = maxHz;
            logSpan = Math.Log(1 + maxHz / FrequencyKnee);
            Intensity = new byte[Width * Height];
        }

        public float ToAxis(float hz) => (float)(Math.Log(1 + Math.Max(0, hz) / FrequencyKnee) / logSpan);

        public float ToHz(float axis) => (float)(FrequencyKnee * (Math.Exp(axis * logSpan) - 1));

        public static RecordingSpectrum Build(float[] mono, int sampleRate, CancellationToken token)
        {
            var spectrum = new RecordingSpectrum(Math.Max(2, Math.Min(2048, (int)Math.Ceiling(mono.Length / (sampleRate * .025)) + 1)),
                sampleRate * .5f);
            int windowSize = Math.Min(FftSize, (int)Math.Round(.12 * sampleRate));
            var window = new double[windowSize];
            for (int i = 0; i < window.Length; i++)
                window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / (window.Length - 1));
            var real = new double[FftSize];
            var imag = new double[FftSize];
            var magnitude = new double[FftSize / 2 + 1];
            var levels = new float[spectrum.Intensity.Length];
            var bin = new double[FrequencyRows + 1];
            for (int y = 0; y < bin.Length; y++)
                bin[y] = Math.Min(FftSize / 2, spectrum.ToHz(y / (float)FrequencyRows) * (double)FftSize / sampleRate);
            float peak = -120;
            for (int x = 0; x < spectrum.Width; x++)
            {
                token.ThrowIfCancellationRequested();
                Array.Clear(real, 0, real.Length);
                Array.Clear(imag, 0, imag.Length);
                int start = (int)Math.Round(x * (mono.Length - 1.0) / (spectrum.Width - 1)) - windowSize / 2;
                for (int i = Math.Max(0, -start); i < Math.Min(windowSize, mono.Length - start); i++)
                    real[i] = mono[start + i] * window[i];
                EngineSoundFft.Transform(real, imag);
                for (int b = 0; b < magnitude.Length; b++)
                    magnitude[b] = Math.Sqrt(real[b] * real[b] + imag[b] * imag[b]);
                for (int y = 0; y < FrequencyRows; y++)
                {
                    double center = (bin[y] + bin[y + 1]) * .5;
                    int k = Math.Min(magnitude.Length - 2, (int)center);
                    double value = magnitude[k] + (magnitude[k + 1] - magnitude[k]) * (center - k);
                    for (int b = (int)Math.Ceiling(bin[y]); b <= (int)bin[y + 1]; b++)
                        value = Math.Max(value, magnitude[b]);
                    float db = (float)(20 * Math.Log10(1e-8 + value * 4 / windowSize));
                    levels[y * spectrum.Width + x] = db;
                    peak = Math.Max(peak, db);
                }
            }
            float ceiling = Math.Max(-45, peak);
            for (int i = 0; i < levels.Length; i++)
                spectrum.Intensity[i] = (byte)Math.Max(0, Math.Min(255, (levels[i] - ceiling + 80) * (255 / 80f)));
            return spectrum;
        }

        public float SnapHz(float timeFraction, float hz, float axisRadius)
        {
            int x = Math.Max(0, Math.Min(Width - 1, (int)Math.Round(timeFraction * (Width - 1))));
            float row = ToAxis(hz) * Height - .5f;
            int start = Math.Max(1, (int)Math.Ceiling(row - axisRadius * Height));
            int end = Math.Min(Height - 2, (int)Math.Floor(row + axisRadius * Height));
            float bestScore = float.NegativeInfinity;
            int best = -1;
            for (int y = start; y <= end; y++)
            {
                int i = y * Width + x;
                int strength = Intensity[i];
                if (strength < 12 || strength < Intensity[i - Width] || strength < Intensity[i + Width]) continue;
                float score = strength - 2 * Math.Abs(y - row);
                if (score <= bestScore) continue;
                bestScore = score;
                best = y;
            }
            return best < 0 ? hz : ToHz((best + .5f) / Height);
        }
    }

    internal readonly struct HarmonicPair
    {
        public readonly int LowerOrder, UpperOrder;
        public readonly float CycleHz;

        private HarmonicPair(int lowerOrder, int upperOrder, float cycleHz)
        {
            LowerOrder = lowerOrder;
            UpperOrder = upperOrder;
            CycleHz = cycleHz;
        }

        public static HarmonicPair FromFrequencies(float lowerHz, float upperHz, int orderGap)
        {
            if (!(lowerHz > 0 && upperHz > lowerHz) || orderGap < 1 || orderGap > 16)
                throw new ArgumentException("Select two separate lines. The second line must be above the first.");
            float step = (upperHz - lowerHz) / orderGap;
            float approximateOrder = lowerHz / step;
            if (!(approximateOrder >= .75 && approximateOrder <= 32.25))
                throw new ArgumentException("The two lines are too close for this order gap. Zoom in or select a different pair.");
            int lower = (int)Math.Round(approximateOrder);
            if (Math.Abs(approximateOrder - lower) > .25f)
                throw new ArgumentException("The spacing does not fit whole engine orders. Try a different pair or change the order gap.");
            int upper = lower + orderGap;
            float cycleHz = (lower * lowerHz + upper * upperHz) / (lower * lower + upper * upper);
            return new HarmonicPair(lower, upper, cycleHz);
        }
    }
}
