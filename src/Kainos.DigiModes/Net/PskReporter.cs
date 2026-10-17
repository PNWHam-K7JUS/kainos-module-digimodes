/*  PskReporter.cs

Reports the stations heard to PSK Reporter (https://pskreporter.info), as WSJT-X does: IPFIX over UDP to
report.pskreporter.info port 4739. Spots are gathered and sent every 5 minutes (PSK Reporter asks for no more
often), each callsign once a batch.

A packet: the IPFIX header (version 10, length, export time, sequence, observation domain), the two templates
(receiver: callsign, locator, software, antenna; sender: callsign, frequency, SNR, mode, locator, information
source, time), then a receiver record and the sender records. Fields from PSK Reporter's enterprise (30351).

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Kainos.DigiModes.Decode;

namespace Kainos.DigiModes.Net
{
    internal class PskSpot
    {
        public string Call, Grid, Mode;
        public long Hz;
        public int Snr;
        public DateTime Time;
    }

    internal class PskReporter : IDisposable
    {
        public const string Host = "report.pskreporter.info";
        public const int Port = 4739;
        private const uint Enterprise = 30351;
        private const ushort ReceiverTemplate = 0x50e2, SenderTemplate = 0x50e3;
        private static readonly Regex CallRx = new Regex(@"^(?=.*\d)(?=.*[A-Z])[A-Z0-9/]{3,11}$");
        private static readonly Regex GridRx = new Regex("^[A-R]{2}[0-9]{2}$");

        private readonly Dictionary<string, PskSpot> _pending = new Dictionary<string, PskSpot>();
        private readonly uint _domain = (uint)new Random().Next();
        private uint _sequence;
        private Timer _timer;
        private UdpClient _udp;

        public string MyCall = "", MyGrid = "", Software = "Kainos", Antenna = "";
        public event Action<string> Status;

        public bool Running { get { return _timer != null; } }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(_ => Flush(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public void Stop()
        {
            if (_timer == null) return;
            _timer.Dispose();
            _timer = null;
            Flush();        // what's gathered so far
            if (_udp != null) { _udp.Close(); _udp = null; }
        }

        public void Dispose() { Stop(); }

        // the sender of a decode, and its grid when the message carries one; null when there's nothing to report
        internal static PskSpot SpotOf(DecodeLine d, long dialHz, string myCall)
        {
            string[] w = (d.Message ?? "").Split(' ');
            string call = null, grid = "";
            if (w.Length >= 2 && w[0] == "CQ")
            {
                int ci = w.Length >= 3 && !CallRx.IsMatch(w[1]) ? 2 : 1;      // CQ DX / CQ POTA ...
                if (ci < w.Length) call = w[ci];
                if (ci + 1 < w.Length && GridRx.IsMatch(w[ci + 1]) && w[ci + 1] != "RR73") grid = w[ci + 1];
            }
            else if (w.Length >= 2)
            {
                call = w[1];
                if (w.Length >= 3 && GridRx.IsMatch(w[2]) && w[2] != "RR73") grid = w[2];
            }
            if (call == null || call.StartsWith("<") || !CallRx.IsMatch(call) || string.Equals(call, myCall, StringComparison.OrdinalIgnoreCase)) return null;
            return new PskSpot { Call = call, Grid = grid, Mode = d.Mode.ToString(), Hz = dialHz + d.Freq, Snr = d.Snr, Time = d.SlotUtc };
        }

        public void Add(DecodeLine d, long dialHz)
        {
            if (dialHz <= 0) return;
            PskSpot s = SpotOf(d, dialHz, MyCall);
            if (s == null) return;
            lock (_pending)
            {
                PskSpot old;
                // a later spot of the same station replaces an earlier one, but keeps the grid it gave
                if (_pending.TryGetValue(s.Call, out old) && s.Grid.Length == 0) s.Grid = old.Grid;
                _pending[s.Call] = s;
            }
        }

        public int Pending { get { lock (_pending) return _pending.Count; } }

        public void Flush()
        {
            List<PskSpot> spots;
            lock (_pending) { spots = _pending.Values.ToList(); _pending.Clear(); }
            if (spots.Count == 0 || MyCall.Length < 3 || MyGrid.Length < 4) return;
            try
            {
                if (_udp == null) _udp = new UdpClient();
                foreach (byte[] p in Packets(spots, DateTime.UtcNow)) _udp.Send(p, p.Length, Host, Port);
                Status?.Invoke("Reported " + spots.Count + " station" + (spots.Count == 1 ? "" : "s") + " to PSK Reporter");
            }
            catch (Exception ex) { Status?.Invoke("PSK Reporter: " + ex.Message); }
        }

        // the packets for these spots (several when they don't fit in one)
        internal List<byte[]> Packets(List<PskSpot> spots, DateTime now)
        {
            List<byte[]> res = new List<byte[]>();
            for (int i = 0; i < spots.Count; i += 30)
                res.Add(packet(spots.Skip(i).Take(30).ToList(), now));
            return res;
        }

        private byte[] packet(List<PskSpot> spots, DateTime now)
        {
            MemoryStream body = new MemoryStream();
            set(body, 3, receiverTemplate());           // options template (receiver)
            set(body, 2, senderTemplate());             // template (senders)
            MemoryStream rx = new MemoryStream();
            str(rx, MyCall.ToUpperInvariant()); str(rx, MyGrid.ToUpperInvariant()); str(rx, Software); str(rx, Antenna);
            set(body, ReceiverTemplate, rx.ToArray());
            MemoryStream tx = new MemoryStream();
            foreach (PskSpot s in spots)
            {
                str(tx, s.Call);
                be(tx, (ulong)Math.Max(0, s.Hz), 5);
                tx.WriteByte(unchecked((byte)(sbyte)Math.Max(-128, Math.Min(127, s.Snr))));
                str(tx, s.Mode);
                str(tx, s.Grid ?? "");
                tx.WriteByte(1);                        // information source: automatically extracted
                be(tx, (ulong)unix(s.Time), 4);
            }
            set(body, SenderTemplate, tx.ToArray());

            MemoryStream m = new MemoryStream();
            byte[] b = body.ToArray();
            be(m, 10, 2);                               // IPFIX version
            be(m, (ulong)(16 + b.Length), 2);
            be(m, (ulong)unix(now), 4);
            be(m, _sequence, 4);
            be(m, _domain, 4);
            m.Write(b, 0, b.Length);
            _sequence += (uint)spots.Count + 1;         // data records sent
            return m.ToArray();
        }

        private static byte[] receiverTemplate()
        {
            MemoryStream t = new MemoryStream();
            be(t, ReceiverTemplate, 2); be(t, 4, 2); be(t, 0, 2);       // template id, field count, scope field count
            field(t, 2, 0xffff); field(t, 4, 0xffff); field(t, 8, 0xffff); field(t, 9, 0xffff);
            return t.ToArray();
        }

        private static byte[] senderTemplate()
        {
            MemoryStream t = new MemoryStream();
            be(t, SenderTemplate, 2); be(t, 7, 2);
            field(t, 1, 0xffff);        // sender callsign
            field(t, 5, 5);             // frequency (Hz, 5 bytes)
            field(t, 6, 1);             // SNR
            field(t, 10, 0xffff);       // mode
            field(t, 3, 0xffff);        // sender locator
            field(t, 11, 1);            // information source
            be(t, 150, 2); be(t, 4, 2); // flowStartSeconds (IANA)
            return t.ToArray();
        }

        private static void field(Stream s, int id, int len) { be(s, (ulong)(0x8000 + id), 2); be(s, (ulong)len, 2); be(s, Enterprise, 4); }

        // a set: its id, its length (padded to 4 bytes), then the content
        private static void set(Stream s, int id, byte[] content)
        {
            int pad = (4 - (content.Length + 4) % 4) % 4;
            be(s, (ulong)id, 2);
            be(s, (ulong)(content.Length + 4 + pad), 2);
            s.Write(content, 0, content.Length);
            for (int i = 0; i < pad; i++) s.WriteByte(0);
        }

        private static void str(Stream s, string v)
        {
            byte[] b = Encoding.UTF8.GetBytes(v ?? "");
            s.WriteByte((byte)Math.Min(254, b.Length));
            s.Write(b, 0, Math.Min(254, b.Length));
        }

        private static void be(Stream s, ulong v, int bytes) { for (int i = bytes - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i))); }
        private static long unix(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
    }
}
