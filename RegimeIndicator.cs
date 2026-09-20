//--------------------------------------------------------------------------------
// Regime — отдельная панель под графиком: запильно цена идёт или безоткатно.
//
// Окно k баров = «старшая свеча», сами бары = «младшие». Три метрики на одной
// шкале (1.0 = случайное блуждание):
//
//   ER (толстая) = √k · |C[i] − C[i−k]| / Σ|ΔC|   — безоткатность хода, быстрая
//   RR (тонкая)  = √k · (HH − LL) / Σ TR          — размах старшей свечи
//   VR (пунктир) = √(дисперсия k-барных / k·дисперсия барных) — медленная, статистическая
//
// ВАЖНО про чувствительность. ER берёт ОДНО смещение за окно, поэтому шумит
// одинаково при любом k: на чистом случайном блуждании его ст.отклонение ≈ 0.72
// и для k=6, и для k=48. Увеличение k замедляет реакцию, но НЕ делает показание
// надёжнее. Поэтому фиксированные пороги вроде «1.20» бессмысленны — на шуме
// они пробиваются больше чем в трети баров. По умолчанию пороги считаются по
// квантилям самого инструмента (см. раздел «2. Пороги»), а за статистически
// честным ответом «это точно не шум» — линия VR. Цифры в README.
//
// Математика — в RegimeCore.cs (проверяется тестами без терминала).
//
// ВАЖНО про загрузку (инцидент 29.08.2026): терминал создаёт индикатор БЕЗ
// конструктора (DataContractSerializer → GetUninitializedObject) и сразу дёргает
// сеттеры через CopyTemplate. Поэтому: никаких инициализаторов полей, сеттеры
// пишут только своё поле, все runtime-объекты ленивые.
//--------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Media;
using RegimeMeter;
using TigerTrade.Chart.Alerts;
using TigerTrade.Chart.Base;
using TigerTrade.Chart.Indicators.Common;
using TigerTrade.Chart.Indicators.Enums;
using TigerTrade.Core.UI.Converters;
using TigerTrade.Dx;
using TigerTrade.Dx.Enums;

namespace TigerTrade.Chart.Indicators.Custom
{
    [TypeConverter(typeof(EnumDescriptionTypeConverter))]
    [DataContract(Name = "RegimeThresholdMode",
        Namespace = "http://schemas.datacontract.org/2004/07/TigerTrade.Chart.Indicators.Custom")]
    public enum RegimeThresholdMode
    {
        [EnumMember(Value = "Auto"), Description("Авто — квантили этого инструмента")] Auto,
        [EnumMember(Value = "Manual"), Description("Вручную — числа ниже")] Manual,
    }

    [DataContract(Name = "RegimeIndicator",
        Namespace = "http://schemas.datacontract.org/2004/07/TigerTrade.Chart.Indicators.Custom")]
    [Indicator("Z_Regime", "Regime", false, Type = typeof(RegimeIndicator))]
    internal sealed class RegimeIndicator : IndicatorBase
    {
        // ======================= 1. Расчёт =======================

        private int _window;
        [DataMember(Name = "Window")]
        [Category("1. Расчёт"), DisplayName("Окно k (во сколько раз «старшая свеча» крупнее бара)")]
        public int Window
        {
            get => _window;
            set
            {
                value = value < RegimeCore.MinWindow ? RegimeCore.MinWindow
                      : (value > RegimeCore.MaxWindow ? RegimeCore.MaxWindow : value);
                if (value == _window) return;
                _window = value; Touch(); OnPropertyChanged();
            }
        }

        private int _smooth;
        [DataMember(Name = "Smooth")]
        [Category("1. Расчёт"), DisplayName("Сглаживание EMA, баров (1 = выкл)")]
        public int Smooth
        {
            get => _smooth;
            set
            {
                value = value < 1 ? 1 : (value > 100 ? 100 : value);
                if (value == _smooth) return;
                _smooth = value; Touch(); OnPropertyChanged();
            }
        }

        // ======================= 2. Пороги =======================

