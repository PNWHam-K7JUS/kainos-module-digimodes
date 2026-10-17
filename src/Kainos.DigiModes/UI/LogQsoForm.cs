/*  LogQsoForm.cs

Log QSO: shown when a QSO completes (as WSJT-X does), with what the QSO gave filled in; edit and press OK to log
it (the ADIF file, and the programs listening over UDP), or Cancel to leave it out.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Kainos.DigiModes.Qso;

namespace Kainos.DigiModes.UI
{
    internal class LogQsoForm : Form
    {
        private readonly QsoRecord _q;
        private readonly TextBox _call, _grid, _sent, _rcvd, _name, _power, _comments, _op;
        private readonly DateTimePicker _on, _off;

        public LogQsoForm(QsoRecord q, string band)
        {
            _q = q;
            Text = "Log QSO";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            TopMost = true;

            int y = 12;
            Func<string, string, int, TextBox> field = (label, value, w) =>
            {
                Controls.Add(new Label { Text = label, Location = new Point(12, y + 3), AutoSize = true });
                TextBox t = new TextBox { Text = value ?? "", Location = new Point(130, y), Width = w };
                Controls.Add(t);
                y += 28;
                return t;
            };
            Func<string, DateTime, DateTimePicker> time = (label, value) =>
            {
                Controls.Add(new Label { Text = label, Location = new Point(12, y + 3), AutoSize = true });
                DateTimePicker p = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd  HH:mm:ss", Value = value, Location = new Point(130, y), Width = 170 };
                Controls.Add(p);
                y += 28;
                return p;
            };
            _call = field("Call", q.Call, 120);
            _call.CharacterCasing = CharacterCasing.Upper;
            _grid = field("Grid", q.Grid, 80);
            _grid.CharacterCasing = CharacterCasing.Upper;
            _on = time("Start (UTC)", q.On);
            _off = time("End (UTC)", q.Off);
            _sent = field("Report sent", q.RstSent, 60);
            _rcvd = field("Report received", q.RstRcvd, 60);
            _name = field("Name", q.Name, 200);
            _power = field("Tx power", q.TxPower, 80);
            _comments = field("Comments", q.Comments, 300);
            _op = field("Operator", q.Operator, 120);
            _op.CharacterCasing = CharacterCasing.Upper;
            Controls.Add(new Label
            {
                Text = (q.Mode ?? "") + "   " + band + "   " + ((q.DialHz + q.AudioHz) / 1e6).ToString("0.000000", CultureInfo.InvariantCulture) + " MHz",
                Location = new Point(12, y + 3), AutoSize = true, ForeColor = SystemColors.GrayText,
            });
            y += 30;
            Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(250, y), Width = 90 };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(346, y), Width = 90 };
            ok.Click += (s, e) => apply();
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(448, y + 40);
        }

        private void apply()
        {
            _q.Call = _call.Text.Trim();
            _q.Grid = _grid.Text.Trim();
            _q.On = DateTime.SpecifyKind(_on.Value, DateTimeKind.Utc);
            _q.Off = DateTime.SpecifyKind(_off.Value, DateTimeKind.Utc);
            _q.RstSent = _sent.Text.Trim();
            _q.RstRcvd = _rcvd.Text.Trim();
            _q.Name = _name.Text.Trim();
            _q.TxPower = _power.Text.Trim();
            _q.Comments = _comments.Text.Trim();
            _q.Operator = _op.Text.Trim();
        }
    }
}
