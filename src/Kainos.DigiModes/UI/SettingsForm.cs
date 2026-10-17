/*  SettingsForm.cs

The FT8 / FT4 settings: transmit level and timing, decoding depth, where the decoder is.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.DigiModes.UI
{
    internal class SettingsForm : Form
    {
        private readonly IKainosHost _host;
        private readonly DigiEngine _e;
        private readonly NumericUpDown _level, _lead, _rxlat, _depth, _watchdog;
        private readonly CheckBox _stop73;
        private readonly TextBox _decoder;

        public SettingsForm(IKainosHost host, DigiEngine e)
        {
            _host = host;
            _e = e;
            Text = "FT8 / FT4 settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 330);

            int y = 14;
            Func<string, decimal, decimal, decimal, decimal, string, NumericUpDown> row = (label, min, max, val, inc, tip) =>
            {
                Label l = new Label { Text = label, Location = new Point(14, y + 3), AutoSize = true };
                NumericUpDown n = new NumericUpDown { Location = new Point(250, y), Width = 80, Minimum = min, Maximum = max, Increment = inc, DecimalPlaces = inc < 1 ? 1 : 0 };
                n.Value = Math.Max(min, Math.Min(max, val));
                new ToolTip().SetToolTip(n, tip);
                Controls.Add(l);
                Controls.Add(n);
                y += 30;
                return n;
            };
            _level = row("Transmit audio level (dB)", -40, 0, (decimal)e.LevelDb, 1,
                "The level of the FT8 / FT4 audio into the transmitter. Set it so the ALC barely moves, as for any digital mode.");
            _lead = row("Start transmitting early (ms)", 0, 1000, (decimal)e.TxLeadMs, 10,
                "Makes up for the delay through the transmit path, so others see your DT near 0.0.");
            _rxlat = row("Receive delay (ms)", 0, 1000, (decimal)e.RxLatencyMs, 10,
                "The delay through the receive path. Raise it if every station's DT reads high by the same amount.");
            _depth = row("Decoding depth (1 fast - 3 deep)", 1, 3, e.Decoder.Depth, 1, "jt9's decoding depth. 3 finds the most, using more CPU.");
            _watchdog = row("Tx watchdog (minutes)", 1, 60, e.WatchdogMinutes, 1, "Transmitting stops when you haven't touched anything for this long.");
            _stop73 = new CheckBox { Text = "Stop transmitting when the QSO ends (73)", Location = new Point(14, y), AutoSize = true, Checked = e.StopAfter73 };
            Controls.Add(_stop73);
            y += 32;

            Controls.Add(new Label { Text = "Decoder folder (jt9.exe and ft8code.exe)", Location = new Point(14, y), AutoSize = true });
            y += 20;
            _decoder = new TextBox { Location = new Point(14, y), Width = 400, Text = host.GetSetting("decoder", "") };
            Button browse = new Button { Text = "Browse...", Location = new Point(420, y - 1), Width = 86 };
            browse.Click += (s, a) =>
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "The folder with jt9.exe (WSJT-X's bin folder)" })
                    if (d.ShowDialog(this) == DialogResult.OK) _decoder.Text = d.SelectedPath;
            };
            Controls.Add(_decoder);
            Controls.Add(browse);
            y += 26;
            Controls.Add(new Label { Text = "Empty: the decoder that came with the module, else WSJT-X's if installed. In use: " + (e.Decoder.Jt9Exe ?? "none"), Location = new Point(14, y), Size = new Size(490, 32), ForeColor = SystemColors.GrayText });
            y += 36;

            LinkLabel log = new LinkLabel { Text = "Open the log folder (kainos-digimodes.adi)", Location = new Point(14, y + 4), AutoSize = true };
            log.LinkClicked += (s, a) => { try { Process.Start("explorer.exe", "\"" + host.DataFolder + "\""); } catch { } };
            Controls.Add(log);

            Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(340, y), Width = 80 };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(426, y), Width = 80 };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(520, y + 40);
            ok.Click += (s, a) => apply();
        }

        private void apply()
        {
            _e.LevelDb = (double)_level.Value;
            _e.TxLeadMs = (double)_lead.Value;
            _e.RxLatencyMs = (double)_rxlat.Value;
            _e.Decoder.Depth = (int)_depth.Value;
            _e.WatchdogMinutes = (int)_watchdog.Value;
            _e.StopAfter73 = _stop73.Checked;
            Func<double, string> s = v => v.ToString(CultureInfo.InvariantCulture);
            _host.SetSetting("level", s(_e.LevelDb));
            _host.SetSetting("lead", s(_e.TxLeadMs));
            _host.SetSetting("rxlat", s(_e.RxLatencyMs));
            _host.SetSetting("depth", _e.Decoder.Depth.ToString());
            _host.SetSetting("watchdog", _e.WatchdogMinutes.ToString());
            _host.SetSetting("stop73", _e.StopAfter73 ? "1" : "0");
            _host.SetSetting("decoder", _decoder.Text.Trim());
            DigiModesModule.FindDecoder(_host, _e);
        }
    }
}
