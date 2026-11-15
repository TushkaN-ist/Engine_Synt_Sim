using System;

namespace RpmSynth
{
    public static class EngineSoundFft
    {
        private const double Tau = 2 * Math.PI;

        public static void Transform(double[] real, double[] imag, bool inverse = false)
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
                double stepCos = Math.Cos(Tau / size), stepSin = Math.Sin((inverse ? Tau : -Tau) / size);
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
