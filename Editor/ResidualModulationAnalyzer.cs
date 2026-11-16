using System;

namespace RpmSynth.Editor
{
    internal sealed class ResidualModulationAnalyzer
    {
        private const int Bins = EngineSoundData.NoisePhaseBins;
        private const int Orders = 24;
        private readonly int cycleLength, cycleCount;
        private readonly double[] residual, filtered;
        private readonly double[] runPower = new double[Bins];
        private readonly double[] real = new double[Bins], imaginary = new double[Bins];
        private readonly double[] sumReal = new double[Orders + 1], sumImaginary = new double[Orders + 1];
        private readonly double[] individualPower = new double[Orders + 1];

        internal ResidualModulationAnalyzer(int cycleLength, int cycleCount)
        {
            this.cycleLength = cycleLength;
            this.cycleCount = cycleCount;
            residual = new double[cycleLength * cycleCount];
            filtered = new double[residual.Length];
        }

        internal void Extract(double[] samples, double[] harmonic, double frequency, int sourceRate, int shift,
            float[] output, float[] bands, int key)
        {
            for (int i = 0; i < residual.Length; i++)
                residual[i] = samples[i] - harmonic[i % cycleLength];
            double rate = frequency * cycleLength;
            ExtractBand(rate, EngineResidualNoise.BodyModulationStartHz, EngineResidualNoise.HighModulationStartHz,
                shift, output, key * 2 * Bins);
            ExtractBand(rate, EngineResidualNoise.HighModulationStartHz, 14000, shift, output, (key * 2 + 1) * Bins);
            double usableHz = Math.Min(rate, sourceRate) * 0.44;
            for (int band = 0; band < EngineResidualNoise.BandCount; band++)
            {
                double low = EngineResidualNoise.LowerEdge(band);
                double high = Math.Min(usableHz, EngineResidualNoise.UpperEdge(band));
                int orderLimit = Math.Max(0, Math.Min(Orders, (int)((high - low) * 0.45 / frequency)));
                int offset = (key * EngineResidualNoise.BandCount + band) * Bins;
                if (orderLimit == 0)
                    for (int i = 0; i < Bins; i++) bands[offset + i] = 1;
                else
                    ExtractBand(rate, low, high, shift, bands, offset, orderLimit, adaptive: true);
            }
        }

        private void ExtractBand(double rate, double low, double high, int shift, float[] output, int offset,
            int orderLimit = Orders, bool adaptive = false)
        {
            Array.Copy(residual, filtered, residual.Length);
            Filter(filtered, rate, low, highPass: true, reverse: false);
            Filter(filtered, rate, low, highPass: true, reverse: true);
            Filter(filtered, rate, high, highPass: false, reverse: false);
            Filter(filtered, rate, high, highPass: false, reverse: true);

            Array.Clear(sumReal, 0, sumReal.Length);
            Array.Clear(sumImaginary, 0, sumImaginary.Length);
            Array.Clear(individualPower, 0, individualPower.Length);
            int valid = 0;
            for (int cycle = 0; cycle < cycleCount; cycle++)
            {
                int start = cycle * cycleLength;
                if (double.IsNaN(filtered[start]) || double.IsNaN(filtered[start + cycleLength - 1])) continue;
                Array.Clear(real, 0, real.Length);
                Array.Clear(imaginary, 0, imaginary.Length);
                double energy = 0;
                for (int i = 0; i < cycleLength; i++)
                {
                    double value = filtered[start + i];
                    real[i * Bins / cycleLength] += value * value;
                    energy += value * value;
                }
                if (energy <= cycleLength * 1e-10) continue;
                valid++;
                EngineSoundFft.Transform(real, imaginary);
                for (int h = 1; h <= orderLimit; h++)
                {
                    double a = real[h] / energy, b = imaginary[h] / energy;
                    sumReal[h] += a;
                    sumImaginary[h] += b;
                    individualPower[h] += a * a + b * b;
                }
            }

            if (valid < 2)
            {
                for (int i = 0; i < Bins; i++) output[offset + i] = 1;
                return;
            }

            // Cross-cycle power rejects modulation whose phase moves randomly from one cycle to the next.
            Array.Clear(real, 0, real.Length);
            Array.Clear(imaginary, 0, imaginary.Length);
            real[0] = 1;
            double taperWidth = adaptive ? Math.Min(9, orderLimit * 0.35) : 9;
            for (int h = 1; h <= orderLimit; h++)
            {
                double coherent = sumReal[h] * sumReal[h] + sumImaginary[h] * sumImaginary[h];
                double incoherent = individualPower[h] / Math.Max(1e-12, coherent);
                double agreement = adaptive
                    ? Math.Sqrt(Math.Max(0, Math.Min(1, (1 - 1.15 * incoherent) * valid / (valid - 1))))
                    : Math.Sqrt(Math.Max(0, 1 - incoherent));
                double scale = agreement * Math.Min(1, (orderLimit + 1 - h) / taperWidth) / valid;
                double angle = h * 2 * Math.PI * shift / 256;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                real[h] = real[Bins - h] = scale * (sumReal[h] * cos - sumImaginary[h] * sin);
                imaginary[h] = scale * (sumReal[h] * sin + sumImaginary[h] * cos);
                imaginary[Bins - h] = -imaginary[h];
            }
            EngineSoundFft.Transform(real, imaginary, inverse: true);
            double total = 0;
            for (int i = 0; i < Bins; i++)
            {
                runPower[i] = Math.Max(0.22, Math.Min(2.8, real[i]));
                total += runPower[i];
            }
            for (int i = 0; i < Bins; i++)
                output[offset + i] = (float)(runPower[i] * Bins / total);
        }

        private static void Filter(double[] data, double rate, double frequency, bool highPass, bool reverse)
        {
            double angle = 2 * Math.PI * Math.Min(frequency, rate * 0.45) / rate;
            double cos = Math.Cos(angle);
            double alpha = Math.Sin(angle) / Math.Sqrt(2);
            double a0 = 1 + alpha, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
            double b0 = (highPass ? 1 + cos : 1 - cos) / (2 * a0);
            double b1 = (highPass ? -2 : 2) * b0;
            double z1 = 0, z2 = 0;
            for (int n = 0; n < data.Length; n++)
            {
                int i = reverse ? data.Length - 1 - n : n;
                double value = data[i];
                if (double.IsNaN(value))
                {
                    z1 = z2 = 0;
                    continue;
                }
                double result = b0 * value + z1;
                z1 = b1 * value - a1 * result + z2;
                z2 = b0 * value - a2 * result;
                data[i] = result;
            }
        }
    }
}
