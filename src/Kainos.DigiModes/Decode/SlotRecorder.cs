/*  SlotRecorder.cs

The receiver's audio at 12 kHz (what jt9 decodes), kept for the last minute with each sample's UTC time, so a
whole FT8 / FT4 period can be cut out once it has ended.

This file is part of the Kainos Digital Modes module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;

namespace Kainos.DigiModes.Decode
{
    // Any input rate to 12 kHz: a windowed-sinc low-pass then decimation when the rate is a multiple of 12 kHz (48 and
    // 192 kHz are what the receiver gives), linear interpolation after the low-pass otherwise.
    internal class Resampler12k
    {
        public const int OutRate = 12000;
        private readonly int _inRate;
        private readonly double[] _taps;
        private readonly double[] _hist;
        private int _histPos;
        private readonly int _decim;        // > 0: integer decimation
        private double _phase;              // fractional path: position of the next output in input samples
        private double _prevFiltered, _step;
        private long _inCount;

        public Resampler12k(int inRate)
        {
            _inRate = inRate;
            _decim = inRate % OutRate == 0 ? inRate / OutRate : 0;
            double ratio = Math.Max(1.0, inRate / (double)OutRate);
            int n = Math.Max(31, (int)(16 * ratio) | 1);
            double fc = 0.45 / ratio;       // cut-off as a fraction of the input rate: 5.4 kHz at the output
            _taps = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double x = i - (n - 1) / 2.0;
                double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (n - 1));    // Blackman
                _taps[i] = sinc * w;
                sum += _taps[i];
            }
            for (int i = 0; i < n; i++) _taps[i] /= sum;
            _hist = new double[n];
            _step = inRate / (double)OutRate;
        }

        public int InRate { get { return _inRate; } }

        public void Process(float[] src, int count, List<float> dst)
        {
            int n = _taps.Length;
            for (int k = 0; k < count; k++)
            {
                _hist[_histPos] = src[k];
                _histPos = (_histPos + 1) % n;
                _inCount++;
                if (_decim > 0)
                {
                    if (_inCount % _decim != 0) continue;
                    dst.Add((float)filtered());
                }
                else
                {
                    double y = filtered();
                    // outputs that fall between the previous input and this one
                    while (_phase <= 1.0)
                    {
                        dst.Add((float)(_prevFiltered + (y - _prevFiltered) * _phase));
                        _phase += _step;
                    }
                    _phase -= 1.0;
                    _prevFiltered = y;
                }
            }
        }

        private double filtered()
        {
            int n = _taps.Length;
            double acc = 0;
            int p = _histPos;
            for (int i = 0; i < n; i++) { acc += _hist[p] * _taps[i]; p = (p + 1) % n; }
            return acc;
        }
    }

    internal class SlotRecorder
    {
        private const int Capacity = 12000 * 60;
        private readonly float[] _ring = new float[Capacity];
        private long _written;                  // samples written since the start (12 kHz)
        private readonly List<KeyValuePair<DateTime, double>> _offsets = new List<KeyValuePair<DateTime, double>>();
        private double _t0 = double.NaN;        // UTC seconds (since DateTime.MinValue) of sample 0
        private readonly List<float> _tmp = new List<float>(4096);
        private Resampler12k _rs;
        private readonly object _lock = new object();

        public double LatencyCorrection;        // seconds: the receive path's delay (added to every sample's time)

        public void Reset()
        {
            lock (_lock)
            {
                _written = 0;
                _offsets.Clear();
                _t0 = double.NaN;
                _rs = null;
            }
        }

        // audio from the receiver at `rate`, read at `nowUtc`
        public void Add(float[] buf, int n, int rate, DateTime nowUtc)
        {
            if (n <= 0 || rate <= 0) return;
            lock (_lock)
            {
                if (_rs == null || _rs.InRate != rate) _rs = new Resampler12k(rate);
                _tmp.Clear();
                _rs.Process(buf, n, _tmp);
                for (int i = 0; i < _tmp.Count; i++) _ring[(_written + i) % Capacity] = _tmp[i];
                _written += _tmp.Count;

                // the newest sample arrived just now (or a little later, when the audio came in a burst): the smallest
                // "now - written / 12k" over the last 20 s is the best estimate of sample 0's time; it follows the radio's
                // clock drifting from the PC's
                double est = Seconds(nowUtc) - _written / 12000.0;
                _offsets.Add(new KeyValuePair<DateTime, double>(nowUtc, est));
                while (_offsets.Count > 0 && (nowUtc - _offsets[0].Key).TotalSeconds > 20) _offsets.RemoveAt(0);
                double min = double.MaxValue;
                foreach (KeyValuePair<DateTime, double> kv in _offsets) if (kv.Value < min) min = kv.Value;
                _t0 = min;
            }
        }

        public static double Seconds(DateTime utc) { return utc.Ticks / 1e7; }

        // UTC time of the newest sample, or NaN
        public double NewestTime
        {
            get { lock (_lock) return double.IsNaN(_t0) ? double.NaN : _t0 + _written / 12000.0 - LatencyCorrection; }
        }

        // the 12 kHz samples written since `pos` (at most dst.Length), moving pos on; for the waterfall
        public int Since(ref long pos, float[] dst)
        {
            lock (_lock)
            {
                if (pos > _written || pos < _written - Capacity) pos = Math.Max(0, _written - dst.Length);
                int n = (int)Math.Min(dst.Length, _written - pos);
                for (int i = 0; i < n; i++) dst[i] = _ring[(pos + i) % Capacity];
                pos += n;
                return n;
            }
        }

        // the samples from startUtc for `seconds` (zeros where there's nothing), or null if it isn't all in the ring
        public float[] Cut(double startUtc, double seconds, out double missing)
        {
            missing = 0;
            lock (_lock)
            {
                if (double.IsNaN(_t0)) return null;
                int n = (int)Math.Round(seconds * 12000);
                float[] o = new float[n];
                long first = (long)Math.Round((startUtc + LatencyCorrection - _t0) * 12000);
                int have = 0;
                for (int i = 0; i < n; i++)
                {
                    long k = first + i;
                    if (k < 0 || k >= _written || k < _written - Capacity) continue;
                    o[i] = _ring[k % Capacity];
                    have++;
                }
                missing = (n - have) / 12000.0;
                return o;
            }
        }
    }
}