        private RegimeThresholdMode _thresholdMode;
        [DataMember(Name = "ThresholdMode")]
        [Category("2. Пороги"), DisplayName("Откуда брать пороги")]
        public RegimeThresholdMode ThresholdMode
        {
            get => _thresholdMode;
            set { if (value == _thresholdMode) return; _thresholdMode = value; Touch(); OnPropertyChanged(); }
        }

        private int _autoLookback;
        [DataMember(Name = "AutoLookback")]
        [Category("2. Пороги"), DisplayName("Авто: по скольким последним барам калибровать")]
        public int AutoLookback
        {
            get => _autoLookback;
            set
            {
                value = value < 50 ? 50 : (value > 20000 ? 20000 : value);
                if (value == _autoLookback) return;
                _autoLookback = value; Touch(); OnPropertyChanged();
            }
        }

        private int _lowPct;
        [DataMember(Name = "LowPct")]
        [Category("2. Пороги"), DisplayName("Авто: нижний процентиль («запил»), %")]
        public int LowPct
        {
            get => _lowPct;
            set
            {
                value = value < 1 ? 1 : (value > 49 ? 49 : value);
                if (value == _lowPct) return;
                _lowPct = value; Touch(); OnPropertyChanged();
            }
        }

        private int _highPct;
        [DataMember(Name = "HighPct")]
        [Category("2. Пороги"), DisplayName("Авто: верхний процентиль («безоткатно»), %")]
        public int HighPct
        {
            get => _highPct;
            set
            {
                value = value < 51 ? 51 : (value > 99 ? 99 : value);
                if (value == _highPct) return;
                _highPct = value; Touch(); OnPropertyChanged();
            }
        }

        // ================= 3. Пороги вручную (когда режим «вручную») =================
        // Значения по умолчанию — измеренные квартили на чистом случайном блуждании,
        // у каждой метрики свои: разброс ER кратно шире, чем у RR.

        private double _erLow;
        [DataMember(Name = "ErLow")]
        [Category("3. Пороги вручную"), DisplayName("ER: «запил» ниже")]
        public double ErLow
        {
            get => _erLow;
            set { value = ClampLevel(value); if (value == _erLow) return; _erLow = value; Touch(); OnPropertyChanged(); }
        }

        private double _erHigh;
        [DataMember(Name = "ErHigh")]
        [Category("3. Пороги вручную"), DisplayName("ER: «безоткатно» выше")]
        public double ErHigh
        {
            get => _erHigh;
            set { value = ClampLevel(value); if (value == _erHigh) return; _erHigh = value; Touch(); OnPropertyChanged(); }
        }

        private double _rrLow;
        [DataMember(Name = "RrLow")]
        [Category("3. Пороги вручную"), DisplayName("RR: «запил» ниже")]
        public double RrLow
        {
            get => _rrLow;
            set { value = ClampLevel(value); if (value == _rrLow) return; _rrLow = value; Touch(); OnPropertyChanged(); }
        }

        private double _rrHigh;
        [DataMember(Name = "RrHigh")]
        [Category("3. Пороги вручную"), DisplayName("RR: «безоткатно» выше")]
        public double RrHigh
        {
            get => _rrHigh;
            set { value = ClampLevel(value); if (value == _rrHigh) return; _rrHigh = value; Touch(); OnPropertyChanged(); }
        }

        private double _vrLow;
        [DataMember(Name = "VrLow")]
        [Category("3. Пороги вручную"), DisplayName("VR: «запил» ниже")]
        public double VrLow
        {
            get => _vrLow;
            set { value = ClampLevel(value); if (value == _vrLow) return; _vrLow = value; Touch(); OnPropertyChanged(); }
        }

        private double _vrHigh;
        [DataMember(Name = "VrHigh")]
        [Category("3. Пороги вручную"), DisplayName("VR: «безоткатно» выше")]
        public double VrHigh
        {
            get => _vrHigh;
            set { value = ClampLevel(value); if (value == _vrHigh) return; _vrHigh = value; Touch(); OnPropertyChanged(); }
        }

        // ======================= 4. Метрики =======================

