/*  DigiModesModule.cs

Kainos Digital Modes: FT8 and FT4 inside Kainos, as a downloadable module (Setup > Modules).
The decoding is WSJT-X's (jt9, shipped in the module's decoder folder); the encoder, timing, sequencing and the
window are this module's own.

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

Copyright (C) 2026 Justin Cron K7JUS
*/

using System;
using System.Globalization;
using System.IO;
using Kainos.DigiModes.UI;
using Kainos.Modules;

namespace Kainos.DigiModes
{
    public class DigiModesModule : IKainosModule
    {
        private IKainosHost _host;
        private DigiEngine _engine;
        private DigiPane _view;
        private IKainosPane _pane;
        private IKainosMenuItem _menu;

        public void Start(IKainosHost host)
        {
            _host = host;
            _engine = new DigiEngine(host);
            _engine.Decoder.WorkFolder = Path.Combine(host.DataFolder, "jt9");
            int v;
            if (int.TryParse(host.GetSetting("watchdog", ""), out v)) _engine.WatchdogMinutes = Math.Max(1, Math.Min(60, v));
            _engine.StopAfter73 = host.GetSetting("stop73", "1") == "1";
            FindDecoder(host, _engine);

            _view = new DigiPane(host, _engine);
            _pane = host.CreatePane("Kainos FT8 / FT4", _view);
            _pane.Closed += (s, e) => close();
            _view.PopToggled += () => _pane.Popped = !_pane.Popped;
            _menu = host.AddMenuItem("FT8 / FT4", "FT8 and FT4 (the Digital Modes module): open / close.", () => { if (_pane.IsOpen) close(); else open(); });
            if (host.GetSetting("open", "0") == "1") open();
        }

        private void open()
        {
            _pane.Open();
            _engine.Relisten();
            _engine.Start();
            _menu.Checked = true;
            _host.SetSetting("open", "1");
        }

        private void close()
        {
            _engine.Stop();
            _pane.Close();
            _host.SetMarkers(0, null);
            _host.SetMarkers(1, null);
            _menu.Checked = false;
            _host.SetSetting("open", "0");
        }

        public void Stop()
        {
            if (_engine != null) _engine.Stop();
        }

        public void Dispose()
        {
            if (_view != null && !_view.IsDisposed) _view.Dispose();
        }

        // jt9.exe and ft8code.exe: the folder in Settings, else the module's own decoder folder, else WSJT-X's
        internal static void FindDecoder(IKainosHost host, DigiEngine e)
        {
            string set = host.GetSetting("decoder", "").Trim();
            string[] candidates =
            {
                set,
                Path.Combine(host.ModuleFolder, "decoder"),
                @"C:\WSJT\wsjtx\bin",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "wsjtx", "bin"),
            };
            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                string jt9 = Path.Combine(c, "jt9.exe");
                if (!File.Exists(jt9)) continue;
                e.Decoder.Jt9Exe = jt9;
                e.Ft8CodeExe = Path.Combine(c, "ft8code.exe");
                host.Log("decoder: " + jt9);
                return;
            }
            e.Decoder.Jt9Exe = null;
            e.Ft8CodeExe = null;
            host.Log("no decoder found");
        }
    }
}
