/*  FtxModulator.cs

FT8 / FT4 audio: Gaussian frequency-shift keying with continuous phase (the GFSK synthesis of ft8_lib, MIT
License, Copyright (c) 2018 Karlis Goba; FT4's ramp symbols as WSJT-X makes them).

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.DigiModes.Ftx
{
    // The modes: FT8 and FT4 (Gaussian FSK, this file), JT65, JT9 and WSPR (plain FSK, OtherModes.cs)
    internal enum FtxMode { FT8, FT4, JT65, JT9, WSPR, FT2 }

    internal static class FtxModes
    {
        public static readonly FtxMode[] All = { FtxMode.FT8, FtxMode.FT4, FtxMode.FT2, FtxMode.JT65, FtxMode.JT9, FtxMode.WSPR };

        public static bool IsFtx(FtxMode m) { return m == FtxMode.FT8 || m == FtxMode.FT4 || m == FtxMode.FT2; }

        // FT2: FT4's signal at twice the speed (4-GFSK, 24 ms symbols, 3.75 s periods). Young, with two implementations
        // (Decodium, WSJT-X Improved): marked experimental
        public static bool Experimental(FtxMode m) { return m == FtxMode.FT2; }
        public static string Label(FtxMode m) { return Experimental(m) ? m + " (experimental)" : m.ToString(); }
        public static bool Ft4Like(FtxMode m) { return m == FtxMode.FT4 || m == FtxMode.FT2; }
        public static bool HasQso(FtxMode m) { return m != FtxMode.WSPR; }                  // WSPR is beacons, no QSOs

        public static double Period(FtxMode m)
        {
            switch (m) { case FtxMode.FT8: return 15.0; case FtxMode.FT4: return 7.5; case FtxMode.FT2: return 3.75; case FtxMode.WSPR: return 120.0; default: return 60.0; }
        }

        public static double SymbolPeriod(FtxMode m)
        {
            switch (m)
            {
                case FtxMode.FT8: return 0.160;
                case FtxMode.FT4: return 0.048;
                case FtxMode.FT2: return 0.024;
                case FtxMode.JT65: return 4096.0 / 11025.0;
                case FtxMode.JT9: return 6912.0 / 12000.0;
                default: return 8192.0 / 12000.0;           // WSPR
            }
        }

        public static double ToneSpacing(FtxMode m) { return 1.0 / SymbolPeriod(m); }

        // the signal's width (Hz)
        public static double Bandwidth(FtxMode m)
        {
            switch (m) { case FtxMode.FT8: return 8 * 6.25; case FtxMode.FT4: return 4 * 20.833; case FtxMode.FT2: return 4 * 41.667; case FtxMode.JT65: return 66 * 11025.0 / 4096; case FtxMode.JT9: return 9 * 12000.0 / 6912; default: return 6; }
        }

        // how long the signal lasts (s)
        public static double Duration(FtxMode m)
        {
            switch (m) { case FtxMode.FT8: return 79 * 0.160; case FtxMode.FT4: return 105 * 0.048; case FtxMode.FT2: return 105 * 0.024; case FtxMode.JT65: return 126 * 4096.0 / 11025; case FtxMode.JT9: return 85 * 6912.0 / 12000; default: return 162 * 8192.0 / 12000; }
        }

        // where in the period the signal starts (DT 0)
        public static double TxStart(FtxMode m) { return IsFtx(m) ? 0.5 : 1.0; }
        public static double Bt(FtxMode m) { return m == FtxMode.FT8 ? 2.0 : 1.0; }

        // when the decode runs: once a signal at DT 0 has ended (FT8 / FT4 also again at the period's end)
        public static double DecodeAt(FtxMode m)
        {
            switch (m) { case FtxMode.FT8: return 13.6; case FtxMode.FT4: return 5.9; case FtxMode.FT2: return 3.1; case FtxMode.WSPR: return 113.5; default: return 50.0; }
        }

        // the label WSJT-X's UDP messages use for the mode
        public static string UdpChar(FtxMode m)
        {
            switch (m) { case FtxMode.FT8: return "~"; case FtxMode.FT4: case FtxMode.FT2: return "+"; case FtxMode.JT65: return "#"; case FtxMode.JT9: return "@"; default: return "`"; }
        }
    }

    internal static class FtxModulator
    {
        private const double GfskK = 5.336446256636997;        // pi * sqrt(2 / ln 2)

        // the waveform for the tones at audio frequency f0 (the lowest tone), amplitude 1
        public static float[] Synthesize(FtxMode mode, byte[] tones, double f0, int rate) { return Synthesize(mode, tones, f0, rate, FtxModes.SymbolPeriod(mode)); }

        // with another symbol length
        internal static float[] Synthesize(FtxMode mode, byte[] tones, double f0, int rate, double symPeriod)
        {
            double bt = FtxModes.Bt(mode);
            int nsps = (int)Math.Round(rate * symPeriod);
            int nsym = tones.Length;
            bool ft4 = FtxModes.Ft4Like(mode);
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