        private bool _showEr;
        [DataMember(Name = "ShowEr")]
        [Category("4. Метрики"), DisplayName("ER — безоткатность (быстрая, шумная)")]
        public bool ShowEr
        {
            get => _showEr;
            set { if (value == _showEr) return; _showEr = value; Touch(); OnPropertyChanged(); }
        }

        private bool _showRr;
        [DataMember(Name = "ShowRr")]
        [Category("4. Метрики"), DisplayName("RR — размах")]
        public bool ShowRr
        {
            get => _showRr;
            set { if (value == _showRr) return; _showRr = value; Touch(); OnPropertyChanged(); }
        }

        private bool _showVr;
        [DataMember(Name = "ShowVr")]
        [Category("4. Метрики"), DisplayName("VR — медленная, статистическая")]
        public bool ShowVr
        {
            get => _showVr;
            set { if (value == _showVr) return; _showVr = value; Touch(); OnPropertyChanged(); }
        }

        private int _vrLookback;
        [DataMember(Name = "VrLookback")]
        [Category("4. Метрики"), DisplayName("VR: по скольким барам усреднять")]
        public int VrLookback
        {
            get => _vrLookback;
            set
            {
                value = value < 8 ? 8 : (value > 2000 ? 2000 : value);
                if (value == _vrLookback) return;
                _vrLookback = value; Touch(); OnPropertyChanged();
            }
        }

        // ======================= 5. Вид =======================

        private bool _showFill;
        [DataMember(Name = "ShowFill")]
        [Category("5. Вид"), DisplayName("Заливка между главной линией и уровнем 1.0")]
        public bool ShowFill
        {
            get => _showFill;
            set { if (value == _showFill) return; _showFill = value; Touch(); OnPropertyChanged(); }
        }

        private bool _showTitle;
        [DataMember(Name = "ShowTitle")]
        [Category("5. Вид"), DisplayName("Строка состояния в углу панели")]
        public bool ShowTitle
        {
            get => _showTitle;
            set { if (value == _showTitle) return; _showTitle = value; Touch(); OnPropertyChanged(); }
        }

        private int _lineWidth;
        [DataMember(Name = "LineWidth")]
        [Category("5. Вид"), DisplayName("Толщина главной линии, px")]
        public int LineWidth
        {
            get => _lineWidth;
            set
            {
                value = value < 1 ? 1 : (value > 6 ? 6 : value);
                if (value == _lineWidth) return;
                _lineWidth = value; _erPen = null; Touch(); OnPropertyChanged();
            }
        }

        private XColor _erColor;
        [DataMember(Name = "ErColor")]
        [Category("5. Вид"), DisplayName("Цвет линии ER")]
        public XColor ErColor
        {
            get => _erColor;
            set { if (value == _erColor) return; _erColor = value; _erBrush = null; _erPen = null; Touch(); OnPropertyChanged(); }
        }

        private XColor _rrColor;
        [DataMember(Name = "RrColor")]
        [Category("5. Вид"), DisplayName("Цвет линии RR")]
        public XColor RrColor
        {
            get => _rrColor;
            set { if (value == _rrColor) return; _rrColor = value; _rrBrush = null; _rrPen = null; Touch(); OnPropertyChanged(); }
        }

        private XColor _vrColor;
        [DataMember(Name = "VrColor")]
        [Category("5. Вид"), DisplayName("Цвет линии VR")]
        public XColor VrColor
        {
            get => _vrColor;
            set { if (value == _vrColor) return; _vrColor = value; _vrBrush = null; _vrPen = null; Touch(); OnPropertyChanged(); }
        }

        private XColor _trendColor;
        [DataMember(Name = "TrendColor")]
        [Category("5. Вид"), DisplayName("Заливка «безоткатно» (с прозрачностью)")]
        public XColor TrendColor
        {
            get => _trendColor;
            set { if (value == _trendColor) return; _trendColor = value; _trendBrush = null; Touch(); OnPropertyChanged(); }
        }

        private XColor _chopColor;
        [DataMember(Name = "ChopColor")]
        [Category("5. Вид"), DisplayName("Заливка «запил» (с прозрачностью)")]
        public XColor ChopColor
        {
            get => _chopColor;
            set { if (value == _chopColor) return; _chopColor = value; _chopBrush = null; Touch(); OnPropertyChanged(); }
        }

