using System;
using System.IO;
using System.IO.Compression;

namespace RpmSynth
{
    // Cycle-synchronous playback of two-cycle recording fragments.
    public sealed class EngineGrainData
    {
        public readonly int RevolutionsPerCycle, SampleRate;
        public readonly float MinimumRpm, MaximumRpm;
        public readonly float[] Rpm;
        public readonly short[][] Grains;
        public int PcmBytes
        {
            get
            {
                int bytes = 0;
                foreach (var grain in Grains) bytes += grain.Length * sizeof(short);
                return bytes;
            }
        }

        public EngineGrainData(int revolutions, int rate, float minimum, float maximum, float[] rpm, short[][] grains)
        {
            RevolutionsPerCycle = revolutions;
            SampleRate = rate;
            MinimumRpm = minimum;
            MaximumRpm = maximum;
            Rpm = rpm;
            Grains = grains;
        }

        public void Save(string path, bool compress = false)
        {
            using (var file = File.Create(path))
            {
                if (compress)
                {
                    using (var marker = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true))
                        marker.Write(0x5A475245); // ERGZ wraps the same data in lossless DEFLATE.
                    using (var deflated = new DeflateStream(file, CompressionLevel.Optimal, leaveOpen: true))
                        Write(deflated);
                }
                else Write(file);
            }
        }

        private void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x31475245); // ERG1, little-endian mono PCM16.
                writer.Write(RevolutionsPerCycle);
                writer.Write(SampleRate);
                writer.Write(MinimumRpm);
                writer.Write(MaximumRpm);
                writer.Write(Rpm.Length);
                for (int i = 0; i < Rpm.Length; i++)
                {
                    writer.Write(Rpm[i]);
                    writer.Write(Grains[i].Length);
                    foreach (short value in Grains[i]) writer.Write(value);
                }
            }
        }

        public static EngineGrainData Load(string path)
        {
            using (var stream = File.OpenRead(path))
                return Load(stream);
        }

        public static EngineGrainData Load(Stream stream)
        {
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                int signature = reader.ReadInt32();
                if (signature == 0x5A475245)
                {
                    using (var inflater = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true))
                    using (var decoded = new MemoryStream())
                    {
                        // Bound uncompressed size before allocating any grain arrays.
                        var block = new byte[8192];
                        int bytes;
                        while ((bytes = inflater.Read(block, 0, block.Length)) > 0)
                        {
                            if (decoded.Length + bytes > 64 * 1024 * 1024)
                                throw new InvalidDataException("Grain profile exceeds the size limit.");
                            decoded.Write(block, 0, bytes);
                        }
                        decoded.Position = 0;
                        return Load(decoded);
                    }
                }
                if (signature != 0x31475245) throw new InvalidDataException("Unknown recorded engine profile format.");
                int rev = reader.ReadInt32(), rate = reader.ReadInt32();
                float minimum = reader.ReadSingle(), maximum = reader.ReadSingle();
                int count = reader.ReadInt32();
                if (rev < 1 || rev > 2 || rate < 8000 || rate > 48000 || count < 2 || count > 4096
                    || !(minimum > 0 && maximum > minimum)) throw new InvalidDataException("Invalid grain profile header.");
                var rpm = new float[count];
                var grains = new short[count][];
                for (int i = 0; i < count; i++)
                {
                    rpm[i] = reader.ReadSingle();
                    int length = reader.ReadInt32();
                    if (!(rpm[i] > 0) || (i > 0 && rpm[i] < rpm[i - 1]) || length < 32 || length > rate * 2)
                        throw new InvalidDataException("Invalid grain entry.");
                    grains[i] = new short[length];
                    for (int j = 0; j < length; j++) grains[i][j] = reader.ReadInt16();
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Extra grain data.");
                return new EngineGrainData(rev, rate, minimum, maximum, rpm, grains);
            }
        }
    }

    public sealed class EngineGrainSynth
    {
        private const int WindowSize = 2048;
        private static readonly float[] Window = MakeWindow();
        private readonly EngineGrainData data;
        private readonly double cyclesPerRpmSample, smoothing;
        private double age, rpm;
        private float gain;
        private int current, previous, nearest, launches;

        public EngineGrainSynth(EngineGrainData data, int sampleRate, float initialRpm)
        {
            if (sampleRate < data.SampleRate)
                throw new ArgumentException("The output rate must be at least the grain storage rate.");
            this.data = data;
            rpm = Sanitize(initialRpm);
            cyclesPerRpmSample = 1.0 / (60 * data.RevolutionsPerCycle * sampleRate);
            smoothing = 1 - Math.Exp(-1.0 / (.012 * sampleRate));
            current = previous = Nearest((float)rpm);
        }

        public void Render(float[] output, float targetRpm, float targetGain = .8f)
        {
            targetRpm = Sanitize(targetRpm);
            targetGain = float.IsNaN(targetGain) ? 0 : Math.Max(0, Math.Min(1, targetGain));
            for (int i = 0; i < output.Length; i++)
            {
                rpm += (targetRpm - rpm) * smoothing;
                gain += (targetGain - gain) * (float)smoothing;
                float oldWeight = ReadWindow(age + 1), newWeight = ReadWindow(age);
                float value = Read(data.Grains[previous], age + 1) * oldWeight
                    + Read(data.Grains[current], age) * newWeight;
                output[i] = value * gain * Math.Min(1, (float)rpm / 100);
                age += rpm * cyclesPerRpmSample;
                if (age < 1) continue;
                age -= 1;
                previous = current;
                current = Select((float)rpm);
            }
        }

        private float Sanitize(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? data.MinimumRpm : Math.Max(0, Math.Min(EngineSoundSynth.MaximumSupportedRpm, value));

        private int Nearest(float speed)
        {
            while (nearest < data.Rpm.Length - 1 && speed > (data.Rpm[nearest] + data.Rpm[nearest + 1]) * .5f) nearest++;
            while (nearest > 0 && speed < (data.Rpm[nearest - 1] + data.Rpm[nearest]) * .5f) nearest--;
            return nearest;
        }

        private int Select(float speed)
        {
            int center = Nearest(speed);
            // At a held RPM, rotate nearby recorded cycles instead of repeating one waveform forever.
            int offset = (++launches % 3) - 1;
            int next = Math.Max(0, Math.Min(data.Rpm.Length - 1, center + offset));
            return Math.Abs(data.Rpm[next] - speed) < Math.Max(45, speed * .035f) ? next : center;
        }

        private static float Read(short[] grain, double cycles)
        {
            double position = cycles * (grain.Length - 1) * .5;
            int i = (int)position;
            i = Math.Min(i, grain.Length - 2);
            double t = position - i, a = grain[i], b = grain[i + 1];
            if (i == 0 || i + 2 >= grain.Length) return (float)(a + (b - a) * t) / 32768f;
            double before = grain[i - 1], after = grain[i + 2];
            double value = a + .5 * t * (b - before + t * (2 * before - 5 * a + 4 * b - after
                + t * (3 * (a - b) + after - before)));
            return (float)(value / 32768f);
        }

        private static float ReadWindow(double cycles)
        {
            double position = cycles * WindowSize * .5;
            int i = (int)position;
            i = Math.Min(i, WindowSize - 1);
            return Window[i] + (Window[i + 1] - Window[i]) * (float)(position - i);
        }

        private static float[] MakeWindow()
        {
            var window = new float[WindowSize + 1];
            for (int i = 0; i < window.Length; i++) window[i] = (float)(.5 - .5 * Math.Cos(2 * Math.PI * i / WindowSize));
            return window;
        }
    }
}
