/*  SettingsForm.cs

The FT8 / FT4 settings: transmit level and timing, decoding, the decoder's folder; logging; and WSJT-X's UDP
messages for other programs.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.DigiModes.UI
{
    internal class SettingsForm : Form
    {
        private readonly IKainosHost _host;
        private readonly DigiEngine _e;
        private readonly NumericUpDown _level, _lead, _rxlat, _depth, _watchdog, _port;
        private readonly CheckBox _stop73, _logPrompt, _udpOn, _udpAccept, _psk, _sndMe, _sndDx, _crisp;
        private readonly TextBox _decoder, _power, _op, _udpHost, _udpId, _pskAntenna, _workLog, _exchange;
        private readonly ComboBox _contest;

        public SettingsForm(IKainosHost host, DigiEngine e)
        {
            _host = host;
            _e = e;
            Text = "FT8 / FT4 settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ToolTip tips = new ToolTip();

            // ---- left: transmitting and decoding ----
            int x0 = 14, y = 14;
            Func<string, int> heading = t => { Controls.Add(new Label { Text = t, Location = new Point(x0, y), AutoSize = true, Font = new Font(Font, FontStyle.Bold) }); y += 24; return y; };
            Func<string, decimal, decimal, decimal, decimal, string, NumericUpDown> row = (label, min, max, val, inc, tip) =>
            {
                Controls.Add(new Label { Text = label, Location = new Point(x0, y + 3), AutoSize = true });
                NumericUpDown n = new NumericUpDown { Location = new Point(x0 + 230, y), Width = 80, Minimum = min, Maximum = max, Increment = inc };
                n.Value = Math.Max(min, Math.Min(max, val));
                tips.SetToolTip(n, tip);
                Controls.Add(n);
                y += 30;
                return n;
            };
            Func<string, bool, string, CheckBox> check = (label, on, tip) =>
            {
                CheckBox c = new CheckBox { Text = label, Location = new Point(x0, y), AutoSize = true, Checked = on };
                tips.SetToolTip(c, tip);
                Controls.Add(c);
                y += 28;
                return c;
            };
            Func<string, string, int, string, TextBox> text = (label, value, w, tip) =>
            {
                Controls.Add(new Label { Text = label, Location = new Point(x0, y + 3), AutoSize = true });
                TextBox t = new TextBox { Location = new Point(x0 + 150, y), Width = w, Text = value };
                tips.SetToolTip(t, tip);
                Controls.Add(t);
                y += 30;
                return t;
            };

            heading("Transmitting and decoding");
            _level = row("Transmit audio level (dB)", -40, 0, (decimal)e.LevelDb, 1,
                "The level of the FT8 / FT4 audio into the transmitter. Set it so the ALC barely moves, as for any digital mode.");
            _lead = row("Start transmitting early (ms)", 0, 1000, (decimal)e.TxLeadMs, 10,
                "Makes up for the delay through the transmit path, so others see your DT near 0.0.");
            _rxlat = row("Receive delay (ms)", 0, 1000, (decimal)e.RxLatencyMs, 10,
                "The delay through the receive path. Raise it if every station's DT reads high by the same amount.");
            _depth = row("Decoding depth (1 fast - 3 deep)", 1, 3, e.Decoder.Depth, 1,
                "jt9's decoding depth for the end-of-period pass. 3 finds the most, using more CPU. (The early pass, which chooses your reply, uses 2 at most.)");
            _watchdog = row("Tx watchdog (minutes)", 1, 60, e.WatchdogMinutes, 1, "Transmitting stops when you haven't touched anything for this long.");
            _stop73 = check("Stop transmitting when the QSO ends (73)", e.StopAfter73, "As WSJT-X does: after 73, transmitting is turned off.");
            _sndMe = check("Sound when someone calls me", host.GetSetting("sndme", "1") == "1", "Windows's exclamation sound when a decode has your callsign.");
            _sndDx = check("Sound when a new DXCC entity calls CQ", host.GetSetting("snddxcc", "0") == "1", "Windows's asterisk sound when a CQ comes from an entity you haven't worked.");
            _crisp = check("Sharp waterfall at 125 % / 150 % display scaling", host.GetSetting("crispwf", "1") == "1",
                "Draws the waterfall at the screen's own resolution (in a window of its own, laid over the pane), where Windows would\r\n" +
                "otherwise enlarge it and blur it. From the module's next start (Setup > Modules, or the next run of Kainos).");

            Controls.Add(new Label { Text = "Decoder folder (jt9.exe and ft8code.exe)", Location = new Point(x0, y), AutoSize = true });
            y += 20;
            _decoder = new TextBox { Location = new Point(x0, y), Width = 230, Text = host.GetSetting("decoder", "") };
            Button browse = new Button { Text = "Browse...", Location = new Point(x0 + 236, y - 1), Width = 80 };
            browse.Click += (s, a) =>
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "The folder with jt9.exe (WSJT-X's bin folder)" })
                    if (d.ShowDialog(this) == DialogResult.OK) _decoder.Text = d.SelectedPath;
            };
            Controls.Add(_decoder);
            Controls.Add(browse);
            y += 26;
            Controls.Add(new Label { Text = "Empty: the decoder that came with the module. In use:\r\n" + (e.Decoder.Jt9Exe ?? "none"), Location = new Point(x0, y), Size = new Size(320, 44), ForeColor = SystemColors.GrayText });
            int leftBottom = y + 44;

            // ---- right: logging, and other programs ----
            x0 = 360; y = 14;
            heading("Logging");
            _logPrompt = check("Ask before logging (the Log QSO window)", host.GetSetting("logprompt", "1") == "1",
                "On: a finished QSO opens Log QSO, to check or add to it before it's logged. Off: it's logged straight away.");
            _power = text("Tx power", host.GetSetting("power", ""), 80, "Filled in on each QSO (for example 5W).");
            _op = text("Operator", host.GetSetting("operator", ""), 110, "Filled in on each QSO, when someone else operates your station.");
            _op.CharacterCasing = CharacterCasing.Upper;
            Controls.Add(new Label { Text = "Worked before: also read this ADIF log", Location = new Point(x0, y), AutoSize = true });
            y += 20;
            _workLog = new TextBox { Location = new Point(x0, y), Width = 270, Text = host.GetSetting("worklog", "") };
            tips.SetToolTip(_workLog, "Your main log exported as ADIF (from N1MM+, Log4OM, HRD ...), so decodes are marked NEW DXCC / NEW BAND / NEW GRID\r\n" +
                                      "against everything you've worked, not only the QSOs made here. Read when Kainos starts and when you press OK.");
            Button wlb = new Button { Text = "Browse...", Location = new Point(x0 + 276, y - 1), Width = 80 };
            wlb.Click += (s, a) =>
            {
                using (OpenFileDialog d = new OpenFileDialog { Filter = "ADIF log (*.adi;*.adif)|*.adi;*.adif|All files|*.*", Title = "Your log, as ADIF" })
                    if (d.ShowDialog(this) == DialogResult.OK) _workLog.Text = d.FileName;
            };
            Controls.Add(_workLog);
            Controls.Add(wlb);
            y += 30;
            LinkLabel log = new LinkLabel { Text = "Open the log folder (kainos-digimodes.adi)", Location = new Point(x0, y), AutoSize = true };
            log.LinkClicked += (s, a) => { try { Process.Start("explorer.exe", "\"" + host.DataFolder + "\""); } catch { } };
            Controls.Add(log);
            y += 34;

            heading("Other programs (WSJT-X UDP)");
            _udpOn = check("Send decodes, status and logged QSOs", host.GetSetting("udp", "1") == "1",
                "WSJT-X's UDP messages, for GridTracker, JTAlert, N1MM+, Log4OM, HRD, DXKeeper and others: set them up as for WSJT-X.");
            _udpHost = text("Address", host.GetSetting("udphost", "127.0.0.1"), 120, "127.0.0.1 for programs on this PC (WSJT-X's default), or a multicast group such as 224.0.0.1 for several.");
            int port;
            if (!int.TryParse(host.GetSetting("udpport", "2237"), out port)) port = 2237;
            Controls.Add(new Label { Text = "Port", Location = new Point(x0, y + 3), AutoSize = true });
            _port = new NumericUpDown { Location = new Point(x0 + 150, y), Width = 80, Minimum = 1, Maximum = 65535, Value = Math.Max(1, Math.Min(65535, port)) };
            tips.SetToolTip(_port, "2237 is WSJT-X's default.");
            Controls.Add(_port);
            y += 30;
            _udpId = text("Name sent", host.GetSetting("udpid", "Kainos"), 120, "How the other programs list this station. A few only accept \"WSJT-X\".");
            _udpAccept = check("Accept their requests (answer a station, halt, free text)", host.GetSetting("udpaccept", "1") == "1",
                "Double-clicking a station in GridTracker (or JTAlert's callsign) answers it here, as double-clicking a decode does.");
            y += 6;
            heading("PSK Reporter");
            _psk = check("Report the stations I hear to pskreporter.info", host.GetSetting("psk", "0") == "1",
                "Every 5 minutes, the stations decoded here (callsign, grid, frequency, SNR) are sent to PSK Reporter with your callsign and grid,\r\n" +
                "so you and others can see on its map where signals are heard. Off by default: it publishes your callsign and location.");
            _pskAntenna = text("Antenna (optional)", host.GetSetting("pskantenna", ""), 160, "Shown with your reports on PSK Reporter, e.g. \"EFHW 40 m\".");
            y += 6;
            heading("Contest");
            Controls.Add(new Label { Text = "Activity", Location = new Point(x0, y + 3), AutoSize = true });
            _contest = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x0 + 150, y), Width = 180 };
            foreach (Qso.Contest c in new[] { Qso.Contest.None, Qso.Contest.FieldDay, Qso.Contest.RttyRoundup, Qso.Contest.NaVhf, Qso.Contest.WwDigi, Qso.Contest.Hound }) _contest.Items.Add(Qso.QsoEngine.Name(c));
            int ci;
            int.TryParse(host.GetSetting("contest", "0"), out ci);
            _contest.SelectedItem = Qso.QsoEngine.Name((Qso.Contest)ci);
            if (_contest.SelectedIndex < 0) _contest.SelectedIndex = 0;
            tips.SetToolTip(_contest, "WSJT-X's contest messages: CQ FD / CQ RU / CQ TEST / CQ WW, your exchange in place of the signal report,\r\n" +
                                      "and automatic sequencing to RR73. When you call CQ, each completed QSO goes straight back to CQ.");
            Controls.Add(_contest);
            y += 30;
            _exchange = text("Your exchange", host.GetSetting("exchange", ""), 120, "");
            _exchange.CharacterCasing = CharacterCasing.Upper;
            Label exHint = new Label { Location = new Point(x0, y), Size = new Size(360, 30), ForeColor = SystemColors.GrayText };
            Controls.Add(exHint);
            Action hint = () =>
            {
                string n = (string)_contest.SelectedItem;
                _exchange.Enabled = n == Qso.QsoEngine.Name(Qso.Contest.FieldDay) || n == Qso.QsoEngine.Name(Qso.Contest.RttyRoundup);
                exHint.Text = n == Qso.QsoEngine.Name(Qso.Contest.FieldDay) ? "Field Day: your class and section, e.g. 1D WWA" :
                              n == Qso.QsoEngine.Name(Qso.Contest.RttyRoundup) ? "Roundup: your state or province (e.g. WA); empty outside the US and Canada for serial numbers" :
                              n == Qso.QsoEngine.Name(Qso.Contest.Hound) ? "Working a DXpedition in Fox / Hound mode: call at 1000 Hz or above; FT8 only" :
                              n == Qso.QsoEngine.Name(Qso.Contest.None) ? "" : "The exchange is your grid square";
            };
            _contest.SelectedIndexChanged += (s, a) => hint();
            hint();
            y += 30;
            int rightBottom = y;

            y = Math.Max(leftBottom, rightBottom) + 8;
            Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(560, y), Width = 80 };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(646, y), Width = 80 };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(740, y + 40);
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
            _host.SetSetting("logprompt", _logPrompt.Checked ? "1" : "0");
            _host.SetSetting("power", _power.Text.Trim());
            _host.SetSetting("operator", _op.Text.Trim());
            _host.SetSetting("udp", _udpOn.Checked ? "1" : "0");
            _host.SetSetting("udphost", _udpHost.Text.Trim());
            _host.SetSetting("udpport", ((int)_port.Value).ToString());
            _host.SetSetting("udpid", _udpId.Text.Trim());
            _host.SetSetting("udpaccept", _udpAccept.Checked ? "1" : "0");
            _host.SetSetting("worklog", _workLog.Text.Trim());
            Qso.Contest c = Qso.Contest.None;
            foreach (Qso.Contest k in Enum.GetValues(typeof(Qso.Contest))) if (Qso.QsoEngine.Name(k) == (string)_contest.SelectedItem) c = k;
            _host.SetSetting("contest", ((int)c).ToString());
            _host.SetSetting("exchange", _exchange.Text.Trim());
            lock (_e.Qso) { _e.Qso.Activity = c; _e.Qso.MyExchange = _exchange.Text.Trim(); }
            _host.SetSetting("sndme", _sndMe.Checked ? "1" : "0");
            _host.SetSetting("snddxcc", _sndDx.Checked ? "1" : "0");
            _host.SetSetting("crispwf", _crisp.Checked ? "1" : "0");
            _host.SetSetting("psk", _psk.Checked ? "1" : "0");
            _host.SetSetting("pskantenna", _pskAntenna.Text.Trim());
            DigiModesModule.FindDecoder(_host, _e);
        }
    }
}
