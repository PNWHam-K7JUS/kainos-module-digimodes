/*  Spectrum.cs

The waterfall's lines: the 12 kHz receive audio through a 2048-point FFT (5.86 Hz a bin, Hann window), a line
every 2048 samples (about 6 a second), 0 - 3000 Hz in dB.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;

namespace Kainos.DigiModes.Decode
{
    internal class SpectrumMaker
    {
        // 4096 points (2.93 Hz a bin, so a 50 Hz FT8 signal is 17 bins wide), a new line every 2048 samples
        // (overlapping halves: about 6 lines a second)
        public const int N = 4096, Hop = 2048, Rate = 12000;
        public const double MaxHz = 3000;
        public static readonly int Bins = (int)(MaxHz * N / Rate);         // 1024
        public static double LineSeconds { get { return Hop / (double)Rate; } }
        public int SamplesSinceLine { get { return _sinceLine; } }         // taken in since the last line was made
        private readonly float[] _buf = new float[N];                       // the last N samples, oldest first from _pos
        private int _pos, _filled, _sinceLine;
        private readonly double[] _win = new double[N];
        private readonly double[] _re = new double[N], _im = new double[N];

        public SpectrumMaker()
        {
            for (int i = 0; i < N; i++) _win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
        }

        // new 12 kHz samples in; a line (Bins values, dB) for every Hop samples out
        public void Push(float[] s, int n, List<float[]> lines)
        {
            for (int i = 0; i < n; i++)
            {
                _buf[_pos] = s[i];
                _pos = (_pos + 1) % N;
                if (_filled < N) _filled++;
                if (++_sinceLine < Hop || _filled < N) continue;
                _sinceLine = 0;
                lines.Add(line());
            }
        }

        private float[] line()
        {
            for (int i = 0; i < N; i++) { _re[i] = _buf[(_pos + i) % N] * _win[i]; _im[i] = 0; }
            Fft(_re, _im);
            float[] db = new float[Bins];
            for (int k = 0; k < Bins; k++) db[k] = (float)(10 * Math.Log10(_re[k] * _re[k] + _im[k] * _im[k] + 1e-20));
            return db;
        }

        // in-place radix-2 FFT
        internal static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                    }
                }
            }
        }
    }
}
