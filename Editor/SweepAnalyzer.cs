using System;
using System.Threading;

namespace RpmSynth.Editor
{
    internal static class SweepAnalyzer
    {
        internal sealed class Result
        {
            public EngineSoundData Data;
            public float[] Times, TrackedRpm, Strength;
            public float[][] Spectrogram;
            public float SpectrogramMaxHz;
        }

        private const int CandidateCount = 320;
        private const int SpectrumSize = 4096;
        private const int AnalysisFftSize = 2048;
        private const int WindowCycles = 4;
        private const int DisplayBins = 128;
        private const int Harmonics = 96;
        private const double Tau = 2 * Math.PI;

        public static Result Analyze(float[] mono, int sampleRate, int cylinders, int revolutions,
            float minimum, float maximum, CancellationToken token, int trackingHarmonic = 0)
        {
            if (sampleRate < 8000 || mono.Length < sampleRate * 0.8)
                throw new ArgumentException("Use a recording at least 0.8 seconds long with a sample rate of 8 kHz or higher.");

            int trackedOrder = trackingHarmonic == 0 ? cylinders : trackingHarmonic;
            int decimation = Math.Max(1, sampleRate / 12000);
            double rate = (double)sampleRate / decimation;
            var reduced = new float[mono.Length / decimation];
            for (int i = 0; i < reduced.Length; i++)
            {
                double mean = 0;
                for (int j = 0; j < decimation; j++) mean += mono[i * decimation + j];
                reduced[i] = (float)(mean / decimation);
            }

            int window = Math.Min(SpectrumSize, (int)(rate * 0.09) | 1);
            int hop = (int)(rate * 0.025);
            int frames = (reduced.Length - window) / hop + 1;
            double dt = hop / rate;
            var speeds = new float[CandidateCount];
            double logRange = Math.Log(maximum / minimum);
            for (int i = 0; i < speeds.Length; i++)
                speeds[i] = (float)(minimum * Math.Exp(logRange * i / (speeds.Length - 1)));

            var result = new Result
            {
                Times = new float[frames],
                TrackedRpm = new float[frames],
                Strength = new float[frames],
                Spectrogram = new float[frames][],
                SpectrogramMaxHz = (float)Math.Min(rate / 2, maximum * Math.Max(trackedOrder, cylinders) * 5 / (60 * revolutions))
            };
            var scores = new float[frames, CandidateCount];
            var parents = new ushort[frames, CandidateCount];
            var frameEnergy = new double[frames];
            var previous = new float[CandidateCount];
            var current = new float[CandidateCount];
            var real = new double[SpectrumSize];
            var imag = new double[SpectrumSize];
            var power = new double[SpectrumSize / 2 + 1];
            double peakEnergy = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                token.ThrowIfCancellationRequested();
                int start = frame * hop;
                result.Times[frame] = (float)((start + (window - 1) * 0.5) / rate);
                Array.Clear(real, 0, real.Length);
                Array.Clear(imag, 0, imag.Length);
                double energy = 0;
                double mean = 0;
                for (int i = 0; i < window; i++) mean += reduced[start + i];
                mean /= window;
                for (int i = 0; i < window; i++)
                {
                    double value = reduced[start + i] - mean;
                    energy += value * value;
                    real[i] = value * Hann(i, window - 1);
                }
                frameEnergy[frame] = energy / window;
                peakEnergy = Math.Max(peakEnergy, frameEnergy[frame]);
                Fft(real, imag);
                for (int i = 0; i < power.Length; i++)
                    power[i] = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

                var display = new float[DisplayBins];
                for (int i = 0; i < display.Length; i++)
                    display[i] = (float)(20 * Math.Log10(1e-8 + Sample(power,
                        i * result.SpectrogramMaxHz * SpectrumSize / (rate * (DisplayBins - 1))) * 4 / window));
                result.Spectrogram[frame] = display;

                double floor = 0.001;
                for (int i = 1; i < power.Length; i++) floor += power[i] / (power.Length - 1);
                int first = 0;
                for (int i = 0; i < speeds.Length; i++)
                {
                    double firingBin = speeds[i] * trackedOrder * SpectrumSize / (60 * revolutions * rate);
                    float score = CombScore(power, firingBin, floor, (double)SpectrumSize / window);
                    scores[frame, i] = score;
                    if (frame == 0)
                    {
                        current[i] = score;
                        continue;
                    }

                    while (speeds[i] - speeds[first] > 20000 * dt) first++;
                    float best = float.NegativeInfinity;
                    int bestParent = i;
                    for (int j = first; j <= i; j++)
                    {
                        double velocity = (speeds[i] - speeds[j]) / dt / 6000;
                        float value = previous[j] - (float)(0.009 * velocity * velocity);
                        if (value <= best) continue;
                        best = value;
                        bestParent = j;
                    }
                    current[i] = score + best;
                    parents[frame, i] = (ushort)bestParent;
                }
                (previous, current) = (current, previous);
            }

