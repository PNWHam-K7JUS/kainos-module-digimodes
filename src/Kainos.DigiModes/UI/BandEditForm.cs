/*  BandEditForm.cs

Edit the Band menu: a line per entry, its name then the FT8 and FT4 dial frequencies in MHz ("-" for none).

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Kainos.DigiModes.Qso;

namespace Kainos.DigiModes.UI
{
    internal class BandEditForm : Form
    {
        private readonly TextBox _text;
        public List<Bands.Entry> Result { get; private set; }

        public BandEditForm(string current)
        {
            Text = "Band frequencies";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 420);
            MinimumSize = new Size(360, 300);

            Label how = new Label
            {
                Dock = DockStyle.Top, Height = 54, Padding = new Padding(8, 8, 8, 0),
                Text = "One band a line: a name, then the FT8 and FT4 dial frequencies in MHz (\"-\" for none).\r\n" +
                       "Add your own, e.g. a DXpedition's:  DXped 14.090 -",
            };
            _text = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 10), Text = current, AcceptsReturn = true, WordWrap = false };
            Panel buttons = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            Button reset = new Button { Text = "Reset to defaults", Location = new Point(8, 9), Width = 130 };
            reset.Click += (s, e) => _text.Text = Bands.Format(Bands.Defaults());
            Button ok = new Button { Text = "OK", Anchor = AnchorStyles.Top | AnchorStyles.Right, Width = 80, Location = new Point(ClientSize.Width - 176, 9) };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Top | AnchorStyles.Right, Width = 80, Location = new Point(ClientSize.Width - 90, 9) };
            ok.Click += (s, e) =>
            {
                string why;
                List<Bands.Entry> l = Bands.Parse(_text.Text, out why);
                if (l == null) { MessageBox.Show(this, why, "Band frequencies", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Result = l;
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.AddRange(new Control[] { reset, ok, cancel });
            Controls.Add(_text);
            Controls.Add(how);
            Controls.Add(buttons);
            CancelButton = cancel;
        }
    }
}
