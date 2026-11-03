/*  FtxModulator.cs

FT8 / FT4 audio: Gaussian frequency-shift keying with continuous phase (the GFSK synthesis of ft8_lib, MIT
License, Copyright (c) 2018 Karlis Goba; FT4's ramp symbols as WSJT-X makes them).

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.DigiModes.Ftx
{
    // The modes: FT8 and FT4 (Gaussian FSK, this file), JT65, JT9, JT4, Q65 and WSPR (plain FSK) and FST4 / FST4W
    // (Gaussian FSK, OtherModes.cs)
    internal enum FtxMode { FT8, FT4, JT65, JT9, WSPR, FT2, Q65, FST4W, FST4, JT4 }

    internal static class FtxModes
    {
        public static readonly FtxMode[] All = { FtxMode.FT8, FtxMode.FT4, FtxMode.FT2, FtxMode.JT65, FtxMode.JT9, FtxMode.JT4, FtxMode.Q65, FtxMode.FST4, FtxMode.WSPR, FtxMode.FST4W };

        // FST4's period (15 s - 30 minutes), and JT4's submode (A - G: its tones 1, 2, 4, 9, 18, 36 or 72 x the baud apart)
        public static int Fst4Period = 60;
        public static char Jt4Sub = 'A';
        public static readonly int[] Fst4Periods = { 15, 30, 60, 120, 300, 900, 1800 };
        private static readonly int[] Jt4Spread = { 1, 2, 4, 9, 18, 36, 72 };

        // Q65's period (15, 30, 60, 120 or 300 s) and submode (A - E: its tones 1, 2, 4, 8 or 16 x the baud apart), and
        // FST4W's period (120, 300, 900 or 1800 s): chosen in the Mode menu
        public static int Q65Period = 30, Fst4wPeriod = 120;
        public static char Q65Sub = 'A';
        public static readonly int[] Q65Periods = { 15, 30, 60, 120, 300 }, Fst4wPeriods = { 120, 300, 900, 1800 };

        // samples a symbol at 12000 a second, as WSJT-X has them
        public static int Q65Nsps(int period) { switch (period) { case 15: return 1800; case 30: return 3600; case 60: return 7200; case 120: return 16000; default: return 41472; } }
        public static int Fst4Nsps(int period) { switch (period) { case 15: return 720; case 30: return 1680; case 60: return 3888; case 120: return 8200; case 300: return 21504; case 900: return 66560; default: return 134400; } }

        // the beacon modes: no QSOs, a share of the periods transmitting
        public static bool IsBeacon(FtxMode m) { return m == FtxMode.WSPR || m == FtxMode.FST4W; }

        public static bool IsFtx(FtxMode m) { return m == FtxMode.FT8 || m == FtxMode.FT4 || m == FtxMode.FT2; }

        // marked experimental until they've been on the air: FT2 (FT4's signal at twice the speed, young, with two
        // implementations: Decodium, WSJT-X Improved), and Q65, FST4, FST4W and JT4 (checked against WSJT-X's own
        // encoders and jt9 only)
        public static bool Experimental(FtxMode m) { return m == FtxMode.FT2 || m == FtxMode.Q65 || m == FtxMode.FST4 || m == FtxMode.FST4W || m == FtxMode.JT4; }
        public static string Label(FtxMode m)
        {
            if (m == FtxMode.Q65) return "Q65-" + Q65Period + Q65Sub;
            if (m == FtxMode.FST4W) return "FST4W-" + Fst4wPeriod;
            if (m == FtxMode.FST4) return "FST4-" + Fst4Period;
            if (m == FtxMode.JT4) return "JT4" + Jt4Sub;
            return m.ToString();
        }
        // in the mode menu
        public static string MenuLabel(FtxMode m) { return Label(m) + (Experimental(m) ? " (experimental)" : ""); }
        public static bool Ft4Like(FtxMode m) { return m == FtxMode.FT4 || m == FtxMode.FT2; }
        public static bool HasQso(FtxMode m) { return !IsBeacon(m); }                       // WSPR and FST4W are beacons, no QSOs

        public static double Period(FtxMode m)
        {
            switch (m) { case FtxMode.FT8: return 15.0; case FtxMode.FT4: return 7.5; case FtxMode.FT2: return 3.75; case FtxMode.WSPR: return 120.0; case FtxMode.Q65: return Q65Period; case FtxMode.FST4W: return Fst4wPeriod; case FtxMode.FST4: return Fst4Period; default: return 60.0; }
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
                case FtxMode.Q65: return Q65Nsps(Q65Period) / 12000.0;
                case FtxMode.FST4W: return Fst4Nsps(Fst4wPeriod) / 12000.0;
                case FtxMode.FST4: return Fst4Nsps(Fst4Period) / 12000.0;
                case FtxMode.JT4: return 2520.0 / 11025.0;
                default: return 8192.0 / 12000.0;           // WSPR
            }
        }

        public static double ToneSpacing(FtxMode m)
        {
            int k = m == FtxMode.Q65 ? 1 << (Q65Sub - 'A') : m == FtxMode.JT4 ? Jt4Spread[Math.Max(0, Math.Min(6, Jt4Sub - 'A'))] : 1;
            return k / SymbolPeriod(m);
        }

        // the signal's width (Hz)
        public static double Bandwidth(FtxMode m)
        {
            switch (m) { case FtxMode.FT8: return 8 * 6.25; case FtxMode.FT4: return 4 * 20.833; case FtxMode.FT2: return 4 * 41.667; case FtxMode.JT65: return 66 * 11025.0 / 4096; case FtxMode.JT9: return 9 * 12000.0 / 6912; case FtxMode.Q65: return 65 * ToneSpacing(m); case FtxMode.FST4W: case FtxMode.FST4: case FtxMode.JT4: return 4 * ToneSpacing(m); default: return 6; }
        }

        // how long the signal lasts (s)
        public static double Duration(FtxMode m)
        {
            switch (m) { case FtxMode.FT8: return 79 * 0.160; case FtxMode.FT4: return 105 * 0.048; case FtxMode.FT2: return 105 * 0.024; case FtxMode.JT65: return 126 * 4096.0 / 11025; case FtxMode.JT9: return 85 * 6912.0 / 12000; case FtxMode.Q65: return 85 * SymbolPeriod(m); case FtxMode.FST4W: case FtxMode.FST4: return 160 * SymbolPeriod(m); case FtxMode.JT4: return 206 * SymbolPeriod(m); default: return 162 * 8192.0 / 12000; }
        }

        // where in the period the signal starts (DT 0)
        // (WSJT-X's Modulator: Q65-15 / 30 and FST4-15 / 30 0.5 s, as FT8; the longer ones and JT4 1 s)
        public static double TxStart(FtxMode m) { return IsFtx(m) || (m == FtxMode.Q65 && Q65Period <= 30) || (m == FtxMode.FST4 && Fst4Period <= 30) ? 0.5 : 1.0; }
        public static double Bt(FtxMode m) { return m == FtxMode.FT8 ? 2.0 : 1.0; }

        // when the decode runs: once a signal at DT 0 has ended (FT8 / FT4 also again at the period's end)
        public static double DecodeAt(FtxMode m)
        {
            switch (m)
            {
                case FtxMode.FT8: return 13.6; case FtxMode.FT4: return 5.9; case FtxMode.FT2: return 3.1; case FtxMode.WSPR: return 113.5;
                case FtxMode.Q65: case FtxMode.FST4W: case FtxMode.FST4: case FtxMode.JT4: return Math.Min(Period(m) - 0.4, TxStart(m) + Duration(m) + 0.6);
                default: return 50.0;
            }
        }

        // the label WSJT-X's UDP messages use for the mode
        public static string UdpChar(FtxMode m)
        {
            switch (m) { case FtxMode.FT8: return "~"; case FtxMode.FT4: case FtxMode.FT2: return "+"; case FtxMode.JT65: return "#"; case FtxMode.JT9: return "@"; case FtxMode.Q65: return ":"; case FtxMode.JT4: return "$"; default: return "`"; }
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
