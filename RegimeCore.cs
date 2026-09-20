//--------------------------------------------------------------------------------
// RegimeCore — чистая математика индикатора Regime, без единой ссылки на API
// терминала: то же самое компилируется в Tests/ и проверяется `dotnet run`.
//
// Идея. Окно из k баров графика играет роль ОДНОЙ «старшей свечи», сами бары —
// «младших». Считаем два отношения:
//
//   ER = √k · |C[i] − C[i−k]| / Σ|ΔC|   — безоткатность: чистый ход против длины пути;
//   RR = √k · (HH − LL) / Σ TR          — размах «старшей свечи» против суммы «младших».
//
// Нормировка √k не косметическая: у случайного блуждания путь растёт как k, а
// смещение и размах — как √k, поэтому БЕЗ неё отношение показывало бы в основном
// √k и ничего больше. С ней у случайного блуждания обе метрики ≈ 1:
//
//   E|C[i] − C[i−k]| = σ√(2k/π),  E Σ|ΔC| = k·σ√(2/π)  ⇒ ER = √k · √k/k = 1
//   E(HH − LL) ≈ 1.596·σ√k,       E Σ TR  ≈ k·1.596·σ  ⇒ RR = √k · √k/k = 1
//
// Шкала: 1.0 — «как случайное блуждание», √k — теоретический потолок (движение
// строго в одну сторону, бары не перекрываются). Для k = 12 потолок ≈ 3.46.
//--------------------------------------------------------------------------------

using System;

namespace RegimeMeter
{
    public static class RegimeCore
    {
        /// <summary>Значение не определено (мало баров или цена не двигалась вовсе).</summary>
        public const double Undefined = double.NaN;

        public const int MinWindow = 2;
        public const int MaxWindow = 500;

        /// <summary>Потолок шкалы для окна k — движение без единого перекрытия баров.</summary>
        public static double Ceiling(int window) => Math.Sqrt(Clamp(window));

        public static int Clamp(int window) =>
            window < MinWindow ? MinWindow : (window > MaxWindow ? MaxWindow : window);

        /// <summary>
        /// Заполняет er/rr для баров [0, count). Первые k баров — Undefined:
        /// окну нужен ещё и бар k−1 слева (предыдущий close для TR и для |ΔC|).
        /// Массивы er/rr должны быть длиной ≥ count.
        /// </summary>
        public static void Compute(double[] high, double[] low, double[] close, int count,
                                   int window, double[] er, double[] rr)
        {
            if (high == null || low == null || close == null || er == null || rr == null) return;
            if (count > high.Length) count = high.Length;
            if (count > low.Length) count = low.Length;
            if (count > close.Length) count = close.Length;
            if (count > er.Length) count = er.Length;
            if (count > rr.Length) count = rr.Length;
            if (count <= 0) return;

            var k = Clamp(window);
            for (var i = 0; i < count; i++) { er[i] = Undefined; rr[i] = Undefined; }
            if (count <= k) return;

            var norm = Math.Sqrt(k);

            // Окно бара i — бары [i−k+1, i]; суммы считаем в лоб, без скользящего
            // накопления: k мал (≤ 500), зато нет дрейфа от вычитаний на длинной истории.
            for (var i = k; i < count; i++)
            {
                var first = i - k + 1;
                double path = 0, trSum = 0;
                double hh = high[first], ll = low[first];

                for (var j = first; j <= i; j++)
                {
                    var prev = close[j - 1];
                    path += Math.Abs(close[j] - prev);
                    trSum += TrueRange(high[j], low[j], prev);
                    if (high[j] > hh) hh = high[j];
                    if (low[j] < ll) ll = low[j];
                }

                er[i] = path > 0 ? norm * Math.Abs(close[i] - close[i - k]) / path : Undefined;
                rr[i] = trSum > 0 ? norm * (hh - ll) / trSum : Undefined;
            }
        }

        /// <summary>True Range: с учётом разрыва от предыдущего закрытия.</summary>
        public static double TrueRange(double high, double low, double prevClose)
        {
            var h = high > prevClose ? high : prevClose;
            var l = low < prevClose ? low : prevClose;
            return h - l;
        }

        /// <summary>EMA по ряду на месте; Undefined пропускаются, состояние не сбрасывают.</summary>
        public static void Smooth(double[] values, int count, int period)
        {
            if (values == null || period <= 1) return;
            if (count > values.Length) count = values.Length;

            var a = 2.0 / (period + 1);
            var state = Undefined;
            for (var i = 0; i < count; i++)
            {
                var v = values[i];
                if (double.IsNaN(v)) continue;
                state = double.IsNaN(state) ? v : state + a * (v - state);
                values[i] = state;
            }
        }

        /// <summary>+1 — безоткатно, −1 — запил, 0 — нейтрально / не определено.</summary>
        public static int Regime(double value, double chopLevel, double trendLevel)
        {
            if (double.IsNaN(value)) return 0;
            if (value >= trendLevel) return 1;
            if (value <= chopLevel) return -1;
            return 0;
        }
    }
}
