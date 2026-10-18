/*  WsprNet.cs

Uploads WSPR spots to WSPRnet (https://wsprnet.org), as WSJT-X does: after each two-minute period, a "wspr"
report for each station heard (its call, grid, power, frequency, SNR, DT, drift), or a "wsprstat" report when none
were, so the map shows you listening. Off unless turned on in Settings.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Kainos.DigiModes.Decode;

namespace Kainos.DigiModes.Net
{
    internal class WsprNet
    {
        public const string Url = "https://wsprnet.org/post/";
        public string MyCall = "", MyGrid = "", Version = "Kainos";
        public int TxPercent, Dbm;
        public event Action<string> Status;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // the query for one spot (or, with spot null, the "nothing heard" status); dial in Hz
        internal string Query(WsprSpot spot, DateTime slotUtc, double dialHz)
        {
            Func<double, string> mhz = hz => (hz / 1e6).ToString("0.000000", CultureInfo.InvariantCulture);
            Func<string, string> e = Uri.EscapeDataString;
            double rqrg = dialHz + 1500;            // the receiver: the middle of the WSPR window
            string common = "&rcall=" + e(MyCall) + "&rgrid=" + e(MyGrid) + "&rqrg=" + mhz(rqrg);
            if (spot == null)
                return "function=wsprstat" + common + "&tpct=" + TxPercent + "&tqrg=" + mhz(rqrg) + "&dbm=" + Dbm +
                       "&version=" + e(Version) + "&mode=2";
            return "function=wspr" + common +
                   "&date=" + slotUtc.ToString("yyMMdd", CultureInfo.InvariantCulture) + "&time=" + slotUtc.ToString("HHmm", CultureInfo.InvariantCulture) +
                   "&sig=" + spot.Snr + "&dt=" + spot.Dt.ToString("0.0", CultureInfo.InvariantCulture) + "&drift=" + spot.Drift +
                   "&tqrg=" + spot.FreqMHz.ToString("0.000000", CultureInfo.InvariantCulture) + "&tcall=" + e(spot.Call) + "&tgrid=" + e(spot.Grid) +
                   "&dbm=" + spot.Dbm + "&version=" + e(Version) + "&mode=2";
        }

        // after a period: its spots (or none)
        public void Upload(DateTime slotUtc, List<WsprSpot> spots, double dialHz)
        {
            if (MyCall.Length < 3 || MyGrid.Length < 4 || dialHz <= 0) return;
            List<string> queries = spots.Count == 0 ? new List<string> { Query(null, slotUtc, dialHz) } : spots.Select(s => Query(s, slotUtc, dialHz)).ToList();
            Task.Run(async () =>
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                int ok = 0;
                foreach (string q in queries)
                {
                    try
                    {
                        using (HttpResponseMessage r = await Http.GetAsync(Url + "?" + q))
                            if (r.IsSuccessStatusCode) ok++;
                            else Status?.Invoke("WSPRnet: " + (int)r.StatusCode + " " + r.ReasonPhrase);
                    }
                    catch (Exception ex) { Status?.Invoke("WSPRnet: " + ex.Message); return; }
                }
                if (spots.Count > 0) Status?.Invoke("Uploaded " + ok + " of " + spots.Count + " WSPR spot" + (spots.Count == 1 ? "" : "s") + " to WSPRnet");
            });
        }
    }
}
