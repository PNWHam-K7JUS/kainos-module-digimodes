/*  WsjtxUdp.cs

WSJT-X's UDP messages (its NetworkMessage protocol), so the programs that work with WSJT-X work with Kainos too:
GridTracker, JTAlert, N1MM+, Log4OM, HRD, DXKeeper and others. Kainos sends the heartbeat, status, each decode
and logged QSOs, and takes the replies those programs send back (answer a station, halt TX, free text, replay).

Each message is Qt QDataStream data, big-endian: magic 0xadbccbda, schema, message type, the sender's id, then
the message's fields. Strings are QByteArray UTF-8 (a 32-bit length, 0xffffffff for null), bool one byte,
QTime the milliseconds since midnight, QDateTime a Julian day (qint64), QTime and a time spec (1 = UTC).

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Kainos.DigiModes.Net
{
    internal enum WsjtxType : uint
    {
        Heartbeat = 0, Status = 1, Decode = 2, Clear = 3, Reply = 4, QsoLogged = 5, Close = 6, Replay = 7,
        HaltTx = 8, FreeText = 9, WsprDecode = 10, Location = 11, LoggedAdif = 12, HighlightCallsign = 13,
    }

    internal class QWriter
    {
        private readonly MemoryStream _m = new MemoryStream();
        public void U8(byte v) { _m.WriteByte(v); }
        public void Bool(bool v) { U8((byte)(v ? 1 : 0)); }
        public void U32(uint v) { for (int i = 3; i >= 0; i--) _m.WriteByte((byte)(v >> (8 * i))); }
        public void I32(int v) { U32(unchecked((uint)v)); }
        public void U64(ulong v) { for (int i = 7; i >= 0; i--) _m.WriteByte((byte)(v >> (8 * i))); }
        public void I64(long v) { U64(unchecked((ulong)v)); }
        public void Double(double v) { U64(unchecked((ulong)BitConverter.DoubleToInt64Bits(v))); }
        public void Utf8(string s)
        {
            if (s == null) { U32(0xffffffff); return; }
            byte[] b = Encoding.UTF8.GetBytes(s);
            U32((uint)b.Length);
            _m.Write(b, 0, b.Length);
        }
        public void QTime(DateTime utc) { U32((uint)utc.TimeOfDay.TotalMilliseconds); }
        public void QDateTime(DateTime utc)
        {
            I64(JulianDay(utc));
            QTime(utc);
            U8(1);                  // UTC
        }
        public static long JulianDay(DateTime d) { return (long)(d.Date - new DateTime(2000, 1, 1)).TotalDays + 2451545; }
        public byte[] ToArray() { return _m.ToArray(); }
    }

    internal class QReader
    {
        private readonly byte[] _b;
        private int _p;
        public QReader(byte[] b) { _b = b; }
        public bool More { get { return _p < _b.Length; } }
        public byte U8() { return _b[_p++]; }
        public bool Bool() { return U8() != 0; }
        public uint U32() { uint v = 0; for (int i = 0; i < 4; i++) v = (v << 8) | _b[_p++]; return v; }
        public int I32() { return unchecked((int)U32()); }
        public ulong U64() { ulong v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | _b[_p++]; return v; }
        public double Double() { return BitConverter.Int64BitsToDouble(unchecked((long)U64())); }
        public string Utf8()
        {
            uint n = U32();
            if (n == 0xffffffff) return null;
            string s = Encoding.UTF8.GetString(_b, _p, (int)n);
            _p += (int)n;
            return s;
        }
        public TimeSpan QTime() { uint ms = U32(); return ms == 0xffffffff ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms); }
    }

    // What the link reports, and what it's asked to do
    internal class WsjtxStatus
    {
        public ulong DialHz;
        public string Mode = "FT8", DxCall = "", Report = "", TxMode = "FT8", DeCall = "", DeGrid = "", DxGrid = "", TxMessage = "";
        public bool TxEnabled, Transmitting, Decoding, TxWatchdog;
        public uint RxDf, TxDf, PeriodS = 15;
        public byte Special;
    }

    internal class WsjtxReply
    {
        public TimeSpan Time;
        public int Snr;
        public double Dt;
        public uint Df;
        public string Mode, Message;
        public bool LowConfidence;
        public byte Modifiers;
    }

    internal class WsjtxUdp : IDisposable
    {
        public const uint Magic = 0xadbccbda;
        public const uint Schema = 3;

        private UdpClient _udp;
        private IPEndPoint _target;
        private Thread _rx;
        private volatile bool _run;
        private Timer _heartbeat;
        private readonly string _version, _revision;

        public string Id = "Kainos";
        public bool AcceptCommands = true;

        public event Action<WsjtxReply> Reply;
        public event Action<bool> HaltTx;           // auto TX only
        public event Action<string, bool> FreeText; // text, send
        public event Action Replay;
        public event Action<string> Problem;

        public WsjtxUdp(string version, string revision) { _version = version; _revision = revision; }

        public bool Running { get { return _udp != null; } }

        // host: an address or name (127.0.0.1, or a multicast group such as 224.0.0.1)
        public bool Start(string host, int port, out string problem)
        {
            problem = null;
            Stop();
            try
            {
                IPAddress addr;
                if (!IPAddress.TryParse(host, out addr))
                {
                    IPAddress[] all = Dns.GetHostAddresses(host);
                    addr = Array.Find(all, a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (addr == null) { problem = "Can't find " + host; return false; }
                }
                _target = new IPEndPoint(addr, port);
                // a port of our own: the programs we send to reply to it
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                if (IsMulticast(addr)) { _udp.Ttl = 1; _udp.MulticastLoopback = true; }
                _run = true;
                _rx = new Thread(receive) { IsBackground = true, Name = "Kainos WSJT-X UDP" };
                _rx.Start();
                _heartbeat = new Timer(_ => Heartbeat(), null, 0, 15000);
                return true;
            }
            catch (Exception ex) { problem = ex.Message; Stop(); return false; }
        }

        public static bool IsMulticast(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return b.Length == 4 && b[0] >= 224 && b[0] <= 239;
        }

        public void Stop()
        {
            if (_udp != null) try { send(header(WsjtxType.Close)); } catch { }
            _run = false;
            if (_heartbeat != null) { _heartbeat.Dispose(); _heartbeat = null; }
            if (_udp != null) { try { _udp.Close(); } catch { } _udp = null; }
            _rx = null;
        }

        public void Dispose() { Stop(); }

        private QWriter header(WsjtxType t)
        {
            QWriter w = new QWriter();
            w.U32(Magic);
            w.U32(Schema);
            w.U32((uint)t);
            w.Utf8(Id);
            return w;
        }

        private void send(QWriter w)
        {
            UdpClient u = _udp;
            if (u == null || _target == null) return;
            byte[] b = w.ToArray();
            try { u.Send(b, b.Length, _target); } catch (Exception ex) { Problem?.Invoke("UDP: " + ex.Message); }
        }

        public void Heartbeat()
        {
            QWriter w = header(WsjtxType.Heartbeat);
            w.U32(Schema);
            w.Utf8(_version);
            w.Utf8(_revision);
            send(w);
        }

        public void Status(WsjtxStatus s)
        {
            QWriter w = header(WsjtxType.Status);
            w.U64(s.DialHz);
            w.Utf8(s.Mode);
            w.Utf8(s.DxCall);
            w.Utf8(s.Report);
            w.Utf8(s.TxMode);
            w.Bool(s.TxEnabled);
            w.Bool(s.Transmitting);
            w.Bool(s.Decoding);
            w.U32(s.RxDf);
            w.U32(s.TxDf);
            w.Utf8(s.DeCall);
            w.Utf8(s.DeGrid);
            w.Utf8(s.DxGrid);
            w.Bool(s.TxWatchdog);
            w.Utf8("");             // sub-mode
            w.Bool(false);          // fast mode
            w.U8(s.Special);         // special operation (0 none, 1 NA VHF, 3 Field Day, 4 RTTY Roundup, 5 WW Digi)
            w.U32(0xffffffff);      // frequency tolerance: not used
            w.U32(s.PeriodS);
            w.Utf8("Kainos");       // configuration name
            w.Utf8(s.TxMessage);
            send(w);
        }

        // mode: "~" FT8, "+" FT4 (WSJT-X's mode characters)
        public void Decode(bool isNew, DateTime slotUtc, int snr, double dt, uint df, string mode, string message, bool lowConfidence)
        {
            QWriter w = header(WsjtxType.Decode);
            w.Bool(isNew);
            w.QTime(slotUtc);
            w.I32(snr);
            w.Double(dt);
            w.U32(df);
            w.Utf8(mode);
            w.Utf8(message);
            w.Bool(lowConfidence);
            w.Bool(false);          // off air: no
            send(w);
        }

        // a WSPR spot (WSJT-X's WSPRDecode message)
        public void WsprDecode(bool isNew, DateTime slotUtc, int snr, double dt, ulong hz, int drift, string call, string grid, int dbm)
        {
            QWriter w = header(WsjtxType.WsprDecode);
            w.Bool(isNew);
            w.QTime(slotUtc);
            w.I32(snr);
            w.Double(dt);
            w.U64(hz);
            w.I32(drift);
            w.Utf8(call);
            w.Utf8(grid);
            w.I32(dbm);
            w.Bool(false);          // off air: no
            send(w);
        }

        public void Clear()
        {
            QWriter w = header(WsjtxType.Clear);
            w.U8(2);                // both windows
            send(w);
        }

        public void QsoLogged(DateTime off, string dxCall, string dxGrid, ulong txHz, string mode, string rptSent, string rptRcvd,
                              string txPower, string comments, string name, DateTime on, string opCall, string myCall, string myGrid,
                              string exchSent = "", string exchRcvd = "")
        {
            QWriter w = header(WsjtxType.QsoLogged);
            w.QDateTime(off);
            w.Utf8(dxCall);
            w.Utf8(dxGrid);
            w.U64(txHz);
            w.Utf8(mode);
            w.Utf8(rptSent);
            w.Utf8(rptRcvd);
            w.Utf8(txPower);
            w.Utf8(comments);
            w.Utf8(name);
            w.QDateTime(on);
            w.Utf8(opCall);
            w.Utf8(myCall);
            w.Utf8(myGrid);
            w.Utf8(exchSent ?? "");
            w.Utf8(exchRcvd ?? "");
            w.Utf8("");             // ADIF propagation mode
            send(w);
        }

        public void LoggedAdif(string adifRecord)
        {
            QWriter w = header(WsjtxType.LoggedAdif);
            w.Utf8("\n<adif_ver:5>3.1.0\n<programid:6>Kainos\n<EOH>\n" + adifRecord);
            send(w);
        }

        private void receive()
        {
            while (_run)
            {
                try
                {
                    UdpClient u = _udp;
                    if (u == null) return;
                    IPEndPoint from = null;
                    byte[] b = u.Receive(ref from);
                    if (AcceptCommands) handle(b);
                }
                catch (SocketException) { if (!_run) return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex) { Problem?.Invoke("UDP message: " + ex.Message); }
            }
        }

        // a message from another program (exposed for the self-test)
        internal void handle(byte[] b)
        {
            QReader r = new QReader(b);
            if (b.Length < 16 || r.U32() != Magic) return;
            r.U32();                                // schema
            WsjtxType t = (WsjtxType)r.U32();
            string id = r.Utf8();
            // messages are addressed by id: take only ours (and those with none)
            if (!string.IsNullOrEmpty(id) && !string.Equals(id, Id, StringComparison.OrdinalIgnoreCase)) return;
            switch (t)
            {
                case WsjtxType.Reply:
                    WsjtxReply rp = new WsjtxReply { Time = r.QTime(), Snr = r.I32(), Dt = r.Double(), Df = r.U32(), Mode = r.Utf8(), Message = r.Utf8(), LowConfidence = r.Bool() };
                    if (r.More) rp.Modifiers = r.U8();
                    Reply?.Invoke(rp);
                    break;
                case WsjtxType.HaltTx:
                    HaltTx?.Invoke(r.More && r.Bool());
                    break;
                case WsjtxType.FreeText:
                    string text = r.Utf8();
                    FreeText?.Invoke(text ?? "", r.More && r.Bool());
                    break;
                case WsjtxType.Replay:
                    Replay?.Invoke();
                    break;
            }
        }

        // for the self-test: a message as another program would send it
        internal static byte[] Build(WsjtxType t, string id, Action<QWriter> body)
        {
            QWriter w = new QWriter();
            w.U32(Magic); w.U32(Schema); w.U32((uint)t); w.Utf8(id);
            body?.Invoke(w);
            return w.ToArray();
        }
    }
}