            int candidate = 0;
            for (int i = 1; i < speeds.Length; i++)
                if (previous[i] > previous[candidate]) candidate = i;
            for (int i = frames - 1; i >= 0; i--)
            {
                result.TrackedRpm[i] = speeds[candidate];
                result.Strength[i] = scores[i, candidate];
                candidate = parents[i, candidate];
            }

            if (peakEnergy < 1e-8)
                throw new InvalidOperationException("The recording is too quiet to analyze.");
            int from = 0, to = frames - 1;
            while (from < to && !Usable(result.Strength[from], frameEnergy[from], peakEnergy)) from++;
            while (to > from && !Usable(result.Strength[to], frameEnergy[to], peakEnergy)) to--;
            if (to - from < 8 || result.TrackedRpm[to] - result.TrackedRpm[from] < 100)
                throw new InvalidOperationException("Could not detect an RPM sweep. Check the selected section, cylinder count, and RPM search range.");

            RefineTrack(result.TrackedRpm, speeds, scores);
            result.Data = Extract(mono, sampleRate, revolutions, result, from, to, dt, token);
            return result;
        }

        private static bool Usable(float strength, double energy, double peak)
            => strength > 1.5f && energy > peak * 0.0008;

        private static float CombScore(double[] power, double fundamental, double floor, double resolution)
        {
            double score = 0, weightSum = 0;
            for (int h = 1; h <= 7 && fundamental * h < power.Length - 2; h++)
            {
                double weight = h == 1 ? 2.7 : 1 / Math.Sqrt(h);
                double center = fundamental * h;
                double side = Math.Min(fundamental * 0.43, Math.Max(resolution * 1.3, center * 0.035));
                double on = Sample(power, center);
                double off = 0.5 * (Sample(power, center - side) + Sample(power, center + side));
                double background = floor + off * 0.32;
                score += weight * Math.Log(1 + on / background);
                weightSum += weight;
            }
            if (weightSum == 0) return 0;
            double between = (Sample(power, 1.5 * fundamental) + Sample(power, 2.5 * fundamental)) * 0.5;
            double first = Sample(power, fundamental);
            double second = Sample(power, 2 * fundamental);
            double octavePenalty = Math.Log(1 + 0.30 * second / (floor + first));
            return (float)(2 * score / weightSum - 0.45 * Math.Log(1 + between / (floor + first))
                - 0.55 * octavePenalty);
        }

        private static void RefineTrack(float[] rpm, float[] speeds, float[,] scores)
        {
            var local = new float[rpm.Length];
            for (int i = 0; i < local.Length; i++)
            {
                int key = Array.BinarySearch(speeds, rpm[i]);
                double level = scores[i, key];
                double sum = 0, weightSum = 0;
                for (int j = Math.Max(0, key - 5); j <= Math.Min(speeds.Length - 1, key + 5); j++)
                {
                    double weight = Math.Exp(5 * (scores[i, j] - level) - 0.115 * (j - key) * (j - key));
                    sum += speeds[j] * weight;
                    weightSum += weight;
                }
                local[i] = (float)(sum / weightSum);
            }
            for (int i = 0; i < rpm.Length; i++)
            {
                float value = 0, weightSum = 0;
                for (int j = Math.Max(0, i - 2); j <= Math.Min(rpm.Length - 1, i + 2); j++)
                {
                    float weight = 3 - Math.Abs(i - j);
                    value += local[j] * weight;
                    weightSum += weight;
                }
                rpm[i] = Math.Max(i == 0 ? 0 : rpm[i - 1], value / weightSum);
            }
        }

