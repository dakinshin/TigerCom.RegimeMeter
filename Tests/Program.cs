// Проверки ядра и индикатора без терминала и без Windows: dotnet run (из этой папки).
// Покрыто:
//   1) калибровка ER/RR — случайное блуждание даёт ≈ 1, тренд ≈ √k, зигзаг ≈ 1/√k;
//   2) добыча массивов из Helper: double[], decimal[], Price(enum), запасной (H+L)/2;
//   3) кэш RegimeData — троттлинг и пересчёт при смене числа баров;
//   4) ПРОГОН «ДЕСЕРИАЛИЗОВАННОГО ИНСТАНСА» — конструктор при загрузке конфигурации
//      НЕ выполняется (инцидент 29.08.2026: NRE в сеттере ломал весь ChartAreaSettings);
//   5) отрисовка: линии и заливка попадают в очередь.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RegimeMeter;
using TigerTrade.Chart.Indicators.Common;
using TigerTrade.Chart.Indicators.Custom;
using TigerTrade.Dx;

static class Program
{
    static int _fails;

    static void Check(bool cond, string what)
    {
        Console.WriteLine((cond ? "  ok   " : "  FAIL ") + what);
        if (!cond) _fails++;
    }

    static int Main()
    {
        const int K = 12;

        Console.WriteLine("1. Калибровка ER/RR");
        {
            // Чистый тренд без перекрытий: ER = √k, RR = √k·(k−1)/k
            var n = 200;
            var h = new double[n]; var l = new double[n]; var c = new double[n];
            for (var i = 0; i < n; i++) { c[i] = 100 + i; h[i] = c[i]; l[i] = c[i]; }
            var er = new double[n]; var rr = new double[n];
            RegimeCore.Compute(h, l, c, n, K, er, rr);
            var ceiling = RegimeCore.Ceiling(K);
            Check(Math.Abs(er[n - 1] - ceiling) < 1e-9, $"тренд: ER = {er[n - 1]:F3} = √k = {ceiling:F3}");
            Check(Math.Abs(rr[n - 1] - ceiling * (K - 1) / K) < 1e-9, $"тренд: RR = {rr[n - 1]:F3}");

            // Зигзаг: чистый ход нулевой, путь максимальный
            for (var i = 0; i < n; i++) { c[i] = 100 + (i % 2); h[i] = c[i]; l[i] = c[i]; }
            RegimeCore.Compute(h, l, c, n, K, er, rr);
            Check(Math.Abs(er[n - 1]) < 1e-9, $"зигзаг: ER = {er[n - 1]:F3} ≈ 0");
            Check(Math.Abs(rr[n - 1] - 1.0 / ceiling) < 1e-9, $"зигзаг: RR = {rr[n - 1]:F3} = 1/√k = {1 / ceiling:F3}");

            // Флет: цена не шевелится вовсе → значение не определено, а не 0/0
            for (var i = 0; i < n; i++) { c[i] = 100; h[i] = 100; l[i] = 100; }
            RegimeCore.Compute(h, l, c, n, K, er, rr);
            Check(double.IsNaN(er[n - 1]) && double.IsNaN(rr[n - 1]), "флет: ER/RR не определены");

            // Случайное блуждание с честным внутрибаровым ходом → обе метрики ≈ 1.
            // Это и есть смысл нормировки на √k: без неё было бы ≈ √k.
            int bars = 6000, sub = 60;
            var rnd = new Random(20260920);
            var rh = new double[bars]; var rl = new double[bars]; var rc = new double[bars];
            double p = 10000;
            for (var i = 0; i < bars; i++)
            {
                double hi = p, lo = p;
                for (var s = 0; s < sub; s++)
                {
                    p += rnd.Next(2) == 0 ? 1 : -1;
                    if (p > hi) hi = p;
                    if (p < lo) lo = p;
                }
                rh[i] = hi; rl[i] = lo; rc[i] = p;
            }
            var rer = new double[bars]; var rrr = new double[bars];
            RegimeCore.Compute(rh, rl, rc, bars, K, rer, rrr);

            double se = 0, sr = 0; var cnt = 0;
            for (var i = K; i < bars; i++)
            {
                if (double.IsNaN(rer[i]) || double.IsNaN(rrr[i])) continue;
                se += rer[i]; sr += rrr[i]; cnt++;
            }
            var meanEr = se / cnt; var meanRr = sr / cnt;
            Check(Math.Abs(meanEr - 1.0) < 0.12, $"случайное блуждание: средний ER = {meanEr:F3} ≈ 1");
            Check(Math.Abs(meanRr - 1.0) < 0.15, $"случайное блуждание: средний RR = {meanRr:F3} ≈ 1");

            // Сглаживание не должно менять уровень ряда
            var smoothed = (double[])rer.Clone();
            RegimeCore.Smooth(smoothed, bars, 5);
            double ss = 0; var sc = 0;
            for (var i = K; i < bars; i++) { if (double.IsNaN(smoothed[i])) continue; ss += smoothed[i]; sc++; }
            Check(Math.Abs(ss / sc - meanEr) < 0.02, $"EMA(5) сохраняет средний уровень: {ss / sc:F3}");
        }

        Console.WriteLine();
        Console.WriteLine("2. Добыча массивов из Helper");
        {
            var n = 50;
            var h = new double[n]; var l = new double[n]; var c = new double[n];
            for (var i = 0; i < n; i++) { c[i] = 100 + i; h[i] = c[i] + 1; l[i] = c[i] - 1; }
            ChartHelper.HighData = h; ChartHelper.LowData = l; ChartHelper.CloseData = c;
            ChartHelper.HighDec = new decimal[n]; ChartHelper.LowDec = new decimal[n];
            for (var i = 0; i < n; i++) { ChartHelper.HighDec[i] = (decimal)h[i]; ChartHelper.LowDec[i] = (decimal)l[i]; }

            var helper = new ChartHelper();

            ChartHelper.UseDecimal = false; ChartHelper.HideClose = false; ChartHelper.ExposePrice = false;
            var got = ChartArrays.Read(helper, "High", n);
            Check(got != null && Math.Abs(got[n - 1] - h[n - 1]) < 1e-9, "double[] читается");

            ChartHelper.UseDecimal = true;
            got = ChartArrays.Read(helper, "High", n);
            Check(got != null && Math.Abs(got[n - 1] - h[n - 1]) < 1e-9, "decimal[] конвертируется");
            ChartHelper.UseDecimal = false;

            string how;
            var cl = ChartArrays.ReadCloses(helper, n, out how);
            Check(cl != null && how == "Helper.Close", $"close напрямую: {how}");

            ChartHelper.HideClose = true; ChartHelper.ExposePrice = true;
            cl = ChartArrays.ReadCloses(helper, n, out how);
            Check(cl != null && how.StartsWith("Helper.Price"), $"close через Price(enum): {how}");

            ChartHelper.ExposePrice = false;
            cl = ChartArrays.ReadCloses(helper, n, out how);
            Check(cl == null, "close не найден → null (индикатор перейдёт на (H+L)/2)");

            Check(ChartArrays.Read(helper, "НетТакого", n) == null, "несуществующее свойство → null, без исключения");
            ChartHelper.HideClose = false;
        }

        Console.WriteLine();
        Console.WriteLine("3. VR и калибровка порогов");
        {
            // VR должен вести себя как ER по уровню, но заметно спокойнее по разбросу:
            // он усредняет десятки перекрывающихся окон вместо одного.
            int bars = 120000, sub = 60;
            var rnd = new Random(20260921);
            var h = new double[bars]; var l = new double[bars]; var c = new double[bars];
            double p = 10000;
            for (var i = 0; i < bars; i++)
            {
                double hi = p, lo = p;
                for (var s = 0; s < sub; s++) { p += rnd.Next(2) == 0 ? 1 : -1; if (p > hi) hi = p; if (p < lo) lo = p; }
                h[i] = hi; l[i] = lo; c[i] = p;
            }
            var er = new double[bars]; var rr = new double[bars]; var vr = new double[bars];
            RegimeCore.Compute(h, l, c, bars, K, er, rr);
            RegimeCore.ComputeVr(c, bars, K, 96, vr);

            var sdEr = Sd(er, K + 100, bars);
            var sdVr = Sd(vr, K + 200, bars);
            var meanVr = Mean(vr, K + 200, bars);
            Check(Math.Abs(meanVr - 1.0) < 0.06, $"VR на шуме: среднее {meanVr:F3} ≈ 1");
            Check(sdVr < sdEr / 2.5, $"VR спокойнее ER: ст.откл {sdVr:F3} против {sdEr:F3}");

            // На чистом тренде VR тоже уходит вверх
            for (var i = 0; i < 400; i++) { c[i] = 100 + i; h[i] = c[i]; l[i] = c[i]; }
            var tvr = new double[400];
            RegimeCore.ComputeVr(c, 400, K, 96, tvr);
            Check(tvr[399] > 3.0, $"тренд: VR = {tvr[399]:F2} (потолок √k = {RegimeCore.Ceiling(K):F2})");

            // Квантили
            var sample = new double[200];
            for (var i = 0; i < 200; i++) sample[i] = i;         // 0..199
            double qLo, qHi;
            Check(RegimeCore.Quantiles(sample, 200, 200, 25, 75, out qLo, out qHi)
                  && Math.Abs(qLo - 49.75) < 0.5 && Math.Abs(qHi - 149.25) < 0.5,
                  $"квантили 25/75 = {qLo:F1}/{qHi:F1}");
            Check(!RegimeCore.Quantiles(sample, 10, 200, 25, 75, out qLo, out qHi),
                  "меньше 20 значений → калибровки нет, остаются ручные пороги");
        }

        Console.WriteLine();
        Console.WriteLine("4. Кэш RegimeData");
        {
            var n = 800;
            var h = new double[n]; var l = new double[n]; var c = new double[n];
            var rnd = new Random(7);
            double p = 500;
            for (var i = 0; i < n; i++)
            {
                p += rnd.NextDouble() - 0.5;
                c[i] = p; h[i] = p + 0.3; l[i] = p - 0.3;
            }
            ChartHelper.HighData = h; ChartHelper.LowData = l; ChartHelper.CloseData = c;
            var helper = new ChartHelper();

            var manual = Settings(K, 1, false);
            var data = new RegimeData();
            Check(data.Update(helper, n, manual, 1000), "первый расчёт выполнен");
            Check(data.HasResult && !double.IsNaN(data.Er(n - 1)), $"ER последнего бара = {data.Er(n - 1):F3}");
            Check(data.ErLow == manual.ErLow && data.ErHigh == manual.ErHigh, "ручной режим: пороги как заданы");
            Check(!data.AutoApplied, "ручной режим: автокалибровки не было");
            Check(!data.Update(helper, n, manual, 1005), "цена не менялась → пересчёта нет");

            c[n - 1] += 5;
            Check(!data.Update(helper, n, manual, 1050), "цена сдвинулась, но троттлинг 100 мс ещё держит");
            Check(data.Update(helper, n, manual, 1200), "прошло > 100 мс → пересчёт");

            var other = manual; other.Window = K + 1;
            Check(data.Update(helper, n, other, 1205), "смена окна пересчитывает сразу, без троттлинга");
            Check(data.Update(helper, n - 10, other, 1206), "смена числа баров пересчитывает сразу");

            var auto = Settings(K, 1, true);
            Check(data.Update(helper, n, auto, 1300), "переключение на автопороги пересчитывает");
            Check(data.AutoApplied, "автопороги посчитаны");
            Check(data.ErLow < data.ErHigh && data.RrLow < data.RrHigh, $"ER[{data.ErLow:F2};{data.ErHigh:F2}] RR[{data.RrLow:F2};{data.RrHigh:F2}]");
            Check(data.ErHigh - data.ErLow > data.RrHigh - data.RrLow, "у ER разброс шире, чем у RR — пороги считаются раздельно");

            // Доля подсвеченных баров = то, что заказано процентилями
            var lit = 0; var total = 0;
            for (var i = 0; i < data.Count; i++)
            {
                if (double.IsNaN(data.Er(i))) continue;
                total++;
                if (data.ErRegime(i) != 0) lit++;
            }
            var share = 100.0 * lit / total;
            Check(share > 20 && share < 45, $"подсвечено {share:F0}% баров при процентилях 15/85 (ожидаем ≈30% на последних 500)");

            Check(!data.Update(null, n, auto, 9000), "helper == null → без исключения");

            data.Reset();
            Check(!data.HasResult, "Reset очищает результат");
        }

        Console.WriteLine();
        Console.WriteLine("5. Прогон «десериализованного инстанса» (конструктор НЕ выполняется)");
        {
            ChartDataProvider.Bars = 300;
            var n = ChartDataProvider.Bars;
            var h = new double[n]; var l = new double[n]; var c = new double[n];
            var rnd = new Random(42);
            double p = 30000;
            for (var i = 0; i < n; i++)
            {
                p += (rnd.NextDouble() - 0.5) * 20;
                c[i] = p; h[i] = p + 6; l[i] = p - 6;
            }
            ChartHelper.HighData = h; ChartHelper.LowData = l; ChartHelper.CloseData = c;

            Check(DeserializedPass(typeof(RegimeIndicator), new RegimeIndicator()), "Regime: сеттеры + Execute/Render/GetMinMax/GetValues/GetLabels не падают");
            Check(DeserializedPass(typeof(RegimeShadeIndicator), new RegimeShadeIndicator()), "Regime Shade: то же самое");

            // Чарт сохранён ПРЕДЫДУЩЕЙ версией: новых DataMember в XML нет,
            // конструктор не выполняется → поля приезжают нулями.
            var zero = default(RegimeSettings).Heal();
            Check(zero.Window == RegimeDefaults.Window && zero.LowPct == RegimeDefaults.LowPct
                  && zero.HighPct == RegimeDefaults.HighPct && zero.ErHigh > zero.ErLow
                  && zero.RrHigh > zero.RrLow && zero.VrHigh > zero.VrLow,
                  "нулевые настройки чинятся дефолтами");
            Check(!string.IsNullOrEmpty(zero.Healed), $"чинилка отчитывается в лог: «{zero.Healed}»");
            Check(Settings(K, 1, true).Heal().Healed == "", "корректные настройки не трогаются");

            Check(OldConfigPass(), "старый чарт: заливка не красит подряд все бары (пороги 0/0 покрасили бы)");
        }

        Console.WriteLine();
        Console.WriteLine("6. Отрисовка");
        {
            var ind = new RegimeIndicator
            {
                Window = K,
                ThresholdMode = RegimeThresholdMode.Manual,
                ErLow = 0.95, ErHigh = 1.05,
                ShowVr = true, VrLookback = 32,
            };
            Invoke(ind, "Execute");

            double min, max;
            Check(ind.GetMinMax(out min, out max), "GetMinMax вернул шкалу");
            Check(min <= 1.0 && max >= 1.0, $"шкала [{min:F2}; {max:F2}] включает уровень 1.0");
            Check(min <= ind.ErLow && max >= ind.ErHigh, "шкала включает оба порога главной метрики");
            IndicatorBase.ScaleMin = min; IndicatorBase.ScaleMax = max;

            var q = new DxVisualQueue();
            ind.Render(q);
            var lines = q.Ops.FindAll(o => o.StartsWith("line")).Count;
            var fills = q.Ops.FindAll(o => o.StartsWith("fill")).Count;
            var texts = q.Ops.FindAll(o => o.StartsWith("text")).Count;
            Check(lines > 100, $"линии нарисованы ({lines} сегментов: три метрики по видимым барам)");
            Check(fills > 0, $"заливка режимов есть ({fills} прямоугольников)");
            Check(texts == 1, "строка состояния одна");

            var values = ind.GetValues(ChartCanvas.FirstBar + 50);
            Check(values.Count == 3, "под курсором показываются все три значения");

            var labels = new List<IndicatorLabelInfo>();
            ind.GetLabels(ref labels);
            Check(labels.Count == 3, "на шкале три метки");

            // Индикатор сняли с графика — DataProvider обнулился
            IndicatorBase.ProviderAvailable = false;
            var q2 = new DxVisualQueue();
            ind.Render(q2);
            Invoke(ind, "Execute");
            ind.CheckNeedRedraw();
            double m1, m2;
            ind.GetMinMax(out m1, out m2);
            Check(q2.Ops.Count == 0, "снятый с графика индикатор ничего не рисует и не падает");
            IndicatorBase.ProviderAvailable = true;

            var shade = new RegimeShadeIndicator
            {
                Window = K,
                ThresholdMode = RegimeThresholdMode.Manual,
                ErLow = 0.95, ErHigh = 1.05,
            };
            Invoke(shade, "Execute");
            var q3 = new DxVisualQueue();
            shade.Render(q3);
            var bands = q3.Ops.FindAll(o => o.StartsWith("fill")).Count;
            Check(bands > 0, $"подсветка фона рисует полосы ({bands})");
            Check(bands < ChartCanvas.Slots, "соседние бары одного режима слиты в одну полосу");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ВСЁ ОК" : $"ПРОВАЛЕНО: {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// Ровно то, что делает терминал при загрузке конфигурации: объект без
    /// конструктора → CopyTemplate → все DataMember-сеттеры → колбэки.
    /// Любое исключение здесь = чарт теряет привязку к инструменту.
    /// </summary>
    static bool DeserializedPass(Type type, IndicatorBase template)
    {
        try
        {
            var raw = (IndicatorBase)RuntimeHelpers.GetUninitializedObject(type);
            raw.CopyTemplate(template, false);

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.GetIndexParameters().Length > 0) continue;
                p.SetValue(raw, p.GetValue(raw));       // запись тем же значением
                p.SetValue(raw, p.GetValue(template));  // и значением из шаблона
            }

            Invoke(raw, "Execute");
            raw.CheckNeedRedraw();
            double min, max;
            raw.GetMinMax(out min, out max);
            IndicatorBase.ScaleMin = min; IndicatorBase.ScaleMax = max;
            raw.Render(new DxVisualQueue());
            raw.GetValues(ChartCanvas.FirstBar + 10);
            var labels = new List<IndicatorLabelInfo>();
            raw.GetLabels(ref labels);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("         " + (ex.InnerException ?? ex));
            return false;
        }
    }

    /// <summary>
    /// Индикатор, восстановленный из чарта версии 0.1: заданы только те свойства,
    /// которые в той версии существовали, всё новое остаётся default(0/false).
    /// </summary>
    static bool OldConfigPass()
    {
        try
        {
            var t = typeof(RegimeIndicator);
            var raw = (RegimeIndicator)RuntimeHelpers.GetUninitializedObject(t);

            var fromXml = new Dictionary<string, object>
            {
                { "Window", 12 }, { "Smooth", 1 },
                { "ShowEr", true }, { "ShowRr", true },
                { "ShowFill", true }, { "ShowTitle", true },
                { "LineWidth", 2 },
            };
            foreach (var kv in fromXml) t.GetProperty(kv.Key).SetValue(raw, kv.Value);

            Invoke(raw, "Execute");
            double min, max;
            raw.GetMinMax(out min, out max);
            IndicatorBase.ScaleMin = min; IndicatorBase.ScaleMax = max;

            var q = new DxVisualQueue();
            raw.Render(q);

            // Пороги 0/0 дали бы Regime = +1 на каждом баре → заливка во всех слотах.
            var fills = q.Ops.FindAll(o => o.StartsWith("fill")).Count;
            return fills > 0 && fills < ChartCanvas.Slots;
        }
        catch (Exception ex)
        {
            Console.WriteLine("         " + (ex.InnerException ?? ex));
            return false;
        }
    }

    static RegimeSettings Settings(int window, int smooth, bool auto)
    {
        RegimeSettings s;
        s.Window = window; s.Smooth = smooth;
        s.NeedVr = false; s.VrLookback = 96;
        s.AutoThresholds = auto; s.AutoLookback = 500; s.LowPct = 15; s.HighPct = 85;
        s.ErLow = RegimeDefaults.ErLow; s.ErHigh = RegimeDefaults.ErHigh;
        s.RrLow = RegimeDefaults.RrLow; s.RrHigh = RegimeDefaults.RrHigh;
        s.VrLow = RegimeDefaults.VrLow; s.VrHigh = RegimeDefaults.VrHigh;
        s.Healed = "";
        return s;
    }

    static double Mean(double[] a, int from, int to)
    {
        double sum = 0; var n = 0;
        for (var i = from; i < to; i++) { if (double.IsNaN(a[i])) continue; sum += a[i]; n++; }
        return n > 0 ? sum / n : double.NaN;
    }

    static double Sd(double[] a, int from, int to)
    {
        var m = Mean(a, from, to);
        double sq = 0; var n = 0;
        for (var i = from; i < to; i++) { if (double.IsNaN(a[i])) continue; sq += (a[i] - m) * (a[i] - m); n++; }
        return n > 1 ? Math.Sqrt(sq / n) : double.NaN;
    }

    static void Invoke(object target, string method)
    {
        var m = target.GetType().GetMethod(method,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        m.Invoke(target, null);
    }
}
