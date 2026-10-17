/*  Program.cs (self-test)

Checks the module without a radio: the encoder against WSJT-X's ft8code, and the whole path (tones -> GFSK at
48 kHz -> noise -> 12 kHz resampler -> jt9) decoding what was sent, for FT8 and FT4; the QSO sequencer; parsing.

  Kainos.DigiModes.SelfTest.exe <folder with jt9.exe and ft8code.exe>

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kainos.DigiModes.Decode;
using Kainos.DigiModes.Ftx;
using Kainos.DigiModes.Qso;
using Kainos.DigiModes.Net;

namespace Kainos.DigiModes.SelfTest
{
    internal static class Program
    {
        private static int _fail;

        private static void check(bool ok, string what)
        {
            System.Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) _fail++;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string bin = args.Length > 0 ? args[0] : @"C:\WSJT\wsjtx\bin";
            string ft8code = Path.Combine(bin, "ft8code.exe"), jt9 = Path.Combine(bin, "jt9.exe");
            string work = Path.Combine(Path.GetTempPath(), "kainos-digimodes-selftest");
            Directory.CreateDirectory(work);

            // 1. the encoder gives ft8code's own FT8 tones
            string[] msgs = { "CQ K7JUS CN87", "K1ABC K7JUS CN87", "K7JUS K1ABC -12", "K1ABC K7JUS R-07", "K7JUS K1ABC RR73",
                              "K1ABC K7JUS 73", "CQ DX VK2XYZ QF56", "W1AW/P K7JUS R+05", "TNX BOB 73 GL" };
            foreach (string m in msgs)
            {
                string why;
                PackedMessage pm = Pack77.Pack(ft8code, m, out why);
                if (pm == null) { check(false, "pack \"" + m + "\": " + why); continue; }
                byte[] tones = FtxEncoder.Ft8Tones(FtxEncoder.PayloadFromBits(pm.Bits77));
                check(pm.Ft8ToneCheck != null && tones.SequenceEqual(pm.Ft8ToneCheck), "FT8 tones match ft8code for \"" + m + "\" (" + pm.Type + ", decodes as \"" + pm.Decoded + "\")");
            }

            // 2. over the air (simulated): FT8 and FT4 decoded by jt9, at several levels
            Jt9Decoder dec = new Jt9Decoder { Jt9Exe = jt9, WorkFolder = work, Depth = 3 };
            Random rnd = new Random(7);
            foreach (FtxMode mode in new[] { FtxMode.FT8, FtxMode.FT4 })
            {
                foreach (double snr in new[] { 0.0, -12.0, mode == FtxMode.FT8 ? -19.0 : -15.0 })
                {
                    string m = mode == FtxMode.FT8 ? "CQ K7JUS CN87" : "K1ABC K7JUS R-12";
                    string why;
                    PackedMessage pm = Pack77.Pack(ft8code, m, out why);
                    byte[] payload = FtxEncoder.PayloadFromBits(pm.Bits77);
                    byte[] tones = mode == FtxMode.FT8 ? FtxEncoder.Ft8Tones(payload) : FtxEncoder.Ft4Tones(payload);
                    const int rate = 48000;
                    double period = FtxModes.Period(mode);
                    float[] wave = FtxModulator.Synthesize(mode, tones, 1200, rate);
                    float[] air = new float[(int)(period * rate)];
                    int at = (int)(FtxModes.TxStart(mode) * rate);
                    // white noise: SNR is in 2500 Hz, as WSJT-X reports it
                    double sigPow = 0.5, noiseSd = Math.Sqrt(sigPow / Math.Pow(10, snr / 10) * (rate / 2.0) / 2500.0);
                    for (int i = 0; i < air.Length; i++)
                    {
                        double n = gauss(rnd) * noiseSd;
                        double s = i >= at && i - at < wave.Length ? wave[i - at] : 0;
                        air[i] = (float)((s + n) * 0.05);
                    }
                    Resampler12k rs = new Resampler12k(rate);
                    List<float> out12 = new List<float>();
                    for (int i = 0; i < air.Length; i += 960) rs.Process(air.Skip(i).Take(960).ToArray(), Math.Min(960, air.Length - i), out12);
                    float[] slot = out12.Take((int)(period * 12000)).ToArray();
                    DateTime slotUtc = new DateTime(2026, 10, 16, 12, 0, mode == FtxMode.FT8 ? 15 : 0, DateTimeKind.Utc);
                    List<DecodeLine> res = dec.Decode(mode, slotUtc, slot, 1200, "K7JUS", "CN87", "K1ABC", out why);
                    DecodeLine hit = res.FirstOrDefault(d => d.Message == m);
                    check(hit != null && Math.Abs(hit.Freq - 1200) <= 3 && Math.Abs(hit.Dt) <= 0.2,
                        mode + " at " + snr + " dB decodes" + (hit != null ? ": \"" + hit.Message + "\" snr " + hit.Snr + " dt " + hit.Dt + " f " + hit.Freq : (why != null ? " (" + why + ")" : "") + " - got " + res.Count + " decodes"));
                }
            }

            // 3. nothing from noise alone
            {
                float[] noise = new float[180000];
                for (int i = 0; i < noise.Length; i++) noise[i] = (float)(gauss(rnd) * 0.05);
                string why;
                List<DecodeLine> res = dec.Decode(FtxMode.FT8, new DateTime(2026, 10, 16, 12, 1, 0, DateTimeKind.Utc), noise, 1500, "K7JUS", "CN87", "", out why);
                check(res.Count == 0, "no decodes from noise (" + res.Count + ")");
            }

            // 4. the slot recorder cuts the right samples by time
            {
                SlotRecorder rec = new SlotRecorder();
                DateTime t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
                float[] chunk = new float[480];
                for (int c = 0; c < 2000; c++)              // 20 s of 48 kHz in 10 ms chunks, a 1 kHz tone starting at 5 s
                {
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        double tt = c * 0.01 + i / 48000.0;
                        chunk[i] = tt >= 5.0 ? (float)Math.Sin(2 * Math.PI * 1000 * tt) : 0f;
                    }
                    rec.Add(chunk, chunk.Length, 48000, t0.AddSeconds((c + 1) * 0.01));
                }
                double miss;
                float[] cut = rec.Cut(SlotRecorder.Seconds(t0) + 4.0, 2.0, out miss);
                int firstLoud = Array.FindIndex(cut, v => Math.Abs(v) > 0.3);
                double onset = 4.0 + firstLoud / 12000.0;
                check(miss == 0 && Math.Abs(onset - 5.0) < 0.01, "slot recorder timing: tone found at " + onset.ToString("0.000") + " s (5.000 expected)");
            }

            // 5. the QSO sequencer: answering a CQ through to RR73 and 73
            {
                QsoEngine q = new QsoEngine { MyCall = "K7JUS", MyGrid = "CN87" };
                List<QsoRecord> log = new List<QsoRecord>();
                q.Logged += r => log.Add(r);
                Func<string, int, DecodeLine> d = (m, s) => new DecodeLine { Message = m, Snr = s, Freq = 1000, Mode = FtxMode.FT8 };
                q.Answer(d("CQ K1ABC FN42", -10));
                check(q.NextMessage == "K1ABC K7JUS CN87", "answer CQ: " + q.NextMessage);
                q.Sequence(new[] { d("K7JUS K1ABC -15", -9) }, null);
                check(q.NextMessage == "K1ABC K7JUS R-09", "report received: " + q.NextMessage);
                q.Sequence(new[] { d("K7JUS K1ABC RR73", -8) }, null);
                check(q.NextMessage == "K1ABC K7JUS 73" && log.Count == 1 && log[0].RstRcvd == "-15" && log[0].Grid == "FN42", "RR73 received, logged: " + q.NextMessage);

                q.Clear(); log.Clear();
                q.Next = 6;
                q.Sequence(new[] { d("K7JUS W9XYZ EN37", -3) }, null);
                check(q.HisCall == "W9XYZ" && q.NextMessage == "W9XYZ K7JUS -03", "CQ answered: " + q.NextMessage);
                q.Sequence(new[] { d("K7JUS W9XYZ R-11", -4) }, null);
                check(q.NextMessage == "W9XYZ K7JUS RR73" && log.Count == 1, "roger received, RR73 next, logged");
                q.Sequence(new[] { d("K7JUS W9XYZ 73", -4) }, null);
                check(q.Done && log.Count == 1, "73 received: QSO done, logged once");
            }

            // 6. parsing jt9's lines (an a-priori decode, a duplicate)
            {
                List<DecodeLine> p = Jt9Decoder.Parse("120015 -12  0.3 1234 ~  K7JUS K1ABC FN42           a1\n120015  -3 -0.1  456 ~  CQ W1AW FN31\n120015  -3 -0.1  456 ~  CQ W1AW FN31\n<DecodeFinished>   0   2        0\n",
                    new DateTime(2026, 10, 16, 12, 0, 15, DateTimeKind.Utc), FtxMode.FT8);
                check(p.Count == 2 && p[0].Ap && p[0].Message == "K7JUS K1ABC FN42" && p[1].Snr == -3 && p[1].Freq == 456, "jt9 output parsed");
            }

            // 9. WSJT-X UDP: a stand-in GridTracker on a local port gets our heartbeat, a decode and a logged QSO in
            // WSJT-X's format, and its Reply and Halt Tx come back to us
            {
                check(Net.QWriter.JulianDay(new DateTime(2026, 10, 16)) == 2461330, "QDateTime Julian day (2026-10-16 = 2461330)");
                using (System.Net.Sockets.UdpClient gt = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
                using (Net.WsjtxUdp link = new Net.WsjtxUdp("Kainos Digital Modes test", "kainos") { Id = "Kainos" })
                {
                    gt.Client.ReceiveTimeout = 3000;
                    int port = ((System.Net.IPEndPoint)gt.Client.LocalEndPoint).Port;
                    string why;
                    check(link.Start("127.0.0.1", port, out why), "UDP link starts" + (why != null ? ": " + why : ""));
                    WsjtxReply gotReply = null;
                    bool? gotHalt = null;
                    link.Reply += r => gotReply = r;
                    link.HaltTx += a => gotHalt = a;

                    System.Net.IPEndPoint from = null;
                    Func<uint, byte[]> next = type =>
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            byte[] b = gt.Receive(ref from);
                            Net.QReader h = new Net.QReader(b);
                            if (h.U32() == Net.WsjtxUdp.Magic && h.U32() >= 2 && h.U32() == type) return b;
                        }
                        return null;
                    };
                    byte[] hb = next(0);
                    Net.QReader q = new Net.QReader(hb);
                    q.U32(); q.U32(); q.U32();
                    check(hb != null && q.Utf8() == "Kainos" && q.U32() == 3 && q.Utf8() == "Kainos Digital Modes test", "heartbeat in WSJT-X's format");

                    DateTime slot = new DateTime(2026, 10, 16, 12, 34, 45, DateTimeKind.Utc);
                    link.Decode(true, slot, -12, 0.3, 1234, "~", "CQ K1ABC FN42", false);
                    byte[] db = next(2);
                    q = new Net.QReader(db);
                    q.U32(); q.U32(); q.U32(); q.Utf8();
                    bool isNew = q.Bool(); TimeSpan tm = q.QTime(); int snr = q.I32(); double dt = q.Double(); uint df = q.U32(); string md = q.Utf8(), msg = q.Utf8();
                    check(isNew && tm == slot.TimeOfDay && snr == -12 && Math.Abs(dt - 0.3) < 1e-9 && df == 1234 && md == "~" && msg == "CQ K1ABC FN42" && !q.Bool() && !q.Bool() && !q.More,
                        "decode in WSJT-X's format");

                    link.QsoLogged(slot.AddMinutes(2), "K1ABC", "FN42", 14075600, "FT8", "-12", "-09", "5W", "", "", slot, "", "K7JUS", "CN87");
                    byte[] lb = next(5);
                    q = new Net.QReader(lb);
                    q.U32(); q.U32(); q.U32(); q.Utf8();
                    long jd = (long)q.U64(); TimeSpan offT = q.QTime(); byte spec = q.U8();
                    check(jd == 2461330 && offT == slot.AddMinutes(2).TimeOfDay && spec == 1 && q.Utf8() == "K1ABC" && q.Utf8() == "FN42" && q.U64() == 14075600, "QSO logged in WSJT-X's format");

                    // GridTracker double-clicks a decode, then halts TX (sent back to where our messages came from)
                    byte[] reply = Net.WsjtxUdp.Build(WsjtxType.Reply, "Kainos", w => { w.QTime(slot); w.I32(-12); w.Double(0.3); w.U32(1234); w.Utf8("~"); w.Utf8("CQ K1ABC FN42"); w.Bool(false); w.U8(0); });
                    gt.Send(reply, reply.Length, from);
                    byte[] halt = Net.WsjtxUdp.Build(WsjtxType.HaltTx, "Kainos", w => w.Bool(true));
                    gt.Send(halt, halt.Length, from);
                    byte[] other = Net.WsjtxUdp.Build(WsjtxType.HaltTx, "WSJT-X", w => w.Bool(false));      // for another station: ignored
                    gt.Send(other, other.Length, from);
                    DateTime until = DateTime.UtcNow.AddSeconds(3);
                    while ((gotReply == null || gotHalt == null) && DateTime.UtcNow < until) System.Threading.Thread.Sleep(20);
                    System.Threading.Thread.Sleep(200);
                    check(gotReply != null && gotReply.Message == "CQ K1ABC FN42" && gotReply.Df == 1234 && gotReply.Snr == -12 && gotReply.Time == slot.TimeOfDay, "Reply from another program arrives");
                    check(gotHalt == true, "Halt Tx (auto only) arrives, and one for another station is ignored");
                }
            }

            // 8. a whole QSO in simulated time: K1ABC calls CQ, we answer it, and each of our replies must go out in the
            // very next period (not a period late), starting on time; then transmitting stops after our 73
            // (in real time, with decoding in the background as in Kainos; FT8 and FT4 side by side, about 2 minutes)
            List<KeyValuePair<bool, string>> qsoResults = new List<KeyValuePair<bool, string>>();
            System.Threading.Tasks.Parallel.ForEach(new[] { FtxMode.FT8, FtxMode.FT4 }, mode =>
            {
                double p = FtxModes.Period(mode);
                DateTime t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
                DateTime vnow = t0.AddSeconds(-1), vstart = vnow;
                SimAir air = new SimAir(vnow);
                FakeHost host = new FakeHost(bin, Path.Combine(work, "qso-" + mode)) { Air = air, Clock = () => vnow, Window = null };
                DigiEngine eng = new DigiEngine(host) { Now = () => vnow, Ft8CodeExe = ft8code, Mode = mode, RxFreq = 1000, TxFreq = 1600, HoldTx = true };
                eng.Decoder.Jt9Exe = jt9;
                eng.Decoder.WorkFolder = Path.Combine(work, "qso-jt9-" + mode);
                eng.Qso.MyCall = "K7JUS"; eng.Qso.MyGrid = "CN87";
                eng.Activity();

                // what K1ABC sends, in the even periods, at 1000 Hz
                string[] his = { "CQ K1ABC FN42", null, "K7JUS K1ABC -10", null, "K7JUS K1ABC RR73" };
                for (int i = 0; i < his.Length; i++)
                {
                    if (his[i] == null) continue;
                    string why;
                    PackedMessage pm = Pack77.Pack(ft8code, his[i], out why);
                    byte[] pl = FtxEncoder.PayloadFromBits(pm.Bits77);
                    byte[] tn = mode == FtxMode.FT8 ? FtxEncoder.Ft8Tones(pl) : FtxEncoder.Ft4Tones(pl);
                    air.Add(t0.AddSeconds(i * p + FtxModes.TxStart(mode)), FtxModulator.Synthesize(mode, tn, 1000, SimAir.Rate));
                }

                List<string> sent = new List<string>();
                bool answered = false;
                eng.TxStarted += (slot, msg) => sent.Add(((int)Math.Round((slot - t0).TotalSeconds / p)) + ":" + msg + "@" + (vnow - slot).TotalSeconds.ToString("0.00"));
                eng.Decoded += (slot, m, list) =>
                {
                    DecodeLine cq = list.FirstOrDefault(d => d.Message == "CQ K1ABC FN42");
                    if (cq == null || answered) return;
                    answered = true;                    // the operator double-clicks the CQ (as DigiPane.answer does)
                    lock (eng.Qso) eng.Qso.Answer(cq);
                    eng.TxSecond = true;
                    eng.TxEnabled = true;
                };
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                while (vnow < t0.AddSeconds(8 * p))
                {
                    eng.Step();
                    System.Threading.Thread.Sleep(15);
                    vnow = vstart + sw.Elapsed;
                }
                while (eng.Decoding) System.Threading.Thread.Sleep(50);
                string got = string.Join("  |  ", sent);
                Func<int, string, bool> at = (k, m) => sent.Any(x => x.StartsWith(k + ":" + m + "@") && double.Parse(x.Substring(x.IndexOf('@') + 1)) <= 0.5);
                bool ok = sent.Count == 3 && at(1, "K1ABC K7JUS CN87") && sent.Count(x => x.StartsWith("3:K1ABC K7JUS R")) == 1 && at(3, sent.First(x => x.StartsWith("3:")).Split(':', '@')[1]) && at(5, "K1ABC K7JUS 73");
                lock (qsoResults)
                {
                    qsoResults.Add(new KeyValuePair<bool, string>(ok, mode + " QSO in real time, each reply in the next period, on time: " + got));
                    qsoResults.Add(new KeyValuePair<bool, string>(!eng.TxEnabled, mode + " transmitting stops after 73"));
                }
            });
            foreach (KeyValuePair<bool, string> r in qsoResults) check(r.Key, r.Value);

            // 7. the module in a stand-in Kainos: start, open the pane, run its timers, resize, close, stop
            {
                List<Exception> uiErrors = new List<Exception>();
                System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
                System.Windows.Forms.Application.ThreadException += (s, e) => uiErrors.Add(e.Exception);
                string stage = "start";
                try
                {
                    FakeHost host = new FakeHost(bin.EndsWith("decoder") ? Path.GetDirectoryName(bin) : bin, Path.Combine(work, "data"));
                    host.Settings["decoder"] = bin;
                    using (System.Windows.Forms.Form w = new System.Windows.Forms.Form { Width = 1200, Height = 300 })
                    {
                        host.Window = w;
                        w.Show();
                        DigiModesModule mod = new DigiModesModule();
                        mod.Start(host);
                        stage = "open";
                        host.Menus[0].Click();
                        pump(1200);
                        stage = "resize";
                        foreach (System.Drawing.Size sz in new[] { new System.Drawing.Size(640, 200), new System.Drawing.Size(1800, 420), new System.Drawing.Size(1100, 260) })
                        { w.Size = sz; pump(200); }
                        // the lists full (scrolling), and the docked size (236 px high at 100%): nothing may stick out
                        // of its parent
                        stage = "fill";
                        foreach (UI.DecodeList l in all(w).OfType<UI.DecodeList>())
                            for (int i = 0; i < 80; i++) l.Add(new UI.DecodeRow { Text = "120015 -12  0.3 1234  CQ K1ABC FN42", Kind = i % 5 == 0 ? UI.RowKind.Cq : UI.RowKind.Normal });
                        foreach (int width in new[] { 1080, 1400 })
                        {
                            w.ClientSize = new System.Drawing.Size(width, 236);
                            pump(300);
                            List<string> outside = all(w).Where(c => c.Visible && c.Parent != null && c.Parent != w &&
                                (c.Left < 0 || c.Top < 0 || c.Right > c.Parent.ClientSize.Width || c.Bottom > c.Parent.ClientSize.Height))
                                .Select(c => c.GetType().Name + " '" + c.Text + "' " + c.Bounds + " in " + c.Parent.ClientSize).ToList();
                            check(outside.Count == 0, "pane fits at " + width + " x 236" + (outside.Count > 0 ? ": " + string.Join("; ", outside.Take(4)) : ""));
                        }
                        stage = "close";
                        host.Menus[0].Click();
                        pump(200);
                        stage = "stop";
                        mod.Stop();
                        mod.Dispose();
                    }
                    check(uiErrors.Count == 0, "module UI runs in a stand-in Kainos" + (uiErrors.Count > 0 ? ": " + uiErrors[0] : ""));
                }
                catch (Exception ex) { check(false, "module UI (" + stage + "): " + ex); }
            }

            System.Console.WriteLine(_fail == 0 ? "All passed" : _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static IEnumerable<System.Windows.Forms.Control> all(System.Windows.Forms.Control root)
        {
            foreach (System.Windows.Forms.Control c in root.Controls)
            {
                yield return c;
                foreach (System.Windows.Forms.Control d in all(c)) yield return d;
            }
        }

        private static void pump(int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        }

        private static double gauss(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }
}
