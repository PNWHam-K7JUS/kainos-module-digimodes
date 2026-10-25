/*  DigiPane.cs

The FT8 / FT4 window: a bar of settings, the audio frequency strip, band activity, the RX frequency (my QSO)
list and the QSO panel with the six standard messages.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Kainos.DigiModes.Decode;
using Kainos.DigiModes.Ftx;
using Kainos.DigiModes.Qso;
using Kainos.Modules;

namespace Kainos.DigiModes.UI
{
    internal class DigiPane : Panel
    {
        private readonly IKainosHost _host;
        private readonly IKainosTheme _t;
        private readonly DigiEngine _e;

        private readonly KButton _mode, _band, _rxSel, _hold, _second, _auto, _enable, _halt, _tune, _pop, _settings;
        private readonly Label _status, _clock;
        private readonly FreqStrip _strip;
        private readonly KainosCrispHost _stripHost;
        private readonly DecodeList _activity, _rxList;
        private readonly Panel _qso;
        private readonly TextBox _hisCall, _hisGrid, _free;
        private readonly KButton[] _tx = new KButton[7];
        private readonly KButton _cq, _clear;
        private readonly Timer _timer;
        private readonly ContextMenuStrip _bandMenu = new ContextMenuStrip();
        private string _statusText = "";
        private DateTime _statusAt;
        private bool _syncing, _built;
        private DateTime _shownSlot;
        private bool _headed;
        private readonly List<DecodeLine> _slotDecodes = new List<DecodeLine>();

        public event Action PopToggled;
        public event Action SettingsChanged;       // the settings window was OKed

        public DigiPane(IKainosHost host, DigiEngine engine)
        {
            _host = host;
            _t = host.Theme;
            _e = engine;
            BackColor = _t.Bg;
            DoubleBuffered = true;

            Func<string, string, KButton> btn = (text, tip) =>
            {
                KButton b = new KButton(_t, text);
                if (tip != null) new ToolTip().SetToolTip(b, tip);
                Controls.Add(b);
                return b;
            };
            _mode = btn("FT8", "The mode: FT8 (15 s), FT4 (7.5 s), FT2 (3.75 s, experimental), JT65 and JT9 (1 minute), WSPR (2-minute beacons)");
            _band = btn("Band", "Tune to a band's FT8 / FT4 frequency (and set DIGU)");
            _rxSel = btn("RX1", "The receiver decoded: RX1 or RX2");
            _hold = btn("Hold Tx", "Keep the transmit frequency when answering someone (call on a clear frequency)");
            _second = btn("Tx 1st", "Transmit in the first (even) or second (odd) periods");
            _auto = btn("Auto seq", "Send the next message of the QSO automatically");
            _enable = btn("Enable Tx", "Transmit the next message in your periods");
            _halt = btn("Halt Tx", "Stop transmitting now");
            _tune = btn("Tune", "A steady tone at your TX frequency (up to 30 s) to set drive and Settings > Transmit audio level: aim for full power with the ALC barely moving. Click again to stop.");
            _pop = btn("Pop out", "Docked under the panadapter, or in its own window");
            _settings = btn("Settings", "Transmit level and timing, decoding, logging, and other programs (WSJT-X UDP)");
            _hold.Toggle = _auto.Toggle = true;

            _status = new Label { ForeColor = _t.Dim, BackColor = _t.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = _t.Font(_t.S(11), false) };
            _clock = new Label { ForeColor = _t.Gold, BackColor = _t.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Consolas", _t.S(13), FontStyle.Bold, GraphicsUnit.Pixel) };
            Controls.Add(_status);
            Controls.Add(_clock);

            _strip = new FreqStrip(_t);
            _stripHost = new KainosCrispHost(_strip, host.GetSetting("crispwf", "1") == "1");     // the waterfall at the screen's own resolution (Kainos.ModuleApi)
            Controls.Add(_stripHost);
            _activity = new DecodeList(_t) { Title = "BAND ACTIVITY" };
            // filters: CQ only (and calls to me), hide stations already worked on this band
            _activity.Chips.Add(new DecodeList.Chip { Text = "CQ only", On = host.GetSetting("fcq", "0") == "1", Toggled = on => { _host.SetSetting("fcq", on ? "1" : "0"); applyFilter(); } });
            _activity.Chips.Add(new DecodeList.Chip { Text = "Hide worked", On = host.GetSetting("fhide", "0") == "1", Toggled = on => { _host.SetSetting("fhide", on ? "1" : "0"); applyFilter(); } });
            _rxList = new DecodeList(_t) { Title = "RX FREQUENCY" };
            Controls.Add(_activity);
            Controls.Add(_rxList);

            _qso = new Panel { BackColor = _t.Panel };
            Controls.Add(_qso);
            // AutoSize off: a text box otherwise takes its height from its font, not from the row it's given
            Func<TextBox> box = () => new TextBox { AutoSize = false, BackColor = _t.Raised, ForeColor = _t.Text, BorderStyle = BorderStyle.FixedSingle, CharacterCasing = CharacterCasing.Upper, Font = _t.Font(_t.S(12), true) };
            _hisCall = box(); _hisGrid = box(); _free = box();
            _hisCall.MaxLength = 11; _hisGrid.MaxLength = 6; _free.MaxLength = 13;
            _qso.Controls.AddRange(new Control[] { _hisCall, _hisGrid, _free });
            for (int i = 0; i < 7; i++)
            {
                int n = i + 1;
                KButton b = new KButton(_t, "") { FontPx = 10.5f, BackColor = _t.Panel };
                b.Click += (s, e) => { _e.Activity(); lock (_e.Qso) _e.Qso.Next = n; refreshQso(); };
                _qso.Controls.Add(b);
                _tx[i] = b;
            }
            _cq = new KButton(_t, "CQ") { BackColor = _t.Panel };
            _clear = new KButton(_t, "Clear") { BackColor = _t.Panel };
            _qso.Controls.Add(_cq);
            _qso.Controls.Add(_clear);

            wire();
            loadSettings();

            _timer = new Timer { Interval = 200 };
            _timer.Tick += (s, e) => tick();
            _timer.Start();
            _built = true;
            PerformLayout();
            refreshAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Stop(); _timer.Dispose(); _bandMenu.Dispose(); }
            base.Dispose(disposing);
        }

        // ---- layout ----

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!_built) return;        // adding the controls lays out before they all exist
            int pad = _t.S(4), bh = _t.S(24), x = pad, y = pad;
            Action<Control, int> place = (c, w) => { c.SetBounds(x, y, _t.S(w), bh); x += _t.S(w) + pad; };
            place(_mode, 70); x += pad;
            place(_band, 64); place(_rxSel, 44); x += pad;
            place(_hold, 64); place(_second, 56); place(_auto, 68); x += pad;
            place(_enable, 76); place(_halt, 64); place(_tune, 52);
            int right = Width - pad;
            _settings.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _pop.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _clock.SetBounds(right - _t.S(200), y, _t.S(200), bh); right -= _t.S(200) + pad;
            _status.SetBounds(x + pad, y, Math.Max(0, right - x - pad), bh);

            y += bh + pad;
            // the QSO panel down the right; on the left the waterfall (about a third of the height, taller when the pane
            // is dragged taller) over band activity and the RX frequency list
            int qsoW = _t.S(270);
            int leftW = Width - 3 * pad - qsoW;
            _qso.SetBounds(Width - pad - qsoW, y, qsoW, Math.Max(_t.S(60), Height - y - pad));
            int wfH = Math.Max(_t.S(48), Math.Min(_t.S(220), (Height - y) * 34 / 100));
            _stripHost.SetBounds(pad, y, leftW, wfH);
            y += wfH + pad;
            int h = Math.Max(_t.S(40), Height - y - pad);
            int actW = (int)(leftW * 0.58);
            _activity.SetBounds(pad, y, actW, h);
            _rxList.SetBounds(pad + actW + pad, y, leftW - actW - pad, h);
            layoutQso();
        }

        private void layoutQso()
        {
            // nine rows: his call and grid, Tx1-Tx6, free text and Tx7, CQ and Clear; shrunk together to fit the height
            const int rows = 9;
            int w = _qso.Width, pad = _t.S(4), gap = 2;
            int rh = Math.Max(14, Math.Min(_t.S(22), (_qso.Height - 2 * pad - (rows - 1) * gap) / rows));
            float fontPx = Math.Max(9f, rh * 0.58f);
            foreach (TextBox b in new[] { _hisCall, _hisGrid, _free })
                if (Math.Abs(b.Font.Size - fontPx) > 0.4f) b.Font = _t.Font(fontPx, true);
            foreach (KButton b in _tx.Concat(new[] { _cq, _clear })) b.FontPx = Math.Min(10.5f, fontPx / _t.Scale);
            int y = pad, inner = w - 3 * pad;
            _hisCall.SetBounds(pad, y, inner * 6 / 10, rh);
            _hisGrid.SetBounds(pad * 2 + inner * 6 / 10, y, inner - inner * 6 / 10, rh);
            y += rh + gap;
            for (int i = 0; i < 6; i++) { _tx[i].SetBounds(pad, y, w - 2 * pad, rh); y += rh + gap; }
            _free.SetBounds(pad, y, inner * 7 / 10, rh);
            _tx[6].SetBounds(pad * 2 + inner * 7 / 10, y, inner - inner * 7 / 10, rh);
            y += rh + gap;
            _cq.SetBounds(pad, y, inner / 2, rh);
            _clear.SetBounds(pad * 2 + inner / 2, y, inner - inner / 2, rh);
        }

        // ---- wiring ----

        private void wire()
        {
            _mode.Click += (s, e) =>
            {
                ContextMenuStrip menu = new ContextMenuStrip();
                foreach (FtxMode m in FtxModes.All)
                {
                    FtxMode mm = m;
                    ToolStripMenuItem it = new ToolStripMenuItem(FtxModes.Label(m)) { Checked = m == _e.Mode };
                    it.Click += (s2, e2) => setMode(mm);
                    menu.Items.Add(it);
                }
                menu.Closed += (s2, e2) => BeginInvoke(new Action(menu.Dispose));
                menu.Show(_mode, new Point(0, _mode.Height));
            };
            _band.Click += (s, e) => { buildBandMenu(); _bandMenu.Show(_band, new Point(0, _band.Height)); };
            _rxSel.Click += (s, e) => { _e.Rx = 1 - _e.Rx; _e.Relisten(); save(); refreshAll(); };
            _hold.Click += (s, e) => { _e.HoldTx = _hold.On; save(); };
            _second.Click += (s, e) => { _e.Activity(); _e.TxSecond = !_e.TxSecond; save(); refreshAll(); };
            _auto.Click += (s, e) => { lock (_e.Qso) _e.Qso.AutoSeq = _auto.On; save(); };
            _enable.Click += (s, e) =>
            {
                _e.Activity();
                if (!_e.TxEnabled && string.IsNullOrEmpty(_e.Qso.MyCall)) { status("Set your callsign first (Setup > DSP > FreeDV (RADE))"); return; }
                _e.TxEnabled = !_e.TxEnabled;
                refreshAll();
            };
            _halt.Click += (s, e) => { _e.Halt(); _e.Tune(false); refreshAll(); };
            _tune.Click += (s, e) => { _e.Activity(); _e.Tune(!_e.Tuning); refreshAll(); };
            _pop.Click += (s, e) => PopToggled?.Invoke();
            _settings.Click += (s, e) => showSettings();

            _strip.RxClicked += hz => { _e.Activity(); _e.RxFreq = hz; if (!_e.HoldTx) _e.TxFreq = hz; save(); refreshAll(); };
            _strip.TxClicked += hz => { _e.Activity(); _e.TxFreq = hz; save(); refreshAll(); };

            _activity.RowDoubleClicked += answer;
            _rxList.RowDoubleClicked += answer;

            _hisCall.TextChanged += (s, e) => { if (_syncing) return; lock (_e.Qso) _e.Qso.HisCall = _hisCall.Text.Trim(); refreshQso(); };
            _hisGrid.TextChanged += (s, e) => { if (_syncing) return; lock (_e.Qso) _e.Qso.HisGrid = _hisGrid.Text.Trim(); refreshQso(); };
            _free.TextChanged += (s, e) => { if (_syncing) return; lock (_e.Qso) _e.Qso.FreeText = _free.Text; refreshQso(); };
            _cq.Click += (s, e) =>
            {
                _e.Activity();
                lock (_e.Qso) { _e.Qso.Clear(); _e.Qso.Next = 6; }
                refreshAll();
            };
            _clear.Click += (s, e) => { _e.Activity(); lock (_e.Qso) _e.Qso.Clear(); refreshAll(); };

            _e.Decoded += (slot, mode, list) => _host.BeginInvoke(() => showDecodes(slot, mode, list));
            _e.SpectrumLine += (line, t) => _host.BeginInvoke(() => _strip.AddLine(line, t));
            _e.TxStarted += (slot, msg) => _host.BeginInvoke(() =>
            {
                string line = slot.ToString("HHmmss") + "  Tx " + _e.LastTxFreq.ToString().PadLeft(4) + "  " + msg;
                allTxt(new[] { DigiPane.line(slot, _host.Radio.VfoHz(_host.Radio.TxReceiver), "Tx", _e.Mode, 0, 0, _e.LastTxFreq, msg) });
                _rxList.Add(new DecodeRow { Text = line, Kind = RowKind.Mine });
            });
            _e.Status += s => _host.BeginInvoke(() => status(s));
            _e.Changed += () => _host.BeginInvoke(refreshAll);
            _e.Qso.Logged += q => _host.BeginInvoke(() => logged(q));
        }

        internal void setMode(FtxMode m)
        {
            if (_e.Mode == m) return;
            FtxMode old = _e.Mode;
            double vfo = _host.Radio.VfoHz(_e.Rx);
            if (_e.Transmitting) _e.Halt();
            _e.TxEnabled = false;           // a new mode starts with transmitting off
            _e.Mode = m;
            lock (_e.Qso) _e.Qso.Jt = m == FtxMode.JT65 || m == FtxMode.JT9;
            // WSPR signals live in 1400 - 1600 Hz
            if (m == FtxMode.WSPR && (_e.TxFreq < 1400 || _e.TxFreq > 1595)) { _e.TxFreq = 1500; _e.RxFreq = 1500; }
            string band = tuneForMode(old, m, vfo);
            _activity.Separator(band ?? "");
            save();
            refreshAll();
        }

        // the new mode's frequency on the band the radio is on: the Band menu entry whose old-mode frequency it's at
        // (so a band of your own, a DXpedition's, follows too), else the one named for the band it's in. Returns the
        // band's name when it tuned
        private string tuneForMode(FtxMode old, FtxMode m, double vfo)
        {
            Bands.Entry e = Bands.Menu.FirstOrDefault(x => x.Freq(old) > 0 && Math.Abs(x.Freq(old) - vfo) <= 3000);
            if (e == null)
            {
                Bands.Band b = Bands.Of(vfo);
                if (b != null) e = Bands.Menu.FirstOrDefault(x => string.Equals(x.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            if (e == null) return null;                 // not on a band in the list: stay where it is
            double f = e.Freq(m);
            if (f <= 0)
            {
                status("No " + m + " frequency for " + e.Name + " in the band list (Band > Edit band frequencies): staying on " +
                       (vfo / 1e6).ToString("0.0000", CultureInfo.InvariantCulture) + " MHz");
                return null;
            }
            int rx = _e.Rx;
            _host.Radio.SetMode(rx, "DIGU");
            if (Math.Abs(f - vfo) > 1) _host.Radio.TuneVfo(rx, f);
            status("Tuned to " + (f / 1e6).ToString("0.0000", CultureInfo.InvariantCulture) + " MHz for " + m + " on " + e.Name);
            return e.Name;
        }

        private void buildBandMenu()
        {
            _bandMenu.Items.Clear();
            int rx = _e.Rx;
            foreach (Bands.Entry b in Bands.Menu)
            {
                double f = b.Freq(_e.Mode);
                if (f <= 0) continue;
                ToolStripMenuItem it = new ToolStripMenuItem(b.Name + "   " + (f / 1e6).ToString("0.0000", CultureInfo.InvariantCulture) + " MHz");
                it.Click += (s, e) =>
                {
                    _host.Radio.SetMode(rx, "DIGU");
                    _host.Radio.TuneVfo(rx, f);
                    _activity.Separator(b.Name);
                    refreshAll();
                };
                _bandMenu.Items.Add(it);
            }
            _bandMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem edit = new ToolStripMenuItem("Edit band frequencies...");
            edit.Click += (s, e) => editBands();
            _bandMenu.Items.Add(edit);
        }

        private void editBands()
        {
            using (BandEditForm f = new BandEditForm(Bands.Format(Bands.Menu)))
            {
                _t.Style(f);
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
                Bands.Menu = f.Result;
                bool isDefault = Bands.Format(f.Result) == Bands.Format(Bands.Defaults());
                _host.SetSetting("bands", isDefault ? "" : Bands.Format(f.Result));
            }
        }

        private void answer(DecodeLine d)
        {
            if (!FtxModes.HasQso(_e.Mode)) return;            // WSPR spots are beacons: nothing to answer
            _e.Activity();
            bool ok;
            lock (_e.Qso) ok = _e.Qso.Answer(d);
            if (!ok) { status("Nobody to answer in \"" + d.Message + "\""); return; }
            _e.RxFreq = d.Freq;
            if (!_e.HoldTx) _e.TxFreq = d.Freq;
            // transmit in the periods he doesn't
            long k = (long)Math.Round(SlotRecorder.Seconds(d.SlotUtc) / FtxModes.Period(d.Mode));
            _e.TxSecond = (k & 1) == 0;
            if (!string.IsNullOrEmpty(_e.Qso.MyCall)) _e.TxEnabled = true;
            save();
            refreshAll();
        }

        private void showDecodes(DateTime slot, FtxMode mode, List<DecodeLine> list)
        {
            // a period comes in two passes (early and final): its decodes stay together under one heading
            if (slot != _shownSlot) { _shownSlot = slot; _slotDecodes.Clear(); _headed = false; }
            _slotDecodes.AddRange(list);
            _strip.SetDecodes(new List<DecodeLine>(_slotDecodes));
            _strip.AddLabels(slot, mode, list);
            if (list.Count == 0) return;
            string my = _e.Qso.MyCall, his = _e.Qso.HisCall;
            if (!_headed) { _activity.Separator(slot.ToString("HH:mm:ss") + " " + mode); _headed = true; }
            double dial = _host.Radio.VfoHz(_e.Rx);
            Bands.Band band = Bands.Of(dial);
            string bandName = band != null ? band.Name.ToUpperInvariant() : "";
            allTxt(list.OrderBy(x => x.Freq).Select(d => line(slot, dial, "Rx", mode, d.Snr, d.Dt, d.Freq, d.Message)));
            clockCheck(list);
            bool alertMe = false, alertDx = false;
            foreach (DecodeLine d in list.OrderBy(x => x.Freq))
            {
                string[] w = d.Message.Split(' ');
                RowKind k = my.Length > 0 && w.Contains(my) ? RowKind.ToMe : d.Message.StartsWith("CQ ") ? RowKind.Cq : RowKind.Normal;
                WorkedInfo wi = Worked.Classify(d.Message, bandName);
                DecodeRow row = new DecodeRow { Decode = d, Text = d.ToString(), Kind = k, Mark = wi != null ? wi.Mark : WorkedMark.None, Note = wi != null ? wi.Country : null };
                _activity.Add(row);
                bool mine = k == RowKind.ToMe || (his.Length > 0 && w.Length > 1 && w[1] == his) || Math.Abs(d.Freq - _e.RxFreq) <= 10;
                if (mine) _rxList.Add(new DecodeRow { Decode = d, Text = d.ToString(), Kind = k, Mark = row.Mark, Note = row.Note });
                alertMe |= k == RowKind.ToMe;
                alertDx |= k == RowKind.Cq && row.Mark == WorkedMark.NewDxcc;
            }
            // sounds: someone calling me; a new DXCC entity calling CQ (Settings)
            try
            {
                if (alertMe && _host.GetSetting("sndme", "1") == "1") System.Media.SystemSounds.Exclamation.Play();
                else if (alertDx && _host.GetSetting("snddxcc", "0") == "1") System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
            refreshQso();
        }

        // a QSO is complete: Log QSO (unless logging without asking), then the ADIF file and QsoCommitted (UDP)
        public event Action<QsoRecord, FtxMode, string> QsoCommitted;      // the QSO, its mode, its ADIF record

        private void logged(QsoRecord q)
        {
            FtxMode mode = _e.Mode;
            q.Mode = mode.ToString();
            q.DialHz = _host.Radio.VfoHz(_host.Radio.TxReceiver);
            q.AudioHz = _e.TxFreq;
            q.TxPower = _host.GetSetting("power", "");
            q.Operator = _host.GetSetting("operator", "");
            if (_host.GetSetting("logprompt", "1") == "1")
            {
                Bands.Band b = Bands.Of(q.DialHz);
                using (LogQsoForm f = new LogQsoForm(q, b != null ? b.Name : ""))
                    if (f.ShowDialog(FindForm()) != DialogResult.OK) { status("Not logged: " + q.Call); return; }
            }
            try
            {
                string path = System.IO.Path.Combine(_host.DataFolder, "kainos-digimodes.adi");
                string rec = AdifLog.Append(path, q, mode, _e.Qso.MyCall, _e.Qso.MyGrid);
                status("Logged " + q.Call + " (" + q.RstSent + " / " + q.RstRcvd + ") to kainos-digimodes.adi");
                Worked.Add(q.Call, q.Grid, Bands.AdifBand(q.DialHz));
                if (q.Contest == Contest.RttyRoundup) _host.SetSetting("serial", _e.Qso.Serial.ToString());
                QsoCommitted?.Invoke(q, mode, rec);
            }
            catch (Exception ex) { status("Couldn't write the log: " + ex.Message); }
        }

        // another program (over UDP) asked to answer a decode, as a double-click here would
        public void AnswerExternal(DecodeLine d) { answer(d); }

        public void RefreshAll() { refreshAll(); }

        private void applyFilter()
        {
            bool cq = _host.GetSetting("fcq", "0") == "1", hide = _host.GetSetting("fhide", "0") == "1";
            _activity.Filter = !cq && !hide ? (Func<DecodeRow, bool>)null :
                r => (!cq || r.Kind == RowKind.Cq || r.Kind == RowKind.ToMe) && (!hide || r.Mark != WorkedMark.WorkedBand || r.Kind == RowKind.ToMe);
        }

        // ---- ALL.TXT: every decode and transmission, in WSJT-X's format (DataFolder\ALL.TXT) ----

        internal static string line(DateTime slot, double dialHz, string rxTx, FtxMode mode, int snr, double dt, int freq, string msg)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:yyMMdd_HHmmss} {1,9:0.000} {2} {3} {4,6} {5,4:0.0} {6,4} {7}",
                slot, dialHz / 1e6, rxTx, mode, snr, dt, freq, msg);
        }

        private void allTxt(IEnumerable<string> lines)
        {
            try
            {
                string f = System.IO.Path.Combine(_host.DataFolder, "ALL.TXT");
                System.IO.File.AppendAllLines(f, lines);
            }
            catch (Exception ex) { _host.Log("ALL.TXT: " + ex.Message); }
        }

        // ---- the clock check: the median DT of the stations heard; a second or more off means the PC's clock is ----

        private readonly List<double> _dts = new List<double>();
        private double _dtMedian = double.NaN;

        private void clockCheck(List<DecodeLine> list)
        {
            foreach (DecodeLine d in list) if (!d.Ap) _dts.Add(d.Dt);
            if (_dts.Count > 60) _dts.RemoveRange(0, _dts.Count - 60);
            if (_dts.Count < 6) { _dtMedian = double.NaN; return; }
            List<double> s = _dts.OrderBy(x => x).ToList();
            _dtMedian = s[s.Count / 2];
            if (Math.Abs(_dtMedian) >= 0.6)
                status("The stations' DT averages " + _dtMedian.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) +
                       " s: your PC's clock looks off. Synchronise it (Windows Settings > Time > Sync now, or Meinberg NTP).");
        }

        // ---- worked before: cty.dat and the logs (Qso/Worked.cs) ----

        internal readonly Worked Worked = new Worked();

        // on a thread of its own (a big log takes a moment); new decodes are marked from then on
        public void LoadWorked()
        {
            string module = _host.ModuleFolder, data = _host.DataFolder, extra = _host.GetSetting("worklog", "").Trim();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string cty = System.IO.Path.Combine(module, "cty.dat");
                    if (!System.IO.File.Exists(cty)) cty = @"C:\WSJT\wsjtx\share\wsjtx\cty.dat";
                    Worked w = Worked;
                    w.Cty = Cty.Load(cty);
                    w.Clear();
                    int n = w.LoadAdif(System.IO.Path.Combine(data, "kainos-digimodes.adi"));
                    if (extra.Length > 0) n += w.LoadAdif(extra);
                    _host.Log("worked before: " + n + " QSOs, cty.dat " + w.Cty.Count + " prefixes");
                }
                catch (Exception ex) { _host.Log("worked before: " + ex.Message); }
            });
        }

        // ---- showing state ----

        private void status(string s) { _statusText = s ?? ""; _statusAt = DateTime.UtcNow; _status.Text = _statusText; }

        // set by the module while its window is open. The timer runs for the pane's whole life, so the markers are only
        // drawn while it's open: before, the next tick put them back after closing (GitHub #42).
        public bool ShowMarkers { get; set; }

        private void tick()
        {
            DateTime now = DateTime.UtcNow;
            double p = FtxModes.Period(_e.Mode);
            double t = SlotRecorder.Seconds(now);
            double into = t - Math.Floor(t / p) * p;
            _clock.Text = now.ToString("HH:mm:ss") + "  " + into.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(4) + "/" + p.ToString("0.#", CultureInfo.InvariantCulture) +
                          (double.IsNaN(_dtMedian) ? "" : "  DT" + _dtMedian.ToString("+0.0;-0.0", CultureInfo.InvariantCulture));
            _clock.ForeColor = _e.Transmitting ? _t.Tx : _t.Gold;
            if (_statusText.Length > 0 && (now - _statusAt).TotalSeconds > 20) { _statusText = ""; _status.Text = ""; }
            if (_statusText.Length == 0)
                _status.Text = !_e.Decoder.Available ? "Decoder (jt9.exe) not found: see Settings" :
                               !_e.Listening ? "Not listening" :
                               FtxModes.Experimental(_e.Mode) ? "FT2 is experimental: checked against WSJT-X Improved, not yet on the air against Decodium" :
                               _e.Mode == FtxMode.WSPR ? "WSPR: " + (_e.TxEnabled ? "beacon on, " + _e.WsprTxPercent + " % of periods, " + _e.WsprDbm + " dBm" : "listening") +
                                   (_host.GetSetting("wsprnet", "0") == "1" ? ", uploading to WSPRnet" : "") :
                               _e.Qso.Activity != Contest.None ? "Contest: " + QsoEngine.Name(_e.Qso.Activity) + (_e.Qso.MyExch.Length > 0 ? " (sending " + _e.Qso.MyExch + ")" : "") : "";
            // the markers on the panadapter: RX (gold) and TX (red) at their audio frequencies above the VFO
            if (!ShowMarkers) return;
            int rx = _e.Rx;
            string m = _host.Radio.Mode(rx);
            int sign = m == "LSB" || m == "DIGL" ? -1 : 1;
            _host.SetMarkers(rx, new[]
            {
                new KainosMarker(sign * _e.RxFreq, "RX", _t.Gold),
                new KainosMarker(sign * _e.TxFreq, "TX", _t.Tx),
            });
            _host.SetMarkers(1 - rx, null);
        }

        private void refreshAll()
        {
            _mode.Text = _e.Mode + " \u25BE";
            // WSPR has no QSOs: the QSO controls rest, and Enable Tx turns the beacon on
            bool qso = FtxModes.HasQso(_e.Mode);
            _qso.Enabled = _hold.Enabled = _second.Enabled = _auto.Enabled = qso;
            _enable.Text = qso ? "Enable Tx" : "Beacon";
            _rxSel.Text = "RX" + (_e.Rx + 1);
            _hold.On = _e.HoldTx;
            _second.Text = _e.TxSecond ? "Tx 2nd" : "Tx 1st";
            _auto.On = _e.Qso.AutoSeq;
            _enable.On = _e.TxEnabled;
            _halt.On = _e.Transmitting && !_e.Tuning;
            _tune.On = _e.Tuning;
            Bands.Band b = Bands.Of(_host.Radio.VfoHz(_e.Rx));
            _band.Text = b != null ? b.Name : "Band";
            _strip.Rx = _e.RxFreq;
            _strip.Tx = _e.TxFreq;
            _strip.BandwidthHz = (int)FtxModes.Bandwidth(_e.Mode);
            _strip.Invalidate();
            refreshQso();
        }

        private void refreshQso()
        {
            QsoEngine q = _e.Qso;
            lock (q)
            {
                q.MyCall = _host.Callsign;
                q.MyGrid = _host.Grid;
                _syncing = true;
                if (_hisCall.Text != q.HisCall && !_hisCall.Focused) _hisCall.Text = q.HisCall;
                if (_hisGrid.Text != q.HisGrid && !_hisGrid.Focused) _hisGrid.Text = q.HisGrid;
                _syncing = false;
                for (int i = 0; i < 6; i++)
                {
                    _tx[i].Text = "Tx" + (i + 1) + "  " + q.Message(i + 1).Trim();
                    _tx[i].On = q.Next == i + 1;
                }
                _tx[6].Text = "Tx7";
                _tx[6].On = q.Next == 7;
            }
        }

        // ---- settings ----

        private void loadSettings()
        {
            Func<string, string, string> g = (k, d) => _host.GetSetting(k, d);
            Func<string, int, int> gi = (k, d) => { int v; return int.TryParse(g(k, ""), out v) ? v : d; };
            Func<string, double, double> gd = (k, d) => { double v; return double.TryParse(g(k, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : d; };
            FtxMode saved;
            _e.Mode = Enum.TryParse(g("mode", "FT8"), out saved) ? saved : FtxMode.FT8;
            _e.Rx = gi("rx", 0) == 1 ? 1 : 0;
            _e.RxFreq = Math.Max(100, Math.Min(2900, gi("rxf", 1500)));
            _e.TxFreq = Math.Max(100, Math.Min(2900, gi("txf", 1500)));
            _e.HoldTx = g("hold", "1") == "1";
            _e.TxSecond = g("second", "0") == "1";
            _e.Qso.AutoSeq = g("auto", "1") == "1";
            _e.LevelDb = Math.Max(-40, Math.Min(0, gd("level", -10)));
            _e.TxLeadMs = Math.Max(0, Math.Min(1000, gd("lead", 0)));
            _e.RxLatencyMs = Math.Max(0, Math.Min(1000, gd("rxlat", 0)));
            _e.Decoder.Depth = Math.Max(1, Math.Min(3, gi("depth", 3)));
            Bands.Load(g("bands", ""));
            _e.Qso.Activity = (Contest)gi("contest", 0);
            _e.Qso.MyExchange = g("exchange", "");
            _e.Qso.Serial = Math.Max(1, gi("serial", 1));
            int dbm = gi("wsprdbm", 37);
            _e.WsprDbm = Array.IndexOf(OtherModes.WsprPowers, dbm) >= 0 ? dbm : 37;
            _e.WsprTxPercent = Math.Max(0, Math.Min(100, gi("wsprpct", 20)));
            _e.Qso.Jt = _e.Mode == FtxMode.JT65 || _e.Mode == FtxMode.JT9;
            applyFilter();
        }

        private void save()
        {
            _host.SetSetting("mode", _e.Mode.ToString());
            _host.SetSetting("rx", _e.Rx.ToString());
            _host.SetSetting("rxf", _e.RxFreq.ToString());
            _host.SetSetting("txf", _e.TxFreq.ToString());
            _host.SetSetting("hold", _e.HoldTx ? "1" : "0");
            _host.SetSetting("second", _e.TxSecond ? "1" : "0");
            _host.SetSetting("auto", _e.Qso.AutoSeq ? "1" : "0");
        }

        private void showSettings()
        {
            using (SettingsForm f = new SettingsForm(_host, _e))
            {
                _t.Style(f);
                if (f.ShowDialog(FindForm()) == DialogResult.OK) { refreshAll(); SettingsChanged?.Invoke(); }
            }
        }
    }
}
