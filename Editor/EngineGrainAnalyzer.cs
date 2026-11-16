using System;
using System.Collections.Generic;

namespace RpmSynth.Editor
{
    internal static class EngineGrainAnalyzer
    {
        public static EngineGrainData Extract(float[] source, int sourceRate, float[] times, float[] rpm,
            int revolutions, int storageRate, int maximumGrains)
        {
            if (storageRate < 8000 || storageRate > sourceRate || maximumGrains < 4)
                throw new ArgumentException("Use a grain rate between 8 kHz and the source rate, and at least four grains.");
            double step = times[1] - times[0];
            var cycles = new double[rpm.Length];
            for (int i = 1; i < cycles.Length; i++)
                cycles[i] = cycles[i - 1] + .5 * (rpm[i - 1] + rpm[i]) * step / (60 * revolutions);
            var starts = new List<int>();
            for (int c = 0; c + 2 <= cycles[cycles.Length - 1]; c++)
                if (TimeAt(c, cycles, times, rpm, revolutions) >= times[0])
                    starts.Add(c);
            if (starts.Count < 4) throw new InvalidOperationException("The source needs at least five complete engine cycles.");
            var filtered = LowPass(source, sourceRate, storageRate * .44);
            int count = Math.Min(maximumGrains, starts.Count);
            var rootRpm = new float[count];
            var grains = new short[count][];
            var reference = new double[512];
            for (int g = 0; g < count; g++)
            {
                double first = starts[(int)Math.Round(g * (starts.Count - 1.0) / (count - 1))];
                double middleTime = TimeAt(first + 1, cycles, times, rpm, revolutions);
                int key = Math.Min(rpm.Length - 2, Math.Max(0, (int)((middleTime - times[0]) / step)));
                float mix = (float)((middleTime - times[key]) / (times[key + 1] - times[key]));
                rootRpm[g] = rpm[key] + (rpm[key + 1] - rpm[key]) * mix;
                double shift = 0;
                if (g > 0)
                {
                    double best = double.NegativeInfinity;
                    for (int candidate = -48; candidate <= 48; candidate++)
                    {
                        double moved = first + candidate / 512.0;
                        if (moved < 0 || moved + 2 > cycles[cycles.Length - 1]) continue;
                        double dot = 0, power = 0;
                        for (int i = 0; i < reference.Length; i += 2)
                        {
                            double y = Sample(filtered, sourceRate * TimeAt(moved + i / 512.0,
                                cycles, times, rpm, revolutions));
                            dot += reference[i] * y;
                            power += y * y;
                        }
                        double score = dot / Math.Sqrt(power + 1e-20);
                        if (score <= best) continue;
                        best = score;
                        shift = candidate / 512.0;
                    }
                }
                first += shift;
                // Root pitch is flattened in phase while preserving two distinct source cycles.
                int length = Math.Max(64, (int)Math.Round(2.0 * 60 * revolutions * storageRate / rootRpm[g]));
                var grain = new short[length + 1];
                for (int i = 0; i <= length; i++)
                {
                    double cycle = first + 2.0 * i / length;
                    double y = Sample(filtered, sourceRate * TimeAt(cycle, cycles, times, rpm, revolutions));
                    grain[i] = (short)Math.Round(Math.Max(-1, Math.Min(.999969, y)) * 32768);
                }
                grains[g] = grain;
                for (int i = 0; i < reference.Length; i++)
                    reference[i] = Sample(filtered, sourceRate * TimeAt(first + 1 + i / 512.0, cycles, times, rpm, revolutions));
            }
            return new EngineGrainData(revolutions, storageRate, rpm[0], rpm[rpm.Length - 1], rootRpm, grains);
        }

        private static double TimeAt(double cycle, double[] cycles, float[] times, float[] rpm, int rev)
        {
            int key = Array.BinarySearch(cycles, cycle);
            if (key < 0) key = ~key - 1;
            key = Math.Max(0, Math.Min(cycles.Length - 2, key));
            double dt = times[key + 1] - times[key];
            double f = rpm[key] / (60.0 * rev), slope = (rpm[key + 1] - rpm[key]) / (60.0 * rev * dt);
            double delta = cycle - cycles[key];
            return times[key] + 2 * delta / (f + Math.Sqrt(Math.Max(0, f * f + 2 * slope * delta)));
        }

        private static double Sample(float[] source, double position)
        {
            int i = (int)position;
            if (i < 0 || i >= source.Length - 1) return 0;
            return source[i] + (source[i + 1] - source[i]) * (position - i);
        }

        private static float[] LowPass(float[] source, int rate, double cutoff)
        {
            const int radius = 40;
            var taps = new double[radius * 2 + 1];
            double sum = 0, f = cutoff / rate;
            for (int i = -radius; i <= radius; i++)
            {
                double sinc = i == 0 ? 2 * f : Math.Sin(2 * Math.PI * f * i) / (Math.PI * i);
                double window = .42 + .5 * Math.Cos(Math.PI * i / radius) + .08 * Math.Cos(2 * Math.PI * i / radius);
                taps[i + radius] = sinc * window;
                sum += taps[i + radius];
            }
            var filtered = new float[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                double y = 0;
                int lo = Math.Max(-radius, -i), hi = Math.Min(radius, source.Length - 1 - i);
                for (int j = lo; j <= hi; j++) y += source[i + j] * taps[j + radius];
                filtered[i] = (float)(y / sum);
            }
            return filtered;
        }
    }
}
