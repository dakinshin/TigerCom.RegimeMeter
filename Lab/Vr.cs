// Эксперимент 5: можно ли сбить разброс, усредняя по перекрывающимся окнам
// (оценка Ло–Маккинлая, variance ratio) вместо одного окна, как в ER.
using System;
using System.Collections.Generic;

static class Vr
{
    const int Sub = 60;
    static double SigmaBar => Math.Sqrt(Sub);

    static void Gen(Random r, int n, double drift, int driftFrom, double[] c)
    {
        double p = 100000;
        var step = drift * SigmaBar / Sub;
        for (var i = 0; i < n; i++)
        {
            var d = i >= driftFrom ? step : 0.0;
            for (var s = 0; s < Sub; s++) p += (r.Next(2) == 0 ? 1 : -1) + d;
            c[i] = p;
        }
    }

    /// <summary>√VR: корень из отношения дисперсии k-барных приращений к k·дисперсии
    /// барных, усреднённый по lookback перекрывающимся окнам. На шкале ER/RR.</summary>
    static void Compute(double[] c, int n, int k, int lookback, double[] outv)
    {
        for (var i = 0; i < n; i++) outv[i] = double.NaN;
        var start = k + lookback;
        for (var i = start; i < n; i++)
        {
            double num = 0, den = 0;
            for (var j = i - lookback + 1; j <= i; j++)
            {
                var a = c[j] - c[j - k]; num += a * a;
                var b = c[j] - c[j - 1]; den += b * b;
            }
            num /= lookback; den /= lookback;
            outv[i] = den > 0 ? Math.Sqrt(num / (k * den)) : double.NaN;
        }
    }

    static double Q(List<double> s, double q)
    {
        if (s.Count == 0) return double.NaN;
        var idx = q * (s.Count - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return s[lo] + (s[hi] - s[lo]) * (idx - lo);
    }

    public static void Run()
    {
        Console.WriteLine("=== 5. √VR — усреднение по перекрывающимся окнам ===");
        Console.WriteLine("k=12, разная глубина усреднения. Для сравнения ER имеет ст.откл 0.72.");
        Console.WriteLine();
        Console.WriteLine("глубина  p10    p25   медиана  p75    p90   ст.откл  доля>1.20  доля<0.85");
        const int k = 12;
        foreach (var look in new[] { 12, 24, 48, 96, 192 })
        {
            const int n = 150000;
            var c = new double[n];
            Gen(new Random(4242 + look), n, 0, int.MaxValue, c);
            var v = new double[n];
            Compute(c, n, k, look, v);

            var vals = new List<double>();
            for (var i = k + look + 10; i < n; i++) if (!double.IsNaN(v[i])) vals.Add(v[i]);
            vals.Sort();
            double sum = 0, sq = 0; int ab = 0, be = 0;
            foreach (var x in vals) { sum += x; sq += x * x; if (x > 1.20) ab++; if (x < 0.85) be++; }
            var mean = sum / vals.Count;
            var sd = Math.Sqrt(sq / vals.Count - mean * mean);
            Console.WriteLine($"{look,-8} {Q(vals, .10),6:F3} {Q(vals, .25),5:F3} {Q(vals, .50),8:F3} {Q(vals, .75),6:F3} {Q(vals, .90),6:F3} {sd,8:F3} {100.0 * ab / vals.Count,9:F1}% {100.0 * be / vals.Count,9:F1}%");
        }

        Console.WriteLine();
        Console.WriteLine("Задержка обнаружения тренда порогом √VR > 1.20 (медиана баров, 3000 прогонов):");
        Console.WriteLine("снос   глубина 24  глубина 48  глубина 96");
        foreach (var drift in new[] { 0.2, 0.3, 0.5, 1.0 })
        {
            Console.Write($"{drift,-6:F1}");
            foreach (var look in new[] { 24, 48, 96 })
            {
                const int runs = 3000;
                var warm = k + look + 40;
                var horizon = 5 * k;
                var n = warm + horizon;
                var lags = new List<double>();
                var rnd = new Random(88 + look + (int)(drift * 100));
                var c = new double[n]; var v = new double[n];
                var missed = 0; var used = 0;
                for (var run = 0; run < runs; run++)
                {
                    Gen(rnd, n, drift, warm, c);
                    Compute(c, n, k, look, v);
                    if (double.IsNaN(v[warm - 1]) || v[warm - 1] > 1.20) continue;
                    used++;
                    var found = -1;
                    for (var i = warm; i < n; i++)
                        if (!double.IsNaN(v[i]) && v[i] > 1.20) { found = i - warm + 1; break; }
                    if (found < 0) missed++; else lags.Add(found);
                }
                lags.Sort();
                Console.Write($"{Q(lags, .50),12:F1}");
            }
            Console.WriteLine();
        }
    }
}
