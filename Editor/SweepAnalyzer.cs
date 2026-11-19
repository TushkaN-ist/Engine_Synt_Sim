using System;
using System.Collections.Generic;
using System.Threading;

namespace RpmSynth.Editor
{
    internal static class SweepAnalyzer
    {
        internal readonly struct RpmAnchor
        {
            public readonly float Time, Rpm;

            public RpmAnchor(float time, float rpm)
            {
                Time = time;
                Rpm = rpm;
            }
        }

        internal enum TrackingOrderSource
        {
            Cylinders,
            RpmAnchors,
            Manual,
            HarmonicPair
        }

        internal sealed class Result
        {
            public EngineSoundData Data;
            public float[] Times, TrackedRpm, Strength;
            public float[][] Spectrogram;
            public float SpectrogramMaxHz;
            public int TrackingOrder;
            public int ReferenceOrder;
            public int SupportingOrder;
            public TrackingOrderSource TrackingSource;
            public bool CorrectedHarmonicJump;
        }

        private const int CandidateCount = 320;
        private const int SpectrumSize = 4096;
        // Keep enough phase samples to measure short high-frequency attacks near idle.
        private const int AnalysisFftSize = 16384;
        private const int WindowCycles = 4;
        private const int DisplayBins = 128;
        private const int BaseHarmonics = 96, MaximumHarmonics = 1536;
        private const double MaximumHarmonicHz = 8500;
        private const double Tau = 2 * Math.PI;

