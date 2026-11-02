/*  OtherModes.cs

JT65, JT9, Q65, WSPR and FST4W transmit tones, and their audio: plain (continuous-phase) FSK, and FST4W's Gaussian FSK.

  JT65: WSJT-X's jt65code gives the 63 data symbols (Reed-Solomon coded, interleaved, Gray coded); they take the
        126-symbol frame's places where the sync pattern (nprc, from WSJT-X's setup65.f90) is 0, the sync tone the
        places where it's 1. Sync is tone 0; a data symbol d is tone d + 2. 11025 / 4096 Hz apart, 4096 / 11025 s each.
  JT9:  WSJT-X's jt9code gives all 85 channel tones (0 sync, 1 - 8 data). 12000 / 6912 Hz apart, 6912 / 12000 s each.
  WSPR: encoded here, after WSJT-X's wsprsim_utils.c: callsign (28 bits) and grid + power (22 bits), the K=32
        rate 1/2 convolutional code (polynomials 0xF2D05351, 0xE4613C47), bit-reversal interleaving and the
        162-bit sync vector; tone = 2 x data bit + sync bit. 12000 / 8192 Hz apart, 8192 / 12000 s each.
  Q65:  WSJT-X's q65code gives the 85 channel symbols: 0 the sync tone (the lowest, at the Tx frequency), a data
        symbol d tone d + 1, of 65. Plain FSK, the tones baud x 1, 2, 4, 8 or 16 apart (submode A - E).
  FST4W: WSJT-X's fst4sim gives the 160 channel tones (0 - 3), which go out as WSJT-X's gen_fst4wave makes them:
        Gaussian FSK (BT 2), the tones a baud apart, the lowest at the Tx frequency (as fst4sim puts it, and jt9
        reports it), a quarter-symbol ramp at each end.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Kainos.DigiModes.Ftx
{
    internal static class OtherModes
    {
        // ---- JT65 ----

        private static readonly byte[] Jt65Sync =
        {
            1,0,0,1,1,0,0,0,1,1,1,1,1,1,0,1,0,1,0,0, 0,1,0,1,1,0,0,1,0,0,0,1,1,1,0,0,1,1,1,1,
            0,1,1,0,1,1,1,1,0,0,0,1,1,0,1,0,1,0,1,1, 0,0,1,1,0,1,0,1,0,1,0,0,1,0,0,0,0,0,0,1,
            1,0,0,0,0,0,0,0,1,1,0,1,0,0,1,0,1,1,0,1, 0,1,0,1,0,0,1,1,0,0,1,0,0,1,0,0,0,0,1,1,
            1,1,1,1,1,1,
        };

        // the 126 tones from jt65code's 63 data symbols
        public static byte[] Jt65Tones(int[] data63)
        {
            if (data63 == null || data63.Length != 63) throw new ArgumentException("63 data symbols expected");
            byte[] t = new byte[126];
            int k = 0;
            for (int i = 0; i < 126; i++) t[i] = Jt65Sync[i] == 1 ? (byte)0 : (byte)(data63[k++] + 2);
            return t;
        }

        // jt65code / jt9code: the message's tones (and how it will be received); null and why when it can't
        public static byte[] JtTones(FtxMode mode, string codeExe, string message, out string decodedAs, out string problem)
        {
            decodedAs = null;
            problem = null;
            if (string.IsNullOrEmpty(codeExe) || !File.Exists(codeExe)) { problem = Path.GetFileName(codeExe ?? (mode == FtxMode.JT65 ? "jt65code.exe" : mode == FtxMode.Q65 ? "q65code.exe" : "jt9code.exe")) + " isn't in the decoder folder"; return null; }
            string output = run(codeExe, message, out problem);
            if (output == null) return null;
            return ParseJt(mode, output, out decodedAs, out problem);
        }

        internal static byte[] ParseJt(FtxMode mode, string output, out string decodedAs, out string problem)
        {
            decodedAs = null;
            problem = null;
            string[] lines = output.Replace("\r", "").Split('\n');
            // " 1. CQ K7JUS CN87          CQ K7JUS CN87             1:Std Msg ..." : message, then as decoded
            foreach (string l in lines)
            {
                string t = l.Trim();
                if (!t.StartsWith("1.")) continue;
                string[] cols = Regex.Split(t.Substring(2).Trim(), @"\s{2,}");
                if (cols.Length >= 2) decodedAs = Pack77.Normalise(cols[1]);
                break;
            }
            string marker = mode == FtxMode.JT65 ? "Information-carrying channel symbols" : "Channel symbols";
            int at = Array.FindIndex(lines, l => l.Trim().StartsWith(marker));
            if (at < 0) { problem = "no channel symbols from the encoder"; return null; }
            List<int> v = new List<int>();
            for (int i = at + 1; i < lines.Length; i++)
                foreach (string w in lines[i].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int x;
                    if (!int.TryParse(w, out x)) goto done;
                    v.Add(x);
                }
            done:
            if (mode == FtxMode.JT65)
            {
                if (v.Count < 63 || v.Take(63).Any(x => x < 0 || x > 63)) { problem = "JT65: expected 63 symbols, got " + v.Count; return null; }
                return Jt65Tones(v.Take(63).ToArray());
            }
            if (mode == FtxMode.Q65)
            {
                if (v.Count < 85 || v.Take(85).Any(x => x < 0 || x > 64)) { problem = "Q65: expected 85 tones, got " + v.Count; return null; }
                return v.Take(85).Select(x => (byte)x).ToArray();
            }
            if (v.Count < 85 || v.Take(85).Any(x => x < 0 || x > 8)) { problem = "JT9: expected 85 tones, got " + v.Count; return null; }
            return v.Take(85).Select(x => (byte)x).ToArray();
        }

        // ---- FST4W ----

        // fst4sim "CALL GRID dBm" period f0 DT fdop delay nfiles snr W: the tones are printed under "Channel symbols:"
        // (and it writes a WAV of the signal, which is thrown away)
        public static byte[] Fst4wTones(string simExe, string call, string grid, int dbm, int period, out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(simExe) || !File.Exists(simExe)) { problem = "fst4sim.exe isn't in the decoder folder"; return null; }
            if (string.IsNullOrEmpty(call) || string.IsNullOrEmpty(grid) || grid.Length < 4) { problem = "FST4W needs your callsign and grid"; return null; }
            string work = Path.Combine(Path.GetTempPath(), "kainos-fst4w");
            Directory.CreateDirectory(work);
            string msg = call.ToUpperInvariant() + " " + grid.Substring(0, 4).ToUpperInvariant() + " " + dbm.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string output = run(simExe, msg, out problem, " " + period.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 1500 0.0 0.0 0.0 1 99 T", work);
            try { foreach (string w in Directory.GetFiles(work, "*.wav")) File.Delete(w); } catch { }
            if (output == null) return null;
            return ParseFst4(output, out problem);
        }

        internal static byte[] ParseFst4(string output, out string problem)
        {
            problem = null;
            string[] lines = output.Replace("\r", "").Split('\n');
            int at = Array.FindIndex(lines, l => l.Trim().StartsWith("Channel symbols"));
            if (at < 0) { problem = "no channel symbols from fst4sim"; return null; }
            List<byte> v = new List<byte>();
            for (int i = at + 1; i < lines.Length && v.Count < 160; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) { if (v.Count > 0) break; continue; }
                if (!t.All(c => c >= '0' && c <= '3')) break;
                foreach (char c in t) v.Add((byte)(c - '0'));
            }
            if (v.Count != 160) { problem = "FST4W: expected 160 tones, got " + v.Count; return null; }
            return v.ToArray();
        }

        // gen_fst4wave: Gaussian FSK (BT 2), the tones a baud apart from f0 (tone 0), continuous phase, the first and
        // last quarter-symbol ramped
        public static float[] SynthesizeFst4(byte[] tones, double f0, int rate, double symPeriod)
        {
            int nsps = (int)Math.Round(rate * symPeriod), nsym = tones.Length;
            double[] pulse = new double[3 * nsps];
            const double k = 5.336446256636997;        // pi * sqrt(2 / ln 2)
            for (int i = 0; i < pulse.Length; i++)
            {
                double t = (i + 1 - 1.5 * nsps) / nsps;         // WSJT-X's (i - 1.5 nsps) / nsps, i from 1
                pulse[i] = (FtxModulator.Erf(k * 2.0 * (t + 0.5)) - FtxModulator.Erf(k * 2.0 * (t - 0.5))) / 2;
            }
            double[] dphi = new double[(nsym + 2) * nsps];
            double peak = 2 * Math.PI / nsps;
            for (int j = 0; j < nsym; j++)
            {
                int ib = j * nsps;
                for (int i = 0; i < 3 * nsps; i++) dphi[ib + i] += peak * pulse[i] * tones[j];
            }
            double shift = 2 * Math.PI * f0 / rate;
            float[] s = new float[nsym * nsps];
            double phi = 0;
            for (int j = nsps, o = 0; j < (nsym + 1) * nsps; j++, o++)
            {
                s[o] = (float)Math.Sin(phi);
                phi += dphi[j] + shift;
                if (phi > 2 * Math.PI) phi -= 2 * Math.PI;
            }
            int q = nsps / 4;
            for (int i = 0; i < q; i++) s[i] *= (float)((1 - Math.Cos(2 * Math.PI * i / (nsps / 2.0))) / 2);
            int k1 = (nsym - 1) * nsps + 3 * nsps / 4;
            for (int i = 0; i <= q && k1 + i < s.Length; i++) s[k1 + i] *= (float)((1 + Math.Cos(2 * Math.PI * i / (nsps / 2.0))) / 2);
            return s;
        }

        private static string run(string exe, string message, out string problem, string more = "", string workDir = null)
        {
            problem = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "\"" + message.Replace("\"", "") + "\"" + more)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = workDir ?? Path.GetDirectoryName(exe),
                };
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } problem = Path.GetFileName(exe) + " didn't finish"; return null; }
                    return o;
                }
            }
            catch (Exception ex) { problem = Path.GetFileName(exe) + " failed: " + ex.Message; return null; }
        }

        // ---- WSPR ----

        private static readonly byte[] WsprSync =
        {
            1,1,0,0,0,0,0,0,1,0,0,0,1,1,1,0,0,0,1,0, 0,1,0,1,1,1,1,0,0,0,0,0,0,0,1,0,0,1,0,1,
            0,0,0,0,0,0,1,0,1,1,0,0,1,1,0,1,0,0,0,1, 1,0,1,0,0,0,0,1,1,0,1,0,1,0,1,0,1,0,0,1,
            0,0,1,0,1,1,0,0,0,1,1,0,1,0,1,0,0,0,1,0, 0,0,0,0,1,0,0,1,0,0,1,1,1,0,1,1,0,0,1,1,
            0,1,0,0,0,1,1,1,0,0,0,0,0,1,0,1,0,0,1,1, 0,0,0,0,0,0,0,1,1,0,1,0,1,1,0,0,0,1,1,0,
            0,0,
        };

        public static readonly int[] WsprPowers = { 0, 3, 7, 10, 13, 17, 20, 23, 27, 30, 33, 37, 40, 43, 47, 50, 53, 57, 60 };

        // a WSPR type 1 message: a plain callsign (no / prefix or suffix), a 4-character grid, power in dBm
        public static byte[] WsprTones(string call, string grid, int dbm, out string problem)
        {
            problem = null;
            call = (call ?? "").Trim().ToUpperInvariant();
            grid = (grid ?? "").Trim().ToUpperInvariant();
            if (call.Contains("/")) { problem = "WSPR here sends plain callsigns only (no / prefix or suffix)"; return null; }
            if (call.Length < 3 || call.Length > 6) { problem = "WSPR callsigns are 3 to 6 characters"; return null; }
            if (grid.Length < 4 || !Regex.IsMatch(grid.Substring(0, 4), "^[A-R]{2}[0-9]{2}$")) { problem = "WSPR needs your 4-character grid square"; return null; }
            if (!WsprPowers.Contains(dbm)) { problem = "WSPR power must be one of " + string.Join(", ", WsprPowers) + " dBm"; return null; }

            // the callsign in six places, its digit third ("K1ABC" -> " K1ABC")
            char[] c6 = "      ".ToCharArray();
            if (char.IsDigit(call[2])) for (int i = 0; i < call.Length; i++) c6[i] = call[i];
            else if (char.IsDigit(call[1]) && call.Length <= 5) for (int i = 0; i < call.Length; i++) c6[i + 1] = call[i];
            else { problem = "WSPR can't send that callsign (its digit must be the 2nd or 3rd character)"; return null; }
            Func<char, int> cc = ch => ch >= '0' && ch <= '9' ? ch - '0' : ch == ' ' ? 36 : ch - 'A' + 10;
            if (!char.IsDigit(c6[2]) || c6.Skip(3).Any(ch => ch != ' ' && !char.IsLetter(ch))) { problem = "WSPR can't send that callsign (letters after its digit only)"; return null; }
            long n = cc(c6[0]);
            n = n * 36 + cc(c6[1]);
            n = n * 10 + cc(c6[2]);
            n = n * 27 + cc(c6[3]) - 10;
            n = n * 27 + cc(c6[4]) - 10;
            n = n * 27 + cc(c6[5]) - 10;

            int g0 = grid[0] - 'A', g1 = grid[1] - 'A', g2 = grid[2] - '0', g3 = grid[3] - '0';
            long m = (179 - 10 * g0 - g2) * 180 + 10 * g1 + g3;
            m = m * 128 + dbm + 64;

            byte[] data = new byte[11];
            data[0] = (byte)(n >> 20);
            data[1] = (byte)(n >> 12);
            data[2] = (byte)(n >> 4);
            data[3] = (byte)(((n & 0x0F) << 4) | ((m >> 18) & 0x0F));
            data[4] = (byte)(m >> 10);
            data[5] = (byte)(m >> 2);
            data[6] = (byte)((m & 0x03) << 6);

            // the convolutional code: two parity bits for every bit in (81 of them: 50 and the 31-bit tail)
            byte[] bits = new byte[176];
            uint state = 0;
            int k = 0;
            for (int i = 0; i < 88; i++)
            {
                int bit = (data[i / 8] >> (7 - i % 8)) & 1;
                state = (state << 1) | (uint)bit;
                bits[k++] = (byte)parity(state & 0xF2D05351u);
                bits[k++] = (byte)parity(state & 0xE4613C47u);
            }
            // interleave: bit p goes to the bit-reversal of the p-th index (of 0 - 255) that's under 162
            byte[] il = new byte[162];
            int p = 0;
            for (int i = 0; p < 162; i++)
            {
                int j = reverse8(i);
                if (j < 162) il[j] = bits[p++];
            }
            byte[] tones = new byte[162];
            for (int i = 0; i < 162; i++) tones[i] = (byte)(2 * il[i] + WsprSync[i]);
            return tones;
        }

        private static int parity(uint x)
        {
            x ^= x >> 16; x ^= x >> 8; x ^= x >> 4; x ^= x >> 2; x ^= x >> 1;
            return (int)(x & 1);
        }

        private static int reverse8(int b)
        {
            int r = 0;
            for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) r |= 1 << (7 - i);
            return r;
        }

        // ---- the audio: continuous-phase FSK, tone n at f0 + n x spacing, with short ramps at each end ----

        public static float[] SynthesizeFsk(FtxMode mode, byte[] tones, double f0, int rate)
        {
            double sym = FtxModes.SymbolPeriod(mode), spacing = FtxModes.ToneSpacing(mode);
            int total = (int)Math.Round(tones.Length * sym * rate);
            float[] s = new float[total];
            double phi = 0;
            for (int i = 0; i < total; i++)
            {
                int k = Math.Min(tones.Length - 1, (int)(i / (sym * rate)));
                s[i] = (float)Math.Sin(phi);
                phi += 2 * Math.PI * (f0 + tones[k] * spacing) / rate;
                if (phi > 2 * Math.PI) phi -= 2 * Math.PI;
            }
            int ramp = rate / 100;
            for (int i = 0; i < ramp && i < total / 2; i++)
            {
                float env = (float)((1 - Math.Cos(Math.PI * i / ramp)) / 2);
                s[i] *= env;
                s[total - 1 - i] *= env;
            }
            return s;
        }
    }
}
