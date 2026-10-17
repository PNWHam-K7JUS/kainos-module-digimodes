/*  QsoEngine.cs

The standard FT8 / FT4 QSO and its messages, with automatic sequencing as WSJT-X does it:

  Tx1  HIS MY GRID      answering his CQ
  Tx2  HIS MY -12       his report (answering a call to my CQ)
  Tx3  HIS MY R-12      roger and his report
  Tx4  HIS MY RR73      roger, 73 (the QSO is complete and logged)
  Tx5  HIS MY 73
  Tx6  CQ MY GRID

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Kainos.DigiModes.Decode;

namespace Kainos.DigiModes.Qso
{
    internal class QsoRecord
    {
        public string Call, Grid, RstSent, RstRcvd, Mode;
        public DateTime On, Off;
        public double DialHz;
        public int AudioHz;
    }

    internal class QsoEngine
    {
        private static readonly Regex GridRx = new Regex("^[A-R]{2}[0-9]{2}$");
        // RR73 is also a grid square (in the Arctic Ocean): WSJT-X, and so this, take it as RR73
        private static bool isGrid(string s) { return s != "RR73" && GridRx.IsMatch(s); }
        private static readonly Regex ReportRx = new Regex(@"^[+-]\d{2}$");
        private static readonly Regex RReportRx = new Regex(@"^R[+-]\d{2}$");

        public string MyCall = "", MyGrid = "";
        public string HisCall = "", HisGrid = "";
        public int HisSnr;                  // how I hear him (my report to him)
        public string ReportRcvd = "";      // what he sent me
        public int Next = 6;                // the Tx message to send next (1-6, 7 = free text)
        public string FreeText = "";
        public bool AutoSeq = true;
        public bool Done;                   // the QSO is over (73 sent, or RR73 / 73 received after mine)
        public DateTime QsoStart;
        private bool _logged;

        public event Action<QsoRecord> Logged;

        public string Grid4 { get { return MyGrid.Length >= 4 ? MyGrid.Substring(0, 4) : MyGrid; } }

        public static string Report(int snr)
        {
            snr = Math.Max(-30, Math.Min(49, snr));
            return snr.ToString("+00;-00", CultureInfo.InvariantCulture);
        }

        public string Message(int n)
        {
            string h = HisCall, m = MyCall, rpt = Report(HisSnr);
            switch (n)
            {
                case 1: return h + " " + m + " " + Grid4;
                case 2: return h + " " + m + " " + rpt;
                case 3: return h + " " + m + " R" + rpt;
                case 4: return h + " " + m + " RR73";
                case 5: return h + " " + m + " 73";
                case 6: return "CQ " + m + " " + Grid4;
                default: return FreeText;
            }
        }

        public string NextMessage { get { return Message(Next).Trim(); } }

        public void Clear()
        {
            HisCall = HisGrid = ReportRcvd = "";
            HisSnr = 0;
            Done = false;
            _logged = false;
            Next = 6;
        }

        // the operator double-clicked a decode: answer whoever sent it
        // returns false when there's nobody to answer in it
        public bool Answer(DecodeLine d)
        {
            string[] w = d.Message.Split(' ');
            if (w.Length < 2) return false;
            string from;
            string grid = "";
            if (w[0] == "CQ")
            {
                // CQ [DX | NA | POTA | ...] CALL [GRID]
                int ci = w.Length >= 3 && !looksLikeCall(w[1]) ? 2 : 1;
                if (ci >= w.Length) return false;
                from = w[ci];
                if (ci + 1 < w.Length && isGrid(w[ci + 1])) grid = w[ci + 1];
            }
            else if (w.Length >= 2 && w[0] == MyCall)
            {
                from = w[1];
            }
            else
            {
                // someone else's QSO: call the sender (second word)
                from = w[1];
                if (w.Length >= 3 && isGrid(w[2])) grid = w[2];
            }
            from = from.Trim('<', '>');
            if (!looksLikeCall(from) || from == MyCall) return false;

            bool sameStation = from == HisCall && !Done;
            HisCall = from;
            if (grid.Length > 0) HisGrid = grid;
            HisSnr = d.Snr;
            if (!sameStation) { ReportRcvd = ""; Done = false; _logged = false; QsoStart = DateTime.UtcNow; }

            // a message to me picks up where it is in the sequence; anything else starts with my grid
            if (w[0] == MyCall && w.Length >= 3) Next = respond(w[2], d, true);
            else Next = 1;
            return true;
        }

        // after each period: the decodes, and what I sent (null if nothing). Returns true if Next changed.
        public bool Sequence(IEnumerable<DecodeLine> decodes, string iSent)
        {
            if (!AutoSeq || string.IsNullOrEmpty(MyCall)) return false;
            int before = Next;
            foreach (DecodeLine d in decodes)
            {
                string[] w = d.Message.Split(' ');
                if (w.Length < 3 || w[0] != MyCall) continue;
                string from = w[1].Trim('<', '>');
                // calling CQ: the first station to answer becomes the QSO
                if (Next == 6 && (HisCall.Length == 0 || Done))
                {
                    if (!looksLikeCall(from)) continue;
                    HisCall = from;
                    HisGrid = isGrid(w[2]) ? w[2] : "";
                    ReportRcvd = "";
                    Done = false;
                    _logged = false;
                    QsoStart = DateTime.UtcNow;
                }
                if (from != HisCall) continue;
                HisSnr = d.Snr;
                Next = respond(w[2], d, false);
                break;
            }
            // after 73 the QSO is over: back to CQ only if the operator asks
            if (iSent != null && Next == 5 && iSent == Message(5)) Done = true;
            return Next != before;
        }

        // his last word -> my next message
        private int respond(string x, DecodeLine d, bool answering)
        {
            if (isGrid(x)) { HisGrid = x; return 2; }                 // he answered my CQ with his grid
            if (ReportRx.IsMatch(x)) { ReportRcvd = x; return 3; }             // his report to me
            if (RReportRx.IsMatch(x)) { ReportRcvd = x.Substring(1); log(); return 4; }    // roger + report: RR73
            if (x == "RR73" || x == "RRR") { log(); return 5; }                // he has mine: 73
            if (x == "73") { log(); Done = true; return 6; }
            return answering ? 1 : Next;
        }

        // sending RR73 completes the QSO too
        public void Sent(string message)
        {
            if (HisCall.Length > 0 && (message == Message(4) || message == Message(5))) log();
            if (message == Message(5)) Done = true;
        }

        private void log()
        {
            if (_logged || HisCall.Length == 0) return;
            _logged = true;
            Logged?.Invoke(new QsoRecord
            {
                Call = HisCall, Grid = HisGrid, RstSent = Report(HisSnr), RstRcvd = ReportRcvd,
                On = QsoStart == default(DateTime) ? DateTime.UtcNow : QsoStart, Off = DateTime.UtcNow,
            });
        }

        private static bool looksLikeCall(string s)
        {
            s = (s ?? "").Trim('<', '>');
            if (s.Length < 3 || s.Length > 11) return false;
            if (s == "CQ" || s == "DE" || s == "QRZ" || s == "DX" || isGrid(s)) return false;
            return s.Any(char.IsDigit) && s.Any(char.IsLetter) && s.All(c => char.IsLetterOrDigit(c) || c == '/');
        }
    }
}