        public static Result Analyze(float[] mono, int sampleRate, int cylinders, int revolutions,
            float minimum, float maximum, CancellationToken token, int trackingHarmonic = 0,
            RpmAnchor[] anchors = null, RpmAnchor[] guidePoints = null, int referenceHarmonic = 0)
        {
            if (sampleRate < 8000 || mono.Length < sampleRate * 0.8)
                throw new ArgumentException("Use a recording at least 0.8 seconds long with a sample rate of 8 kHz or higher.");

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
            double firstTime = (window - 1) * 0.5 / rate;
            var anchorFrames = GetAnchorFrames(anchors, frames, firstTime, dt);
            var pins = BuildTrackingPins(anchors, anchorFrames, guidePoints, frames, firstTime, dt, minimum, maximum);
            int trackedOrder = trackingHarmonic;
            var trackingSource = referenceHarmonic > 0 ? TrackingOrderSource.HarmonicPair : TrackingOrderSource.Manual;
            if (trackedOrder == 0)
            {
                if (anchors != null)
                    trackedOrder = EstimateOrderAtAnchors(reduced, rate, revolutions, anchors, token);
                trackingSource = trackedOrder > 0 ? TrackingOrderSource.RpmAnchors : TrackingOrderSource.Cylinders;
                if (trackedOrder == 0) trackedOrder = cylinders;
            }
            var speeds = new float[CandidateCount];
            double logRange = Math.Log(maximum / minimum);
            for (int i = 0; i < speeds.Length; i++)
                speeds[i] = (float)(minimum * Math.Exp(logRange * i / (speeds.Length - 1)));
            if (pins.Length > 0)
            {
                var candidates = new SortedSet<float>(speeds);
                foreach (var pin in pins) candidates.Add(pin.Rpm);
                speeds = new float[candidates.Count];
                candidates.CopyTo(speeds);
            }

            var result = new Result
            {
                Times = new float[frames],
                TrackedRpm = new float[frames],
                Strength = new float[frames],
                Spectrogram = new float[frames][],
                TrackingOrder = trackedOrder,
                ReferenceOrder = referenceHarmonic,
                TrackingSource = trackingSource,
                SpectrogramMaxHz = (float)Math.Min(rate / 2, maximum * Math.Max(trackedOrder, cylinders) * 5 / (60 * revolutions))
            };
            var scores = new float[frames, speeds.Length];
            var ridgeEvidence = pins.Length == 0 && referenceHarmonic == 0 ? new float[frames, speeds.Length] : null;
            var parents = new ushort[frames, speeds.Length];
            var frameEnergy = new double[frames];
            var previous = new float[speeds.Length];
            var current = new float[speeds.Length];
            var real = new double[SpectrumSize];
            var imag = new double[SpectrumSize];
            var power = new double[SpectrumSize / 2 + 1];
            double resolution = (double)SpectrumSize / window;
            double peakEnergy = 0;
            int nextPin = 0;
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
                EngineSoundFft.Transform(real, imag);
                for (int i = 0; i < power.Length; i++)
                    power[i] = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

                var display = new float[DisplayBins];
                for (int i = 0; i < display.Length; i++)
                    display[i] = (float)(20 * Math.Log10(1e-8 + Sample(power,
                        i * result.SpectrogramMaxHz * SpectrumSize / (rate * (DisplayBins - 1))) * 4 / window));
                result.Spectrogram[frame] = display;

                double floor = 0.001;
                for (int i = 1; i < power.Length; i++) floor += power[i] / (power.Length - 1);
                float segmentLow = minimum, segmentHigh = maximum, expected = 0;
                double maxRise = 20000 * dt, velocityScale = 6000;
                int pinned = -1;
                while (nextPin < pins.Length && frame > pins[nextPin].Frame) nextPin++;
                if (nextPin > 0) segmentLow = pins[nextPin - 1].Rpm;
                if (nextPin < pins.Length)
                {
                    segmentHigh = pins[nextPin].Rpm;
                    if (frame == pins[nextPin].Frame) pinned = Array.BinarySearch(speeds, segmentHigh);
                    if (nextPin > 0)
                    {
                        int a = pins[nextPin - 1].Frame, b = pins[nextPin].Frame;
                        expected = segmentLow + (segmentHigh - segmentLow) * (frame - a) / (b - a);
                        double averageRise = (segmentHigh - segmentLow) / (b - a);
                        maxRise = Math.Max(maxRise, 4 * averageRise);
                        velocityScale = Math.Max(velocityScale, averageRise / dt);
                    }
                }
                int first = 0;
                for (int i = 0; i < speeds.Length; i++)
                {
                    current[i] = float.NegativeInfinity;
                    if (pins.Length > 0 && ((pinned >= 0 && i != pinned)
                        || speeds[i] < segmentLow || speeds[i] > segmentHigh)) continue;
                    double firingBin = speeds[i] * trackedOrder * SpectrumSize / (60 * revolutions * rate);
                    float score = CombScore(power, firingBin, floor, resolution, out float ridge);
                    if (ridgeEvidence != null) ridgeEvidence[frame, i] = ridge;
                    if (referenceHarmonic > 0)
                        score += PairEvidence(power, firingBin / trackedOrder, trackedOrder, referenceHarmonic, floor, resolution);
                    if (expected > 0)
                    {
                        // Prefer the expected harmonic without forcing a straight RPM ramp between anchors.
                        double distance = Math.Log(speeds[i] / expected);
                        score -= (float)(22 * distance * distance);
                    }
                    scores[frame, i] = score;
                    if (frame == 0)
                    {
                        current[i] = score;
                        continue;
                    }

                    while (speeds[i] - speeds[first] > maxRise) first++;
                    float best = float.NegativeInfinity;
                    int bestParent = i;
                    for (int j = first; j <= i; j++)
                    {
                        double velocity = (speeds[i] - speeds[j]) / dt / velocityScale;
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
            if (float.IsNegativeInfinity(previous[candidate]))
                throw new InvalidOperationException("Could not connect the tracking points. Check that RPM rises across the selected section.");
            for (int i = frames - 1; i >= 0; i--)
            {
                result.TrackedRpm[i] = speeds[candidate];
                result.Strength[i] = scores[i, candidate];
                candidate = parents[i, candidate];
            }

            if (peakEnergy < 1e-8)
                throw new InvalidOperationException("The recording is too quiet to analyze.");
            if (ridgeEvidence != null)
                result.CorrectedHarmonicJump = TryCorrectHarmonicJump(result, speeds, scores, ridgeEvidence,
                    parents, reduced, rate, revolutions, dt, token);
            int from = 0, to = frames - 1;
            if (anchorFrames == null)
            {
                int firstRequired = pins.Length == 0 ? to : pins[0].Frame;
                int lastRequired = pins.Length == 0 ? from : pins[pins.Length - 1].Frame;
                while (from < firstRequired && from < to && !Usable(result.Strength[from], frameEnergy[from], peakEnergy)) from++;
                while (to > lastRequired && to > from && !Usable(result.Strength[to], frameEnergy[to], peakEnergy)) to--;
            }
            if (to - from < 8 || result.TrackedRpm[to] - result.TrackedRpm[from] < 100)
                throw new InvalidOperationException("Could not detect an RPM sweep. Check the selected section, cylinder count, and RPM search range.");

            if (pins.Length == 0)
                RefineTrack(result.TrackedRpm, speeds, scores);
            else
                SmoothPinnedTrack(result.TrackedRpm, pins);
            result.Data = Extract(mono, sampleRate, revolutions, result, from, to, dt, token);
            return result;
        }

        private static int[] GetAnchorFrames(RpmAnchor[] anchors, int frames, double firstTime, double dt)
        {
            if (anchors == null) return null;
            if (anchors.Length < 2) throw new ArgumentException("Enter at least two RPM anchors.");
            var indices = new int[anchors.Length];
            indices[indices.Length - 1] = frames - 1;
            for (int i = 1; i < indices.Length; i++)
            {
                if (i < indices.Length - 1)
                    indices[i] = (int)Math.Round((anchors[i].Time - firstTime) / dt);
                if (!(anchors[i].Rpm > anchors[i - 1].Rpm) || indices[i] <= indices[i - 1]
                    || (i < indices.Length - 1 && indices[i] >= frames - 1))
                    throw new ArgumentException("RPM anchors must increase in time and RPM. Keep checkpoints inside the sweep and at least one analysis frame apart.");
            }
            return indices;
        }

        private readonly struct TrackingPin
        {
            public readonly int Frame;
            public readonly float Rpm;

            public TrackingPin(int frame, float rpm)
            {
                Frame = frame;
                Rpm = rpm;
            }
        }

        private static TrackingPin[] BuildTrackingPins(RpmAnchor[] anchors, int[] anchorFrames, RpmAnchor[] guides,
            int frames, double firstTime, double dt, float minimum, float maximum)
        {
            if (anchorFrames == null && (guides == null || guides.Length == 0)) return Array.Empty<TrackingPin>();
            var pins = new List<TrackingPin>();
            if (anchorFrames != null)
                for (int i = 0; i < anchorFrames.Length; i++)
                    pins.Add(new TrackingPin(anchorFrames[i], anchors[i].Rpm));
            if (guides != null)
                foreach (var guide in guides)
                {
                    int frame = Math.Max(0, Math.Min(frames - 1, (int)Math.Round((guide.Time - firstTime) / dt)));
                    pins.Add(new TrackingPin(frame, guide.Rpm));
                }
            pins.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            for (int i = 0; i < pins.Count; i++)
            {
                var pin = pins[i];
                if (!(pin.Rpm >= minimum && pin.Rpm <= maximum))
                    throw new ArgumentException($"A tracking point at {pin.Rpm:F0} RPM is outside the search limits. Adjust the point or the RPM range.");
                if (i == 0) continue;
                if (pin.Frame == pins[i - 1].Frame)
                    throw new ArgumentException("Two tracking points fall on the same analysis frame. Keep points and RPM anchors at least 25 ms apart.");
                if (pin.Rpm < pins[i - 1].Rpm)
                    throw new ArgumentException("Tracking points must follow rising RPM. Check the chosen lines and points.");
            }
            return pins.ToArray();
        }

        private static void SmoothPinnedTrack(float[] rpm, TrackingPin[] pins)
        {
            var raw = (float[])rpm.Clone();
            int next = 0;
            for (int i = 1; i < rpm.Length - 1; i++)
            {
                while (next < pins.Length && pins[next].Frame < i) next++;
                if (next < pins.Length && pins[next].Frame == i) continue;
                rpm[i] = (raw[i - 1] + 2 * raw[i] + raw[i + 1]) * 0.25f;
            }
        }

        private static float PairEvidence(double[] power, double cycleBin, int primaryOrder, int referenceOrder,
            double floor, double resolution)
        {
            // Follow both selected ridges, then use weaker cycle orders as supporting evidence.
            double reference = LineContrast(power, cycleBin * referenceOrder, cycleBin, floor, resolution);
            double support = 0, weight = 0;
            for (int order = 1; order <= 16 && cycleBin * order < power.Length - 2; order++)
            {
                if (order == primaryOrder || order == referenceOrder) continue;
                double w = 1 / Math.Sqrt(order);
                support += w * LineContrast(power, cycleBin * order, cycleBin, floor, resolution);
                weight += w;
            }
            return (float)(.8 * reference + (weight > 0 ? .55 * support / weight : 0));
        }

        private static double LineContrast(double[] power, double center, double spacing, double floor, double resolution)
        {
            if (center < resolution || center >= power.Length - 2) return 0;
            double side = Math.Min(spacing * .45, 1.5 * resolution);
            double background = floor + (Sample(power, center - side) + Sample(power, center + side)) * .35;
            return Math.Log(1 + Sample(power, center) / background);
        }

        private static bool Usable(float strength, double energy, double peak)
            => strength > 1.5f && energy > peak * 0.0008;

        private const int MaximumAutomaticTrackingOrder = 16;
        private const double EstimateWindowSeconds = 0.19;
        private const double AnchorTolerance = 0.03;

        private sealed class OrderSpectrum
        {
            private readonly double[] real = new double[SpectrumSize], imag = new double[SpectrumSize];
            private readonly double[] power = new double[SpectrumSize / 2 + 1];
            private readonly int window;
            private double floor;
            public readonly double BinsPerHz;
            public double Resolution => (double)SpectrumSize / window;

            public OrderSpectrum(double rate)
            {
                window = Math.Min(SpectrumSize, (int)Math.Round(rate * EstimateWindowSeconds));
                BinsPerHz = SpectrumSize / rate;
            }

            public void Read(float[] samples, int center)
            {
                int first = Math.Max(0, Math.Min(samples.Length - window, center - window / 2));
                double mean = 0;
                for (int i = 0; i < window; i++) mean += samples[first + i];
                mean /= window;
                Array.Clear(real, 0, real.Length);
                Array.Clear(imag, 0, imag.Length);
                for (int i = 0; i < window; i++) real[i] = (samples[first + i] - mean) * Hann(i, window - 1);
                EngineSoundFft.Transform(real, imag);
                floor = 0.001;
                for (int i = 0; i < power.Length; i++)
                {
                    power[i] = Math.Sqrt(Power(real, imag, i));
                    if (i != 0) floor += power[i] / (power.Length - 1);
                }
            }

            public float Score(double bin)
                => bin >= Resolution * 2 && bin * 2 < power.Length - 2
                    ? CombScore(power, bin, floor, Resolution) : 0;

            public double RidgeStrength(double bin)
            {
                if (bin < 2 * Resolution || bin >= power.Length - 2) return 0;
                double side = Math.Max(1.3 * Resolution, bin * .025);
                double off = (Sample(power, bin - side) + Sample(power, bin + side)) * .5;
                return Math.Log(1 + Sample(power, bin) / (floor + .32 * off));
            }
        }

        private static int EstimateOrderAtAnchors(float[] samples, double rate, int revolutions, RpmAnchor[] anchors,
            CancellationToken token)
        {
            var spectrum = new OrderSpectrum(rate);
            var totals = new double[MaximumAutomaticTrackingOrder + 1];
            var nearBest = new int[totals.Length];
            var scores = new float[totals.Length];
            double lastTime = double.NegativeInfinity;
            int observations = 0;
            foreach (var anchor in anchors)
            {
                token.ThrowIfCancellationRequested();
                double time = Math.Max(EstimateWindowSeconds / 2,
                    Math.Min(samples.Length / rate - EstimateWindowSeconds / 2, anchor.Time));
                if (time - lastTime < EstimateWindowSeconds) continue;
                spectrum.Read(samples, (int)Math.Round(time * rate));
                int winner = 1;
                for (int order = 1; order <= MaximumAutomaticTrackingOrder; order++)
                {
                    double bin = anchor.Rpm * order * spectrum.BinsPerHz / (60 * revolutions);
                    scores[order] = float.NegativeInfinity;
                    for (int offset = -2; offset <= 2; offset++)
                    {
                        double candidate = bin * (1 + offset * AnchorTolerance / 2);
                        float score = spectrum.Score(candidate) - .15f * Math.Abs(offset);
                        scores[order] = Math.Max(scores[order], score);
                    }
                    if (scores[order] > scores[winner]) winner = order;
                }
                if (scores[winner] < 2) continue;
                observations++;
                lastTime = time;
                for (int order = 1; order <= MaximumAutomaticTrackingOrder; order++)
                {
                    totals[order] += scores[order];
                    if (scores[order] >= scores[winner] - .45f) nearBest[order]++;
                }
            }

            if (observations < 2) return 0;
            int best = 1;
            for (int order = 2; order <= MaximumAutomaticTrackingOrder; order++)
                if (totals[order] > totals[best]) best = order;
            double runnerUp = double.NegativeInfinity;
            for (int order = 1; order <= MaximumAutomaticTrackingOrder; order++)
                if (order != best) runnerUp = Math.Max(runnerUp, totals[order]);
            double lead = (totals[best] - runnerUp) / observations;
            if (lead < .25 || nearBest[best] * 4 < observations * 3)
                return 0;
            return best;
        }

        private static float CombScore(double[] power, double fundamental, double floor, double resolution)
            => CombScore(power, fundamental, floor, resolution, out _);

        private static float CombScore(double[] power, double fundamental, double floor, double resolution, out float ridge)
        {
            ridge = 0;
            double score = 0, weightSum = 0;
            for (int h = 1; h <= 7 && fundamental * h < power.Length - 2; h++)
            {
                double weight = h == 1 ? 2.7 : 1 / Math.Sqrt(h);
                double center = fundamental * h;
                double side = Math.Min(fundamental * 0.43, Math.Max(resolution * 1.3, center * 0.035));
                double on = Sample(power, center);
                double off = 0.5 * (Sample(power, center - side) + Sample(power, center + side));
                double background = floor + off * 0.32;
                double contrast = Math.Log(1 + on / background);
                if (h == 1) ridge = (float)contrast;
                score += weight * contrast;
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

        private static bool TryCorrectHarmonicJump(Result result, float[] speeds, float[,] comb, float[,] ridge,
            ushort[,] parents, float[] samples, double rate, int revolutions, double dt, CancellationToken token)
        {
            var rpm = result.TrackedRpm;
            int window = Math.Max(3, (int)Math.Round(.2 / dt));
            int jump = -1;
            float largestRatio = 1.35f;
            for (int left = window; left + 2 * window < rpm.Length; left++)
            {
                int right = left + window;
                float rise = rpm[right] - rpm[left];
                float neighboringRise = Math.Max(rpm[left] - rpm[left - window], rpm[right + window] - rpm[right]);
                float ratio = rpm[right] / rpm[left];
                if (rise < Math.Max(350, 4 * neighboringRise) || ratio <= largestRatio) continue;

                int lower = Array.BinarySearch(speeds, rpm[left]);
                int higher = Array.BinarySearch(speeds, rpm[right]);
                int weakFrames = 0;
                for (int frame = left + 1; frame < right; frame++)
                {
                    if (rpm[frame] <= rpm[left] + .18f * rise || rpm[frame] >= rpm[right] - .18f * rise) continue;
                    int key = Array.BinarySearch(speeds, rpm[frame]);
                    float sidePeak = Math.Max(ridge[frame, lower], ridge[frame, higher]);
                    if (ridge[frame, key] + .35f < sidePeak) weakFrames++;
                }
                if (weakFrames < 2) continue;
                largestRatio = ratio;
                jump = left;
            }
            if (jump < 0) return false;

            // In a competing-ridge region, require a continuous path and give the
            // observed line more weight than harmonics that also fit its submultiple.
            const float ridgeWeight = 1.3f;
            double relativeRise = .022 * dt / .025;
            double maxRise = 20000 * dt;
            var previous = new float[speeds.Length];
            var current = new float[speeds.Length];
            for (int i = 0; i < speeds.Length; i++) previous[i] = comb[0, i] + ridgeWeight * ridge[0, i];
            for (int frame = 1; frame < rpm.Length; frame++)
            {
                token.ThrowIfCancellationRequested();
                int first = 0;
                for (int i = 0; i < speeds.Length; i++)
                {
                    while (first < i - 1 && speeds[i] - speeds[first] > Math.Min(maxRise, speeds[first] * relativeRise)) first++;
                    float best = float.NegativeInfinity;
                    int bestParent = i;
                    for (int j = first; j <= i; j++)
                    {
                        double velocity = (speeds[i] - speeds[j]) / (dt * 6000);
                        float value = previous[j] - (float)(.009 * velocity * velocity);
                        if (value <= best) continue;
                        best = value;
                        bestParent = j;
                    }
                    current[i] = best + comb[frame, i] + ridgeWeight * ridge[frame, i];
                    parents[frame, i] = (ushort)bestParent;
                }
                (previous, current) = (current, previous);
            }
            int candidate = 0;
            for (int i = 1; i < speeds.Length; i++)
                if (previous[i] > previous[candidate]) candidate = i;
            var replacement = new ushort[rpm.Length];
            double originalEvidence = 0, replacementEvidence = 0;
            for (int frame = rpm.Length - 1; frame >= 0; frame--)
            {
                replacement[frame] = (ushort)candidate;
                int original = Array.BinarySearch(speeds, rpm[frame]);
                originalEvidence += comb[frame, original] + ridgeWeight * ridge[frame, original];
                replacementEvidence += comb[frame, candidate] + ridgeWeight * ridge[frame, candidate];
                candidate = parents[frame, candidate];
            }

            int end = jump + window;
            float before = speeds[replacement[jump]] / rpm[jump];
            float after = speeds[replacement[end]] / rpm[end];
            bool joinsUpperRidge = before >= 1.25f && Math.Abs(after - 1) <= .09f;
            bool joinsLowerRidge = after <= .8f && Math.Abs(before - 1) <= .09f;
            if (!joinsUpperRidge && !joinsLowerRidge) return false;
            // Near the acceptance boundary, check a second order against the two possible paths.
            // This cannot rescue a path with substantially worse spectral support.
            if (replacementEvidence < .96 * originalEvidence)
            {
                if (replacementEvidence < .94 * originalEvidence) return false;
                result.SupportingOrder = FindSupportingOrder(result, speeds, replacement, samples, rate, revolutions,
                    dt, jump, end, token);
                if (result.SupportingOrder == 0) return false;
            }

            for (int frame = 0; frame < rpm.Length; frame++)
            {
                int key = replacement[frame];
                rpm[frame] = speeds[key];
                result.Strength[frame] = comb[frame, key];
                for (int i = 0; i < speeds.Length; i++) comb[frame, i] += ridgeWeight * ridge[frame, i];
            }
            return true;
        }

        private static int FindSupportingOrder(Result result, float[] speeds, ushort[] replacement,
            float[] samples, double rate, int revolutions, double dt, int jumpStart, int jumpEnd, CancellationToken token)
        {
            // Establish the supporting line where the paths agree, then see which path it
            // favors where they diverge. Exact multiples can reinforce an octave error.
            const double minimumStrength = 1.5;
            int firstOrder = Math.Max(1, (result.TrackingOrder + 2) / 3);
            int lastOrder = Math.Min(48, result.TrackingOrder * 3);
            if (firstOrder > lastOrder) return 0;
            var commonHits = new int[lastOrder + 1];
            var supportHits = new int[lastOrder + 1];
            var spectrum = new OrderSpectrum(rate);
            double binsPerRpm = spectrum.BinsPerHz / (60 * revolutions);
            int stride = Math.Max(1, (int)Math.Round(.14 / dt));
            int common = 0, disputed = 0;
            for (int frame = stride; frame < replacement.Length - stride; frame += stride)
            {
                if (frame >= jumpStart - stride && frame <= jumpEnd + stride) continue;
                float oldRpm = result.TrackedRpm[frame], newRpm = speeds[replacement[frame]];
                bool isCommon = Math.Abs(newRpm - oldRpm) <= .06f * oldRpm;
                if (!isCommon && Math.Abs(newRpm - oldRpm) < .24f * oldRpm) continue;
                token.ThrowIfCancellationRequested();
                spectrum.Read(samples, (int)Math.Round(result.Times[frame] * rate));
                if (isCommon) common++;
                else disputed++;
                for (int order = firstOrder; order <= lastOrder; order++)
                {
                    if (order % result.TrackingOrder == 0) continue;
                    double newStrength = spectrum.RidgeStrength(order * newRpm * binsPerRpm);
                    if (newStrength < minimumStrength) continue;
                    if (isCommon) commonHits[order]++;
                    else if (newStrength > spectrum.RidgeStrength(order * oldRpm * binsPerRpm) + .35)
                        supportHits[order]++;
                }
            }
            if (common < 6 || disputed < 6) return 0;
            int best = 0;
            double bestCoverage = 0;
            for (int order = firstOrder; order <= lastOrder; order++)
            {
                double commonCoverage = (double)commonHits[order] / common;
                double disputedCoverage = (double)supportHits[order] / disputed;
                // Require a recognizable line on the shared path and stronger agreement where it matters.
                if (commonCoverage < .55 || disputedCoverage < .65) continue;
                double coverage = Math.Min(commonCoverage, disputedCoverage);
                if (coverage <= bestCoverage) continue;
                best = order;
                bestCoverage = coverage;
            }
            return best;
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
            double harmonicLimit = Math.Min(MaximumHarmonicHz, rate * 0.44);
            int harmonics = Math.Max(BaseHarmonics, Math.Min(MaximumHarmonics,
                (int)Math.Ceiling(harmonicLimit / (minimum * rpmToHz))));
            int keys = Math.Max(3, Math.Min(180, (int)Math.Ceiling((maximum - minimum) / 40) + 1));
            var rpm = new float[keys];
            var cosine = new float[keys * harmonics];
            var sine = new float[cosine.Length];
            var residual = new float[keys * 3];
            var residualSpectrum = new float[keys * EngineResidualNoise.BandCount];
            var modulation = new float[keys * 2 * EngineSoundData.NoisePhaseBins];
            var bandModulation = new float[keys * EngineResidualNoise.BandCount * EngineSoundData.NoisePhaseBins];
            const int phaseBins = EngineSoundData.MechanicalPhaseBins;
            var mechanical = new float[keys * phaseBins];
            var real = new double[AnalysisFftSize];
            var imag = new double[AnalysisFftSize];
            var window = new double[AnalysisFftSize];
            var cycleSamples = new double[AnalysisFftSize];
            var harmonicWave = new double[AnalysisFftSize / WindowCycles];
            var harmonicImag = new double[harmonicWave.Length];
            var attackAnalyzer = new ResonantAttackAnalyzer(harmonicWave.Length, WindowCycles);
            var modulationAnalyzer = new ResidualModulationAnalyzer(harmonicWave.Length, WindowCycles);
            var attacks = new float[keys * EngineAttackData.Stride];
            bool hasAttacks = false;
            var phaseEnergy = new double[WindowCycles * phaseBins];
            var noiseReal = new double[8192];
            var noiseImag = new double[noiseReal.Length];
            var sourceReal = new double[noiseReal.Length];
            var sourceImag = new double[noiseReal.Length];
            var sourcePower = new double[EngineResidualNoise.BandCount];
            for (int i = 0; i < window.Length; i++) window[i] = Hann(i, window.Length);

            int center = from;
            double peak = 0;
            for (int key = 0; key < keys; key++)
            {
                token.ThrowIfCancellationRequested();
                rpm[key] = key == keys - 1 ? maximum : minimum + (maximum - minimum) * key / (keys - 1);
                while (center < to - 1 && track[center + 1] < rpm[key]) center++;
                double mix = track[center + 1] > track[center]
                    ? (rpm[key] - track[center]) / (track[center + 1] - track[center]) : 0;
                double frequency = rpm[key] * rpmToHz;
                double centerCycle = cycles[center] + (track[center] + rpm[key]) * rpmToHz * 0.5 * mix * dt;
                int interval = Math.Max(0, center - 1);
                double firstCycle = centerCycle - WindowCycles * 0.5;
                while (interval > 0 && cycles[interval] > firstCycle) interval--;

                double weight = 0;
                double firstPosition = 0, lastPosition = 0;
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
                    if (i == 0) firstPosition = position;
                    if (i == real.Length - 1) lastPosition = position;
                    real[i] = 0;
                    imag[i] = 0;
                    cycleSamples[i] = double.NaN;
                    if (position < 0 || position >= samples.Length - 1) continue;
                    int sample = (int)position;
                    cycleSamples[i] = ReadSample(samples, sample, position - sample);
                    real[i] = cycleSamples[i] * window[i];
                    weight += window[i];
                }
                EngineSoundFft.Transform(real, imag);
                Array.Clear(harmonicWave, 0, harmonicWave.Length);
                Array.Clear(harmonicImag, 0, harmonicImag.Length);
                for (int h = 1; h <= harmonics && frequency * h < harmonicLimit; h++)
                {
                    int bin = h * WindowCycles;
                    double amplitude = 2 / weight;
                    if (h > BaseHarmonics)
                    {
                        // Reject diffuse noise masquerading as a high harmonic in a short FFT window.
                        double centerPower = Power(real, imag, bin);
                        double floor = (Power(real, imag, bin - 2) + Power(real, imag, bin + 2)) * 0.5;
                        double prominence = Math.Max(0, Math.Min(1, 1 - 2.5 * floor / (centerPower + 1e-20)));
                        amplitude *= prominence * Math.Min(1, (harmonicLimit - frequency * h) / 650);
                    }
                    int index = key * harmonics + h - 1;
                    float a = cosine[index] = (float)(real[bin] * amplitude);
                    float b = sine[index] = (float)(-imag[bin] * amplitude);
                    harmonicWave[h] = harmonicWave[harmonicWave.Length - h] = a * 0.5;
                    harmonicImag[h] = -b * 0.5;
                    harmonicImag[harmonicWave.Length - h] = b * 0.5;
                }
                EngineSoundFft.Transform(harmonicWave, harmonicImag, inverse: true);
                foreach (double sample in harmonicWave) peak = Math.Max(peak, Math.Abs(sample));
                int phaseShift = key > 0 ? Align(cosine, sine, key, harmonics) : 0;
                modulationAnalyzer.Extract(cycleSamples, harmonicWave, frequency, rate, phaseShift,
                    modulation, bandModulation, key);
                hasAttacks |= attackAnalyzer.Extract(cycleSamples, harmonicWave, frequency, phaseShift, attacks, key);
                ExtractMechanical(cycleSamples, harmonicWave, phaseEnergy, frequency,
                    phaseShift, mechanical, key * phaseBins);
                ExtractNoiseSpectrum(samples, rate, harmonicWave, result.Times[0], track, cycles, dt,
                    rpmToHz, firstCycle, firstPosition, lastPosition, noiseReal, noiseImag, sourceReal,
                    sourceImag, sourcePower, residualSpectrum, key * EngineResidualNoise.BandCount);
            }
            if (peak < 0.001)
                throw new InvalidOperationException("The selected section has too few prominent harmonics.");
            float gain = (float)Math.Min(16, 0.76 / peak);
            for (int i = 0; i < cosine.Length; i++)
            {
                cosine[i] *= gain;
                sine[i] *= gain;
            }
            for (int i = 0; i < residualSpectrum.Length; i++) residualSpectrum[i] *= gain;
            if (hasAttacks)
                for (int offset = 0; offset < attacks.Length; offset += EngineAttackData.Samples + 1)
                    for (int i = 1; i <= EngineAttackData.Samples; i++)
                        attacks[offset + i] = Math.Max(-0.28f, Math.Min(0.28f, attacks[offset + i] * gain));
            bool hasMechanical = false;
            for (int i = 0; i < mechanical.Length; i++)
            {
                mechanical[i] = Math.Min(0.12f, mechanical[i] * gain);
                hasMechanical |= mechanical[i] > 0.001f;
            }
            return new EngineSoundData(revolutions, harmonics, rpm, cosine, sine, residual,
                hasMechanical ? mechanical : null, residualSpectrum, hasAttacks ? attacks : null, modulation,
                bandResidualModulation: bandModulation);
        }

        private static double Power(double[] real, double[] imag, int bin) =>
            real[bin] * real[bin] + imag[bin] * imag[bin];

        private static double ReadSample(float[] samples, int i, double fraction)
        {
            double a = samples[i], b = samples[i + 1];
            if (i == 0 || i + 2 >= samples.Length) return a + (b - a) * fraction;
            double before = samples[i - 1], after = samples[i + 2];
            return a + 0.5 * fraction * (b - before + fraction *
                (2 * before - 5 * a + 4 * b - after + fraction * (3 * (a - b) + after - before)));
        }

        private static void ExtractNoiseSpectrum(float[] samples, int rate, double[] periodic, double firstTime,
            float[] rpm, double[] cycles, double dt, double rpmToHz, double firstCycle, double left, double right,
            double[] real, double[] imag, double[] srcReal, double[] srcImag, double[] sourcePower, float[] output, int offset)
        {
            Array.Clear(real, 0, real.Length);
            Array.Clear(imag, 0, imag.Length);
            Array.Clear(srcReal, 0, srcReal.Length);
            Array.Clear(srcImag, 0, srcImag.Length);
            Array.Clear(sourcePower, 0, sourcePower.Length);
            int start = Math.Max(0, (int)Math.Ceiling(Math.Max(left, (left + right - real.Length) * 0.5)));
            int end = Math.Min(samples.Length, Math.Min(start + real.Length, (int)Math.Floor(right) + 1));
            int count = end - start;
            if (count < 8) return;
            double weightSquared = 0;
            int last = cycles.Length - 1;
            for (int n = 0; n < count; n++)
            {
                double t = ((start + n) / (double)rate - firstTime) / dt;
                int i = Math.Min(last, Math.Max(0, (int)Math.Floor(t)));
                double sampleRpm = rpm[i], fraction = t - i;
                if (t > 0 && i < last) sampleRpm += 0.5 * (rpm[i + 1] - rpm[i]) * fraction;
                double phase = cycles[i] + sampleRpm * fraction * dt * rpmToHz - firstCycle;
                double p = (phase - Math.Floor(phase)) * periodic.Length;
                int index = (int)p;
                double tone = periodic[index] + (periodic[(index + 1) % periodic.Length] - periodic[index]) * (p - index);
                double weight = Hann(n, count - 1);
                srcReal[n] = samples[start + n] * weight;
                real[n] = srcReal[n] - tone * weight;
                weightSquared += weight * weight;
            }
            EngineSoundFft.Transform(real, imag);
            EngineSoundFft.Transform(srcReal, srcImag);
            double scale = 2 / (real.Length * weightSquared);
            for (int bin = 1; bin < real.Length * 0.48; bin++)
            {
                int band = EngineResidualNoise.GetBand(bin * (double)rate / real.Length);
                if (band < 0) continue;
                output[offset + band] += (float)(Power(real, imag, bin) * scale);
                sourcePower[band] += Power(srcReal, srcImag, bin) * scale;
            }
            for (int band = 0; band < sourcePower.Length; band++)
                output[offset + band] = (float)Math.Sqrt(Math.Min(output[offset + band], sourcePower[band]));
        }

        private static void ExtractMechanical(double[] samples, double[] periodic, double[] energy,
            double frequency, int shift, float[] output, int offset)
        {
            const int bins = EngineSoundData.MechanicalPhaseBins;
            int samplesPerBin = periodic.Length / bins;
            Array.Clear(energy, 0, energy.Length);
            double filter = 1 - Math.Exp(-Tau * 1600 / (frequency * periodic.Length));
            double smooth = 0;
            int validCycles = 0;
            for (int cycle = 0; cycle < WindowCycles; cycle++)
            {
                int first = cycle * periodic.Length;
                if (double.IsNaN(samples[first]) || double.IsNaN(samples[first + periodic.Length - 1])) continue;
                if (validCycles == 0) smooth = samples[first] - periodic[0];
                for (int i = 0; i < periodic.Length; i++)
                {
                    double residual = samples[first + i] - periodic[i];
                    smooth += filter * (residual - smooth);
                    double high = residual - smooth;
                    energy[validCycles * bins + i / samplesPerBin] += high * high / samplesPerBin;
                }
                validCycles++;
            }
            if (validCycles < 2) return;

            // Fold repeatable residual attacks into one cycle, rejecting spikes found in only one cycle.
            double average = 0;
            for (int i = 0; i < bins; i++)
            {
                double highest = 0, sum = 0;
                for (int cycle = 0; cycle < validCycles; cycle++)
                {
                    double value = energy[cycle * bins + i];
                    sum += value;
                    highest = Math.Max(value, highest);
                }
                energy[i] = (sum - highest) / (validCycles - 1);
                average += energy[i] / bins;
            }
            for (int i = 0; i < bins; i++)
            {
                double position = i + shift * (bins / 256.0);
                int a = (int)position % bins;
                double level = energy[a] + (energy[(a + 1) % bins] - energy[a]) * (position - Math.Floor(position));
                output[offset + i] = (float)Math.Sqrt(Math.Max(0, level - 2.5 * average));
            }
        }

        // Pick a common mechanical phase before morphing between adjacent RPM keys.
        private static int Align(float[] cosine, float[] sine, int key, int harmonics)
        {
            const int phaseBins = 256;
            int offset = key * harmonics;
            double bestScore = double.NegativeInfinity;
            int shift = 0;
            for (int step = 0; step < phaseBins; step++)
            {
                double a = step * Tau / phaseBins;
                double score = 0;
                for (int h = 1; h <= 24; h++)
                {
                    int i = offset + h - 1, j = i - harmonics;
                    double dot = cosine[i] * cosine[j] + sine[i] * sine[j];
                    double cross = sine[i] * cosine[j] - cosine[i] * sine[j];
                    score += dot * Math.Cos(a * h) + cross * Math.Sin(a * h);
                }
                if (score <= bestScore) continue;
                bestScore = score;
                shift = step;
            }
            for (int h = 1; h <= harmonics; h++)
            {
                int i = offset + h - 1;
                double a = shift * h * Tau / phaseBins;
                double cos = Math.Cos(a), sin = Math.Sin(a);
                float previousCos = cosine[i];
                cosine[i] = (float)(previousCos * cos + sine[i] * sin);
                sine[i] = (float)(sine[i] * cos - previousCos * sin);
            }
            return shift;
        }

        private static double Hann(int i, int length) => 0.5 - 0.5 * Math.Cos(Tau * i / length);

        private static double Sample(double[] values, double position)
        {
            if (position < 0 || position >= values.Length - 1) return 0;
            int i = (int)position;
            return values[i] + (values[i + 1] - values[i]) * (position - i);
        }

    }
}
