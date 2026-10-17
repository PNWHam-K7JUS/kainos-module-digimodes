/*  AdifLog.cs

QSOs appended to an ADIF file (DataFolder\kainos-digimodes.adi), which any logging program can import.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Kainos.DigiModes.Ftx;

namespace Kainos.DigiModes.Qso
{
    internal static class AdifLog
    {
        public static void Append(string path, QsoRecord q, FtxMode mode, string myCall, string myGrid)
        {
            bool fresh = !File.Exists(path);
            StringBuilder sb = new StringBuilder();
            if (fresh) sb.Append("Kainos Digital Modes log\r\n<ADIF_VER:5>3.1.4 <PROGRAMID:22>Kainos Digital Modes <EOH>\r\n");
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
            f(sb, "STATION_CALLSIGN", myCall);
            f(sb, "MY_GRIDSQUARE", myGrid);
            sb.Append("<EOR>\r\n");
            File.AppendAllText(path, sb.ToString(), Encoding.ASCII);
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
    }
}
