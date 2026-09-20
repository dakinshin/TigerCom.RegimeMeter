//--------------------------------------------------------------------------------
// RegimeData — кэш рядов ER/RR/VR поверх RegimeCore: знает, когда пересчитывать,
// умеет доставать цены из Helper терминала и калибровать пороги по самому
// инструменту. Держится отдельно от индикаторов, чтобы панель и подсветка фона
// считали ровно одно и то же и чтобы всё это гонялось в тестах без терминала.
//--------------------------------------------------------------------------------

using System;

namespace RegimeMeter
{
    /// <summary>
    /// Значения по умолчанию в одном месте: их ставят конструкторы индикаторов
    /// И чинилка настроек в RegimeData.Update. Второе обязательно, потому что
    /// конфигурация могла быть сохранена ПРЕДЫДУЩЕЙ версией индикатора: новых
    /// DataMember-полей в XML нет, конструктор при загрузке не выполняется, и
    /// поля приезжают нулями. Пороги 0/0 покрасили бы весь график одним цветом.
    /// </summary>
    public static class RegimeDefaults
    {
        public const int Window = 12;
        public const int Smooth = 1;

        public const int AutoLookback = 500;
        public const int LowPct = 15;
        public const int HighPct = 85;

        // Квартили чистого случайного блуждания, измерены на 400 000 баров
        public const double ErLow = 0.42, ErHigh = 1.49;
        public const double RrLow = 0.84, RrHigh = 1.22;
        public const double VrLow = 0.85, VrHigh = 1.09;

        public const int VrLookback = 96;
        public const int LineWidth = 2;
    }

    public struct RegimeSettings
    {
        public int Window;
        public int Smooth;

        public bool NeedVr;
        public int VrLookback;

        /// <summary>Пороги по квантилям самого инструмента вместо ручных чисел.</summary>
        public bool AutoThresholds;
        public int AutoLookback;
        public double LowPct, HighPct;

        public double ErLow, ErHigh, RrLow, RrHigh, VrLow, VrHigh;

        /// <summary>Что пришлось починить после загрузки старой конфигурации; "" — всё было в порядке.</summary>
        public string Healed;

        /// <summary>
        /// Привести настройки в рабочий вид. Нули и бессмыслицу заменяем дефолтами:
        /// после обновления индикатора в сохранённом чарте новых полей просто нет,
        /// а конструктор при десериализации не выполняется.
        /// </summary>
        public RegimeSettings Heal()
        {
            var s = this;
            var fixes = "";

            if (s.Window < RegimeCore.MinWindow) { s.Window = RegimeDefaults.Window; fixes += "Window "; }
            if (s.Smooth < 1) s.Smooth = RegimeDefaults.Smooth;
            if (s.VrLookback < 8) { s.VrLookback = RegimeDefaults.VrLookback; fixes += "VrLookback "; }
            if (s.AutoLookback < 50) { s.AutoLookback = RegimeDefaults.AutoLookback; fixes += "AutoLookback "; }

            if (s.LowPct < 1 || s.LowPct > 49) { s.LowPct = RegimeDefaults.LowPct; fixes += "LowPct "; }
            if (s.HighPct < 51 || s.HighPct > 99) { s.HighPct = RegimeDefaults.HighPct; fixes += "HighPct "; }

            if (!(s.ErHigh > s.ErLow) || s.ErLow <= 0)
            { s.ErLow = RegimeDefaults.ErLow; s.ErHigh = RegimeDefaults.ErHigh; fixes += "ER-пороги "; }
            if (!(s.RrHigh > s.RrLow) || s.RrLow <= 0)
            { s.RrLow = RegimeDefaults.RrLow; s.RrHigh = RegimeDefaults.RrHigh; fixes += "RR-пороги "; }
            if (!(s.VrHigh > s.VrLow) || s.VrLow <= 0)
            { s.VrLow = RegimeDefaults.VrLow; s.VrHigh = RegimeDefaults.VrHigh; fixes += "VR-пороги "; }

            s.Healed = fixes.Length > 0 ? fixes.Trim() : "";
            return s;
        }

        public bool SameAs(RegimeSettings o) =>
            Window == o.Window && Smooth == o.Smooth && Healed == o.Healed &&
            NeedVr == o.NeedVr && VrLookback == o.VrLookback &&
            AutoThresholds == o.AutoThresholds && AutoLookback == o.AutoLookback &&
            LowPct == o.LowPct && HighPct == o.HighPct &&
            ErLow == o.ErLow && ErHigh == o.ErHigh &&
            RrLow == o.RrLow && RrHigh == o.RrHigh &&
            VrLow == o.VrLow && VrHigh == o.VrHigh;
    }

    public sealed class RegimeData
    {
        private double[] _er, _rr, _vr;
        private int _count;
        private RegimeSettings _applied;
        private bool _hasApplied;
        private int _lastTick;
        private double _lastClose;
        private bool _hasResult;

        /// <summary>Минимальный интервал полного пересчёта, мс (если число баров не менялось).</summary>
        public int ThrottleMs = 100;

        /// <summary>Как были получены close — для однократной записи в лог.</summary>
        public string CloseSource { get; private set; }

        public int Count => _count;
        public bool HasResult => _hasResult;

