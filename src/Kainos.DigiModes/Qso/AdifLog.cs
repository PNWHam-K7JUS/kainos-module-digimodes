/*  AdifLog.cs

QSOs appended to an ADIF file (DataFolder\kainos-digimodes.adi), which any logging program can import.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kainos.DigiModes.Ftx;

namespace Kainos.DigiModes.Qso
{
    internal static class AdifLog
    {
        // appends the QSO; returns its ADIF record (for the programs told over UDP)
        public static string Append(string path, QsoRecord q, FtxMode mode, string myCall, string myGrid)
        {
            string rec = Record(q, mode, myCall, myGrid);
            bool fresh = !File.Exists(path);
            File.AppendAllText(path, (fresh ? "Kainos Digital Modes log\r\n<ADIF_VER:5>3.1.4 <PROGRAMID:22>Kainos Digital Modes <EOH>\r\n" : "") + rec + "\r\n", Encoding.ASCII);
            return rec;
        }

        public static string Record(QsoRecord q, FtxMode mode, string myCall, string myGrid)
        {
            StringBuilder sb = new StringBuilder();
            f(sb, "CALL", q.Call);
            f(sb, "GRIDSQUARE", q.Grid);
            if (mode == FtxMode.FT8) f(sb, "MODE", "FT8");
            else { f(sb, "MODE", "MFSK"); f(sb, "SUBMODE", "FT4"); }
            f(sb, "RST_SENT", q.RstSent);
            f(sb, "RST_RCVD", q.RstRcvd);
            f(sb, "QSO_DATE", q.On.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            f(sb, "TIME_ON", q.On.ToString("HHmmss", CultureInfo.InvariantCulture));
            f(sb, "QSO_DATE_OFF", q.Off.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            f(sb, "TIME_OFF", q.Off.ToString("HHmmss", CultureInfo.InvariantCulture));
            if (q.DialHz > 0)
            {
                double mhz = (q.DialHz + q.AudioHz) / 1e6;
                f(sb, "FREQ", mhz.ToString("0.000000", CultureInfo.InvariantCulture));
                f(sb, "BAND", Bands.AdifBand(q.DialHz));
            }
            f(sb, "NAME", q.Name);
            f(sb, "TX_PWR", q.TxPower);
            f(sb, "COMMENT", q.Comments);
            f(sb, "OPERATOR", q.Operator);
            f(sb, "STATION_CALLSIGN", myCall);
            f(sb, "MY_GRIDSQUARE", myGrid);
            contest(sb, q);
            sb.Append("<EOR>");
            return sb.ToString();
        }

        // a contest QSO: the contest, both exchanges, and the received one in its own ADIF fields
        private static void contest(StringBuilder sb, QsoRecord q)
        {
            if (q.Contest == Contest.None) return;
            string id = q.Contest == Contest.FieldDay ? "ARRL-FD" : q.Contest == Contest.RttyRoundup ? "ARRL-RTTY" :
                        q.Contest == Contest.WwDigi ? "WW-DIGI" : "ARRL-VHF";
            f(sb, "CONTEST_ID", id);
            f(sb, "STX_STRING", q.ExchSent);
            f(sb, "SRX_STRING", q.ExchRcvd);
            string[] r = (q.ExchRcvd ?? "").Split(' ');
            if (q.Contest == Contest.FieldDay && r.Length == 2) { f(sb, "CLASS", r[0]); f(sb, "ARRL_SECT", r[1]); }
            if (q.Contest == Contest.RttyRoundup && r.Length == 2)
            {
                int serial;
                if (int.TryParse(r[1], out serial)) f(sb, "SRX", serial.ToString(CultureInfo.InvariantCulture));
                else f(sb, "STATE", r[1]);
            }
        }

        private static void f(StringBuilder sb, string name, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            sb.Append('<').Append(name).Append(':').Append(v.Length).Append('>').Append(v).Append(' ');
        }
    }

    internal static class Bands
    {
        public class Band { public string Name; public double Ft8, Ft4; public double Lo, Hi; }

        // the usual dial frequencies (Hz)
        public static readonly Band[] All =
        {
            new Band { Name = "160m", Ft8 = 1840000, Ft4 = 0,        Lo = 1.8e6,  Hi = 2.0e6 },
            new Band { Name = "80m",  Ft8 = 3573000, Ft4 = 3575000,  Lo = 3.5e6,  Hi = 4.0e6 },
            new Band { Name = "60m",  Ft8 = 5357000, Ft4 = 0,        Lo = 5.3e6,  Hi = 5.41e6 },
            new Band { Name = "40m",  Ft8 = 7074000, Ft4 = 7047500,  Lo = 7.0e6,  Hi = 7.3e6 },
            new Band { Name = "30m",  Ft8 = 10136000, Ft4 = 10140000, Lo = 10.1e6, Hi = 10.15e6 },
            new Band { Name = "20m",  Ft8 = 14074000, Ft4 = 14080000, Lo = 14.0e6, Hi = 14.35e6 },
            new Band { Name = "17m",  Ft8 = 18100000, Ft4 = 18104000, Lo = 18.068e6, Hi = 18.168e6 },
            new Band { Name = "15m",  Ft8 = 21074000, Ft4 = 21140000, Lo = 21.0e6, Hi = 21.45e6 },
            new Band { Name = "12m",  Ft8 = 24915000, Ft4 = 24919000, Lo = 24.89e6, Hi = 24.99e6 },
            new Band { Name = "10m",  Ft8 = 28074000, Ft4 = 28180000, Lo = 28.0e6, Hi = 29.7e6 },
            new Band { Name = "6m",   Ft8 = 50313000, Ft4 = 50318000, Lo = 50.0e6, Hi = 54.0e6 },
            new Band { Name = "2m",   Ft8 = 144174000, Ft4 = 144170000, Lo = 144e6, Hi = 148e6 },
        };

        public static Band Of(double hz)
        {
            foreach (Band b in All) if (hz >= b.Lo && hz <= b.Hi) return b;
            return null;
        }

        public static string AdifBand(double hz) { Band b = Of(hz); return b != null ? b.Name.ToUpperInvariant() : ""; }

        // ---- the Band menu: editable (Settings > Band frequencies). The band edges above stay fixed (they say which
        // band a frequency is in); the menu is a list of name, FT8 and FT4 dial frequencies, any of them the user's ----

        public class Entry { public string Name; public double Ft8, Ft4; }

        public static List<Entry> Menu = Defaults();

        public static List<Entry> Defaults() { return All.Select(b => new Entry { Name = b.Name, Ft8 = b.Ft8, Ft4 = b.Ft4 }).ToList(); }

        // one line each: name, FT8 MHz, FT4 MHz ("-" or nothing for none), e.g. "40m 7.074 7.0475" or "DXped 14.090 -"
        public static string Format(IEnumerable<Entry> list)
        {
            Func<double, string> mhz = hz => hz > 0 ? (hz / 1e6).ToString("0.0#####", CultureInfo.InvariantCulture) : "-";
            return string.Join("\r\n", list.Select(e => e.Name + " " + mhz(e.Ft8) + " " + mhz(e.Ft4)));
        }

        // null and the first line it couldn't read when something's wrong
        public static List<Entry> Parse(string text, out string problem)
        {
            problem = null;
            List<Entry> res = new List<Entry>();
            int n = 0;
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                n++;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] p = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                double f8, f4 = 0;
                if (p.Length < 2 || !mhzOk(p[1], out f8) || (p.Length >= 3 && !mhzOk(p[2], out f4)) || f8 + f4 <= 0)
                {
                    problem = "Line " + n + " (\"" + line + "\"): expected a name, then the FT8 and FT4 frequencies in MHz (\"-\" for none)";
                    return null;
                }
                res.Add(new Entry { Name = p[0], Ft8 = f8, Ft4 = f4 });
            }
            if (res.Count == 0) { problem = "The list is empty"; return null; }
            return res;
        }

        private static bool mhzOk(string s, out double hz)
        {
            hz = 0;
            if (s == "-") return true;
            double mhz;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out mhz) || mhz < 0.1 || mhz > 1300) return false;
            hz = Math.Round(mhz * 1e6);
            return true;
        }

        // from the saved setting ("" = the defaults)
        public static void Load(string saved)
        {
            string why;
            List<Entry> l = string.IsNullOrWhiteSpace(saved) ? null : Parse(saved, out why);
            Menu = l ?? Defaults();
        }
    }
}
