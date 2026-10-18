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
        public string Name = "", Comments = "", TxPower = "", Operator = "";
        public DateTime On, Off;
        public double DialHz;
        public int AudioHz;
        public Contest Contest;
        public string ExchSent = "", ExchRcvd = "";     // contest exchanges ("1D WWA", "579 WA", a grid)
    }

    // WSJT-X's special operating activities (the numbers are WSJT-X's, sent in its UDP status)
    internal enum Contest { None = 0, NaVhf = 1, FieldDay = 3, RttyRoundup = 4, WwDigi = 5, Hound = 7 }

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

        // ---- contests: the exchange in place of the report, as WSJT-X does them ----
        //   NA VHF / WW Digi: the grid.   Field Day: class and section ("1D WWA").   RTTY Roundup: RST and state /
        //   province, or a serial number outside the US and Canada.
        //   I answer a CQ:  HIS MY <exch>    he: MY HIS R <exch>    I: HIS MY RR73  (logged)
        //   I call CQ:      he: MY HIS <exch>    I: HIS MY R <exch>    he: MY HIS RR73  (logged, back to CQ)

        public Contest Activity = Contest.None;
        public string MyExchange = "";      // Field Day "1D WWA"; Roundup "WA" (or "#" / empty: serial numbers)
        public int Serial = 1;              // Roundup serial, for stations outside the US and Canada
        public string ExchRcvd = "";
        private bool _fromMyCq;             // the QSO began as an answer to my CQ (a contest run goes back to CQ)

        private static readonly Regex FdClass = new Regex(@"^\d{1,2}[A-F]$");
        private static readonly Regex Section = new Regex("^[A-Z]{2,4}$");
        private static readonly Regex Rst = new Regex("^5[1-9]9$");
        private static readonly Regex Serialrx = new Regex(@"^\d{1,4}$");

        public static string CqTag(Contest c)
        {
            switch (c) { case Contest.NaVhf: return "TEST"; case Contest.FieldDay: return "FD"; case Contest.RttyRoundup: return "RU"; case Contest.WwDigi: return "WW"; default: return null; }
        }

        public static string Name(Contest c)
        {
            switch (c) { case Contest.NaVhf: return "NA VHF Contest"; case Contest.FieldDay: return "ARRL Field Day"; case Contest.RttyRoundup: return "ARRL RTTY Roundup"; case Contest.WwDigi: return "WW Digi"; case Contest.Hound: return "Hound (DXpedition)"; default: return "None"; }
        }

        // ---- Hound: working a DXpedition in WSJT-X's Fox / Hound mode (FT8 DXpedition Mode User Guide, K1JT) ----
        //   I call:    FOX MY GRID          (above 1000 Hz, in the second periods)
        //   Fox:       MY FOX -13           or   OTHER RR73; MY <FOX> -13
        //   I:         FOX MY R-11          (on the frequency where Fox called me; 300 Hz away on each repeat)
        //   Fox:       MY FOX RR73          or   MY RR73; OTHER <FOX> -09      (complete: logged)
        // FOX is the DXpedition's base call (KH7Z for KH1/KH7Z).

        public bool Hound { get { return Activity == Contest.Hound; } }
        public int FoxCalledAt;             // the audio frequency Fox called me on (Hz), 0 until he has
        public string FoxBase { get { return BaseCall(HisCall); } }

        // the callsign without a prefix or suffix: KH1/KH7Z -> KH7Z, W1AW/P -> W1AW
        public static string BaseCall(string call)
        {
            string[] p = (call ?? "").Trim('<', '>').Split('/');
            string best = "";
            foreach (string s in p) if (s.Length > best.Length && s.Any(char.IsDigit) && s.Any(char.IsLetter)) best = s;
            return best.Length > 0 ? best : (call ?? "");
        }

        private bool isFox(string c)
        {
            c = (c ?? "").Trim('<', '>');
            return c.Length > 0 && (c == HisCall || c == FoxBase || BaseCall(c) == FoxBase);
        }

        // Fox's messages to me: a report (with the frequency he called me on), or RR73. Returns true if it moved the QSO.
        private bool houndHeard(DecodeLine d)
        {
            string msg = d.Message;
            // the multi-stream form: "A RR73; B <FOX> -12"
            int semi = msg.IndexOf(';');
            if (semi > 0)
            {
                string[] a = msg.Substring(0, semi).Trim().Split(' '), b = msg.Substring(semi + 1).Trim().Split(' ');
                if (a.Length >= 2 && a[0] == MyCall && a[1] == "RR73") { log(); Done = true; return true; }
                // the Fox's call here is a hash, which jt9 often shows as <...>: only a Fox sends this form, so it's him
                if (b.Length >= 3 && b[0] == MyCall && (isFox(b[1]) || b[1] == "<...>") && ReportRx.IsMatch(b[2]))
                {
                    ReportRcvd = b[2]; FoxCalledAt = d.Freq; HisSnr = d.Snr; Next = 3; return true;
                }
                return false;
            }
            string[] w = msg.Split(' ');
            if (w.Length < 3 || w[0] != MyCall || !isFox(w[1])) return false;
            if (ReportRx.IsMatch(w[2])) { ReportRcvd = w[2]; FoxCalledAt = d.Freq; HisSnr = d.Snr; Next = 3; return true; }
            if (RReportRx.IsMatch(w[2])) { ReportRcvd = w[2].Substring(1); FoxCalledAt = d.Freq; HisSnr = d.Snr; Next = 3; return true; }
            if (w[2] == "RR73" || w[2] == "RRR" || w[2] == "73") { log(); Done = true; return true; }
            return false;
        }

        // RST for the Roundup: 5x9, x from the SNR (-24 dB and below 2 ... -3 and above 9)
        public static string RstOf(int snr) { return "5" + Math.Max(2, Math.Min(9, 9 + (int)Math.Floor((snr + 3) / 3.0))) + "9"; }

        public string MyExch
        {
            get
            {
                switch (Activity)
                {
                    case Contest.NaVhf: case Contest.WwDigi: return Grid4;
                    case Contest.FieldDay: return (MyExchange ?? "").Trim().ToUpperInvariant();
                    case Contest.RttyRoundup:
                        string x = (MyExchange ?? "").Trim().ToUpperInvariant();
                        return RstOf(HisSnr) + " " + (x.Length == 0 || x == "#" ? Serial.ToString("0000", CultureInfo.InvariantCulture) : x);
                    default: return "";
                }
            }
        }

        // the exchange in the words after the callsigns (without a leading R), or null if it isn't one for this contest
        public string ExchangeIn(string[] words)
        {
            string a = words.Length > 0 ? words[0] : "", b = words.Length > 1 ? words[1] : "";
            switch (Activity)
            {
                case Contest.NaVhf: case Contest.WwDigi: return isGrid(a) && words.Length == 1 ? a : null;
                case Contest.FieldDay: return words.Length == 2 && FdClass.IsMatch(a) && Section.IsMatch(b) ? a + " " + b : null;
                case Contest.RttyRoundup: return words.Length == 2 && (Rst.IsMatch(a) || a == "5NN") && (Section.IsMatch(b) || Serialrx.IsMatch(b)) ? a + " " + b : null;
                default: return null;
            }
        }

        public string Message(int n)
        {
            string h = HisCall, m = MyCall, rpt = Report(HisSnr);
            if (Hound)
                switch (n)
                {
                    case 1: case 2: return FoxBase + " " + m + " " + Grid4;
                    case 3: case 4: return FoxBase + " " + m + " R" + rpt;
                    case 5: return FoxBase + " " + m + " 73";
                    case 6: return "CQ " + m + " " + Grid4;
                    default: return FreeText;
                }
            if (Activity != Contest.None)
                switch (n)
                {
                    case 1: return h + " " + m + " " + MyExch;
                    case 2: return h + " " + m + " R " + MyExch;
                    case 3: case 4: return h + " " + m + " RR73";
                    case 5: return h + " " + m + " 73";
                    case 6: return "CQ " + CqTag(Activity) + " " + m + " " + Grid4;
                    default: return FreeText;
                }
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
            int semi = d.Message.IndexOf(';');
            if (semi > 0)
            {
                // Fox's multi-stream message ("A RR73; B <FOX> -12"): the Fox is the hashed call in the second part
                string[] b = d.Message.Substring(semi + 1).Trim().Split(' ');
                if (b.Length < 2) return false;
                from = b[1] == "<...>" && HisCall.Length > 0 ? HisCall : b[1];      // the hash unresolved: the Fox already chosen
            }
            else if (w[0] == "CQ")
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

            // a Hound: Fox's message to me picks up where the QSO is; anything else from Fox starts my call (Tx1)
            if (Hound)
            {
                if (!sameStation) FoxCalledAt = 0;
                if (!houndHeard(d)) Next = 1;
                return true;
            }

            // a message to me picks up where it is in the sequence; anything else starts with my grid
            _fromMyCq = false;
            if (w[0] == MyCall && w.Length >= 3) Next = respond(w.Skip(2).ToArray(), d, true);
            else Next = 1;
            return true;
        }

        // after each period: the decodes, and what I sent (null if nothing). Returns true if Next changed.
        public bool Sequence(IEnumerable<DecodeLine> decodes, string iSent)
        {
            if (!AutoSeq || string.IsNullOrEmpty(MyCall)) return false;
            int before = Next;
            if (Hound)
            {
                bool moved = false;
                if (HisCall.Length > 0 && !Done) foreach (DecodeLine d in decodes) if (houndHeard(d)) { moved = true; break; }
                return moved || Next != before;
            }
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
                    ReportRcvd = ExchRcvd = "";
                    _fromMyCq = true;
                    Done = false;
                    _logged = false;
                    QsoStart = DateTime.UtcNow;
                }
                if (from != HisCall) continue;
                HisSnr = d.Snr;
                Next = respond(w.Skip(2).ToArray(), d, false);
                break;
            }
            // after 73 the QSO is over: back to CQ only if the operator asks
            if (iSent != null && Next == 5 && iSent == Message(5)) Done = true;
            return Next != before;
        }

        // his words after the callsigns -> my next message
        private int respond(string[] rest, DecodeLine d, bool answering)
        {
            string x = rest.Length > 0 ? rest[0] : "";
            if (Activity != Contest.None)
            {
                bool roger = x == "R" && rest.Length > 1;
                string ex = ExchangeIn(roger ? rest.Skip(1).ToArray() : rest);
                if (ex != null)
                {
                    ExchRcvd = ex;
                    if (Activity == Contest.NaVhf || Activity == Contest.WwDigi) HisGrid = ex;
                    if (roger) { log(); return 3; }                 // he has mine and sent his: RR73, and it's in the log
                    return 2;                                       // he answered my CQ with his exchange: R and mine
                }
                if (x == "RR73" || x == "RRR" || x == "73") { log(); return endContestQso(); }
                return answering ? 1 : Next;
            }
            if (isGrid(x)) { HisGrid = x; return 2; }                 // he answered my CQ with his grid
            if (ReportRx.IsMatch(x)) { ReportRcvd = x; return 3; }             // his report to me
            if (RReportRx.IsMatch(x)) { ReportRcvd = x.Substring(1); log(); return 4; }    // roger + report: RR73
            if (x == "RR73" || x == "RRR") { log(); return 5; }                // he has mine: 73
            if (x == "73") { log(); Done = true; return 6; }
            return answering ? 1 : Next;
        }

        // a contest QSO is complete: a run (it began from my CQ) goes straight back to CQ and keeps transmitting; an
        // answered CQ is over
        private int endContestQso()
        {
            if (_fromMyCq) { HisCall = HisGrid = ExchRcvd = ""; _fromMyCq = false; Done = false; _logged = false; return 6; }
            Done = true;
            return Next;
        }

        // sending RR73 completes the QSO too
        public void Sent(string message)
        {
            if (Hound) return;                      // a Hound's QSO is complete at Fox's RR73
            if (Activity != Contest.None)
            {
                if (HisCall.Length > 0 && message == Message(3)) { log(); Done = true; }
                return;
            }
            if (HisCall.Length > 0 && (message == Message(4) || message == Message(5))) log();
            if (message == Message(5)) Done = true;
        }

        private void log()
        {
            if (_logged || HisCall.Length == 0) return;
            _logged = true;
            QsoRecord q = new QsoRecord
            {
                Call = HisCall, Grid = HisGrid, RstSent = Report(HisSnr), RstRcvd = ReportRcvd,
                On = QsoStart == default(DateTime) ? DateTime.UtcNow : QsoStart, Off = DateTime.UtcNow,
                Contest = Activity,
            };
            if (Activity != Contest.None && !Hound)
            {
                q.ExchSent = MyExch;
                q.ExchRcvd = ExchRcvd;
                if (Activity == Contest.RttyRoundup)
                {
                    q.RstSent = q.ExchSent.Split(' ')[0];
                    q.RstRcvd = ExchRcvd.Split(' ')[0];
                    string x = (MyExchange ?? "").Trim();
                    if (x.Length == 0 || x == "#") Serial++;         // the next station gets the next number
                }
            }
            Logged?.Invoke(q);
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
