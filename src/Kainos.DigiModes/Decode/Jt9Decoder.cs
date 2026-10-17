/*  Jt9Decoder.cs

Decoding with jt9, the decoder of WSJT-X (and its forks), run once per period on a 12 kHz WAV of that period, as
WSJT-X's own command-line mode does:  jt9 -8 (FT8) or -5 (FT4) -d depth ... yymmdd_hhmmss.wav
It prints a line per decode:  "hhmmss  snr  dt  freq ~  message".

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Kainos.DigiModes.Ftx;

namespace Kainos.DigiModes.Decode
{
    internal class DecodeLine
    {
        public DateTime SlotUtc;        // the start of the period it was heard in
        public int Snr;
        public double Dt;
        public int Freq;                // audio Hz
        public string Message;
        public bool Ap;                 // an a-priori ("a1" ... ) decode: less certain
        public FtxMode Mode;

        public override string ToString()
        {
            return SlotUtc.ToString("HHmmss") + " " + Snr.ToString("+0;-0", CultureInfo.InvariantCulture).PadLeft(3) + " " +
                   Dt.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(4) + " " + Freq.ToString().PadLeft(4) + "  " + Message;
        }
    }

    internal class Jt9Decoder
    {
        private static readonly Regex Line = new Regex(@"^\s*(\d{4,6})\s+(-?\d+)\s+(-?\d+\.\d+)\s+(\d+)\s+\S\s+(.*?)\s*$", RegexOptions.Compiled);
        private static readonly Regex ApTag = new Regex(@"\s+(a\d+|\?)$", RegexOptions.Compiled);

        public string Jt9Exe;
        public string WorkFolder;       // jt9's data and temp files (wisdom, decoded.txt), and the WAVs
        public int Depth = 3;
        public int LowHz = 200, HighHz = 3000;
        public int TimeoutMs = 12000;

        public bool Available { get { return !string.IsNullOrEmpty(Jt9Exe) && File.Exists(Jt9Exe); } }

        // samples: the period at 12 kHz; returns the decodes (none on failure, with why in problem)
        public List<DecodeLine> Decode(FtxMode mode, DateTime slotUtc, float[] samples, int rxFreq, string myCall, string myGrid, string hisCall, out string problem)
        {
            problem = null;
            List<DecodeLine> res = new List<DecodeLine>();
            if (!Available) { problem = "jt9.exe not found"; return res; }
            Directory.CreateDirectory(WorkFolder);
            string wav = Path.Combine(WorkFolder, slotUtc.ToString("yyMMdd_HHmmss") + ".wav");
            try
            {
                Wav.Write16(wav, samples, 12000);
                List<string> a = new List<string>
                {
                    mode == FtxMode.FT8 ? "-8" : "-5",
                    "-d", Depth.ToString(CultureInfo.InvariantCulture),
                    "-f", rxFreq.ToString(CultureInfo.InvariantCulture),
                    "-L", LowHz.ToString(CultureInfo.InvariantCulture),
                    "-H", HighHz.ToString(CultureInfo.InvariantCulture),
                    "-a", q(WorkFolder), "-t", q(WorkFolder), "-e", q(Path.GetDirectoryName(Jt9Exe)),
                };
                if (okCall(myCall)) { a.Add("-c"); a.Add(myCall); }
                if (okGrid(myGrid)) { a.Add("-G"); a.Add(myGrid.Substring(0, 4)); }
                if (okCall(hisCall)) { a.Add("-x"); a.Add(hisCall); }
                a.Add(q(wav));

                ProcessStartInfo psi = new ProcessStartInfo(Jt9Exe, string.Join(" ", a))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = WorkFolder,
                };
                string output;
                using (Process p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginErrorReadLine();
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(TimeoutMs)) { try { p.Kill(); } catch { } problem = "jt9 took too long"; }
                }
                res = Parse(output, slotUtc, mode);
            }
            catch (Exception ex) { problem = "jt9 failed: " + ex.Message; }
            finally { try { File.Delete(wav); } catch { } }
            return res;
        }

        internal static List<DecodeLine> Parse(string output, DateTime slotUtc, FtxMode mode)
        {
            List<DecodeLine> res = new List<DecodeLine>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                Match m = Line.Match(raw);
                if (!m.Success) continue;
                string msg = m.Groups[5].Value.Trim();
                bool ap = false;
                Match t = ApTag.Match(msg);
                if (t.Success) { ap = true; msg = msg.Substring(0, t.Index).Trim(); }
                msg = Pack77.Normalise(msg);
                if (msg.Length == 0 || !seen.Add(msg)) continue;
                res.Add(new DecodeLine
                {
                    SlotUtc = slotUtc, Mode = mode, Ap = ap, Message = msg,
                    Snr = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    Dt = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    Freq = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                });
            }
            return res;
        }

        private static string q(string s) { return "\"" + s.TrimEnd('\\') + "\""; }
        private static bool okCall(string c) { return !string.IsNullOrEmpty(c) && c.Length >= 3 && c.Length <= 11 && c.All(ch => char.IsLetterOrDigit(ch) || ch == '/'); }
        private static bool okGrid(string g) { return !string.IsNullOrEmpty(g) && g.Length >= 4 && Regex.IsMatch(g.Substring(0, 4), "^[A-R]{2}[0-9]{2}$"); }
    }

    internal static class Wav
    {
        // 16-bit mono; scaled so the audio's RMS is about 2000 (jt9 works on integers, the receiver's audio is around +-0.1)
        public static void Write16(string path, float[] s, int rate)
        {
            double sum = 0; int cnt = 0;
            for (int i = 0; i < s.Length; i++) if (s[i] != 0) { sum += s[i] * (double)s[i]; cnt++; }
            double rms = cnt > 0 ? Math.Sqrt(sum / cnt) : 0;
            double g = rms > 1e-9 ? 2000.0 / rms : 1.0;
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                int data = s.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + data); w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(data);
                for (int i = 0; i < s.Length; i++) w.Write((short)Math.Max(-32767, Math.Min(32767, Math.Round(s[i] * g))));
            }
        }

        public static float[] Read16(string path, out int rate)
        {
            byte[] b = File.ReadAllBytes(path);
            rate = BitConverter.ToInt32(b, 24);
            int pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                if (id == "data")
                {
                    int n = Math.Min(len, b.Length - pos - 8) / 2;
                    float[] s = new float[n];
                    for (int i = 0; i < n; i++) s[i] = BitConverter.ToInt16(b, pos + 8 + 2 * i) / 32768f;
                    return s;
                }
                pos += 8 + len;
            }
            return new float[0];
        }
    }
}