        // =========================== Служебное ===========================
        // Никаких «= new» у полей: конструктор при загрузке конфигурации не выполняется.

        private RegimeData _data;
        private RegimeData Data => _data ?? (_data = new RegimeData());

        private XBrush _erBrush, _rrBrush, _vrBrush, _trendBrush, _chopBrush;
        private XBrush ErBrush => _erBrush ?? (_erBrush = new XBrush(_erColor));
        private XBrush RrBrush => _rrBrush ?? (_rrBrush = new XBrush(_rrColor));
        private XBrush VrBrush => _vrBrush ?? (_vrBrush = new XBrush(_vrColor));
        private XBrush TrendBrush => _trendBrush ?? (_trendBrush = new XBrush(_trendColor));
        private XBrush ChopBrush => _chopBrush ?? (_chopBrush = new XBrush(_chopColor));

        private XPen _erPen, _rrPen, _vrPen;
        private XPen ErPen => _erPen ?? (_erPen = new XPen(ErBrush, _lineWidth < 1 ? 1 : _lineWidth, XDashStyle.Solid));
        private XPen RrPen => _rrPen ?? (_rrPen = new XPen(RrBrush, 1, XDashStyle.Solid));
        private XPen VrPen => _vrPen ?? (_vrPen = new XPen(VrBrush, 1, XDashStyle.Dash));

        private int _touch;
        private int _calcVersion;
        private long _renderedVersion;
        private bool _sourceLogged;

        [Browsable(false)]
        public override IndicatorCalculation Calculation => IndicatorCalculation.OnPriceChange;

        public RegimeIndicator()
        {
            Window = 12;
            Smooth = 1;

            ThresholdMode = RegimeThresholdMode.Auto;
            AutoLookback = 500;
            LowPct = 15;
            HighPct = 85;

            // Квартили чистого случайного блуждания, измерены на 400 000 баров
            ErLow = 0.42; ErHigh = 1.49;
            RrLow = 0.84; RrHigh = 1.22;
            VrLow = 0.85; VrHigh = 1.09;

            ShowEr = true; ShowRr = true; ShowVr = false; VrLookback = 96;

            ShowFill = true; ShowTitle = true; LineWidth = 2;
            ErColor = Color.FromArgb(255, 235, 195, 80);
            RrColor = Color.FromArgb(255, 120, 150, 200);
            VrColor = Color.FromArgb(255, 200, 200, 200);
            TrendColor = Color.FromArgb(55, 60, 190, 90);
            ChopColor = Color.FromArgb(55, 220, 60, 60);
        }

        private static double ClampLevel(double v) => v < 0.01 ? 0.01 : (v > 25 ? 25 : v);

        // ========================= Расчёт =========================

        private void Touch() { _touch++; }

        /// <summary>Версия состояния БЕЗ обращения к DataProvider — для CheckNeedRedraw.</summary>
        private long CurrentVersion() => 1L + ((long)_calcVersion << 20) + _touch;

        private RegimeSettings BuildSettings()
        {
            RegimeSettings s;
            s.Window = Window;
            s.Smooth = Smooth;
            s.NeedVr = ShowVr;
            s.VrLookback = VrLookback;
            s.AutoThresholds = ThresholdMode == RegimeThresholdMode.Auto;
            s.AutoLookback = AutoLookback;
            s.LowPct = LowPct;
            s.HighPct = HighPct;
            s.ErLow = ErLow; s.ErHigh = ErHigh;
            s.RrLow = RrLow; s.RrHigh = RrHigh;
            s.VrLow = VrLow; s.VrHigh = VrHigh;
            return s;
        }

