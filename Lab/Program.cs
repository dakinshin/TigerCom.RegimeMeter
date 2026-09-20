// Стенд чувствительности: распределение ER/RR на шуме и задержка обнаружения
// смены характера движения. Использует настоящий RegimeCore.cs.
using System;
using System.Collections.Generic;
using RegimeMeter;

static class Program
{
    const int Sub = 60;          // «тиков» внутри бара
    static double SigmaBar => Math.Sqrt(Sub);   // ст.откл. приращения закрытий, в тиках

    /// <summary>
    /// Бары из случайного блуждания. drift — снос в единицах SigmaBar за бар,
    /// включается с бара driftFrom.
    /// </summary>
    static void Gen(Random r, int n, double drift, int driftFrom,
                    double[] h, double[] l, double[] c)
    {
        double p = 100000;
        var step = drift * SigmaBar / Sub;   // снос на один подшаг
        for (var i = 0; i < n; i++)
        {
            var d = i >= driftFrom ? step : 0.0;
            double hi = p, lo = p;
            for (var s = 0; s < Sub; s++)
            {
                p += (r.Next(2) == 0 ? 1 : -1) + d;
                if (p > hi) hi = p;
                if (p < lo) lo = p;
            }
            h[i] = hi; l[i] = lo; c[i] = p;
        }
    }

