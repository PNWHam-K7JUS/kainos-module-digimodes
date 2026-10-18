/*  WsprDecoder.cs

WSPR decoding with WSJT-X's wsprd, run once per two-minute period on a 12 kHz WAV of it (114 s):
  wsprd -f <dial MHz> -a <folder> yymmdd_hhmm.wav
It prints a line per spot: time, SNR, DT, frequency (MHz), drift, then the message (call grid dBm).

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kainos.DigiModes.Decode
{
    internal class WsprSpot
    {
        public DateTime SlotUtc;
        public int Snr, Drift, Dbm;
        public double Dt, FreqMHz;
        public string Call = "", Grid = "";
        public int AudioHz;             // the offset from the dial (Hz)

        public string Message { get { return (Call + " " + Grid + " " + Dbm).Replace("  ", " "); } }

        public override string ToString()
        {
            return SlotUtc.ToString("HHmm") + " " + Snr.ToString("+0;-0", CultureInfo.InvariantCulture).PadLeft(3) + " " +
                   Dt.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(4) + " " + FreqMHz.ToString("0.000000", CultureInfo.InvariantCulture) + " " +
                   Drift.ToString().PadLeft(2) + "  " + Message;
        }
    }

    internal class WsprDecoder
    {
        // " 1210 -21  0.2  14.097063  0  K7JUS CN87 37"   (time hhmm, SNR, DT, MHz, drift, message)
        private static readonly Regex Line = new Regex(@"^\s*(\d{4})\s+(-?\d+)\s+(-?\d+\.\d+)\s+(\d+\.\d+)\s+(-?\d+)\s+(.+?)\s*$", RegexOptions.Compiled);

        public string Exe;
        public string WorkFolder;
        public int TimeoutMs = 60000;
        public string LastOutput = "";

        public bool Available { get { return !string.IsNullOrEmpty(Exe) && File.Exists(Exe); } }

        public List<WsprSpot> Decode(DateTime slotUtc, float[] samples, double dialMHz, out string problem)
        {
            problem = null;
            List<WsprSpot> res = new List<WsprSpot>();
            if (!Available) { problem = "wsprd.exe not found"; return res; }
            Directory.CreateDirectory(WorkFolder);
            string wav = Path.Combine(WorkFolder, slotUtc.ToString("yyMMdd_HHmm") + ".wav");
            try
            {
                Wav.Write16(wav, samples, 12000);
                ProcessStartInfo psi = new ProcessStartInfo(Exe, "-f " + dialMHz.ToString("0.000000", CultureInfo.InvariantCulture) +
                                                                  " -a \"" + WorkFolder.TrimEnd('\\') + "\" \"" + wav + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = WorkFolder,
                };
                using (Process p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginErrorReadLine();
                    LastOutput = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(TimeoutMs)) { try { p.Kill(); } catch { } problem = "wsprd took too long"; }
                }
                res = Parse(LastOutput, slotUtc, dialMHz);
            }
            catch (Exception ex) { problem = "wsprd failed: " + ex.Message; }
            finally { try { File.Delete(wav); } catch { } }
            return res;
        }

        internal static List<WsprSpot> Parse(string output, DateTime slotUtc, double dialMHz)
        {
            List<WsprSpot> res = new List<WsprSpot>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                Match m = Line.Match(raw);
                if (!m.Success) continue;
                string[] w = m.Groups[6].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int dbm;
                if (w.Length < 2 || !int.TryParse(w[w.Length - 1], out dbm)) continue;
                WsprSpot s = new WsprSpot
                {
                    SlotUtc = slotUtc,
                    Snr = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    Dt = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    FreqMHz = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                    Drift = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    Call = w[0].Trim('<', '>'), Grid = w.Length >= 3 ? w[1] : "", Dbm = dbm,
                };
                s.AudioHz = (int)Math.Round((s.FreqMHz - dialMHz) * 1e6);
                if (seen.Add(s.Call + "|" + s.Grid)) res.Add(s);
            }
            return res;
        }
    }
}
