/*  FtxModulator.cs

FT8 / FT4 audio: Gaussian frequency-shift keying with continuous phase (the GFSK synthesis of ft8_lib, MIT
License, Copyright (c) 2018 Karlis Goba; FT4's ramp symbols as WSJT-X makes them).

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.DigiModes.Ftx
{
    internal enum FtxMode { FT8, FT4 }

    internal static class FtxModes
    {
        public static double Period(FtxMode m) { return m == FtxMode.FT8 ? 15.0 : 7.5; }
        public static double SymbolPeriod(FtxMode m) { return m == FtxMode.FT8 ? 0.160 : 0.048; }
        public static double ToneSpacing(FtxMode m) { return 1.0 / SymbolPeriod(m); }
        public static double Bandwidth(FtxMode m) { return m == FtxMode.FT8 ? 8 * 6.25 : 4 * 20.833; }
        // where in the period the signal starts (DT 0)
        public static double TxStart(FtxMode m) { return m == FtxMode.FT8 ? 0.5 : 0.5; }
        public static double Bt(FtxMode m) { return m == FtxMode.FT8 ? 2.0 : 1.0; }
    }

    internal static class FtxModulator
    {
        private const double GfskK = 5.336446256636997;        // pi * sqrt(2 / ln 2)

        // the waveform for the tones at audio frequency f0 (the lowest tone), amplitude 1
        public static float[] Synthesize(FtxMode mode, byte[] tones, double f0, int rate)
        {
            double symPeriod = FtxModes.SymbolPeriod(mode), bt = FtxModes.Bt(mode);
            int nsps = (int)Math.Round(rate * symPeriod);
            int nsym = tones.Length;
            bool ft4 = mode == FtxMode.FT4;
            // dphi covers a dummy symbol before and after (each with its neighbour's tone), so the phase ramps smoothly
            double[] dphi = new double[(nsym + 2) * nsps];
            double baseStep = 2 * Math.PI * f0 / rate, peak = 2 * Math.PI / nsps;
            for (int i = 0; i < dphi.Length; i++) dphi[i] = baseStep;

            double[] pulse = new double[3 * nsps];
            for (int i = 0; i < pulse.Length; i++)
            {
                double t = i / (double)nsps - 1.5;
                pulse[i] = (Erf(GfskK * bt * (t + 0.5)) - Erf(GfskK * bt * (t - 0.5))) / 2;
            }
            for (int i = 0; i < nsym; i++)
            {
                int ib = i * nsps;
                for (int j = 0; j < 3 * nsps; j++) dphi[j + ib] += peak * tones[i] * pulse[j];
            }
            for (int j = 0; j < 2 * nsps; j++)
            {
                dphi[j] += peak * pulse[j + nsps] * tones[0];
                dphi[j + nsym * nsps] += peak * pulse[j] * tones[nsym - 1];
            }

            // FT8: the symbols only, with a short ramp; FT4: the dummy symbols are sent too, as full-symbol ramps
            int n = ft4 ? (nsym + 2) * nsps : nsym * nsps;
            int off = ft4 ? 0 : nsps;
            float[] s = new float[n];
            double phi = 0;
            for (int k = 0; k < n; k++)
            {
                s[k] = (float)Math.Sin(phi);
                phi += dphi[k + off];
                if (phi > 2 * Math.PI) phi -= 2 * Math.PI;
            }
            int ramp = ft4 ? nsps : nsps / 8;
            for (int i = 0; i < ramp; i++)
            {
                float env = (float)((1 - Math.Cos(Math.PI * i / ramp)) / 2);
                s[i] *= env;
                s[n - 1 - i] *= env;
            }
            return s;
        }

        // Abramowitz and Stegun 7.1.26 (error under 1.5e-7)
        internal static double Erf(double x)
        {
            double sign = x < 0 ? -1 : 1;
            x = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.3275911 * x);
            double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
            return sign * y;
        }
    }
}
