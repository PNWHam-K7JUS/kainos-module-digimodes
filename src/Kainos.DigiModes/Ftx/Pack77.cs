/*  Pack77.cs

The 77-bit FT8 / FT4 message, packed by WSJT-X's own ft8code (shipped in the module's decoder folder), so every
message type WSJT-X knows (standard, /P and /R, hashed compound calls, contest and free text) is packed exactly as
WSJT-X packs it. ft8code also prints FT8's channel tones, which the self-test compares with FtxEncoder's.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Kainos.DigiModes.Ftx
{
    internal class PackedMessage
    {
        public string Message;      // as sent
        public string Decoded;      // as a receiver will show it (ft8code's unpacked text)
        public string Bits77;
        public byte[] Ft8ToneCheck; // ft8code's own FT8 tones (79), for checking
        public string Type;         // "Standard msg", "Free text" ...
    }

    internal static class Pack77
    {
        private static readonly Dictionary<string, PackedMessage> _cache = new Dictionary<string, PackedMessage>();

        // null and a reason when ft8code isn't there or can't pack it
        public static PackedMessage Pack(string ft8codeExe, string message, out string problem)
        {
            problem = null;
            message = Normalise(message);
            if (message.Length == 0) { problem = "Nothing to send"; return null; }
            lock (_cache) { PackedMessage hit; if (_cache.TryGetValue(message, out hit)) return hit; }
            if (string.IsNullOrEmpty(ft8codeExe) || !File.Exists(ft8codeExe)) { problem = "ft8code.exe isn't in the decoder folder"; return null; }

            string output;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ft8codeExe, "\"" + message.Replace("\"", "") + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(ft8codeExe),
                };
                using (Process p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } problem = "ft8code didn't finish"; return null; }
                }
            }
            catch (Exception ex) { problem = "ft8code failed: " + ex.Message; return null; }

            PackedMessage pm = Parse(output, message, out problem);
            if (pm != null) lock (_cache) { if (_cache.Count > 500) _cache.Clear(); _cache[message] = pm; }
            return pm;
        }

        public static string Normalise(string m)
        {
            return string.Join(" ", (m ?? "").ToUpperInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        // ft8code's output:
        //     Message                               Decoded                             Err i3.n3
        //  1. CQ K7JUS CN87                         CQ K7JUS CN87                            1.  Standard msg
        //  Source-encoded message, 77 bits:
        //  0000...
        //  Channel symbols (79 tones):
        //    Sync               Data               Sync               Data               Sync
        //  3140652 0000... 3140652 4636... 3140652
        internal static PackedMessage Parse(string output, string message, out string problem)
        {
            problem = null;
            string[] lines = output.Replace("\r", "").Split('\n');
            PackedMessage pm = new PackedMessage { Message = message };
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.StartsWith("1.") && pm.Decoded == null)
                {
                    // the row is in fixed columns: message (37), decoded (37), then i3.n3 and the type
                    string row = lines[i];
                    int m0 = row.IndexOf("1.") + 3;
                    if (row.Length > m0 + 38)
                    {
                        string rest = row.Substring(m0 + 38);
                        pm.Decoded = Normalise(rest.Length > 37 ? rest.Substring(0, 37) : rest);
                        if (rest.Length > 37) pm.Type = rest.Substring(37).Trim().TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '.').Trim();
                    }
                }
                if (l.StartsWith("Source-encoded message") && i + 1 < lines.Length) pm.Bits77 = lines[i + 1].Trim();
                if (l.StartsWith("Channel symbols"))
                {
                    StringBuilder sb = new StringBuilder();
                    for (int k = i + 1; k < lines.Length && sb.Length < 79; k++)
                        foreach (char c in lines[k]) if (c >= '0' && c <= '7') sb.Append(c);
                    if (sb.Length >= 79) pm.Ft8ToneCheck = sb.ToString(0, 79).Select(c => (byte)(c - '0')).ToArray();
                }
            }
            if (pm.Bits77 == null || pm.Bits77.Length != 77 || pm.Bits77.Any(c => c != '0' && c != '1'))
            {
                problem = "ft8code couldn't pack \"" + message + "\"";
                return null;
            }
            if (pm.Decoded == null) pm.Decoded = message;
            return pm;
        }
    }
}