        /// <summary>Пересчёт рядов. Только из Execute()/Render()/GetMinMax() — колбэков терминала.</summary>
        private void EnsureCalc()
        {
            var dp = DataProvider;
            if (dp == null) return;
            if (!Data.Update(Helper, dp.Count, BuildSettings(), Environment.TickCount)) return;

            _calcVersion++;
            if (!_sourceLogged)
            {
                _sourceLogged = true;
                Log.Info($"close: {Data.CloseSource}; k={Window}, smooth={Smooth}, bars={dp.Count}, " +
                         $"ceiling={RegimeCore.Ceiling(Window):F2}, auto={Data.AutoApplied}, " +
                         $"ER[{Data.ErLow:F2};{Data.ErHigh:F2}] RR[{Data.RrLow:F2};{Data.RrHigh:F2}]");
            }
        }

        protected override void Execute()
        {
            try { EnsureCalc(); }
            catch (Exception ex) { LogError("execute", ex); }
        }

        public override bool CheckNeedRedraw()
        {
            try { return CurrentVersion() != _renderedVersion; }
            catch { return false; }
        }

        // ======== Главная метрика: она ведёт заливку, пороговые линии и заголовок ========

        private int MainMetric() => ShowEr ? 0 : (ShowRr ? 1 : (ShowVr ? 2 : -1));

        private double Value(int metric, int bar)
        {
            switch (metric)
            {
                case 0: return Data.Er(bar);
                case 1: return Data.Rr(bar);
                case 2: return Data.Vr(bar);
                default: return RegimeCore.Undefined;
            }
        }

        private double LevelLow(int metric) =>
            metric == 0 ? Data.ErLow : (metric == 1 ? Data.RrLow : Data.VrLow);

        private double LevelHigh(int metric) =>
            metric == 0 ? Data.ErHigh : (metric == 1 ? Data.RrHigh : Data.VrHigh);

        private int RegimeAt(int metric, int bar) =>
            RegimeCore.Regime(Value(metric, bar), LevelLow(metric), LevelHigh(metric));

        // ========================= Шкала панели =========================

        public override bool GetMinMax(out double min, out double max)
        {
            min = 0; max = 2;
            try
            {
                EnsureCalc();
                var ceiling = RegimeCore.Ceiling(Window);
                if (!Data.HasResult) { max = ceiling; return true; }

                var lo = double.MaxValue;
                var hi = double.MinValue;
                var slots = Canvas.Count;
                for (var i = 0; i < slots; i++)
                {
                    var idx = Canvas.GetIndex(i);
                    if (idx < 0 || idx >= Data.Count) continue;
                    if (ShowEr) Accumulate(Data.Er(idx), ref lo, ref hi);
                    if (ShowRr) Accumulate(Data.Rr(idx), ref lo, ref hi);
                    if (ShowVr) Accumulate(Data.Vr(idx), ref lo, ref hi);
                }
                if (lo > hi) { min = 0; max = ceiling; return true; }

                // Уровень 1.0 и пороги главной метрики всегда в кадре.
                lo = Math.Min(lo, 1.0);
                hi = Math.Max(hi, 1.0);
                var main = MainMetric();
                if (main >= 0)
                {
                    lo = Math.Min(lo, LevelLow(main));
                    hi = Math.Max(hi, LevelHigh(main));
                }

                var pad = Math.Max(0.04, (hi - lo) * 0.08);
                min = Math.Max(0, lo - pad);
                max = hi + pad;
            }
            catch (Exception ex) { LogError("minmax", ex); min = 0; max = 2; }
            return true;
        }

        private static void Accumulate(double v, ref double lo, ref double hi)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }

        // ========================= Отрисовка =========================

        public override void Render(DxVisualQueue visual)
        {
            try
            {
                EnsureCalc();
                _renderedVersion = CurrentVersion();

                var dp = DataProvider;
                if (dp == null || !Data.HasResult) return;

                var rect = Canvas.Rect;
                var axisPen = new XPen(new XBrush(Canvas.Theme.ChartAxisColor), 1, XDashStyle.Solid);
                var dashPen = new XPen(new XBrush(Canvas.Theme.ChartAxisColor), 1, XDashStyle.Dash);

                var main = MainMetric();
                DrawLevel(visual, axisPen, rect, 1.0);
                if (main >= 0)
                {
                    DrawLevel(visual, dashPen, rect, LevelHigh(main));
                    DrawLevel(visual, dashPen, rect, LevelLow(main));
                }

                if (ShowFill && main >= 0) DrawFill(visual, rect, main);
                if (ShowVr) DrawSeries(visual, VrPen, 2);
                if (ShowRr) DrawSeries(visual, RrPen, 1);
                if (ShowEr) DrawSeries(visual, ErPen, 0);
                if (ShowTitle) DrawTitle(visual, rect, main);
            }
            catch (Exception ex)
            {
                _renderedVersion = CurrentVersion(); // не зацикливать перерисовку на той же ошибке
                LogError("render", ex);
            }
        }