        private static EngineSoundData Extract(float[] samples, int rate, int revolutions, Result result,
            int from, int to, double dt, CancellationToken token)
        {
            var track = result.TrackedRpm;
            var cycles = new double[track.Length];
            double rpmToHz = 1.0 / (60 * revolutions);
            for (int i = 1; i < track.Length; i++)
                cycles[i] = cycles[i - 1] + (track[i - 1] + track[i]) * 0.5 * rpmToHz * dt;

            float minimum = track[from], maximum = track[to];
            int keys = Math.Max(3, Math.Min(180, (int)Math.Ceiling((maximum - minimum) / 40) + 1));
            var rpm = new float[keys];
            var cosine = new float[keys * Harmonics];
            var sine = new float[cosine.Length];
            var residual = new float[keys * 3];
            var real = new double[AnalysisFftSize];
            var imag = new double[AnalysisFftSize];
            var window = new double[AnalysisFftSize];
            for (int i = 0; i < window.Length; i++) window[i] = Hann(i, window.Length);

            int center = from;
            for (int key = 0; key < keys; key++)
            {
                token.ThrowIfCancellationRequested();
                rpm[key] = minimum + (maximum - minimum) * key / (keys - 1);
                while (center < to - 1 && track[center + 1] < rpm[key]) center++;
                double mix = track[center + 1] > track[center]
                    ? (rpm[key] - track[center]) / (track[center + 1] - track[center]) : 0;
                double frequency = rpm[key] * rpmToHz;
                double centerCycle = cycles[center] + (track[center] + rpm[key]) * rpmToHz * 0.5 * mix * dt;
                int interval = Math.Max(0, center - 1);
                double firstCycle = centerCycle - WindowCycles * 0.5;
                while (interval > 0 && cycles[interval] > firstCycle) interval--;

                double weight = 0;
                for (int i = 0; i < real.Length; i++)
                {
                    double cycle = firstCycle + (double)i * WindowCycles / real.Length;
                    while (interval < cycles.Length - 2 && cycles[interval + 1] < cycle) interval++;
                    double elapsed;
                    if (cycle < 0)
                        elapsed = cycle / (track[0] * rpmToHz);
                    else if (cycle > cycles[cycles.Length - 1])
                        elapsed = (cycles.Length - 1) * dt
                            + (cycle - cycles[cycles.Length - 1]) / (track[track.Length - 1] * rpmToHz);
                    else
                    {
                        double f = track[interval] * rpmToHz;
                        double slope = (track[interval + 1] * rpmToHz - f) / dt;
                        double delta = cycle - cycles[interval];
                        elapsed = interval * dt + 2 * delta / (f + Math.Sqrt(f * f + 2 * slope * delta));
                    }
                    double position = (result.Times[0] + elapsed) * rate;
                    real[i] = 0;
                    imag[i] = 0;
                    if (position < 0 || position >= samples.Length - 1) continue;
                    int sample = (int)position;
                    real[i] = (samples[sample] + (samples[sample + 1] - samples[sample])
                        * (position - sample)) * window[i];
                    weight += window[i];
                }
                Fft(real, imag);
                for (int h = 1; h <= Harmonics && frequency * h < rate * 0.48; h++)
                {
                    int bin = h * WindowCycles;
                    double amplitude = 2 / weight;
                    int index = key * Harmonics + h - 1;
                    cosine[index] = (float)(real[bin] * amplitude);
                    sine[index] = (float)(-imag[bin] * amplitude);
                }
                if (key > 0) Align(cosine, sine, key);

                int limit = Math.Min(real.Length / 2, (int)(rate * 0.48 * WindowCycles / frequency));
                for (int bin = 1; bin < limit; bin++)
                {
                    int remainder = bin % WindowCycles;
                    if ((remainder <= 1 || remainder >= WindowCycles - 1) && bin <= Harmonics * WindowCycles + 1)
                        continue;
                    double hz = bin * frequency / WindowCycles;
                    int band = key * 3 + (hz < 250 ? 0 : hz < 1400 ? 1 : 2);
                    residual[band] += (float)(2 * (real[bin] * real[bin] + imag[bin] * imag[bin])
                        / (weight * weight * 1.5));
                }
                for (int band = key * 3; band < key * 3 + 3; band++)
                    residual[band] = Math.Min(0.35f, (float)Math.Sqrt(residual[band] * 2));
            }

            double peak = 0;
            for (int key = 0; key < keys; key++)
            {
                for (int i = 0; i < 256; i++)
                {
                    double sum = 0;
                    for (int h = 1; h <= Harmonics; h++)
                    {
                        int index = key * Harmonics + h - 1;
                        double angle = i * h * Tau / 256;
                        sum += cosine[index] * Math.Cos(angle) + sine[index] * Math.Sin(angle);
                    }
                    peak = Math.Max(peak, Math.Abs(sum));
                }
            }
            if (peak < 0.001)
                throw new InvalidOperationException("The selected section has too few prominent harmonics.");
            float gain = (float)Math.Min(16, 0.76 / peak);
            for (int i = 0; i < cosine.Length; i++)
            {
                cosine[i] *= gain;
                sine[i] *= gain;
            }
            for (int i = 0; i < residual.Length; i++) residual[i] = Math.Min(0.14f, residual[i] * gain);
            return new EngineSoundData(revolutions, Harmonics, rpm, cosine, sine, residual);
        }

