/*  Program.cs (self-test)

Checks the module without a radio: the encoder against WSJT-X's ft8code, and the whole path (tones -> GFSK at
48 kHz -> noise -> 12 kHz resampler -> jt9) decoding what was sent, for FT8 and FT4; the QSO sequencer; parsing.

  Kainos.DigiModes.SelfTest.exe <folder with jt9.exe and ft8code.exe>
  Kainos.DigiModes.SelfTest.exe <folder> --show      the pane in a stand-in Kainos with a made-up waterfall, to look at
                                         [--blurry]  ... with the waterfall as Windows stretches it (Sharp waterfall off)

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
            // before any control exists (the UI tests catch exceptions on the UI thread)
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
            string bin = args.Length > 0 ? args[0] : @"C:\WSJT\wsjtx\bin";
            string ft8code = Path.Combine(bin, "ft8code.exe"), jt9 = Path.Combine(bin, "jt9.exe");
            string work = Path.Combine(Path.GetTempPath(), "kainos-digimodes-selftest");
            Directory.CreateDirectory(work);
            if (args.Contains("--show")) return show(bin, work);

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
            foreach (FtxMode mode in new[] { FtxMode.FT8, FtxMode.FT4, FtxMode.FT2 })
            {
                foreach (double snr in mode == FtxMode.FT2 ? new[] { 0.0, -8.0, -11.0 } : new[] { 0.0, -12.0, mode == FtxMode.FT8 ? -19.0 : -15.0 })
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

            // 17. JT65, JT9 and WSPR on the air (simulated): our tones and FSK decoded by WSJT-X's jt9 and wsprd
            {
                foreach (FtxMode mode in new[] { FtxMode.JT65, FtxMode.JT9 })
                {
                    string exe = Path.Combine(bin, mode == FtxMode.JT65 ? "jt65code.exe" : "jt9code.exe"), m = "CQ K7JUS CN87", why, asDec;
                    byte[] tones = OtherModes.JtTones(mode, exe, m, out asDec, out why);
                    if (tones == null) { check(false, mode + " tones: " + why); continue; }
                    float[] w = OtherModes.SynthesizeFsk(mode, tones, 1200, 12000);
                    float[] slot = new float[720000];
                    int at = (int)(FtxModes.TxStart(mode) * 12000);
                    for (int i = 0; i < slot.Length; i++) slot[i] = (float)(gauss(rnd) * 0.05 + (i >= at && i - at < w.Length ? w[i - at] * 0.03 : 0));
                    DateTime st = new DateTime(2026, 10, 16, 12, 10, 0, DateTimeKind.Utc);
                    if (args.Contains("--keepwav")) Wav.Write16(Path.Combine(work, "test-" + mode + ".wav"), slot, 12000);
                    List<DecodeLine> res = dec.Decode(mode, st, slot, 1200, "K7JUS", "CN87", "", out why);
                    DecodeLine hit = res.FirstOrDefault(r => r.Message == m);
                    check(hit != null && Math.Abs(hit.Dt) <= 0.3 && Math.Abs(hit.Freq - 1200) <= 3 && asDec == m,
                        mode + " decodes: " + (hit != null ? hit.ToString() : "nothing (" + (why ?? "") + ", " + string.Join(" | ", res.Select(r => r.Message)) + ")") + ", encoder says it reads \"" + asDec + "\"");
                }
            }

            // 18. the engine in JT65 and WSPR, on a simulated clock (decoding as it happens: JT65's and WSPR's decodes
            // run long before the next period, so the frozen clock doesn't matter)
            {
                // JT65: W9XYZ calls CQ; we answer; his report; our R-report; his RRR; our 73
                DateTime t0 = new DateTime(2026, 10, 16, 13, 0, 0, DateTimeKind.Utc);
                DateTime vnow = t0.AddSeconds(-1);
                SimAir air = new SimAir(vnow);
                FakeHost host = new FakeHost(bin, Path.Combine(work, "jt65")) { Air = air, Clock = () => vnow, Window = null };
                DigiEngine eng = new DigiEngine(host) { Now = () => vnow, SyncDecode = true, Mode = FtxMode.JT65, RxFreq = 1000, TxFreq = 1400, HoldTx = true };
                eng.Decoder.Jt9Exe = jt9; eng.Decoder.WorkFolder = Path.Combine(work, "jt65-jt9"); eng.Jt65CodeExe = Path.Combine(bin, "jt65code.exe");
                eng.Qso.MyCall = "K7JUS"; eng.Qso.MyGrid = "CN87"; eng.Qso.Jt = true;
                eng.Activity();
                string[] his = { "CQ W9XYZ EN37", null, "K7JUS W9XYZ -14", null, "K7JUS W9XYZ RRR" };
                for (int i = 0; i < his.Length; i++)
                {
                    if (his[i] == null) continue;
                    string why, asDec;
                    byte[] tn = OtherModes.JtTones(FtxMode.JT65, eng.Jt65CodeExe, his[i], out asDec, out why);
                    air.Add(t0.AddSeconds(i * 60 + 1.0), OtherModes.SynthesizeFsk(FtxMode.JT65, tn, 1000, SimAir.Rate));
                }
                List<string> sent = new List<string>();
                bool answered = false;
                eng.TxStarted += (slot, msg) => sent.Add(((int)Math.Round((slot - t0).TotalSeconds / 60)) + ":" + msg);
                eng.Decoded += (slot, m, list) =>
                {
                    DecodeLine cq = list.FirstOrDefault(d => d.Message == "CQ W9XYZ EN37");
                    if (cq == null || answered) return;
                    answered = true;
                    lock (eng.Qso) eng.Qso.Answer(cq);
                    eng.TxSecond = true;
                    eng.TxEnabled = true;
                };
                while (vnow < t0.AddSeconds(6 * 60)) { eng.Step(); vnow = vnow.AddMilliseconds(50); }
                check(sent.Count == 3 && sent[0] == "1:W9XYZ K7JUS CN87" && sent[1].StartsWith("3:W9XYZ K7JUS R-") && sent[2] == "5:W9XYZ K7JUS 73",
                    "JT65 QSO through the engine, each reply in the next minute: " + string.Join("  |  ", sent));

                // WSPR: W9XYZ's beacon in the first period is decoded; ours goes out at the start of a period (100 % share)
                DateTime w0 = new DateTime(2026, 10, 16, 14, 0, 0, DateTimeKind.Utc);
                DateTime wnow = w0.AddSeconds(-1);
                SimAir wair = new SimAir(wnow);
                FakeHost wh = new FakeHost(bin, Path.Combine(work, "wsprengine")) { Air = wair, Clock = () => wnow, Window = null, Vfo = 14095600 };
                DigiEngine we = new DigiEngine(wh) { Now = () => wnow, SyncDecode = true, Mode = FtxMode.WSPR, TxFreq = 1500, WsprDbm = 37, WsprTxPercent = 100 };
                we.Wspr.Exe = Path.Combine(bin, "wsprd.exe"); we.Wspr.WorkFolder = Path.Combine(work, "wsprengine-wsprd");
                we.Qso.MyCall = "K7JUS"; we.Qso.MyGrid = "CN87";
                string wwhy;
                wair.Add(w0.AddSeconds(1), OtherModes.SynthesizeFsk(FtxMode.WSPR, OtherModes.WsprTones("W9XYZ", "EN37", 30, out wwhy), 1450, SimAir.Rate).Select(v => v * 0.3f).ToArray());
                List<DecodeLine> heard = new List<DecodeLine>();
                List<string> wsent = new List<string>();
                we.Decoded += (slot, m, list) => heard.AddRange(list);
                we.TxStarted += (slot, msg) => wsent.Add(((int)Math.Round((slot - w0).TotalSeconds / 120)) + ":" + msg + "@" + (wnow - slot).TotalSeconds.ToString("0.0"));
                while (wnow < w0.AddSeconds(118)) { we.Step(); wnow = wnow.AddMilliseconds(50); }
                we.TxEnabled = true;
                while (wnow < w0.AddSeconds(125)) { we.Step(); wnow = wnow.AddMilliseconds(50); }
                DecodeLine wsp = heard.FirstOrDefault(d => d.Wspr != null && d.Wspr.Call == "W9XYZ");
                check(wsp != null && wsp.Wspr.Grid == "EN37" && wsp.Wspr.Dbm == 30 && Math.Abs(wsp.Wspr.FreqMHz - 14.09705) < 0.00002,
                    "WSPR through the engine: W9XYZ's beacon decoded (" + (wsp != null ? wsp.Wspr.ToString() : "nothing") + ")");
                check(wsent.Count == 1 && wsent[0].StartsWith("1:K7JUS CN87 37@"), "WSPR beacon sent at the start of a period: " + string.Join(" | ", wsent));
            }

            // 17b. WSPR: our encoder and FSK, decoded by WSJT-X's wsprd
            {
                string why;
                byte[] wt = OtherModes.WsprTones("K7JUS", "CN87", 37, out why);
                if (wt == null) check(false, "WSPR tones: " + why);
                else
                {
                    float[] w = OtherModes.SynthesizeFsk(FtxMode.WSPR, wt, 1500, 12000);
                    float[] slot = new float[(int)(114 * 12000)];
                    for (int i = 0; i < slot.Length; i++) slot[i] = (float)(gauss(rnd) * 0.05 + (i >= 12000 && i - 12000 < w.Length ? w[i - 12000] * 0.01 : 0));
                    Decode.WsprDecoder wd = new Decode.WsprDecoder { Exe = Path.Combine(bin, "wsprd.exe"), WorkFolder = Path.Combine(work, "wspr") };
                    List<Decode.WsprSpot> sp = wd.Decode(new DateTime(2026, 10, 16, 12, 10, 0, DateTimeKind.Utc), slot, 14.0956, out why);
                    Decode.WsprSpot s0 = sp.FirstOrDefault();
                    check(s0 != null && s0.Call == "K7JUS" && s0.Grid == "CN87" && s0.Dbm == 37 && Math.Abs(s0.FreqMHz - 14.0971) < 0.00001 && Math.Abs(s0.Dt) <= 0.5,
                        "WSPR decodes (wsprd): " + (s0 != null ? s0.ToString() : "nothing (" + (why ?? "") + ", raw: " + wd.LastOutput + ")"));
                }
                byte[] bad = OtherModes.WsprTones("K7JUS/P", "CN87", 37, out why);
                byte[] badPwr = OtherModes.WsprTones("K7JUS", "CN87", 36, out why);
                check(bad == null && badPwr == null, "WSPR refuses what it can't send (compound calls, odd powers)");
            }

            // 16. Hound (DXpedition): the QSO with a Fox, his two-part messages, frequencies; and jt9 resolving the Fox's
            // hashed call when it's told who we're working
            {
                Func<string, int, int, DecodeLine> d = (m, s, f) => new DecodeLine { Message = m, Snr = s, Freq = f, Mode = FtxMode.FT8 };
                QsoEngine h = new QsoEngine { MyCall = "K7JUS", MyGrid = "CN87", Activity = Contest.Hound };
                List<QsoRecord> hl = new List<QsoRecord>();
                h.Logged += r => hl.Add(r);
                h.Answer(d("CQ KH1/KH7Z", -9, 420));
                string tx1 = h.NextMessage;
                h.Sequence(new[] { d("W9XYZ RR73; K7JUS <KH1/KH7Z> -17", -11, 480) }, tx1);
                string tx3 = h.NextMessage;
                int at = h.FoxCalledAt;
                h.Sequence(new[] { d("K7JUS RR73; G4AAA <KH1/KH7Z> -09", -10, 480) }, tx3);
                bool flow = tx1 == "KH7Z K7JUS CN87" && tx3 == "KH7Z K7JUS R-11" && at == 480 && hl.Count == 1 && hl[0].Call == "KH1/KH7Z" && hl[0].RstRcvd == "-17" && h.Done;

                // the plain form: "K7JUS KH7Z -13", then "K7JUS KH7Z RR73"
                QsoEngine h2 = new QsoEngine { MyCall = "K7JUS", MyGrid = "CN87", Activity = Contest.Hound, HisCall = "KH1/KH7Z", Next = 1 };
                h2.Sequence(new[] { d("K7JUS KH7Z -13", -8, 360) }, null);
                bool plain = h2.Next == 3 && h2.FoxCalledAt == 360;
                h2.Sequence(new[] { d("K7JUS KH7Z RR73", -8, 360) }, null);
                plain &= h2.Done;

                // frequencies: calls at 1000 Hz or above; R+report where Fox called, then 300 Hz away, then back
                DigiEngine he = new DigiEngine(new FakeHost(bin, Path.Combine(work, "hound"))) { TxFreq = 800 };
                he.Qso.Activity = Contest.Hound; he.Qso.HisCall = "KH1/KH7Z"; he.Qso.Next = 1;
                int c1 = he.houndFreq();
                he.Qso.Next = 3; he.Qso.FoxCalledAt = 420;
                int r1 = he.houndFreq(), r2 = he.houndFreq(), r3 = he.houndFreq();
                bool freqs = c1 == 1000 && r1 == 420 && r2 == 720 && r3 == 420;

                string why;
                PackedMessage pa = Pack77.Pack(ft8code, tx1, out why), pb = Pack77.Pack(ft8code, tx3, out why);
                bool packs = pa != null && pa.Decoded == tx1 && pb != null && pb.Decoded == tx3;

                // a Fox's two-part message on the air: decoded, with <KH1/KH7Z> resolved when jt9 knows we're working him
                string fox = "W9XYZ RR73; K7JUS <KH1/KH7Z> -17";
                PackedMessage pf = Pack77.Pack(ft8code, fox, out why);
                bool air = false; string got = why ?? "";
                if (pf != null)
                {
                    float[] wave = FtxModulator.Synthesize(FtxMode.FT8, FtxEncoder.Ft8Tones(FtxEncoder.PayloadFromBits(pf.Bits77)), 480, 12000);
                    float[] slot = new float[180000];
                    for (int i = 0; i < slot.Length; i++) slot[i] = (float)(gauss(rnd) * 0.02 + (i >= 6000 && i - 6000 < wave.Length ? wave[i - 6000] * 0.1 : 0));
                    List<DecodeLine> res = dec.Decode(FtxMode.FT8, new DateTime(2026, 10, 16, 12, 2, 0, DateTimeKind.Utc), slot, 480, "K7JUS", "CN87", "KH1/KH7Z", out why);
                    got = string.Join(" | ", res.Select(r => r.Message));
                    DecodeLine heard = res.FirstOrDefault(r => r.Message.StartsWith("W9XYZ RR73; K7JUS"));
                    // jt9 shows the hash as <...> (it can't resolve it in this mode): the sequencer must still take it as the Fox
                    QsoEngine h3 = new QsoEngine { MyCall = "K7JUS", MyGrid = "CN87", Activity = Contest.Hound, HisCall = "KH1/KH7Z", Next = 1 };
                    air = heard != null && h3.Sequence(new[] { heard }, null) && h3.Next == 3 && h3.FoxCalledAt == heard.Freq;
                }
                check(flow, "Hound: call the Fox (base call), his report in a two-part message, R+report, his RR73 logs it");
                check(plain, "Hound: Fox's plain report and RR73");
                check(freqs, "Hound frequencies: call at 1000 Hz or above (" + c1 + "), R+report at " + r1 + ", then " + r2 + ", then " + r3);
                check(packs, "Hound messages pack exactly");
                check(air, "a Fox's two-part message off the air (jt9: " + got + ") moves the Hound to R+report");
            }

            // 14. the Band menu list: parse, format, mistakes caught
            {
                string why;
                List<Bands.Entry> l = Bands.Parse("40m 7.074 7.0475\r\n# a comment\r\nDXped 14.090 -\r\n60m 5.357", out why);
                bool ok = l != null && l.Count == 3 && l[1].Name == "DXped" && l[1].Ft8 == 14090000 && l[1].Ft4 == 0 && l[2].Ft4 == 0 && l[0].Ft4 == 7047500;
                string badWhy;
                List<Bands.Entry> bad = Bands.Parse("40m 7.074\r\n20m fourteen", out badWhy);
                List<Bands.Entry> round = Bands.Parse(Bands.Format(Bands.Defaults()), out why);
                check(ok && bad == null && badWhy != null && badWhy.StartsWith("Line 2") && round != null && Bands.Format(round) == Bands.Format(Bands.Defaults()),
                    "band frequencies: parse, mistakes caught (" + badWhy + "), defaults round-trip");
            }

            // 15. contests: every message the sequencer makes packs exactly (ft8code), and each flow runs to the log
            {
                Func<string, int, DecodeLine> d = (m, s) => new DecodeLine { Message = m, Snr = s, Freq = 1000, Mode = FtxMode.FT8 };
                List<string> problems = new List<string>();
                Action<QsoEngine> packs = q =>
                {
                    foreach (int n in new[] { 1, 2, 3, 6 })
                    {
                        string m = q.Message(n), why;
                        PackedMessage pm = Pack77.Pack(ft8code, m, out why);
                        if (pm == null || pm.Decoded != m) problems.Add(m + " -> " + (pm != null ? pm.Decoded : why));
                    }
                };

                // Field Day: I answer W9XYZ's CQ; he sends R and his; I send RR73 and it's logged
                QsoEngine fd = new QsoEngine { MyCall = "K7JUS", MyGrid = "CN87", Activity = Contest.FieldDay, MyExchange = "1D WWA" };
                List<QsoRecord> logs = new List<QsoRecord>();
                fd.Logged += r => logs.Add(r);
                fd.Answer(d("CQ FD W9XYZ EN37", -8));
                string fd1 = fd.NextMessage;
                fd.Sequence(new[] { d("K7JUS W9XYZ R 6A WI", -7) }, null);
                string fd3 = fd.NextMessage;
                fd.Sent(fd3);
                bool fdOk = fd1 == "W9XYZ K7JUS 1D WWA" && fd3 == "W9XYZ K7JUS RR73" && logs.Count == 1 && logs[0].ExchRcvd == "6A WI" && logs[0].ExchSent == "1D WWA" && fd.Done;
                string adif = AdifLog.Record(logs.Count > 0 ? logs[0] : new QsoRecord(), FtxMode.FT8, "K7JUS", "CN87");
                fdOk &= adif.Contains("<CONTEST_ID:7>ARRL-FD") && adif.Contains("<CLASS:2>6A") && adif.Contains("<ARRL_SECT:2>WI") && adif.Contains("<STX_STRING:6>1D WWA");
                packs(fd);

                // Field Day run: I call CQ FD; W9XYZ answers with his; I send R and mine; his RR73 logs it and I'm back to CQ
                QsoEngine run = new QsoEngine { MyCall = "K7JUS", MyGrid = "CN87", Activity = Contest.FieldDay, MyExchange = "1D WWA", Next = 6 };
                List<QsoRecord> runLog = new List<QsoRecord>();
                run.Logged += r => runLog.Add(r);
                string cq = run.NextMessage;
                run.Sequence(new[] { d("K7JUS W9XYZ 6A WI", -3) }, cq);
                string r2 = run.NextMessage;
                run.Sequence(new[] { d("K7JUS W9XYZ RR73", -3) }, r2);
                bool runOk = cq == "CQ FD K7JUS CN87" && r2 == "W9XYZ K7JUS R 1D WWA" && runLog.Count == 1 && runLog[0].Call == "W9XYZ" && run.Next == 6 && !run.Done && run.HisCall == "";

                // RTTY Roundup outside the US: serial numbers, counting up
                QsoEngine ru = new QsoEngine { MyCall = "DL1ABC", MyGrid = "JO62", Activity = Contest.RttyRoundup, MyExchange = "", Serial = 12 };
                List<QsoRecord> ruLog = new List<QsoRecord>();
                ru.Logged += r => ruLog.Add(r);
                ru.Answer(d("CQ RU K7JUS CN87", -2));
                string ru1 = ru.NextMessage;
                ru.Sequence(new[] { d("DL1ABC K7JUS R 579 WA", -2) }, null);
                ru.Sent(ru.NextMessage);
                bool ruOk = ru1 == "K7JUS DL1ABC 599 0012" && ruLog.Count == 1 && ruLog[0].RstRcvd == "579" && ruLog[0].ExchRcvd == "579 WA" && ru.Serial == 13;
                packs(ru);

                // NA VHF: grids
                QsoEngine vhf = new QsoEngine { MyCall = "K7JUS", MyGrid = "CN87", Activity = Contest.NaVhf };
                vhf.Answer(d("CQ TEST W9XYZ EN37", -10));
                string v1 = vhf.NextMessage;
                vhf.Sequence(new[] { d("K7JUS W9XYZ R EN37", -10) }, null);
                bool vhfOk = v1 == "W9XYZ K7JUS CN87" && vhf.NextMessage == "W9XYZ K7JUS RR73" && vhf.HisGrid == "EN37";
                packs(vhf);
                packs(new QsoEngine { MyCall = "K7JUS", MyGrid = "CN87", HisCall = "W9XYZ", Activity = Contest.WwDigi });

                check(fdOk, "Field Day: answer a CQ, his R and exchange, RR73, logged with CLASS / ARRL_SECT");
                check(runOk, "Field Day run: CQ FD, his exchange, R and mine, his RR73 logs it and it's back to CQ");
                check(ruOk, "RTTY Roundup: serial numbers outside the US and Canada (" + ru1 + "), counting up");
                check(vhfOk, "NA VHF: grids as the exchange");
                check(problems.Count == 0, "every contest message packs exactly" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : ""));
            }

            // 13. band activity filters, and ALL.TXT's line format
            {
                FakeHost th = new FakeHost(bin, Path.Combine(work, "filter"));
                UI.DecodeList l = new UI.DecodeList(th);
                l.Separator("12:00:00");
                l.Add(new UI.DecodeRow { Text = "a", Kind = UI.RowKind.Cq });
                l.Add(new UI.DecodeRow { Text = "b", Kind = UI.RowKind.Normal });
                l.Add(new UI.DecodeRow { Text = "c", Kind = UI.RowKind.ToMe });
                l.Add(new UI.DecodeRow { Text = "d", Kind = UI.RowKind.Cq, Mark = WorkedMark.WorkedBand });
                l.Separator("12:00:15");
                l.Add(new UI.DecodeRow { Text = "e", Kind = UI.RowKind.Normal });
                int all = l.Shown;
                l.Filter = r => r.Kind == UI.RowKind.Cq || r.Kind == UI.RowKind.ToMe;
                int cqOnly = l.Shown;               // heading, a, c, d (the second heading has nothing under it but is the last)
                l.Filter = r => (r.Kind == UI.RowKind.Cq || r.Kind == UI.RowKind.ToMe) && (r.Mark != WorkedMark.WorkedBand || r.Kind == UI.RowKind.ToMe);
                int both = l.Shown;
                l.Filter = null;
                check(all == 7 && cqOnly == 5 && both == 4 && l.Shown == 7, "band activity filters: all " + all + ", CQ only " + cqOnly + ", and hide worked " + both);

                string al = UI.DigiPane.line(new DateTime(2026, 10, 16, 12, 30, 15, DateTimeKind.Utc), 14074000, "Rx", FtxMode.FT8, -12, 0.3, 1234, "CQ K1ABC FN42");
                check(al == "261016_123015    14.074 Rx FT8    -12  0.3 1234 CQ K1ABC FN42", "ALL.TXT line as WSJT-X writes it: [" + al + "]");
            }

            // 12. worked before: countries from cty.dat, and how new each sender is
            {
                string ctyPath = Path.Combine(Path.GetDirectoryName(bin.TrimEnd('\\')), bin.EndsWith("decoder") ? "cty.dat" : @"share\wsjtx\cty.dat");
                if (!File.Exists(ctyPath)) ctyPath = @"C:\WSJT\wsjtx\share\wsjtx\cty.dat";
                Cty cty = Cty.Load(ctyPath);
                Func<string, string> c = call => { CtyEntity e = cty.Lookup(call); return e != null ? e.Name : "?"; };
                string got = string.Join(", ", new[] { "K7JUS", "VK2XYZ", "W1AW/P", "VP2E/K1ABC", "K1ABC/KH6", "9M6/LA6VM", "W1AW/4", "JA1ABC" }.Select(x => x + "=" + c(x)));
                check(c("K7JUS") == "United States" && c("VK2XYZ") == "Australia" && c("W1AW/P") == "United States" && c("VP2E/K1ABC") == "Anguilla" &&
                      c("K1ABC/KH6") == "Hawaii" && c("9M6/LA6VM") == "Spratly Islands" && c("W1AW/4") == "United States" && c("JA1ABC") == "Japan", "cty.dat lookups: " + got);

                Worked wk = new Worked { Cty = cty };
                File.WriteAllText(Path.Combine(work, "test.adi"), "test\r\n<EOH>\r\n<CALL:6>VK2ABC <GRIDSQUARE:6>QF56ab <BAND:3>20m <MODE:3>FT8 <EOR>\r\n<call:5>W1AW <band:3>40M <EOR>\r\n");
                int n = wk.LoadAdif(Path.Combine(work, "test.adi"));
                Func<string, string, WorkedMark> m = (msg, b) => { WorkedInfo i = wk.Classify(msg, b); return i != null ? i.Mark : (WorkedMark)(-1); };
                check(n == 2 && m("CQ JA1ABC PM95", "20M") == WorkedMark.NewDxcc && m("CQ VK3XYZ QF22", "40M") == WorkedMark.NewDxccBand &&
                      m("CQ VK3XYZ QF22", "20M") == WorkedMark.NewGrid && m("CQ VK2ABC QF56", "20M") == WorkedMark.WorkedBand &&
                      m("K7JUS VK2ABC RR73", "20M") == WorkedMark.WorkedBand && m("CQ W1AW FN31", "40M") == WorkedMark.NewGrid,
                    "worked before: new DXCC, new on the band, new grid, already worked (from an ADIF log of " + n + ")");
            }

            // 11. PSK Reporter: who a decode reports, and the IPFIX packet (read back field by field; nothing is sent)
            {
                Func<string, DecodeLine> dl = m => new DecodeLine { Message = m, Snr = -7, Freq = 1234, Mode = FtxMode.FT8, SlotUtc = new DateTime(2026, 10, 16, 12, 0, 15, DateTimeKind.Utc) };
                PskSpot a = PskReporter.SpotOf(dl("CQ DX VK2XYZ QF56"), 14074000, "K7JUS"), b = PskReporter.SpotOf(dl("K7JUS W9XYZ RR73"), 14074000, "K7JUS");
                PskSpot c = PskReporter.SpotOf(dl("W9XYZ K7JUS R-07"), 14074000, "K7JUS"), h = PskReporter.SpotOf(dl("K1ABC <KH1/KH7Z> -11"), 14074000, "K7JUS");
                check(a != null && a.Call == "VK2XYZ" && a.Grid == "QF56" && a.Hz == 14075234 && b != null && b.Call == "W9XYZ" && b.Grid == "" && c == null && h == null,
                    "PSK Reporter: senders and grids from decodes (RR73 isn't a grid; not my own call, not hashed calls)");

                PskReporter psk = new PskReporter { MyCall = "K7JUS", MyGrid = "CN87", Software = "Kainos test" };
                List<byte[]> pk = psk.Packets(new List<PskSpot> { a, b }, new DateTime(2026, 10, 16, 12, 5, 0, DateTimeKind.Utc));
                byte[] p0 = pk[0];
                Func<int, int, ulong> be = (o, n) => { ulong v = 0; for (int i = 0; i < n; i++) v = (v << 8) | p0[o + i]; return v; };
                bool ok = pk.Count == 1 && be(0, 2) == 10 && (int)be(2, 2) == p0.Length;
                // walk the sets: options template (3), template (2), receiver data (0x50e2), sender data (0x50e3); each padded to 4
                List<int> ids = new List<int>();
                int pos = 16, senderAt = -1;
                while (ok && pos + 4 <= p0.Length)
                {
                    int id = (int)be(pos, 2), len = (int)be(pos + 2, 2);
                    if (len < 4 || len % 4 != 0 || pos + len > p0.Length) { ok = false; break; }
                    ids.Add(id);
                    if (id == 0x50e3) senderAt = pos + 4;
                    pos += len;
                }
                ok = ok && pos == p0.Length && ids.SequenceEqual(new[] { 3, 2, 0x50e2, 0x50e3 }) && senderAt > 0;
                // the first sender record: callsign, 5-byte frequency, SNR, mode, grid, source, time
                if (ok)
                {
                    int q2 = senderAt;
                    Func<string> s = () => { int n = p0[q2]; string v = System.Text.Encoding.UTF8.GetString(p0, q2 + 1, n); q2 += 1 + n; return v; };
                    string call = s(); ulong hz = be(q2, 5); q2 += 5; int snr = (sbyte)p0[q2++]; string mode = s(), grid = s(); int src = p0[q2++]; ulong t = be(q2, 4);
                    ok = call == "VK2XYZ" && hz == 14075234 && snr == -7 && mode == "FT8" && grid == "QF56" && src == 1 && t == 1792152015;
                }
                check(ok, "PSK Reporter packet: IPFIX header, templates and records read back (sets " + string.Join(",", ids.Select(i => i.ToString("x"))) + ")");
            }

            // 10. the waterfall's FFT: a 1000 Hz tone peaks in its bin (1000 * 4096 / 12000 = 341.3)
            {
                SpectrumMaker sm = new SpectrumMaker();
                float[] tone = new float[SpectrumMaker.N * 2];
                for (int i = 0; i < tone.Length; i++) tone[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 12000.0);
                List<float[]> lines = new List<float[]>();
                sm.Push(tone, tone.Length, lines);
                int peak = lines.Count > 0 ? Array.IndexOf(lines[0], lines[0].Max()) : -1;
                check(lines.Count == 3 && (peak == 341 || peak == 342), "waterfall FFT (4096 points, half overlap): 1000 Hz in bin " + peak + ", " + lines.Count + " lines from 8192 samples");
            }

            // 10b. the waterfall's smoothing evens out the noise floor (less spread in dB from bin to bin), signals stay
            {
                Func<double, double> spread = sm =>
                {
                    SpectrumMaker m = new SpectrumMaker { Smooth = sm };
                    Random r2 = new Random(11);
                    float[] noise = new float[SpectrumMaker.N * 6];
                    for (int i = 0; i < noise.Length; i++) noise[i] = (float)(gauss(r2) * 0.01);
                    List<float[]> ls = new List<float[]>();
                    m.Push(noise, noise.Length, ls);
                    float[] last = ls[ls.Count - 1];
                    double mean = last.Average();
                    return Math.Sqrt(last.Select(v => (v - mean) * (v - mean)).Average());
                };
                double raw = spread(0), smooth = spread(0.5);
                check(smooth < raw * 0.75, "waterfall smoothing: noise floor spread " + raw.ToString("0.0") + " dB raw, " + smooth.ToString("0.0") + " dB smoothed");
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
            bool quick = args.Contains("--quick");            // skips the real-time QSO (2 minutes)
            System.Threading.Tasks.Parallel.ForEach(quick ? new FtxMode[0] : new[] { FtxMode.FT8, FtxMode.FT4, FtxMode.FT2 }, mode =>
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
                        // the waterfall: a signal at 1500 Hz shows bright where 1500 Hz is, dark at 500 Hz
                        stage = "waterfall";
                        Kainos.Modules.KainosCrispHost crisp = all(w).OfType<Kainos.Modules.KainosCrispHost>().First();
                        Console.WriteLine("      waterfall " + (crisp.Crisp ? "crisp, at " + Kainos.Modules.KainosCrispHost.DpiScale(crisp.Content) * 100 + " %" : "not crisp: " + crisp.NotCrispBecause) + "; " + crisp.Describe());
                        UI.FreqStrip strip = (UI.FreqStrip)crisp.Content;
                        for (int i = 0; i < 60; i++)
                        {
                            float[] line = new float[SpectrumMaker.Bins];
                            for (int k = 0; k < line.Length; k++) line[k] = -60 + (float)(gauss(rnd) * 2);
                            for (int k = 508; k < 524; k++) line[k] = -20;
                            strip.AddLine(line, 1000 + i * SpectrumMaker.LineSeconds);
                        }
                        pump(200);
                        using (System.Drawing.Bitmap shot = new System.Drawing.Bitmap(strip.Width, strip.Height))
                        {
                            strip.DrawToBitmap(shot, new System.Drawing.Rectangle(0, 0, strip.Width, strip.Height));
                            int yy = strip.Height / 2;
                            System.Drawing.Color hot = shot.GetPixel((int)(1515.0 / 3000 * (strip.Width - 1)), yy), cold = shot.GetPixel((int)(500.0 / 3000 * (strip.Width - 1)) + 3, yy);
                            check(hot.GetBrightness() > cold.GetBrightness() + 0.3f, "waterfall shows a signal where it is (" + hot + " vs " + cold + ")");
                        }
                        // a callsign on its trace: a decode at 500 Hz whose signal was in the middle of those lines is labelled there
                        double lastT = 1000 + 59 * SpectrumMaker.LineSeconds, midT = lastT - 20 * SpectrumMaker.LineSeconds;
                        double half = 79 * 0.160 / 2 + FtxModes.TxStart(FtxMode.FT8);
                        DateTime lslot = new DateTime((long)Math.Round((midT - half) * 1e7), DateTimeKind.Utc);
                        strip.AddLabels(lslot, FtxMode.FT8, new[] { new DecodeLine { Message = "CQ W9XYZ EN37", Freq = 500, Snr = -5, Dt = 0, Mode = FtxMode.FT8, SlotUtc = lslot } });
                        pump(100);
                        using (System.Drawing.Bitmap shot = new System.Drawing.Bitmap(strip.Width, strip.Height))
                        {
                            strip.DrawToBitmap(shot, new System.Drawing.Rectangle(0, 0, strip.Width, strip.Height));
                            int lx = (int)(500.0 / 3000 * (strip.Width - 1)), labelTop = 15, rowY = labelTop + 20, golden = 0;
                            for (int yy = rowY - 8; yy <= rowY + 8; yy++)
                                for (int xx = lx; xx < Math.Min(strip.Width, lx + 50); xx++)
                                {
                                    System.Drawing.Color px = shot.GetPixel(xx, yy);
                                    if (px.R > 150 && px.G > 120 && px.B < 140 && px.R > px.B + 50) golden++;      // the gold CQ label's text
                                }
                            check(golden > 5, "callsign drawn on its trace in the waterfall (" + golden + " gold pixels at row 20, 500 Hz)");
                        }

                        // changing mode tunes to that mode's frequency on the same band (or stays where there's none)
                        stage = "mode tuning";
                        UI.DigiPane pane = all(w).OfType<UI.DigiPane>().First();
                        host.Vfo = 14074000; host.ModeName = "USB";
                        pane.setMode(FtxMode.FT4); double f4 = host.Vfo; string md = host.ModeName;
                        pane.setMode(FtxMode.WSPR); double fw = host.Vfo;
                        pane.setMode(FtxMode.FT2); double f2 = host.Vfo;
                        pane.setMode(FtxMode.JT65); double fj = host.Vfo;
                        pane.setMode(FtxMode.FT8); double f8 = host.Vfo;
                        host.Vfo = 1840000;
                        pane.setMode(FtxMode.FT4); double f160 = host.Vfo;
                        pane.setMode(FtxMode.FT8);
                        check(f4 == 14080000 && md == "DIGU" && fw == 14095600 && f2 == 14084000 && fj == 14076000 && f8 == 14074000 && f160 == 1840000,
                            "changing mode tunes on the same band: FT4 " + f4 + ", WSPR " + fw + ", FT2 " + f2 + ", JT65 " + fj + ", FT8 " + f8 + "; FT4 on 160 m stays " + f160);

                        // Tune: keys the radio and sends the tone; off un-keys it
                        stage = "tune";
                        DigiEngine teng = new DigiEngine(host) { TxFreq = 1500 };
                        teng.Tune(true);
                        for (int i = 0; i < 20; i++) { teng.Step(); pump(10); }      // pump: MOX is set through the window (BeginInvoke)
                        bool keyed = teng.Tuning && teng.Transmitting && host.Mox && host.TxSamples > 0;
                        teng.Tune(false);
                        for (int i = 0; i < 5; i++) { teng.Step(); pump(10); }
                        pump(100);
                        check(keyed && !teng.Tuning && !teng.Transmitting && !host.Mox, "Tune keys the radio with a tone, and stops (" + host.TxSamples + " samples)");
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

        // --show: the pane in a window like Kainos's (DPI-unaware, as Kainos is), the waterfall fed with a few made-up
        // signals, until the window is closed
        private static int show(string bin, string work)
        {
            FakeHost host = new FakeHost(bin.EndsWith("decoder") ? Path.GetDirectoryName(bin) : bin, Path.Combine(work, "data"));
            host.Settings["decoder"] = bin;
            if (Environment.GetCommandLineArgs().Contains("--blurry")) host.Settings["crispwf"] = "0";
            System.Windows.Forms.Form w = new System.Windows.Forms.Form { Text = "Digital Modes preview", Width = 1300, Height = 420, BackColor = System.Drawing.Color.FromArgb(10, 14, 28) };
            host.Window = w;
            DigiModesModule mod = new DigiModesModule();
            Random rnd = new Random(3);
            int n = 0;
            System.Windows.Forms.Timer feed = new System.Windows.Forms.Timer { Interval = (int)(SpectrumMaker.LineSeconds * 1000) };
            feed.Tick += (s, e) =>
            {
                Kainos.Modules.KainosCrispHost ch = all(w).OfType<Kainos.Modules.KainosCrispHost>().FirstOrDefault();
                if (ch == null) return;
                if (n == 0) w.Text += ch.Crisp ? "  (waterfall crisp, " + Kainos.Modules.KainosCrispHost.DpiScale(ch.Content) * 100 + " %)" : "  (waterfall not crisp: " + ch.NotCrispBecause + ")";
                float[] line = new float[SpectrumMaker.Bins];
                for (int k = 0; k < line.Length; k++) line[k] = -60 + (float)(gauss(rnd) * 2);
                int[] at = { 300, 520, 700, 1015, 1240, 1390, 1610, 1830, 2210, 2560 };
                for (int j = 0; j < at.Length; j++)
                    if (((n / 60) + j) % 3 != 0)
                        for (int k = at[j] * SpectrumMaker.Bins / 3000; k < (at[j] + 50) * SpectrumMaker.Bins / 3000; k++) line[k] = -40 + j * 2 + (float)(gauss(rnd) * 3);
                ((UI.FreqStrip)ch.Content).AddLine(line, 1000 + n * SpectrumMaker.LineSeconds);
                n++;
            };
            w.Shown += (s, e) => { mod.Start(host); host.Menus[0].Click(); feed.Start(); };
            System.Windows.Forms.Application.Run(w);
            feed.Dispose();
            mod.Stop();
            mod.Dispose();
            return 0;
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
