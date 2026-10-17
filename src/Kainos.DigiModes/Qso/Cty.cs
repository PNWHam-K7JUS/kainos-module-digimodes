/*  Cty.cs

Which DXCC entity a callsign belongs to, from cty.dat (AD1C's country file, https://www.country-files.com,
shipped with the module and with WSJT-X).

cty.dat: an entity line "Name: CQ: ITU: Cont: Lat: Lon: TZ: Prefix:" then its prefixes, comma separated, ending
with ';'. "=CALL" is a whole callsign; (cq) [itu] <lat/lon> {cont} ~tz~ after a prefix override the entity's.
A lookup tries the whole callsign, then the longest prefix.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Kainos.DigiModes.Qso
{
    internal class CtyEntity
    {
        public string Name, Continent, Prefix;
        public int Cq, Itu;
        public double Lat, Lon;      // north and east positive (cty.dat has west positive; turned round here)
        public bool Dxcc = true;     // false for the WAE-only entries (prefix starting with '*')
    }

    internal class Cty
    {
        private readonly Dictionary<string, CtyEntity> _exact = new Dictionary<string, CtyEntity>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CtyEntity> _prefix = new Dictionary<string, CtyEntity>(StringComparer.OrdinalIgnoreCase);
        private int _longest;
        private static readonly Regex Overrides = new Regex(@"\(.*?\)|\[.*?\]|<.*?>|\{.*?\}|~.*?~");
        private static readonly HashSet<string> Suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P", "M", "MM", "AM", "QRP", "R", "A", "B", "LH", "J", "T" };

        public int Count { get { return _prefix.Count + _exact.Count; } }

        public static Cty Load(string path)
        {
            Cty c = new Cty();
            if (path == null || !File.Exists(path)) return c;
            string text = File.ReadAllText(path);
            CtyEntity cur = null;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (!char.IsWhiteSpace(raw[0]))
                {
                    // the entity line
                    string[] f = line.Split(':');
                    if (f.Length < 8) { cur = null; continue; }
                    string pfx = f[7].Trim();
                    cur = new CtyEntity
                    {
                        Name = f[0].Trim(), Cq = toInt(f[1]), Itu = toInt(f[2]), Continent = f[3].Trim(),
                        Lat = toD(f[4]), Lon = -toD(f[5]), Prefix = pfx.TrimStart('*'), Dxcc = !pfx.StartsWith("*"),
                    };
                    continue;
                }
                if (cur == null) continue;
                foreach (string item in line.Trim().TrimEnd(';').Split(','))
                {
                    string p = Overrides.Replace(item.Trim(), "");
                    if (p.Length == 0) continue;
                    if (p[0] == '=') c._exact[p.Substring(1)] = cur;
                    else { c._prefix[p] = cur; c._longest = Math.Max(c._longest, p.Length); }
                }
            }
            return c;
        }

        private static int toInt(string s) { int v; return int.TryParse(s.Trim(), out v) ? v : 0; }
        private static double toD(string s) { double v; return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }

        public CtyEntity Lookup(string call)
        {
            if (string.IsNullOrEmpty(call)) return null;
            call = call.Trim('<', '>').ToUpperInvariant();
            CtyEntity e;
            if (_exact.TryGetValue(call, out e)) return e;
            string key = PrefixPart(call);
            for (int n = Math.Min(_longest, key.Length); n > 0; n--)
                if (_prefix.TryGetValue(key.Substring(0, n), out e)) return e;
            return null;
        }

        // the part of a callsign that says where it is: W1AW/P -> W1AW, VP2E/K1ABC -> VP2E, K1ABC/KH6 -> KH6
        // (a portable digit, W1AW/4, stays in its own country)
        internal static string PrefixPart(string call)
        {
            List<string> parts = new List<string>();
            foreach (string p in call.Split('/'))
                if (p.Length > 0 && !Suffixes.Contains(p) && !(p.Length == 1 && char.IsDigit(p[0]))) parts.Add(p);
            if (parts.Count == 0) return call;
            if (parts.Count == 1) return parts[0];
            // two parts: the shorter is the prefix
            return parts[0].Length <= parts[1].Length ? parts[0] : parts[1];
        }
    }
}
