//--------------------------------------------------------------------------------
// RegimeData — кэш рядов ER/RR поверх RegimeCore: знает, когда пересчитывать,
// и умеет доставать цены из Helper терминала. Держится отдельно от индикатора,
// чтобы оба индикатора (панель и подсветка фона) считали одно и то же, и чтобы
// всё это гонялось в тестах без терминала.
//--------------------------------------------------------------------------------

using System;

namespace RegimeMeter
{
    public sealed class RegimeData
    {
        private double[] _er, _rr;
        private int _count;
        private int _window, _smooth;
        private int _lastTick;
        private double _lastClose;
        private bool _hasResult;

        /// <summary>Минимальный интервал полного пересчёта, мс (если число баров не менялось).</summary>
        public int ThrottleMs = 100;

        /// <summary>Как были получены close — для однократной записи в лог.</summary>
        public string CloseSource { get; private set; }

        public int Count => _count;

        public double Er(int bar) => bar >= 0 && bar < _count && _er != null ? _er[bar] : RegimeCore.Undefined;
        public double Rr(int bar) => bar >= 0 && bar < _count && _rr != null ? _rr[bar] : RegimeCore.Undefined;

        public bool HasResult => _hasResult;

        /// <summary>
        /// Пересчитать при необходимости. helper — объект Helper терминала,
        /// nowTicks — Environment.TickCount. Возвращает true, если пересчитали.
        /// </summary>
        public bool Update(object helper, int barCount, int window, int smooth, int nowTicks)
        {
            if (helper == null || barCount <= 0) { _hasResult = false; return false; }

            var k = RegimeCore.Clamp(window);
            if (smooth < 1) smooth = 1;

            var high = ChartArrays.Read(helper, "High", barCount);
            var low = ChartArrays.Read(helper, "Low", barCount);
            if (high == null || low == null) { _hasResult = false; return false; }

            string how;
            var close = ChartArrays.ReadCloses(helper, barCount, out how);
            if (close == null)
            {
                // Массива закрытий у этой версии терминала нет — работаем по серединам
                // баров. ER при этом чуть занижен (середина сглаживает путь), пороги
                // лучше сдвинуть; в README про это сказано.
                close = new double[barCount];
                for (var i = 0; i < barCount; i++) close[i] = (high[i] + low[i]) / 2.0;
                how = "(H+L)/2 — Helper.Close не найден";
            }
            CloseSource = how;

            var lastClose = close[barCount - 1];
            var settingsChanged = k != _window || smooth != _smooth;
            var barsChanged = barCount != _count;
            var priceChanged = lastClose != _lastClose;

            if (!settingsChanged && !barsChanged && _hasResult)
            {
                if (!priceChanged) return false;
                // Цена шевелится — не чаще, чем раз в ThrottleMs (TickCount может
                // переполниться в минус, поэтому сравниваем разницу как unchecked).
                if (ThrottleMs > 0 && unchecked(nowTicks - _lastTick) < ThrottleMs) return false;
            }

            if (_er == null || _er.Length < barCount) { _er = new double[barCount]; _rr = new double[barCount]; }

            RegimeCore.Compute(high, low, close, barCount, k, _er, _rr);
            RegimeCore.Smooth(_er, barCount, smooth);
            RegimeCore.Smooth(_rr, barCount, smooth);

            _count = barCount;
            _window = k;
            _smooth = smooth;
            _lastClose = lastClose;
            _lastTick = nowTicks;
            _hasResult = true;
            return true;
        }

        public void Reset()
        {
            _er = null; _rr = null; _count = 0; _window = 0; _smooth = 0;
            _hasResult = false; _lastClose = 0; _lastTick = 0;
        }
    }
}
