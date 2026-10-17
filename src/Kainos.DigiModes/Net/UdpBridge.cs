/*  UdpBridge.cs

Connects the FT8 / FT4 station to WSJT-X's UDP messages (WsjtxUdp.cs): what happens here goes out, and what
GridTracker, JTAlert and the loggers ask for comes in.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Kainos.DigiModes.Decode;
using Kainos.DigiModes.Ftx;
using Kainos.DigiModes.Qso;
using Kainos.DigiModes.UI;
using Kainos.Modules;

namespace Kainos.DigiModes.Net
{
    internal class UdpBridge : IDisposable
    {
        private readonly IKainosHost _host;
        private readonly DigiEngine _e;
        private readonly DigiPane _pane;
        private readonly WsjtxUdp _udp;
        private readonly List<DecodeLine> _recent = new List<DecodeLine>();     // for Replay: the last two minutes
        private Timer _statusTimer;
        private string _lastStatus = "";

        public UdpBridge(IKainosHost host, DigiEngine engine, DigiPane pane, string version)
        {
            _host = host;
            _e = engine;
            _pane = pane;
            _udp = new WsjtxUdp("Kainos Digital Modes " + version, "kainos");
            _udp.Problem += s => _host.Log(s);

            _e.Decoded += (slot, mode, list) =>
            {
                lock (_recent)
                {
                    _recent.AddRange(list);
                    _recent.RemoveAll(d => (DateTime.UtcNow - d.SlotUtc).TotalMinutes > 2);
                }
                if (!_udp.Running) return;
                foreach (DecodeLine d in list) send(d, true);
                status(true);
            };
            _e.Changed += () => status(false);
            _e.TxStarted += (slot, msg) => status(true);
            _e.TxEnded += () => status(true);
            _pane.QsoCommitted += (q, mode, rec) =>
            {
                if (!_udp.Running) return;
                _udp.QsoLogged(q.Off, q.Call, q.Grid, (ulong)Math.Max(0, q.DialHz + q.AudioHz), mode.ToString(), q.RstSent, q.RstRcvd,
                               q.TxPower, q.Comments, q.Name, q.On, q.Operator, _e.Qso.MyCall, _e.Qso.MyGrid);
                _udp.LoggedAdif(rec);
            };

            // what the other programs ask for
            _udp.Reply += r => _host.BeginInvoke(() =>
            {
                DateTime slot = DateTime.UtcNow.Date + r.Time;
                if (slot > DateTime.UtcNow.AddMinutes(1)) slot = slot.AddDays(-1);
                FtxMode m = r.Mode == "+" ? FtxMode.FT4 : FtxMode.FT8;
                if (m != _e.Mode) { _host.Log("UDP reply for " + m + " ignored while in " + _e.Mode); return; }
                _pane.AnswerExternal(new DecodeLine { SlotUtc = slot, Snr = r.Snr, Dt = r.Dt, Freq = (int)r.Df, Message = Pack77.Normalise(r.Message), Mode = m });
            });
            _udp.HaltTx += autoOnly => _host.BeginInvoke(() =>
            {
                if (autoOnly) _e.TxEnabled = false; else _e.Halt();
                _pane.RefreshAll();
            });
            _udp.FreeText += (text, send) => _host.BeginInvoke(() =>
            {
                lock (_e.Qso)
                {
                    if (text.Length > 0) { _e.Qso.FreeText = text; _e.Qso.Next = 7; }
                }
                if (send) { _e.Activity(); _e.TxEnabled = true; }
                _pane.RefreshAll();
            });
            _udp.Replay += () =>
            {
                List<DecodeLine> all;
                lock (_recent) all = _recent.ToList();
                foreach (DecodeLine d in all) send(d, false);
            };
        }

        // from the settings: on or off, where to, our id
        public void Apply()
        {
            bool on = _host.GetSetting("udp", "1") == "1";
            string host = _host.GetSetting("udphost", "127.0.0.1").Trim();
            int port;
            if (!int.TryParse(_host.GetSetting("udpport", "2237"), out port) || port <= 0 || port > 65535) port = 2237;
            _udp.Id = string.IsNullOrWhiteSpace(_host.GetSetting("udpid", "")) ? "Kainos" : _host.GetSetting("udpid", "").Trim();
            _udp.AcceptCommands = _host.GetSetting("udpaccept", "1") == "1";
            _udp.Stop();
            if (_statusTimer != null) { _statusTimer.Dispose(); _statusTimer = null; }
            if (!on) return;
            string why;
            if (!_udp.Start(host.Length > 0 ? host : "127.0.0.1", port, out why)) { _host.Log("UDP: " + why); return; }
            _host.Log("UDP to " + host + ":" + port + " as " + _udp.Id);
            _lastStatus = "";
            // the status again every few seconds (the dial frequency, the time in the period ...), or when it changes
            _statusTimer = new Timer(_ => status(false), null, 1000, 3000);
        }

        private void send(DecodeLine d, bool isNew)
        {
            _udp.Decode(isNew, d.SlotUtc, d.Snr, d.Dt, (uint)Math.Max(0, d.Freq), d.Mode == FtxMode.FT4 ? "+" : "~", d.Message, d.Ap);
        }

        private void status(bool force)
        {
            if (!_udp.Running) return;
            try
            {
                WsjtxStatus s;
                lock (_e.Qso)
                {
                    s = new WsjtxStatus
                    {
                        DialHz = (ulong)Math.Max(0, _host.Radio.VfoHz(_e.Rx)),
                        Mode = _e.Mode.ToString(), TxMode = _e.Mode.ToString(),
                        DxCall = _e.Qso.HisCall, DxGrid = _e.Qso.HisGrid,
                        Report = _e.Qso.HisCall.Length > 0 ? QsoEngine.Report(_e.Qso.HisSnr) : "",
                        DeCall = _host.Callsign, DeGrid = _host.Grid,
                        TxEnabled = _e.TxEnabled, Transmitting = _e.Transmitting, Decoding = _e.Decoding,
                        RxDf = (uint)_e.RxFreq, TxDf = (uint)_e.TxFreq,
                        PeriodS = (uint)Math.Round(FtxModes.Period(_e.Mode)),
                        TxMessage = _e.Qso.NextMessage,
                    };
                }
                string key = string.Join("|", s.DialHz, s.Mode, s.DxCall, s.DxGrid, s.Report, s.TxEnabled, s.Transmitting, s.RxDf, s.TxDf, s.TxMessage, s.DeCall, s.DeGrid);
                // the timer sends only changes; events always send
                if (!force && key == _lastStatus && DateTime.UtcNow.Second % 15 > 2) return;
                _lastStatus = key;
                _udp.Status(s);
            }
            catch (Exception ex) { _host.Log("UDP status: " + ex.Message); }
        }

        public void Dispose()
        {
            if (_statusTimer != null) { _statusTimer.Dispose(); _statusTimer = null; }
            _udp.Dispose();
        }

        internal WsjtxUdp Link { get { return _udp; } }
    }
}
