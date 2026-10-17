/*  DigiEngine.cs

The running FT8 / FT4 station: the receiver's audio into the slot recorder, a decode at the end of every period,
automatic sequencing, and transmitting in the chosen periods, timed by the PC's clock (keep it synchronised:
Windows Time, Meinberg NTP or similar).

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kainos.DigiModes.Decode;
using Kainos.DigiModes.Ftx;
using Kainos.DigiModes.Qso;
using Kainos.Modules;

namespace Kainos.DigiModes
{
    internal class DigiEngine
    {
        private readonly IKainosHost _host;
        private readonly SlotRecorder _rec = new SlotRecorder();
        public readonly Jt9Decoder Decoder = new Jt9Decoder();
        public readonly QsoEngine Qso = new QsoEngine();

        // settings (the UI sets them; the worker reads them)
        public volatile FtxMode Mode = FtxMode.FT8;
        public volatile int Rx;                 // 0 = RX1, 1 = RX2
        public volatile int RxFreq = 1500, TxFreq = 1500;
        public volatile bool HoldTx;            // TX frequency stays where it is when answering
        public volatile bool TxSecond;          // transmit in the second (odd) periods
        public volatile bool TxEnabled;
        public double LevelDb = -10;
        public double TxLeadMs;                 // start sending this much early (the transmit path's delay)
        public double RxLatencyMs;              // the receive path's delay
        public int WatchdogMinutes = 6;
        public bool StopAfter73 = true;
        public string Ft8CodeExe;

        public event Action<DateTime, FtxMode, List<DecodeLine>> Decoded;
        public event Action<string> Status;
        public event Action<DateTime, string> TxStarted;    // period start, message
        public event Action TxEnded;
        public event Action Changed;            // something the UI shows (sequence, TX on/off)
        public event Action RxLost;             // the receive tap was taken by something else

        private Thread _thread;
        private volatile bool _run;
        private volatile bool _halt;
        private Task _decodeTask = Task.CompletedTask;
        // the clock (the self-test runs the engine on a simulated one), and decoding on the engine's own thread (self-test)
        public Func<DateTime> Now = () => DateTime.UtcNow;
        public bool SyncDecode;

        private DateTime _earlySlot = DateTime.MinValue, _finalSlot = DateTime.MinValue;    // the last period given each pass
        private DateTime _sequencedSlot = DateTime.MinValue;    // the latest period whose decodes the QSO has seen
        private readonly Dictionary<long, HashSet<string>> _shown = new Dictionary<long, HashSet<string>>();   // per period: messages reported
        private DateTime _lastTxSlot = DateTime.MinValue;
        private readonly HashSet<long> _txSlots = new HashSet<long>();     // periods (by start ticks) we sent in
        private DateTime _lastActivity = DateTime.UtcNow;
        private string _lastSent;
        private volatile bool _tapOk;

        public DigiEngine(IKainosHost host) { _host = host; }

        public bool Running { get { return _run; } }
        public bool Transmitting { get; private set; }
        public double RecorderNewest { get { return _rec.NewestTime; } }

        public void Activity() { _lastActivity = Now(); }

        public void Start()
        {
            if (_run) return;
            _run = true;
            _rec.Reset();
            _thread = new Thread(work) { IsBackground = true, Name = "Kainos FT8/FT4", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
            _halt = true;
            if (_thread != null && !_thread.Join(1500)) _host.Log("engine thread didn't stop in time");
            _thread = null;
            endTx();
            _host.Audio.ReleaseRx();
            _tapOk = false;
        }

        // Halt TX: stops the transmission now, and transmitting until enabled again
        public void Halt()
        {
            TxEnabled = false;
            _halt = true;
            Changed?.Invoke();
        }

        private int _tapRx = -1;

        private bool ensureTap()
        {
            int rx = Rx;
            if (_tapOk && _tapRx == rx) return true;
            string why;
            if (!_host.Audio.AcquireRx(rx, () => { _tapOk = false; RxLost?.Invoke(); }, out why))
            {
                Status?.Invoke("Can't listen to RX" + (rx + 1) + ": " + why);
                return false;
            }
            _tapOk = true;
            _tapRx = rx;
            _rec.Reset();
            return true;
        }

        public bool Listening { get { return _tapOk; } }

        // the receive tap was taken (RTTY / CW opened): listen again (the UI calls this when the pane is reopened)
        public void Relisten() { _tapOk = false; }

        private void work()
        {
            while (_run)
            {
                Step();
                Thread.Sleep(Transmitting ? 10 : 20);
            }
        }

        private readonly float[] _buf = new float[16384];

        // one turn of the engine: take the audio, decode what's due, transmit (the self-test calls this directly)
        public void Step()
        {
            try
            {
                if (_tapOk || ensureTap())
                {
                    int rate = _host.Audio.RxRate, n;
                    _rec.LatencyCorrection = RxLatencyMs / 1000.0;
                    while ((n = _host.Audio.ReadRx(_buf, _buf.Length)) > 0) _rec.Add(_buf, n, rate, Now());
                    scheduleDecode();
                }
                txTick();
            }
            catch (Exception ex) { _host.Log("engine: " + ex); if (!SyncDecode) Thread.Sleep(200); }
        }

        public bool Decoding { get { return !_decodeTask.IsCompleted; } }

        // ---- decoding ----
        //
        // Two passes a period, as WSJT-X does: an early one as soon as a signal at DT 0 has ended (FT8 13.6 s, FT4 5.9 s
        // into the period), so the reply is chosen before the next period starts, and a final one at the period's end
        // for stations late in time. The final pass reports only what the early one didn't.

        private static double earlyAt(FtxMode m) { return m == FtxMode.FT8 ? 13.6 : 5.9; }
        private static DateTime slotTime(double s) { return new DateTime((long)Math.Round(s * 1e7), DateTimeKind.Utc); }

        private void scheduleDecode()
        {
            double newest = _rec.NewestTime;
            if (double.IsNaN(newest) || !_decodeTask.IsCompleted) return;
            FtxMode mode = Mode;
            double p = FtxModes.Period(mode);

            // the period being received: its early pass
            double sCur = Math.Floor(newest / p) * p;
            DateTime cur = slotTime(sCur);
            if (newest - sCur >= earlyAt(mode) && cur > _earlySlot)
            {
                _earlySlot = cur;
                runPass(sCur, cur, mode, true);
                return;
            }
            // the period that has just ended (its last 0.2 s in): the final pass
            double sPrev = Math.Floor((newest + 0.2) / p) * p - p;
            DateTime prev = slotTime(sPrev);
            if (prev > _finalSlot)
            {
                _finalSlot = prev;
                runPass(sPrev, prev, mode, false);
            }
        }

        private void runPass(double s, DateTime slot, FtxMode mode, bool early)
        {
            bool sent;
            lock (_txSlots) sent = _txSlots.Contains(slot.Ticks);
            if (sent) { deliver(slot, mode, new List<DecodeLine>()); return; }      // ours: nothing to decode, but the QSO moves on
            double p = FtxModes.Period(mode), missing;
            float[] samples = _rec.Cut(s, p, out missing);
            if (samples == null || missing > p * 0.5) return;           // just started: not enough of the period
            int rxf = RxFreq;
            string my = Qso.MyCall, grid = Qso.MyGrid, his = Qso.HisCall;
            Action job = () =>
            {
                string why;
                // the early pass at depth 2 at most (about 0.5 s; depth 3 takes 1.7 s), so it's done before the next period
                List<DecodeLine> res = Decoder.Decode(mode, slot, samples, rxf, my, grid, his, out why, early ? Math.Min(2, Decoder.Depth) : Decoder.Depth);
                if (why != null) Status?.Invoke(why);
                deliver(slot, mode, res);
            };
            if (SyncDecode) { job(); return; }
            _decodeTask = Task.Run(job);
        }

        private void deliver(DateTime slot, FtxMode mode, List<DecodeLine> res)
        {
            List<DecodeLine> fresh;
            bool first;
            lock (_shown)
            {
                HashSet<string> seen;
                first = !_shown.TryGetValue(slot.Ticks, out seen);
                if (first) _shown[slot.Ticks] = seen = new HashSet<string>();
                fresh = res.Where(d => seen.Add(d.Message)).ToList();
                foreach (long old in _shown.Keys.Where(k => k < slot.Ticks - TimeSpan.FromMinutes(2).Ticks).ToList()) _shown.Remove(old);
            }
            if (fresh.Count > 0 || first)
                try { Decoded?.Invoke(slot, mode, fresh); } catch (Exception ex) { _host.Log("decoded handler: " + ex); }
            lock (Qso)
            {
                string sent = null;
                if (slot > _sequencedSlot) { sent = _lastSent; _lastSent = null; _sequencedSlot = slot; }
                bool changed = Qso.Sequence(fresh, sent);
                // the QSO is over (73 sent or received): stop, as WSJT-X does; calling CQ again starts afresh
                if (Qso.Done && StopAfter73 && TxEnabled) { TxEnabled = false; changed = true; }
                if (changed) Changed?.Invoke();
            }
        }

        // how long into our period transmitting waits for the decode of the period before it (so the reply answers it),
        // before starting anyway with what it has
        private static double maxWait(FtxMode m) { return m == FtxMode.FT8 ? 1.2 : 0.6; }

        // ---- transmitting ----

        private float[] _txWave;
        private int _txPos;
        private DateTime _txSlot;
        private DateTime _moxAskedAt;
        private bool _moxSeen;

        private void txTick()
        {
            DateTime now = Now();
            if (Transmitting) { feedTx(now); return; }
            _halt = false;
            if (!TxEnabled) return;

            if ((now - _lastActivity).TotalMinutes > WatchdogMinutes)
            {
                TxEnabled = false;
                Status?.Invoke("Transmit stopped: nothing from you for " + WatchdogMinutes + " minutes (the Tx watchdog)");
                Changed?.Invoke();
                return;
            }

            FtxMode mode = Mode;
            double p = FtxModes.Period(mode);
            double t = SlotRecorder.Seconds(now);
            double s = Math.Floor(t / p) * p;
            long k = (long)Math.Round(s / p);
            bool mine = (k & 1) == (TxSecond ? 1 : 0);
            double into = t - s;
            DateTime slot = new DateTime((long)Math.Round(s * 1e7), DateTimeKind.Utc);
            if (!mine || slot == _lastTxSlot || into > 2.0) return;      // not ours, already sent, or too late to start
            // the other station's period must be decoded first, so the message answers what he just sent
            DateTime prevSlot = slotTime(s - p);
            bool heard = _sequencedSlot >= prevSlot;
            if (!heard && _tapOk && into < maxWait(mode)) return;
            if (!_host.Radio.PowerOn) { Status?.Invoke("The radio is off"); TxEnabled = false; Changed?.Invoke(); return; }
            string m = _host.Radio.Mode(_host.Radio.TxReceiver);
            if (m != "DIGU" && m != "USB") { Status?.Invoke("Transmit needs DIGU (or USB) on the transmitting VFO"); TxEnabled = false; Changed?.Invoke(); return; }

            string msg;
            lock (Qso) msg = Qso.NextMessage;
            string why;
            PackedMessage pm = Pack77.Pack(Ft8CodeExe, msg, out why);
            if (pm == null) { Status?.Invoke(why); TxEnabled = false; Changed?.Invoke(); return; }
            if (pm.Decoded != null && pm.Decoded != Pack77.Normalise(msg)) Status?.Invoke("Will be received as: " + pm.Decoded);

            int rate = _host.Audio.TxRate;
            if (rate <= 0) { Status?.Invoke("No transmit audio stream"); return; }
            byte[] payload = FtxEncoder.PayloadFromBits(pm.Bits77);
            byte[] tones = mode == FtxMode.FT8 ? FtxEncoder.Ft8Tones(payload) : FtxEncoder.Ft4Tones(payload);
            float[] wave = FtxModulator.Synthesize(mode, tones, TxFreq, rate);
            float amp = (float)Math.Pow(10, LevelDb / 20.0);
            for (int i = 0; i < wave.Length; i++) wave[i] *= amp;

            // silence up to the signal's start (FT8 / FT4: 0.5 s into the period), less the transmit path's delay;
            // starting late drops what's already past
            double lead = FtxModes.TxStart(mode) - into - TxLeadMs / 1000.0;
            int silence = (int)Math.Round(lead * rate);
            float[] all;
            if (silence >= 0) { all = new float[silence + wave.Length]; Array.Copy(wave, 0, all, silence, wave.Length); }
            else { int skip = Math.Min(wave.Length, -silence); all = new float[wave.Length - skip]; Array.Copy(wave, skip, all, 0, all.Length); }

            _txWave = all;
            _txPos = 0;
            _txSlot = slot;
            _lastTxSlot = slot;
            lock (_txSlots) { _txSlots.Add(slot.Ticks); if (_txSlots.Count > 200) _txSlots.Clear(); }
            _lastSent = pm.Message;
            lock (Qso) Qso.Sent(pm.Message);
            _halt = false;
            Transmitting = true;
            _moxSeen = false;
            _moxAskedAt = now;
            _host.Audio.TxStart();
            _host.BeginInvoke(() => _host.Radio.SetMox(true));
            _host.Log("TX " + slot.ToString("HHmmss.f") + " " + mode + " " + TxFreq + " Hz: " + pm.Message);
            TxStarted?.Invoke(slot, pm.Message);
            Changed?.Invoke();
            feedTx(now);
        }

        private void feedTx(DateTime now)
        {
            bool keyed = _host.Radio.Transmitting;
            if (keyed) _moxSeen = true;
            // stopped: Halt, or un-keyed elsewhere (the MOX button, a foot switch), or MOX never came
            if (_halt || (_moxSeen && !keyed) || (!_moxSeen && (now - _moxAskedAt).TotalSeconds > 2))
            {
                if (!_moxSeen && !_halt) Status?.Invoke("The radio didn't go into transmit");
                endTx();
                return;
            }
            int rate = _host.Audio.TxRate;
            int ahead = Math.Max(2048, rate / 4);
            while (_txPos < _txWave.Length && _host.Audio.TxQueued < ahead)
            {
                int n = Math.Min(4096, _txWave.Length - _txPos);
                float[] part = new float[n];
                Array.Copy(_txWave, _txPos, part, 0, n);
                int w = _host.Audio.TxWrite(part, n);
                _txPos += w;
                if (w < n) break;
            }
            if (_txPos >= _txWave.Length && _host.Audio.TxQueued == 0) endTx();
        }

        private void endTx()
        {
            if (!Transmitting) return;
            Transmitting = false;
            _host.Audio.TxStop();
            _host.BeginInvoke(() => _host.Radio.SetMox(false));
            _txWave = null;
            TxEnded?.Invoke();
            Changed?.Invoke();
        }
    }
}