        /// <summary>Действующие пороги: ручные либо посчитанные по квантилям.</summary>
        public double ErLow { get; private set; }
        public double ErHigh { get; private set; }
        public double RrLow { get; private set; }
        public double RrHigh { get; private set; }
        public double VrLow { get; private set; }
        public double VrHigh { get; private set; }

        /// <summary>true, если автопороги удалось посчитать (иначе действуют ручные).</summary>
        public bool AutoApplied { get; private set; }

        /// <summary>Непустая строка, если настройки пришлось чинить после загрузки старой конфигурации.</summary>
        public string Healed { get; private set; }

        public double Er(int bar) => Get(_er, bar);
        public double Rr(int bar) => Get(_rr, bar);
        public double Vr(int bar) => Get(_vr, bar);

        private double Get(double[] a, int bar) =>
            a != null && bar >= 0 && bar < _count ? a[bar] : RegimeCore.Undefined;

        public int ErRegime(int bar) => RegimeCore.Regime(Er(bar), ErLow, ErHigh);
        public int RrRegime(int bar) => RegimeCore.Regime(Rr(bar), RrLow, RrHigh);
        public int VrRegime(int bar) => RegimeCore.Regime(Vr(bar), VrLow, VrHigh);

        /// <summary>
        /// Пересчитать при необходимости. helper — объект Helper терминала,
        /// nowTicks — Environment.TickCount. Возвращает true, если пересчитали.
        /// </summary>
        public bool Update(object helper, int barCount, RegimeSettings s, int nowTicks)
        {
            if (helper == null || barCount <= 0) { _hasResult = false; return false; }

            s = s.Heal();
            s.Window = RegimeCore.Clamp(s.Window);
            Healed = s.Healed;

            var high = ChartArrays.Read(helper, "High", barCount);
            var low = ChartArrays.Read(helper, "Low", barCount);
            if (high == null || low == null) { _hasResult = false; return false; }

            string how;
            var close = ChartArrays.ReadCloses(helper, barCount, out how);
            if (close == null)
            {
                // Массива закрытий у этой версии терминала нет — работаем по серединам
                // баров. ER при этом занижен (середина сглаживает путь); автопороги
                // это частично компенсируют, но в лог факт пишем.
                close = new double[barCount];
                for (var i = 0; i < barCount; i++) close[i] = (high[i] + low[i]) / 2.0;
                how = "(H+L)/2 — Helper.Close не найден";
            }
            CloseSource = how;

            var lastClose = close[barCount - 1];
            var settingsChanged = !_hasApplied || !s.SameAs(_applied);
            var barsChanged = barCount != _count;

            if (!settingsChanged && !barsChanged && _hasResult)
            {
                if (lastClose == _lastClose) return false;
                // Цена шевелится — не чаще, чем раз в ThrottleMs (TickCount может
                // переполниться в минус, поэтому разницу считаем unchecked).
                if (ThrottleMs > 0 && unchecked(nowTicks - _lastTick) < ThrottleMs) return false;
            }

            if (_er == null || _er.Length < barCount)
            {
                _er = new double[barCount];
                _rr = new double[barCount];
                _vr = new double[barCount];
            }

            RegimeCore.Compute(high, low, close, barCount, s.Window, _er, _rr);
            RegimeCore.Smooth(_er, barCount, s.Smooth);
            RegimeCore.Smooth(_rr, barCount, s.Smooth);

            if (s.NeedVr)
            {
                RegimeCore.ComputeVr(close, barCount, s.Window, s.VrLookback, _vr);
                RegimeCore.Smooth(_vr, barCount, s.Smooth);
            }
            else
            {
                for (var i = 0; i < barCount; i++) _vr[i] = RegimeCore.Undefined;
            }

            ApplyThresholds(s, barCount);

            _count = barCount;
            _applied = s;
            _hasApplied = true;
            _lastClose = lastClose;
            _lastTick = nowTicks;
            _hasResult = true;
            return true;
        }

        /// <summary>
        /// Ручные пороги либо квантили по последним AutoLookback барам. Каждой
        /// метрике — свои: разброс у ER кратно шире, чем у RR, общая пара чисел
        /// для обеих даёт мусор на одной из них.
        /// </summary>
        private void ApplyThresholds(RegimeSettings s, int barCount)
        {
            ErLow = s.ErLow; ErHigh = s.ErHigh;
            RrLow = s.RrLow; RrHigh = s.RrHigh;
            VrLow = s.VrLow; VrHigh = s.VrHigh;
            AutoApplied = false;
            if (!s.AutoThresholds) return;

            double lo, hi;
            var any = false;
            if (RegimeCore.Quantiles(_er, barCount, s.AutoLookback, s.LowPct, s.HighPct, out lo, out hi))
            { ErLow = lo; ErHigh = hi; any = true; }
            if (RegimeCore.Quantiles(_rr, barCount, s.AutoLookback, s.LowPct, s.HighPct, out lo, out hi))
            { RrLow = lo; RrHigh = hi; any = true; }
            if (s.NeedVr && RegimeCore.Quantiles(_vr, barCount, s.AutoLookback, s.LowPct, s.HighPct, out lo, out hi))
            { VrLow = lo; VrHigh = hi; }
            AutoApplied = any;
        }

        public void Reset()
        {
            _er = null; _rr = null; _vr = null;
            _count = 0; _hasApplied = false; _hasResult = false;
            _lastClose = 0; _lastTick = 0; AutoApplied = false; Healed = null;
        }
    }
}
