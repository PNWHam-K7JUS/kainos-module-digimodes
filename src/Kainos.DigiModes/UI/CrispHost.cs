/*  CrispHost.cs

Shows a control at the screen's own resolution although Kainos is not DPI-aware. At 125 % / 150 % Windows draws
Kainos at 100 % and stretches the picture, which blurs fine detail such as the waterfall. A per-monitor-aware child
window can't live inside a DPI-unaware one (Windows refuses, even with SetThreadDpiHostingBehavior MIXED), but a
separate top-level window can be per-monitor aware. So the content goes in a borderless window of its own, owned by
the pane's form and kept exactly over this placeholder; Windows doesn't stretch it.

The content then works in physical pixels: its ClientSize, mouse positions and drawing are in screen pixels, so it
multiplies its design sizes by DpiScale(this). At 100 %, on Windows before 10 1607, or if Kainos is ever made
DPI-aware, there's nothing to gain and the content simply sits in the placeholder (DpiScale is then 1).
(No OS version check: without a Windows 10 manifest .NET reports 6.2; the missing entry points tell instead.)

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kainos.DigiModes.UI
{
    internal class CrispHost : Control
    {
        private readonly Control _content;
        private CrispWindow _window;                // null when the content sits in the placeholder
        private readonly Timer _poll = new Timer { Interval = 100 };
        private readonly List<Control> _chain = new List<Control>();    // the ancestors whose moves move us

        public CrispHost(Control content, bool allowed)
        {
            _content = content;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            NotCrispBecause = "not needed (100 %, or Kainos is DPI-aware)";
            if (!allowed) NotCrispBecause = "turned off in Settings";
            else if (Wanted)
            {
                try { _window = new CrispWindow(this, content); NotCrispBecause = null; }
                catch (Exception ex) { _window = null; NotCrispBecause = ex.Message; }
            }
            if (_window == null) { content.Dock = DockStyle.Fill; Controls.Add(content); }
            else
            {
                _poll.Tick += (s, e) => Track();
                _poll.Start();
            }
        }

        public bool Crisp { get { return _window != null; } }
        public Control Content { get { return _content; } }
        public string NotCrispBecause { get; private set; }
        // for the self-test's log
        internal string Describe()
        {
            Func<IntPtr, string> d = h => "aw " + GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(h)) + " dpi " + GetDpiForWindow(h);
            return "placeholder " + d(Handle) + ", content " + (_content.IsHandleCreated ? d(_content.Handle) : "-") + (_window != null ? ", window " + d(_window.Handle) + " visible " + IsWindowVisible(_window.Handle) + " bounds " + _window.Bounds : "");
        }

        // Worth doing: the screen isn't at 100 % and this process (so Kainos) is DPI-unaware
        private static bool Wanted
        {
            get
            {
                try
                {
                    if (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) != 0) return false;
                    IntPtr old = SetThreadDpiAwarenessContext(PerMonitorV2);
                    if (old == IntPtr.Zero) return false;
                    uint dpi;
                    try { dpi = GetDpiForSystem(); }
                    finally { SetThreadDpiAwarenessContext(old); }
                    return dpi > 96;
                }
                catch (EntryPointNotFoundException) { return false; }
            }
        }

        // The content's pixels per design pixel: the screen's scale when it's crisp, else 1
        public static float DpiScale(Control c)
        {
            if (c == null || !c.IsHandleCreated) return 1f;
            try { uint d = GetDpiForWindow(c.Handle); return d > 0 ? d / 96f : 1f; }
            catch (EntryPointNotFoundException) { return 1f; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (Brush b = new SolidBrush(_content.BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Track(); }
        protected override void OnHandleDestroyed(EventArgs e) { if (_window != null && !_window.IsDisposed) hideWindow(); base.OnHandleDestroyed(e); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Track(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Track(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); Track(); }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); hookChain(); Track(); }

        // follow every ancestor's moves and resizes (the pane is moved by Kainos, the form by the user)
        private void hookChain()
        {
            if (_window == null) return;
            foreach (Control c in _chain) { c.LocationChanged -= chainMoved; c.SizeChanged -= chainMoved; c.VisibleChanged -= chainMoved; c.ParentChanged -= chainParent; }
            _chain.Clear();
            for (Control c = Parent; c != null; c = c.Parent)
            {
                c.LocationChanged += chainMoved; c.SizeChanged += chainMoved; c.VisibleChanged += chainMoved; c.ParentChanged += chainParent;
                _chain.Add(c);
            }
        }
        private void chainMoved(object s, EventArgs e) { Track(); }
        private void chainParent(object s, EventArgs e) { hookChain(); Track(); }

        private void hideWindow() { if (_window.Visible) _window.Hide(); }

        private RECT _last;
        private bool _clipped;

        // put the window over the placeholder (only the part of it its ancestors show), or hide it
        public void Track()
        {
            if (_window == null || _window.IsDisposed) return;
            Form owner = FindForm();
            bool show = owner != null && IsHandleCreated && owner.IsHandleCreated && IsWindowVisible(Handle) && !IsIconic(owner.Handle) && Width > 0 && Height > 0;
            if (!show) { hideWindow(); return; }
            if (_window.Owner != owner) _window.Owner = owner;

            // the part of the placeholder its ancestors show (Kainos's own, unscaled, coordinates)
            Rectangle me = RectangleToScreen(ClientRectangle), shown = me;
            for (Control c = Parent; c != null; c = c.Parent) shown.Intersect(c.RectangleToScreen(c.ClientRectangle));
            if (shown.Width <= 0 || shown.Height <= 0) { hideWindow(); return; }

            IntPtr old = SetThreadDpiAwarenessContext(PerMonitorV2);       // so the window rectangle is in screen pixels
            try
            {
                RECT r;
                GetWindowRect(Handle, out r);
                double kx = (r.R - r.L) / (double)me.Width, ky = (r.B - r.T) / (double)me.Height;
                RECT vis = new RECT
                {
                    L = r.L + (int)Math.Round((shown.Left - me.Left) * kx), T = r.T + (int)Math.Round((shown.Top - me.Top) * ky),
                    R = r.L + (int)Math.Round((shown.Right - me.Left) * kx), B = r.T + (int)Math.Round((shown.Bottom - me.Top) * ky),
                };

                bool moved = !_window.Visible || r.L != _last.L || r.T != _last.T || r.R != _last.R || r.B != _last.B;
                if (moved)
                    SetWindowPos(_window.Handle, IntPtr.Zero, r.L, r.T, r.R - r.L, r.B - r.T, SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
                _last = r;
                if (!_window.Visible) _window.Show();       // without taking the focus (ShowWithoutActivation)

                bool clip = vis.L != r.L || vis.T != r.T || vis.R != r.R || vis.B != r.B;
                if (clip) SetWindowRgn(_window.Handle, CreateRectRgn(vis.L - r.L, vis.T - r.T, vis.R - r.L, vis.B - r.T), true);
                else if (_clipped) SetWindowRgn(_window.Handle, IntPtr.Zero, true);
                _clipped = clip;
            }
            finally { SetThreadDpiAwarenessContext(old); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _poll.Dispose();
                foreach (Control c in _chain) { c.LocationChanged -= chainMoved; c.SizeChanged -= chainMoved; c.VisibleChanged -= chainMoved; c.ParentChanged -= chainParent; }
                _chain.Clear();
                if (_window != null && !_window.IsDisposed) _window.Dispose();      // disposes the content with it
            }
            base.Dispose(disposing);
        }

        // ---- the window ----

        private class CrispWindow : Form
        {
            private readonly CrispHost _host;

            public CrispWindow(CrispHost host, Control content)
            {
                _host = host;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = content.BackColor;
                IntPtr old = SetThreadDpiAwarenessContext(PerMonitorV2);
                if (old == IntPtr.Zero) throw new InvalidOperationException("no per-monitor DPI awareness");
                try
                {
                    CreateHandle();
                    content.Dock = DockStyle.Fill;
                    Controls.Add(content);
                    makeHandles(content);   // now, while the thread is per-monitor aware (WinForms would wait until shown)
                }
                finally { SetThreadDpiAwarenessContext(old); }
                if (GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(Handle)) != 2) throw new InvalidOperationException("window isn't per-monitor aware");
            }

            private static void makeHandles(Control c)
            {
                IntPtr h = c.Handle;
                foreach (Control d in c.Controls) makeHandles(d);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams p = base.CreateParams;
                    p.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return p;
                }
            }

            protected override void WndProc(ref Message m)
            {
                // a click here activates Kainos's window, as a click on the placeholder would, and never this one
                if (m.Msg == WM_MOUSEACTIVATE)
                {
                    Form owner = Owner;
                    if (owner != null && Form.ActiveForm != owner) owner.Activate();
                    m.Result = (IntPtr)MA_NOACTIVATE;
                    return;
                }
                // moved to a monitor with another scale: the size is ours to set (Track), the content redraws
                if (m.Msg == WM_DPICHANGED) { _host.BeginInvoke((Action)(() => { _host._last = new RECT(); _host.Track(); Invalidate(true); })); m.Result = IntPtr.Zero; return; }
                base.WndProc(ref m);
            }
        }

        // ---- Win32 ----

        private static readonly IntPtr PerMonitorV2 = new IntPtr(-4);
        private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
        private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_DPICHANGED = 0x02E0;
        private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    }
}
