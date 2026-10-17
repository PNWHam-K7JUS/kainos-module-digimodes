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

            System.Console.WriteLine(_fail == 0 ? "All passed" : _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static double gauss(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }
}
