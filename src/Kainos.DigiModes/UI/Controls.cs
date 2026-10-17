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
    }

    // An owner-drawn list of decodes, newest at the bottom; it follows the bottom unless scrolled up
    internal class DecodeList : Control
    {
        private readonly IKainosTheme _t;
        private readonly List<DecodeRow> _rows = new List<DecodeRow>();
        private int _top;
        private bool _follow = true;
        private int _hoverRow = -1;
        private int _dragFrom = -1, _dragTop;
        public string Title = "";
        public int MaxRows = 600;

        public event Action<DecodeLine> RowDoubleClicked;

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
            _rows.Add(r);
            if (_rows.Count > MaxRows) { _rows.RemoveRange(0, _rows.Count - MaxRows); _top = Math.Max(0, _top - 1); }
            if (_follow) _top = maxTop();
            Invalidate();
        }

        public void Separator(string label)
        {
            if (_rows.Count > 0 && _rows[_rows.Count - 1].Kind == RowKind.Separator) _rows[_rows.Count - 1].Text = label;
            else Add(new DecodeRow { Kind = RowKind.Separator, Text = label });
            Invalidate();
        }

        public void ClearRows() { _rows.Clear(); _top = 0; _follow = true; Invalidate(); }

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
                    using (Brush fb = new SolidBrush(fore)) g.DrawString(r.Text, mono, fb, _t.S(4), y + 1);
                }
            }
            Rectangle tb = thumb();
            if (!tb.IsEmpty)
                using (Brush b = new SolidBrush(_dragFrom >= 0 ? _t.Dim : _t.Line)) g.FillRectangle(b, tb);
        }
    }

    // 0 - 3000 Hz of audio: the decodes of the last period as ticks, RX (gold) and TX (red) frequencies.
    // Left click sets RX, right click (or Shift + click) sets TX.
    internal class FreqStrip : Control
    {
        private readonly IKainosTheme _t;
        public int Rx = 1500, Tx = 1500, BandwidthHz = 50;
        public int MaxHz = 3000;
        private List<DecodeLine> _last = new List<DecodeLine>();

        public event Action<int> RxClicked, TxClicked;

        public FreqStrip(IKainosTheme t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Surface;
            Cursor = Cursors.Cross;
        }

        public void SetDecodes(List<DecodeLine> d) { _last = d ?? new List<DecodeLine>(); Invalidate(); }

        private float x(double hz) { return (float)(hz / MaxHz * (Width - 1)); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int hz = (int)Math.Round(Math.Max(100, Math.Min(MaxHz - 100, e.X / (double)Math.Max(1, Width - 1) * MaxHz)) / 5.0) * 5;
            if (e.Button == MouseButtons.Right || (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) != 0)) TxClicked?.Invoke(hz);
            else if (e.Button == MouseButtons.Left) RxClicked?.Invoke(hz);
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_t.Surface);
            int h = Height;
            using (Font f = _t.Font(_t.S(9.5f), false))
            using (Brush fb = new SolidBrush(_t.Faint))
            using (Pen grid = new Pen(_t.Line))
            {
                for (int hz = 500; hz < MaxHz; hz += 500)
                {
                    float xx = x(hz);
                    g.DrawLine(grid, xx, h - _t.S(5), xx, h);
                    g.DrawString(hz.ToString(), f, fb, xx + 2, h - _t.S(13));
                }
            }
            foreach (DecodeLine d in _last)
            {
                Color c = d.Message.StartsWith("CQ ") ? _t.Gold : _t.Ice;
                using (Brush b = new SolidBrush(Color.FromArgb(150, c))) g.FillRectangle(b, x(d.Freq), 2, Math.Max(2, x(BandwidthHz)), h - _t.S(14));
            }
            using (Brush rb = new SolidBrush(Color.FromArgb(70, _t.Gold))) g.FillRectangle(rb, x(Rx), 0, Math.Max(2, x(BandwidthHz)), h);
            using (Pen rp = new Pen(_t.Gold, 2)) g.DrawLine(rp, x(Rx), 0, x(Rx), h);
            using (Pen tp = new Pen(_t.Tx, 2) { DashStyle = DashStyle.Dash }) g.DrawLine(tp, x(Tx), 0, x(Tx), h);
        }
    }
}