        // Pick a common mechanical phase before morphing between adjacent RPM keys.
        private static void Align(float[] cosine, float[] sine, int key)
        {
            const int phaseBins = 256;
            int offset = key * Harmonics;
            double bestScore = double.NegativeInfinity;
            int shift = 0;
            for (int step = 0; step < phaseBins; step++)
            {
                double a = step * Tau / phaseBins;
                double score = 0;
                for (int h = 1; h <= 24; h++)
                {
                    int i = offset + h - 1, j = i - Harmonics;
                    double dot = cosine[i] * cosine[j] + sine[i] * sine[j];
                    double cross = sine[i] * cosine[j] - cosine[i] * sine[j];
                    score += dot * Math.Cos(a * h) + cross * Math.Sin(a * h);
                }
                if (score <= bestScore) continue;
                bestScore = score;
                shift = step;
            }
            for (int h = 1; h <= Harmonics; h++)
            {
                int i = offset + h - 1;
                double a = shift * h * Tau / phaseBins;
                double cos = Math.Cos(a), sin = Math.Sin(a);
                float previousCos = cosine[i];
                cosine[i] = (float)(previousCos * cos + sine[i] * sin);
                sine[i] = (float)(sine[i] * cos - previousCos * sin);
            }
        }

        private static double Hann(int i, int length) => 0.5 - 0.5 * Math.Cos(Tau * i / length);

        private static double Sample(double[] values, double position)
        {
            if (position < 0 || position >= values.Length - 1) return 0;
            int i = (int)position;
            return values[i] + (values[i + 1] - values[i]) * (position - i);
        }

        private static void Fft(double[] real, double[] imag)
        {
            int length = real.Length;
            for (int i = 1, j = 0; i < length; i++)
            {
                int bit = length >> 1;
                while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
                j ^= bit;
                if (i >= j) continue;
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
            for (int size = 2; size <= length; size <<= 1)
            {
                int half = size >> 1;
                double stepCos = Math.Cos(-Tau / size), stepSin = Math.Sin(-Tau / size);
                for (int start = 0; start < length; start += size)
                {
                    double cos = 1, sin = 0;
                    for (int i = start; i < start + half; i++)
                    {
                        int j = i + half;
                        double r = real[j] * cos - imag[j] * sin;
                        double im = real[j] * sin + imag[j] * cos;
                        real[j] = real[i] - r;
                        imag[j] = imag[i] - im;
                        real[i] += r;
                        imag[i] += im;
                        double nextCos = cos * stepCos - sin * stepSin;
                        sin = sin * stepCos + cos * stepSin;
                        cos = nextCos;
                    }
                }
            }
        }
    }
}