    static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        var idx = q * (sorted.Count - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    static void Main()
    {
        var ks = new[] { 6, 12, 24, 48 };

        Console.WriteLine("=== 1. Распределение на чистом шуме (случайное блуждание) ===");
        Console.WriteLine("k    смет  метрика   p10    p25    медиана  p75    p90    ст.откл  доля>1.20  доля<0.85");
        foreach (var smooth in new[] { 1, 3 })
        foreach (var k in ks)
        {
            const int n = 400000;
            var h = new double[n]; var l = new double[n]; var c = new double[n];
            Gen(new Random(1000 + k), n, 0, int.MaxValue, h, l, c);
            var er = new double[n]; var rr = new double[n];
            RegimeCore.Compute(h, l, c, n, k, er, rr);
            RegimeCore.Smooth(er, n, smooth);
            RegimeCore.Smooth(rr, n, smooth);

            foreach (var pair in new[] { Tuple.Create("ER", er), Tuple.Create("RR", rr) })
            {
                var vals = new List<double>();
                for (var i = k + 50; i < n; i++) if (!double.IsNaN(pair.Item2[i])) vals.Add(pair.Item2[i]);
                vals.Sort();
                double sum = 0, sq = 0; int above = 0, below = 0;
                foreach (var v in vals) { sum += v; sq += v * v; if (v > 1.20) above++; if (v < 0.85) below++; }
                var mean = sum / vals.Count;
                var sd = Math.Sqrt(sq / vals.Count - mean * mean);
                Console.WriteLine($"{k,-4} {smooth,-5} {pair.Item1,-8} {Quantile(vals, .10),6:F3} {Quantile(vals, .25),6:F3} {Quantile(vals, .50),8:F3} {Quantile(vals, .75),6:F3} {Quantile(vals, .90),6:F3} {sd,8:F3} {100.0 * above / vals.Count,9:F1}% {100.0 * below / vals.Count,9:F1}%");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 2. Задержка обнаружения тренда (баров после смены характера) ===");
        Console.WriteLine("Снос в единицах σ бара. Порог ER > 1.20. 4000 прогонов, учтены только те,");
        Console.WriteLine("где на момент переключения индикатор НЕ был уже сработавшим.");
        Console.WriteLine();
        Console.WriteLine("снос  k    смет  медиана  p25   p75   не поймал за 3k баров");
        foreach (var drift in new[] { 0.2, 0.3, 0.5, 1.0 })
        {
            foreach (var k in ks)
            foreach (var smooth in new[] { 1, 3 })
            {
                const int runs = 4000;
                var warm = 120;
                var lags = new List<double>();
                var missed = 0; var used = 0;
                var rnd = new Random(7000 + k * 31 + smooth * 7 + (int)(drift * 100));
                var horizon = 3 * k;
                var n = warm + horizon + 2;
                var h = new double[n]; var l = new double[n]; var c = new double[n];
                var er = new double[n]; var rr = new double[n];

                for (var run = 0; run < runs; run++)
                {
                    Gen(rnd, n, drift, warm, h, l, c);
                    RegimeCore.Compute(h, l, c, n, k, er, rr);
                    RegimeCore.Smooth(er, n, smooth);

                    if (double.IsNaN(er[warm - 1]) || er[warm - 1] > 1.20) continue; // уже сработал
                    used++;
                    var found = -1;
                    for (var i = warm; i < n; i++)
                        if (!double.IsNaN(er[i]) && er[i] > 1.20) { found = i - warm + 1; break; }
                    if (found < 0) missed++; else lags.Add(found);
                }
                lags.Sort();
                var miss = used > 0 ? 100.0 * missed / used : 0;
                Console.WriteLine($"{drift,-5:F1} {k,-4} {smooth,-5} {Quantile(lags, .50),8:F1} {Quantile(lags, .25),5:F1} {Quantile(lags, .75),5:F1} {miss,10:F1}%");
            }
            Console.WriteLine();
        }

        Console.WriteLine("=== 3. Сколько баров нового режима нужно окну (снос 1.0σ, без сглаживания) ===");
        Console.WriteLine("Средний ER через m баров после начала тренда:");
        Console.Write("m:      ");
        for (var m = 1; m <= 16; m++) Console.Write($"{m,6}");
        Console.WriteLine();
        foreach (var k in ks)
        {
            const int runs = 6000;
            var warm = 120;
            var horizon = 20;
            var n = warm + horizon;
            var sums = new double[horizon + 1];
            var cnts = new int[horizon + 1];
            var rnd = new Random(31337 + k);
            var h = new double[n]; var l = new double[n]; var c = new double[n];
            var er = new double[n]; var rr = new double[n];
            for (var run = 0; run < runs; run++)
            {
                Gen(rnd, n, 1.0, warm, h, l, c);
                RegimeCore.Compute(h, l, c, n, k, er, rr);
                for (var m = 1; m <= 16 && warm + m - 1 < n; m++)
                {
                    var v = er[warm + m - 1];
                    if (double.IsNaN(v)) continue;
                    sums[m] += v; cnts[m]++;
                }
            }
            Console.Write($"k={k,-4}  ");
            for (var m = 1; m <= 16; m++) Console.Write($"{(cnts[m] > 0 ? sums[m] / cnts[m] : double.NaN),6:F2}");
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("=== 4. Ложные срабатывания подряд (шум, ER > 1.20) ===");
        Console.WriteLine("Как часто порог пробивается и как долго держится — чтобы понять, что фильтровать.");
        Console.WriteLine("k    смет  срабатываний на 1000 баров  средняя длина, баров  доля длиннее k/2");
        foreach (var smooth in new[] { 1, 3 })
        foreach (var k in ks)
        {
            const int n = 400000;
            var h = new double[n]; var l = new double[n]; var c = new double[n];
            Gen(new Random(555 + k), n, 0, int.MaxValue, h, l, c);
            var er = new double[n]; var rr = new double[n];
            RegimeCore.Compute(h, l, c, n, k, er, rr);
            RegimeCore.Smooth(er, n, smooth);

            var runsLen = new List<int>();
            var cur = 0;
            for (var i = k + 50; i < n; i++)
            {
                if (!double.IsNaN(er[i]) && er[i] > 1.20) cur++;
                else { if (cur > 0) runsLen.Add(cur); cur = 0; }
            }
            if (cur > 0) runsLen.Add(cur);
            double sum = 0; var longOnes = 0;
            foreach (var r in runsLen) { sum += r; if (r > k / 2) longOnes++; }
            var per1000 = 1000.0 * runsLen.Count / (n - k - 50);
            Console.WriteLine($"{k,-4} {smooth,-5} {per1000,26:F2} {(runsLen.Count > 0 ? sum / runsLen.Count : 0),21:F1} {(runsLen.Count > 0 ? 100.0 * longOnes / runsLen.Count : 0),16:F1}%");
        }
    }
}
