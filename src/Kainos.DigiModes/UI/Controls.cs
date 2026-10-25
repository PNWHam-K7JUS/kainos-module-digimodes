/*  Controls.cs

Small controls drawn in the Kainos look (IKainosTheme): a push button, a list of decodes, and the audio frequency
strip where the receive and transmit frequencies are set.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Kainos.DigiModes.Decode;
using Kainos.Modules;

namespace Kainos.DigiModes.UI
{
    internal class KButton : Control
    {
        private readonly IKainosTheme _t;
        private bool _on, _hover;
        public float FontPx = 11;
        public bool Toggle;                 // a click flips On

        public KButton(IKainosTheme t, string text)
        {
            _t = t;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg;
            Cursor = Cursors.Hand;
        }

        public bool On { get { return _on; } set { if (_on != value) { _on = value; Invalidate(); } } }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            _t.DrawButton(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Text, _on, Enabled, _hover, _t.S(FontPx));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { if (Toggle) On = !On; base.OnClick(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    }

    internal enum RowKind { Normal, Cq, ToMe, Mine, Separator }

    internal class DecodeRow
    {
        public DecodeLine Decode;       // null for my transmissions and separators
        public string Text;
        public RowKind Kind;
        public Qso.WorkedMark Mark;     // how new the sender is (Qso/Worked.cs)
        public string Note;             // the sender's country, right-aligned
    }

    // An owner-drawn list of decodes, newest at the bottom; it follows the bottom unless scrolled up
    internal class DecodeList : Control
    {
        private readonly IKainosTheme _t;
        private readonly List<DecodeRow> _all = new List<DecodeRow>();     // every row
        private List<DecodeRow> _rows = new List<DecodeRow>();             // the rows shown (those the filter lets through)
        private int _top;
        private bool _follow = true;
        private int _hoverRow = -1;
        private int _dragFrom = -1, _dragTop;
        public string Title = "";
        public int MaxRows = 600;

        public event Action<DecodeLine> RowDoubleClicked;

        // ---- filtering, with toggle chips in the heading ("CQ only", "Hide worked") ----

        internal class Chip { public string Text; public bool On; public Action<bool> Toggled; public RectangleF Bounds; }
        public readonly List<Chip> Chips = new List<Chip>();
        private Func<DecodeRow, bool> _filter;

        public Func<DecodeRow, bool> Filter { get { return _filter; } set { _filter = value; rebuild(); } }
        internal int Shown { get { return _rows.Count; } }

        private bool shows(DecodeRow r) { return r.Kind == RowKind.Separator || r.Kind == RowKind.Mine || _filter == null || _filter(r); }

        private void rebuild()
        {
            List<DecodeRow> v = new List<DecodeRow>();
            foreach (DecodeRow r in _all)
            {
                if (!shows(r)) continue;
                // a heading with nothing under it (everything filtered out) gives way to the next one
                if (r.Kind == RowKind.Separator && v.Count > 0 && v[v.Count - 1].Kind == RowKind.Separator) v[v.Count - 1] = r;
                else v.Add(r);
            }
            _rows = v;
            if (_follow) _top = maxTop(); else _top = Math.Min(_top, maxTop());
            Invalidate();
        }

        public DecodeList(IKainosTheme t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = t.Panel;
        }

        private int rowH { get { return _t.S(17); } }
        private int headH { get { return _t.S(18); } }
        private int barRoom { get { return _t.S(10); } }
        private int visibleRows { get { return Math.Max(1, (Height - headH) / rowH); } }
        private int maxTop() { return Math.Max(0, _rows.Count - visibleRows); }

        public void Add(DecodeRow r)
        {
            _all.Add(r);
            if (_all.Count > MaxRows) { _all.RemoveRange(0, _all.Count - MaxRows); rebuild(); return; }
            if (shows(r))
            {
                if (r.Kind == RowKind.Separator && _rows.Count > 0 && _rows[_rows.Count - 1].Kind == RowKind.Separator) _rows[_rows.Count - 1] = r;
                else _rows.Add(r);
            }
            if (_follow) _top = maxTop();
            Invalidate();
        }

        public void Separator(string label)
        {
            if (_all.Count > 0 && _all[_all.Count - 1].Kind == RowKind.Separator) _all[_all.Count - 1].Text = label;
            else Add(new DecodeRow { Kind = RowKind.Separator, Text = label });
            Invalidate();
        }

        public void ClearRows() { _all.Clear(); _rows.Clear(); _top = 0; _follow = true; Invalidate(); }

        private void scrollTo(int top)
        {
            _top = Math.Max(0, Math.Min(maxTop(), top));
            _follow = _top >= maxTop();
            Invalidate();
        }

        // the slim Kainos scroll bar on the right (as Setup's page list): only while there's more than fits
        private Rectangle thumb()
        {
            int vis = visibleRows;
            if (_rows.Count <= vis) return Rectangle.Empty;
            int trackTop = headH + _t.S(3), track = Height - trackTop - _t.S(3);
            int th = Math.Max(_t.S(24), track * vis / _rows.Count);
            int ty = trackTop + (track - th) * _top / Math.Max(1, maxTop());
            return new Rectangle(Width - _t.S(7), ty, _t.S(4), th);
        }

        protected override void OnResize(EventArgs e) { if (_follow) _top = maxTop(); else _top = Math.Min(_top, maxTop()); base.OnResize(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scrollTo(_top - Math.Sign(e.Delta) * 3);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            foreach (Chip c in Chips)
                if (c.Bounds.Contains(e.X, e.Y)) { c.On = !c.On; c.Toggled?.Invoke(c.On); Invalidate(); return; }
            Rectangle th = thumb();
            if (!th.IsEmpty && e.X >= Width - barRoom)
            {
                if (th.Contains(e.X, e.Y) || (e.X >= th.X - 3 && e.Y >= th.Top && e.Y <= th.Bottom)) { _dragFrom = e.Y; _dragTop = _top; Capture = true; }
                else scrollTo(_top + (e.Y < th.Top ? -1 : 1) * Math.Max(1, visibleRows - 1));       // page up / down
                Invalidate();
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragFrom >= 0) { _dragFrom = -1; Capture = false; Invalidate(); }
            base.OnMouseUp(e);
        }

        private static string markText(Qso.WorkedMark m)
        {
            switch (m)
            {
                case Qso.WorkedMark.NewDxcc: return "NEW DXCC";
                case Qso.WorkedMark.NewDxccBand: return "NEW BAND";
                case Qso.WorkedMark.NewGrid: return "NEW GRID";
                case Qso.WorkedMark.NewGridBand: return "GRID BAND";
                default: return null;
            }
        }

        private Color markColour(Qso.WorkedMark m)
        {
            return m == Qso.WorkedMark.NewDxcc ? Color.FromArgb(0xff, 0x8a, 0xe6) :        // a magenta that stands out from gold and red
                   m == Qso.WorkedMark.NewDxccBand ? _t.Violet : _t.Ice;
        }

        private int rowAt(int y)
        {
            if (y < headH) return -1;
            int i = _top + (y - headH) / rowH;
            return i >= 0 && i < _rows.Count ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragFrom >= 0)
            {
                Rectangle th = thumb();
                int track = Height - headH - _t.S(6) - th.Height;
                if (track > 0) scrollTo(_dragTop + (e.Y - _dragFrom) * maxTop() / track);
                return;
            }
            int r = e.X >= Width - barRoom ? -1 : rowAt(e.Y);
            if (r != _hoverRow) { _hoverRow = r; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hoverRow = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int r = rowAt(e.Y);
            if (r >= 0 && _rows[r].Decode != null) RowDoubleClicked?.Invoke(_rows[r].Decode);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_t.Panel);
            int w = Width - barRoom;
            using (Font head = _t.Font(_t.S(10.5f), true))
            using (Font mono = new Font("Consolas", _t.S(12.5f), FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush dim = new SolidBrush(_t.Dim))
            {
                using (Brush hb = new SolidBrush(_t.Surface)) g.FillRectangle(hb, 0, 0, w, headH);
                g.DrawString(Title, head, dim, _t.S(6), _t.S(3));
                // the filter chips at the heading's right
                float cx = w - _t.S(4);
                for (int ci = Chips.Count - 1; ci >= 0; ci--)
                {
                    Chip c = Chips[ci];
                    SizeF cs = g.MeasureString(c.Text, head);
                    c.Bounds = new RectangleF(cx - cs.Width - _t.S(8), _t.S(2), cs.Width + _t.S(8), headH - _t.S(4));
                    using (Brush cb = new SolidBrush(c.On ? _t.Selected : _t.Raised)) g.FillRectangle(cb, c.Bounds);
                    using (Pen cp = new Pen(c.On ? _t.Gold : _t.Line)) g.DrawRectangle(cp, c.Bounds.X, c.Bounds.Y, c.Bounds.Width, c.Bounds.Height);
                    using (Brush ct = new SolidBrush(c.On ? _t.Gold : _t.Dim)) g.DrawString(c.Text, head, ct, c.Bounds.X + _t.S(4), c.Bounds.Y + (c.Bounds.Height - cs.Height) / 2);
                    cx = c.Bounds.X - _t.S(4);
                }
                using (Pen p = new Pen(_t.Line)) g.DrawLine(p, 0, headH - 1, w, headH - 1);
                int y = headH;
                for (int i = _top; i < _rows.Count && y < Height; i++, y += rowH)
                {
                    DecodeRow r = _rows[i];
                    if (r.Kind == RowKind.Separator)
                    {
                        using (Pen p = new Pen(_t.Line)) g.DrawLine(p, _t.S(4), y + rowH / 2, w - _t.S(4), y + rowH / 2);
                        if (!string.IsNullOrEmpty(r.Text))
                        {
                            SizeF sz = g.MeasureString(r.Text, head);
                            float x = w - sz.Width - _t.S(10);
                            using (Brush bb = new SolidBrush(_t.Panel)) g.FillRectangle(bb, x - 3, y, sz.Width + 6, rowH);
                            using (Brush fb = new SolidBrush(_t.Faint)) g.DrawString(r.Text, head, fb, x, y + 1);
                        }
                        continue;
                    }
                    Color back = r.Kind == RowKind.Cq ? Color.FromArgb(60, _t.Gold) : r.Kind == RowKind.ToMe ? Color.FromArgb(90, _t.Tx) :
                                 r.Kind == RowKind.Mine ? Color.FromArgb(55, _t.Violet) : Color.Empty;
                    if (i == _hoverRow) back = back.IsEmpty ? _t.Raised : Color.FromArgb(Math.Min(255, back.A + 50), back);
                    if (!back.IsEmpty) using (Brush b = new SolidBrush(back)) g.FillRectangle(b, 0, y, w, rowH);
                    Color fore = r.Kind == RowKind.ToMe ? Color.White : r.Kind == RowKind.Cq ? _t.Gold : r.Kind == RowKind.Mine ? _t.Violet : _t.Text;
                    if (r.Decode != null && r.Decode.Ap) fore = Color.FromArgb(200, fore);
                    if (r.Mark == Qso.WorkedMark.WorkedBand && r.Kind != RowKind.ToMe) fore = _t.Faint;          // already worked on this band
                    // how new the sender is, and its country, at the right
                    string tag = markText(r.Mark);
                    float right = w - _t.S(4);
                    if (!string.IsNullOrEmpty(r.Note) || tag != null)
                    {
                        using (Font small = _t.Font(_t.S(10.5f), false))
                        using (Font bold = _t.Font(_t.S(10f), true))
                        {
                            if (!string.IsNullOrEmpty(r.Note))
                            {
                                SizeF ns = g.MeasureString(r.Note, small);
                                right -= ns.Width;
                                using (Brush nb = new SolidBrush(r.Mark == Qso.WorkedMark.WorkedBand ? _t.Faint : _t.Dim)) g.DrawString(r.Note, small, nb, right, y + 2);
                            }
                            if (tag != null)
                            {
                                SizeF ts = g.MeasureString(tag, bold);
                                RectangleF pill = new RectangleF(right - ts.Width - _t.S(8), y + 2, ts.Width + _t.S(4), rowH - 4);
                                Color pc = markColour(r.Mark);
                                using (Brush pb = new SolidBrush(Color.FromArgb(70, pc))) g.FillRectangle(pb, pill);
                                using (Brush tb2 = new SolidBrush(pc)) g.DrawString(tag, bold, tb2, pill.X + _t.S(2), y + 2);
                                right = pill.X;
                            }
                        }
                    }
                    g.SetClip(new RectangleF(0, y, Math.Max(0, right - _t.S(4)), rowH));
                    using (Brush fb = new SolidBrush(fore)) g.DrawString(r.Text, mono, fb, _t.S(4), y + 1);
                    g.ResetClip();
                }
            }
            Rectangle tb = thumb();
            if (!tb.IsEmpty)
                using (Brush b = new SolidBrush(_dragFrom >= 0 ? _t.Dim : _t.Line)) g.FillRectangle(b, tb);
        }
    }

    // 0 - 3000 Hz of audio (what's inside the DIGU passband): a waterfall of the receiver's audio (newest at the top),
    // and along the top each station decoded in the last period as a block at its audio frequency, as wide as its
    // signal, labelled with its callsign (gold: calling CQ). RX is the gold line, TX the red dashed one. Left click sets
    // RX, right click (or Shift + click) sets TX; hovering over a block shows its message.
    internal class FreqStrip : Control
    {
        private readonly IKainosTheme _t;
        public int Rx = 1500, Tx = 1500, BandwidthHz = 50;
        public int MaxHz = 3000;
        public double PeriodSeconds = 15;       // a line across the waterfall where each period starts (GitHub #45)
        private List<DecodeLine> _last = new List<DecodeLine>();
        private readonly ToolTip _tip = new ToolTip { InitialDelay = 200, ReshowDelay = 100, ShowAlways = true };     // always: in a KainosCrispHost its window is never the active one
        private string _tipText = "";
        private const string Hint = "Waterfall of the audio (Hz) in your DIGU passband; along the top, the stations heard in the last period (gold = calling CQ).\r\n" +
                                    "Left-click: receive here (RX, gold line).   Right-click or Shift+click: transmit here (TX, red line).";

        public event Action<int> RxClicked, TxClicked;

        public FreqStrip(IKainosTheme t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Surface;
            Cursor = Cursors.Cross;
            _tip.SetToolTip(this, Hint);
        }

        protected override void Dispose(bool disposing) { if (disposing) { _tip.Dispose(); if (_wf != null) _wf.Dispose(); } base.Dispose(disposing); }

        public void SetDecodes(List<DecodeLine> d) { _last = d ?? new List<DecodeLine>(); Invalidate(); }

        private float x(double hz) { return (float)(hz / MaxHz * (Width - 1)); }

        // In a KainosCrispHost the strip is at the screen's own resolution: sizes are in screen pixels, _k per design pixel
        private float _k = 1;
        private int S(float designPx) { return (int)Math.Round(_t.S(designPx) * _k); }
        private float F(float designPx) { return _t.S(designPx) * _k; }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _k = KainosCrispHost.DpiScale(this); }
        protected override void OnSizeChanged(EventArgs e) { _k = KainosCrispHost.DpiScale(this); base.OnSizeChanged(e); }

        // the station a decode is from: the call after CQ (and any CQ modifier), else the second word
        internal static string Sender(string msg)
        {
            string[] w = (msg ?? "").Split(' ');
            if (w.Length >= 2 && w[0] == "CQ") return w.Length >= 3 && w[1].Length <= 4 && !w[1].Any(char.IsDigit) ? w[2] : w[1];
            return w.Length >= 2 ? w[1].Trim('<', '>') : msg;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int hz = (int)Math.Round(Math.Max(100, Math.Min(MaxHz - 100, e.X / (double)Math.Max(1, Width - 1) * MaxHz)) / 5.0) * 5;
            if (e.Button == MouseButtons.Right || (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) != 0)) TxClicked?.Invoke(hz);
            else if (e.Button == MouseButtons.Left) RxClicked?.Invoke(hz);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            double hz = e.X / (double)Math.Max(1, Width - 1) * MaxHz;
            DecodeLine d = _last.FirstOrDefault(l => hz >= l.Freq && hz <= l.Freq + BandwidthHz);
            string t = d != null ? d.Freq + " Hz  " + d.Snr.ToString("+0;-0") + " dB   " + d.Message + "\r\nLeft-click: RX here.  Right-click: TX here." : Hint;
            if (t != _tipText) { _tipText = t; _tip.SetToolTip(this, t); }
        }

        // ---- the waterfall: newest line at the top, 0 - 3000 Hz across, scrolled a row a line ----

        private const int WfRows = 1200;     // a row a screen pixel: enough for a tall pane at 150 %
        private readonly int _wfCols = SpectrumMaker.Bins;
        private int[] _px;
        private Bitmap _wf;
        private bool _wfDirty;
        private int _wfLines;
        private float _floor = float.NaN;
        public float RangeDb = 30;          // the colours span this much above the noise floor
        private static int[] _palette;

        private int labelH { get { return S(15); } }
        private int scaleH { get { return S(11); } }
        private Rectangle wfRect { get { return new Rectangle(0, labelH, Width, Math.Max(0, Height - labelH - scaleH)); } }

        // ---- callsigns on their traces: each decode labelled where (frequency) and when (row) it was heard ----

        private class TraceLabel { public double T; public int Hz; public string Text; public bool Cq; }
        private readonly List<TraceLabel> _labels = new List<TraceLabel>();
        private readonly double[] _rowT = new double[WfRows];      // the UTC time (s) of each row, newest first

        // a period's decodes: each label at the middle of its signal (the period's start + 0.5 s + half the signal, + DT)
        public void AddLabels(DateTime slotUtc, Ftx.FtxMode mode, IEnumerable<DecodeLine> list)
        {
            double half = Ftx.FtxModes.Duration(mode) / 2;
            double s0 = Decode.SlotRecorder.Seconds(slotUtc) + Ftx.FtxModes.TxStart(mode) + half;
            foreach (DecodeLine d in list)
                _labels.Add(new TraceLabel { T = s0 + d.Dt, Hz = d.Freq, Text = Sender(d.Message), Cq = d.Message.StartsWith("CQ ") });
            if (_labels.Count > 400) _labels.RemoveRange(0, _labels.Count - 400);
            Invalidate();      // all of it: a rectangle passed from Kainos's (DPI-unaware) thread would be scaled down in a KainosCrispHost
        }

        // the row showing time t, or -1 when it has scrolled off (or isn't in yet)
        private int rowOf(double t, int rows)
        {
            if (_wfLines == 0 || rows <= 0) return -1;
            int have = Math.Min(Math.Min(_wfLines, WfRows), rows);
            if (t > _rowT[0] + 0.2 || t < _rowT[have - 1] - 0.2) return -1;
            int best = 0;
            double bd = double.MaxValue;
            for (int r = 0; r < have; r++) { double dd = Math.Abs(_rowT[r] - t); if (dd < bd) { bd = dd; best = r; } }
            return best;
        }

        // a spectrum line (dB, 0 - 3000 Hz) and the UTC time of its middle (s), on the UI thread
        public void AddLine(float[] db, double t)
        {
            if (db == null || db.Length < _wfCols) return;
            if (_px == null) _px = new int[_wfCols * WfRows];
            Array.Copy(_rowT, 0, _rowT, 1, WfRows - 1);
            _rowT[0] = t;
            if (_palette == null) _palette = makePalette(_t);
            // the noise floor: a low percentile of the line, followed slowly (quickly for the first lines)
            float[] sorted = (float[])db.Clone();
            Array.Sort(sorted);
            float f = sorted[sorted.Length / 5];
            _floor = float.IsNaN(_floor) ? f : _floor + (f - _floor) * (_wfLines < 10 ? 0.5f : 0.05f);
            _wfLines++;
            Array.Copy(_px, 0, _px, _wfCols, _wfCols * (WfRows - 1));
            for (int i = 0; i < _wfCols; i++)
            {
                float v = (db[i] - _floor + 3) / RangeDb;
                int k = (int)(Math.Max(0, Math.Min(1, v)) * 255);
                _px[i] = _palette[k];
            }
            _wfDirty = true;
            Invalidate();      // all of it: a rectangle passed from Kainos's (DPI-unaware) thread would be scaled down in a KainosCrispHost
        }

        public void ClearWaterfall() { if (_px != null) Array.Clear(_px, 0, _px.Length); _floor = float.NaN; _wfLines = 0; _wfDirty = true; Invalidate(); }

        // the Kainos colours: navy, deep blue, ice, gold, white
        private static int[] makePalette(IKainosTheme t)
        {
            // the noise floor sits in the first third: a gentle navy-to-blue ramp, so it reads as an even colour, not grain
            Color[] stops = { t.Bg, Color.FromArgb(10, 30, 62), Color.FromArgb(18, 60, 118), t.Ice, t.Gold, Color.White };
            double[] at = { 0, 0.22, 0.42, 0.66, 0.87, 1 };
            int[] p = new int[256];
            for (int i = 0; i < 256; i++)
            {
                double v = i / 255.0;
                int s = 0;
                while (s < stops.Length - 2 && v > at[s + 1]) s++;
                double u = (v - at[s]) / (at[s + 1] - at[s]);
                Color a = stops[s], b = stops[s + 1];
                int r = (int)(a.R + (b.R - a.R) * u), g = (int)(a.G + (b.G - a.G) * u), bl = (int)(a.B + (b.B - a.B) * u);
                p[i] = unchecked((int)0xff000000) | (r << 16) | (g << 8) | bl;
            }
            return p;
        }

        private void drawWaterfall(Graphics g)
        {
            Rectangle r = wfRect;
            if (r.Height <= 0) return;
            using (Brush bg = new SolidBrush(_t.Bg)) g.FillRectangle(bg, r);
            if (_px == null) return;
            if (_wf == null) _wf = new Bitmap(_wfCols, WfRows, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            if (_wfDirty)
            {
                System.Drawing.Imaging.BitmapData bd = _wf.LockBits(new Rectangle(0, 0, _wfCols, WfRows), System.Drawing.Imaging.ImageLockMode.WriteOnly, _wf.PixelFormat);
                System.Runtime.InteropServices.Marshal.Copy(_px, 0, bd.Scan0, _px.Length);
                _wf.UnlockBits(bd);
                _wfDirty = false;
            }
            // one row a pixel; across, the 1024 bins scaled smoothly to the width (no blocks), edges not faded
            int rows = Math.Min(Math.Min(WfRows, _wfLines), r.Height);     // only the rows that have arrived (the rest stays navy)
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (rows > 0)
            using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(_wf, new Rectangle(r.X, r.Y, r.Width, rows), 0, 0, _wfCols, rows, GraphicsUnit.Pixel, ia);
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.InterpolationMode = InterpolationMode.Default;

            // where each period starts (between the rows either side of it), with its UTC time at the left
            if (PeriodSeconds > 0)
            using (Pen pen = new Pen(Color.FromArgb(110, _t.Ice), 1) { DashStyle = DashStyle.Dash })
            using (Font tf = _t.Font(F(8.5f), false))
            using (Brush tb = new SolidBrush(Color.FromArgb(190, _t.Ice)))
            using (Brush bg = new SolidBrush(Color.FromArgb(150, _t.Bg)))
            {
                for (int row = 0; row + 1 < rows; row++)
                {
                    double newer = Math.Floor(_rowT[row] / PeriodSeconds), older = Math.Floor(_rowT[row + 1] / PeriodSeconds);
                    if (newer == older || _rowT[row + 1] <= 0) continue;
                    float ly = r.Y + row + 0.5f;
                    g.DrawLine(pen, r.X, ly, r.Right, ly);
                    string at = DateTime.MinValue.AddSeconds(newer * PeriodSeconds % 86400).ToString("HH:mm:ss");
                    SizeF sz = g.MeasureString(at, tf);
                    if (ly + 1 + sz.Height > r.Bottom) continue;
                    g.FillRectangle(bg, r.X + 2 * _k, ly + 1, sz.Width, sz.Height);
                    g.DrawString(at, tf, tb, r.X + 2 * _k, ly + 1);
                }
            }

            // the callsigns on their traces: a small tag at the signal's left edge, at the row of its middle
            using (Font lf = _t.Font(F(9.5f), true))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                foreach (TraceLabel l in _labels)
                {
                    int row = rowOf(l.T, rows);
                    if (row < 0) continue;
                    SizeF sz = g.MeasureString(l.Text, lf);
                    float lx = x(l.Hz), ly = r.Y + row - sz.Height / 2;
                    if (ly < r.Y || ly + sz.Height > r.Bottom) continue;
                    using (Brush bg = new SolidBrush(Color.FromArgb(170, _t.Bg))) g.FillRectangle(bg, lx, ly + _k, sz.Width, sz.Height - 2 * _k);
                    using (Brush tb = new SolidBrush(l.Cq ? _t.Gold : Color.White)) g.DrawString(l.Text, lf, tb, lx, ly);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_t.Surface);
            drawWaterfall(g);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int h = Height;
            using (Font f = _t.Font(F(9.5f), false))
            using (Font call = _t.Font(F(9.5f), true))
            using (Brush fb = new SolidBrush(_t.Faint))
            using (Pen grid = new Pen(_t.Line))
            {
                // the scale along the bottom
                g.DrawString("AUDIO Hz", f, fb, 2 * _k, h - scaleH - _k);
                for (int hz = 500; hz < MaxHz; hz += 500)
                {
                    float xx = x(hz);
                    g.DrawLine(grid, xx, h - S(4), xx, h);
                    g.DrawString(hz.ToString(), f, fb, xx + 2 * _k, h - scaleH - _k);
                }
                // the stations of the last period along the top, with their callsigns where there's room
                float bw = Math.Max(2 * _k, x(BandwidthHz));
                foreach (DecodeLine d in _last.OrderBy(l => l.Freq))
                {
                    bool cq = d.Message.StartsWith("CQ ");
                    Color c = cq ? _t.Gold : _t.Ice;
                    float bx = x(d.Freq);
                    using (Brush b = new SolidBrush(Color.FromArgb(150, c))) g.FillRectangle(b, bx, _k, bw, labelH - 2 * _k);
                    string who = Sender(d.Message);
                    SizeF sz = g.MeasureString(who, call);
                    if (sz.Width <= bw + S(30))
                        using (Brush tb = new SolidBrush(cq ? _t.Gold : _t.Text)) g.DrawString(who, call, tb, bx + _k, (labelH - sz.Height) / 2);
                }
                // RX and TX, tagged at the top
                using (Brush rb = new SolidBrush(Color.FromArgb(60, _t.Gold))) g.FillRectangle(rb, x(Rx), 0, bw, h);
                using (Pen rp = new Pen(_t.Gold, 2 * _k)) g.DrawLine(rp, x(Rx), 0, x(Rx), h);
                using (Pen tp = new Pen(_t.Tx, 2 * _k) { DashStyle = DashStyle.Dash }) g.DrawLine(tp, x(Tx), 0, x(Tx), h);
                tag(g, call, "RX", x(Rx), _t.Gold, Rx <= Tx);
                tag(g, call, "TX", x(Tx), _t.Tx, Tx < Rx);
            }
        }

        // a small label beside a line: on its left when `left` (so RX and TX on the same frequency don't overlap)
        private void tag(Graphics g, Font f, string text, float lx, Color c, bool left)
        {
            SizeF sz = g.MeasureString(text, f);
            float tx = left ? lx - sz.Width - 2 * _k : lx + 3 * _k;
            if (tx < 0) tx = lx + 3 * _k;
            if (tx + sz.Width > Width) tx = lx - sz.Width - 2 * _k;
            using (Brush bg = new SolidBrush(_t.Surface)) g.FillRectangle(bg, tx, 0, sz.Width, sz.Height - 2 * _k);
            using (Brush b = new SolidBrush(c)) g.DrawString(text, f, b, tx, -_k);
        }
    }
}
