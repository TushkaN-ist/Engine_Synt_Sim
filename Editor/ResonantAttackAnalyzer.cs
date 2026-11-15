using System;

namespace RpmSynth.Editor
{
    internal sealed class ResonantAttackAnalyzer
    {
        private const int Bins = 128;
        private const int Lead = 36;
        private const int AlignmentRadius = 18;
        private readonly int cycleLength, cycleCount;
        private readonly double[] filtered;
        private readonly double[] energy, consensus, sorted;
        private readonly int[] valid;
        private readonly double[] grain = new double[EngineAttackData.Samples];
        private readonly double[] reference = new double[EngineAttackData.Samples];

        public ResonantAttackAnalyzer(int cycleLength, int cycleCount)
        {
            this.cycleLength = cycleLength;
            this.cycleCount = cycleCount;
            filtered = new double[cycleLength * cycleCount];
            energy = new double[Bins * cycleCount];
            consensus = new double[Bins];
            sorted = new double[Bins];
            valid = new int[cycleCount];
        }

        public bool Extract(double[] source, double[] harmonic, double frequency, int phaseShift, float[] output, int key)
        {
            int offset = key * EngineAttackData.Stride;
            for (int i = 0; i < EngineAttackData.Slots; i++)
                output[offset + i * (EngineAttackData.Samples + 1)] = -1;

            int count = PrepareResidual(source, harmonic, frequency);
            if (count < 2) return false;
            for (int bin = 0; bin < Bins; bin++)
            {
                double sum = 0, largest = 0;
                for (int i = 0; i < count; i++)
                {
                    double level = LocalEnergy(valid[i], bin);
                    sum += level;
                    largest = Math.Max(largest, level);
                }
                sorted[bin] = consensus[bin] = (sum - largest) / (count - 1);
            }
            Array.Sort(sorted);
            double floor = Math.Max(0.000002, sorted[Bins / 2]);
            bool found = false;
            for (int slot = 0; slot < EngineAttackData.Slots; slot++)
            {
                int bin = Array.IndexOf(consensus, Max(consensus));
                if (consensus[bin] < floor * 3.5) break;
                int target = offset + slot * (EngineAttackData.Samples + 1);
                found |= StoreGrain(count, bin, frequency, phaseShift, output, target, floor);
                int separation = Math.Min(Bins / 2, Math.Max(6, (int)Math.Ceiling(Bins * frequency * 0.005)));
                for (int i = -separation; i <= separation; i++) consensus[(bin + i + Bins) % Bins] = 0;
            }
            return found;
        }

        private int PrepareResidual(double[] source, double[] harmonic, double frequency)
        {
            Array.Clear(energy, 0, energy.Length);
            double rate = cycleLength * frequency;
            double high = Math.Exp(-2 * Math.PI * 600 / rate);
            double low = 1 - Math.Exp(-2 * Math.PI * 8500 / rate);
            double old = 0, high1 = 0, high2 = 0, low1 = 0, low2 = 0;
            bool available = false;
            for (int i = 0; i < source.Length; i++)
            {
                if (double.IsNaN(source[i]))
                {
                    available = false;
                    filtered[i] = 0;
                    continue;
                }
                double value = source[i] - harmonic[i % cycleLength];
                if (!available)
                {
                    old = value;
                    high1 = high2 = low1 = low2 = 0;
                    available = true;
                }
                double previousHigh = high1;
                high1 = high * (high1 + value - old);
                high2 = high * (high2 + high1 - previousHigh);
                old = value;
                low1 += low * (high2 - low1);
                low2 += low * (low1 - low2);
                filtered[i] = low2;
                int phaseBin = i % cycleLength * Bins / cycleLength;
                energy[i / cycleLength * Bins + phaseBin] += low2 * low2 * Bins / cycleLength;
            }
            int count = 0;
            for (int i = 0; i < cycleCount; i++)
                if (!double.IsNaN(source[i * cycleLength]) && !double.IsNaN(source[(i + 1) * cycleLength - 1]))
                    valid[count++] = i;
            return count;
        }

        private double LocalEnergy(int cycle, int bin)
        {
            int offset = cycle * Bins;
            return (energy[offset + (bin + Bins - 1) % Bins] + 2 * energy[offset + bin]
                + energy[offset + (bin + 1) % Bins]) * 0.25;
        }

        private bool StoreGrain(int count, int bin, double frequency, int phaseShift, float[] output, int target, double floor)
        {
            int best = -1;
            double typical = consensus[bin], difference = double.PositiveInfinity;
            double step = cycleLength * frequency / EngineAttackData.SampleRate;
            double at = (bin + 0.5) * cycleLength / Bins;
            for (int j = 0; j < count; j++)
            {
                int cycle = valid[j];
                double center = cycle * cycleLength + at;
                if (!Fits(center, step) || LocalEnergy(cycle, bin) < floor * 2) continue;
                double error = Math.Abs(LocalEnergy(cycle, bin) - typical);
                if (error >= difference) continue;
                difference = error;
                best = cycle;
            }
            if (best < 0) return false;
            double origin = best * cycleLength + at;
            double referenceEnergy = 0;
            for (int i = 0; i < grain.Length; i++)
            {
                reference[i] = grain[i] = Read(origin + (i - Lead) * step);
                if (i < 144 && i % 2 == 0) referenceEnergy += grain[i] * grain[i];
            }
            int mixed = 1;
            for (int j = 0; j < count; j++)
            {
                int cycle = valid[j];
                if (cycle == best || LocalEnergy(cycle, bin) < floor * 2) continue;
                double center = cycle * cycleLength + at;
                if (!Fits(center, step)) continue;
                double bestCorrelation = 0.30;
                int alignment = 0;
                for (int shift = -AlignmentRadius; shift <= AlignmentRadius; shift += 2)
                {
                    double dot = 0, power = 0;
                    for (int i = 0; i < 144; i += 2)
                    {
                        double value = Read(center + (i - Lead + shift) * step);
                        dot += reference[i] * value;
                        power += value * value;
                    }
                    double correlation = dot / Math.Sqrt(referenceEnergy * power + 1e-20);
                    if (correlation <= bestCorrelation) continue;
                    bestCorrelation = correlation;
                    alignment = shift;
                }
                if (bestCorrelation <= 0.30) continue;
                for (int i = 0; i < grain.Length; i++)
                    grain[i] += Read(center + (i - Lead + alignment) * step);
                mixed++;
            }

            double startPhase = (at - Lead * step) / cycleLength - phaseShift / 256.0;
            output[target] = (float)(startPhase - Math.Floor(startPhase));
            // Fade at both ends to retain the attack while keeping the repeated grain continuous.
            for (int i = 0; i < grain.Length; i++)
            {
                double start = Math.Min(1, i / 24.0);
                double end = Math.Min(1, (grain.Length - 1 - i) / 168.0);
                double taper = (0.5 - 0.5 * Math.Cos(Math.PI * start)) * (0.5 - 0.5 * Math.Cos(Math.PI * end));
                output[target + 1 + i] = (float)(grain[i] * taper / mixed);
            }
            return true;
        }

        private bool Fits(double center, double step) =>
            center - (Lead + AlignmentRadius) * step >= 0
            && center + (grain.Length - Lead + AlignmentRadius) * step < filtered.Length - 1;

        private double Read(double at)
        {
            int i = (int)at;
            return filtered[i] + (filtered[i + 1] - filtered[i]) * (at - i);
        }

        private static double Max(double[] values)
        {
            double max = 0;
            foreach (double value in values) max = Math.Max(value, max);
            return max;
        }
    }
}