        private void DrawLevel(DxVisualQueue visual, XPen pen, Rect rect, double value)
        {
            if (double.IsNaN(value)) return;
            var y = GetY(value);
            if (y < rect.Top || y > rect.Bottom) return;
            visual.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));
        }

        private void DrawSeries(DxVisualQueue visual, XPen pen, int metric)
        {
            var slots = Canvas.Count;
            var count = Data.Count;
            double px = 0, py = 0;
            var has = false;

            for (var i = 0; i < slots; i++)
            {
                var idx = Canvas.GetIndex(i);
                if (idx < 0 || idx >= count) { has = false; continue; }

                var v = Value(metric, idx);
                if (double.IsNaN(v) || double.IsInfinity(v)) { has = false; continue; }

                var x = Canvas.GetX(idx);
                var y = GetY(v);
                if (has) visual.DrawLine(pen, new Point(px, py), new Point(x, y));
                px = x; py = y; has = true;
            }
        }

        /// <summary>Столбики от уровня 1.0 до главной линии: зелёные выше верхнего порога, красные ниже нижнего.</summary>
        private void DrawFill(DxVisualQueue visual, Rect rect, int metric)
        {
            var slots = Canvas.Count;
            var count = Data.Count;
            var w = Canvas.ColumnWidth;
            if (w < 1) w = 1;
            var baseY = GetY(1.0);

            for (var i = 0; i < slots; i++)
            {
                var idx = Canvas.GetIndex(i);
                if (idx < 0 || idx >= count) continue;

                var regime = RegimeAt(metric, idx);
                if (regime == 0) continue;

                var v = Value(metric, idx);
                var y = GetY(v);
                var top = Math.Min(y, baseY);
                var bottom = Math.Max(y, baseY);
                if (top < rect.Top) top = rect.Top;
                if (bottom > rect.Bottom) bottom = rect.Bottom;
                var h = bottom - top;
                if (h < 1) h = 1;

                var x = Canvas.GetX(idx) - w / 2.0;
                visual.FillRectangle(regime > 0 ? TrendBrush : ChopBrush, new Rect(x, top, w, h));
            }
        }

        private void DrawTitle(DxVisualQueue visual, Rect rect, int main)
        {
            var last = Data.Count - 1 - Canvas.Start;
            if (last < 0) return;

            var text = "k" + Window.ToString(CultureInfo.InvariantCulture);
            if (ShowEr) text += Part("ER", Data.Er(last));
            if (ShowRr) text += Part("RR", Data.Rr(last));
            if (ShowVr) text += Part("VR", Data.Vr(last));

            var regime = main >= 0 ? RegimeAt(main, last) : 0;
            text += regime > 0 ? "  ▲ безоткатно" : (regime < 0 ? "  ▼ запил" : "  · нейтрально");
            if (main >= 0)
                text += string.Format(CultureInfo.InvariantCulture, "  [{0:0.00}…{1:0.00}]{2}",
                    LevelLow(main), LevelHigh(main), Data.AutoApplied ? " авто" : "");

            var font = Canvas.ChartFont;
            var w = font.GetWidth(text) + 8;
            var h = font.GetHeight() + 2;
            if (w < 8 || w > rect.Width) w = Math.Min(rect.Width, 380);

            var box = new Rect(rect.Left + 4, rect.Top + 2, w, h);
            if (regime > 0) visual.FillRectangle(TrendBrush, box);
            else if (regime < 0) visual.FillRectangle(ChopBrush, box);

            visual.DrawString(text, font, Canvas.Theme.ChartFontBrush,
                new Rect(box.Left + 4, box.Top, box.Width - 8, box.Height), XTextAlignment.Left);
        }

        private static string Part(string name, double v) =>
            double.IsNaN(v) ? "" : "  " + name + " " + v.ToString("0.00", CultureInfo.InvariantCulture);

        // ========================= Значения под курсором / метки шкалы =========================

        public override List<IndicatorValueInfo> GetValues(int cursorPos)
        {
            var info = new List<IndicatorValueInfo>();
            try
            {
                if (!Data.HasResult) return info;
                Add(info, ShowEr, "ER", Data.Er(cursorPos), ErBrush);
                Add(info, ShowRr, "RR", Data.Rr(cursorPos), RrBrush);
                Add(info, ShowVr, "VR", Data.Vr(cursorPos), VrBrush);
            }
            catch (Exception ex) { LogError("values", ex); }
            return info;
        }

        private static void Add(List<IndicatorValueInfo> info, bool show, string name, double v, XBrush brush)
        {
            if (!show || double.IsNaN(v)) return;
            info.Add(new IndicatorValueInfo(name + " " + v.ToString("0.00", CultureInfo.InvariantCulture), brush));
        }

        public override void GetLabels(ref List<IndicatorLabelInfo> labels)
        {
            try
            {
                if (!Data.HasResult || labels == null) return;
                var last = Data.Count - 1 - Canvas.Start;
                if (last < 0) return;

                if (ShowEr && !double.IsNaN(Data.Er(last))) labels.Add(new IndicatorLabelInfo(Data.Er(last), _erColor));
                if (ShowRr && !double.IsNaN(Data.Rr(last))) labels.Add(new IndicatorLabelInfo(Data.Rr(last), _rrColor));
                if (ShowVr && !double.IsNaN(Data.Vr(last))) labels.Add(new IndicatorLabelInfo(Data.Vr(last), _vrColor));
            }
            catch (Exception ex) { LogError("labels", ex); }
        }

        public override void CopyTemplate(IndicatorBase indicator, bool style)
        {
            var i = (RegimeIndicator)indicator;
            Window = i.Window; Smooth = i.Smooth;
            ThresholdMode = i.ThresholdMode; AutoLookback = i.AutoLookback;
            LowPct = i.LowPct; HighPct = i.HighPct;
            ErLow = i.ErLow; ErHigh = i.ErHigh;
            RrLow = i.RrLow; RrHigh = i.RrHigh;
            VrLow = i.VrLow; VrHigh = i.VrHigh;
            ShowEr = i.ShowEr; ShowRr = i.ShowRr; ShowVr = i.ShowVr; VrLookback = i.VrLookback;
            ShowFill = i.ShowFill; ShowTitle = i.ShowTitle; LineWidth = i.LineWidth;
            ErColor = i.ErColor; RrColor = i.RrColor; VrColor = i.VrColor;
            TrendColor = i.TrendColor; ChopColor = i.ChopColor;
            base.CopyTemplate(indicator, style);
        }

        // ========================= Лог =========================

        private int _lastErrorTick;
        private string _lastErrorKey;

        private void LogError(string where, Exception ex)
        {
            var now = Environment.TickCount;
            var key = where + ":" + ex.GetType().Name;
            if (key == _lastErrorKey && _lastErrorTick != 0 && unchecked(now - _lastErrorTick) < 10000) return;
            _lastErrorKey = key; _lastErrorTick = now;
            Log.Info($"{where} error: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");
        }

        internal static class Log
        {
            private static readonly object Gate = new object();
            private const long MaxBytes = 2 * 1024 * 1024;

            private static string Path() => System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "TigerTrade", "Indicators", "regime.log");

            public static void Info(string message)
            {
                try
                {
                    var path = Path();
                    lock (Gate)
                    {
                        try
                        {
                            var fi = new FileInfo(path);
                            if (fi.Exists && fi.Length > MaxBytes)
                            {
                                var old = path + ".old";
                                if (File.Exists(old)) File.Delete(old);
                                File.Move(path, old);
                            }
                        }
                        catch { }
                        File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\r\n");
                    }
                }
                catch { }
            }
        }
    }
}
