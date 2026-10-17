/*  Worked.cs

What you've worked, from your logs (the module's own kainos-digimodes.adi, and another ADIF log if you name one,
such as your logging program's export): callsigns, grids and DXCC entities, on each band and on any. Each decode's
sender is then marked: a new DXCC entity, an entity new on this band, a new grid, a new grid on this band, or a
station already worked on this band.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Kainos.DigiModes.Qso
{
    internal enum WorkedMark { None, NewDxcc, NewDxccBand, NewGrid, NewGridBand, WorkedBand }

    internal class WorkedInfo
    {
        public WorkedMark Mark;
        public string Call, Grid, Country;
        public CtyEntity Entity;
    }

    internal class Worked
    {
        private static readonly Regex Field = new Regex(@"<(\w+):(\d+)(?::\w)?>", RegexOptions.Compiled);
        private static readonly Regex GridRx = new Regex("^[A-R]{2}[0-9]{2}$");
        private static readonly Regex CallRx = new Regex(@"^(?=.*\d)(?=.*[A-Z])[A-Z0-9/]{3,11}$");
        private readonly HashSet<string> _calls = new HashSet<string>(), _grids = new HashSet<string>(), _ents = new HashSet<string>();
        private readonly object _lock = new object();
        public Cty Cty = new Cty();
        public int Qsos { get; private set; }

        public void Clear() { lock (_lock) { _calls.Clear(); _grids.Clear(); _ents.Clear(); Qsos = 0; } }

        // an ADIF file: every QSO in it
        public int LoadAdif(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
            string text = File.ReadAllText(path);
            int eoh = text.IndexOf("<EOH>", StringComparison.OrdinalIgnoreCase);
            int pos = eoh >= 0 ? eoh + 5 : 0, n = 0;
            Dictionary<string, string> rec = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (pos < text.Length)
            {
                Match m = Field.Match(text, pos);
                int eor = text.IndexOf("<EOR>", pos, StringComparison.OrdinalIgnoreCase);
                if (eor >= 0 && (!m.Success || eor < m.Index))
                {
                    string call, band, grid;
                    rec.TryGetValue("CALL", out call); rec.TryGetValue("BAND", out band); rec.TryGetValue("GRIDSQUARE", out grid);
                    if (!string.IsNullOrEmpty(call)) { Add(call, grid, band); n++; }
                    rec.Clear();
                    pos = eor + 5;
                    continue;
                }
                if (!m.Success) break;
                int len = int.Parse(m.Groups[2].Value);
                int start = m.Index + m.Length;
                if (start + len > text.Length) break;
                rec[m.Groups[1].Value] = text.Substring(start, len).Trim();
                pos = start + len;
            }
            return n;
        }

        // a QSO: its call, grid (any length) and band ("20M", "20m" ...)
        public void Add(string call, string grid, string band)
        {
            call = (call ?? "").Trim().ToUpperInvariant();
            band = (band ?? "").Trim().ToUpperInvariant();
            string g4 = (grid ?? "").Trim().ToUpperInvariant();
            g4 = g4.Length >= 4 ? g4.Substring(0, 4) : "";
            CtyEntity e = Cty.Lookup(call);
            lock (_lock)
            {
                Qsos++;
                _calls.Add(call); _calls.Add(call + "|" + band);
                if (g4.Length == 4) { _grids.Add(g4); _grids.Add(g4 + "|" + band); }
                if (e != null) { _ents.Add(e.Name); _ents.Add(e.Name + "|" + band); }
            }
        }

        // the sender of a decoded message and how new it is, on this band (dial frequency in Hz)
        public WorkedInfo Classify(string message, string band)
        {
            string[] w = (message ?? "").Split(' ');
            string call = null, grid = "";
            if (w.Length >= 2 && w[0] == "CQ")
            {
                int ci = w.Length >= 3 && !CallRx.IsMatch(w[1]) ? 2 : 1;
                if (ci < w.Length) call = w[ci];
                if (ci + 1 < w.Length && GridRx.IsMatch(w[ci + 1]) && w[ci + 1] != "RR73") grid = w[ci + 1];
            }
            else if (w.Length >= 2)
            {
                call = w[1];
                if (w.Length >= 3 && GridRx.IsMatch(w[2]) && w[2] != "RR73") grid = w[2];
            }
            if (call == null) return null;
            call = call.Trim('<', '>');
            if (!CallRx.IsMatch(call)) return null;
            band = (band ?? "").ToUpperInvariant();
            CtyEntity e = Cty.Lookup(call);
            WorkedInfo i = new WorkedInfo { Call = call, Grid = grid, Entity = e, Country = e != null ? e.Name : "" };
            lock (_lock)
            {
                if (e != null && e.Dxcc && !_ents.Contains(e.Name)) i.Mark = WorkedMark.NewDxcc;
                else if (e != null && e.Dxcc && band.Length > 0 && !_ents.Contains(e.Name + "|" + band)) i.Mark = WorkedMark.NewDxccBand;
                else if (grid.Length == 4 && !_grids.Contains(grid)) i.Mark = WorkedMark.NewGrid;
                else if (grid.Length == 4 && band.Length > 0 && !_grids.Contains(grid + "|" + band)) i.Mark = WorkedMark.NewGridBand;
                else if (_calls.Contains(call + "|" + band)) i.Mark = WorkedMark.WorkedBand;
            }
            return i;
        }
    }
}
