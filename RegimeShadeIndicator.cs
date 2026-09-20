//--------------------------------------------------------------------------------
// Regime Shade — тот же расчёт, что и в Regime, но рисуется ПОВЕРХ графика цены:
// вертикальная полупрозрачная подсветка баров, попавших в режим «безоткатно»
// или «запил». Отдельный индикатор, потому что панельный (Z_Regime) живёт в своей
// области и до свечей не дотягивается.
//
// Ставится отдельно и не обязателен: нужен, чтобы режим ловился боковым зрением,
// когда смотришь на цену, а не на панель. Настройки расчёта дублируются —
// держите k, пороги и сглаживание такими же, как в панели.
//--------------------------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Media;
using RegimeMeter;
using TigerTrade.Chart.Base;
using TigerTrade.Chart.Indicators.Common;
using TigerTrade.Chart.Indicators.Enums;
using TigerTrade.Core.UI.Converters;
using TigerTrade.Dx;
using TigerTrade.Dx.Enums;

namespace TigerTrade.Chart.Indicators.Custom
{
    [TypeConverter(typeof(EnumDescriptionTypeConverter))]
    [DataContract(Name = "RegimeShadeSource",
        Namespace = "http://schemas.datacontract.org/2004/07/TigerTrade.Chart.Indicators.Custom")]
    public enum RegimeShadeSource
    {
        [EnumMember(Value = "Er"), Description("ER — быстро, но шумно")] Er,
        [EnumMember(Value = "Rr"), Description("RR — размах")] Rr,
        [EnumMember(Value = "Vr"), Description("VR — медленно, но надёжно")] Vr,
        [EnumMember(Value = "ErAndVr"), Description("ER и VR — подсвечивать, когда согласны")] ErAndVr,
        [EnumMember(Value = "ErAndRr"), Description("ER и RR — подсвечивать, когда согласны")] ErAndRr,
    }

    [DataContract(Name = "RegimeShadeIndicator",
        Namespace = "http://schemas.datacontract.org/2004/07/TigerTrade.Chart.Indicators.Custom")]
    [Indicator("Z_RegimeShade", "Regime Shade", true, Type = typeof(RegimeShadeIndicator))]
    internal sealed class RegimeShadeIndicator : IndicatorBase
    {
        // ======================= 1. Расчёт =======================

        private int _window;
        [DataMember(Name = "Window")]
        [Category("1. Расчёт"), DisplayName("Окно k")]
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

        private RegimeShadeSource _source;
        [DataMember(Name = "Source")]
        [Category("1. Расчёт"), DisplayName("По какой метрике")]
        public RegimeShadeSource Source
        {
            get => _source;
            set { if (value == _source) return; _source = value; Touch(); OnPropertyChanged(); }
        }

        private int _vrLookback;
        [DataMember(Name = "VrLookback")]
        [Category("1. Расчёт"), DisplayName("VR: по скольким барам усреднять")]
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

        // ================= 3. Пороги вручную =================

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

        // ======================= 4. Вид =======================

        private bool _shadeTrend;
        [DataMember(Name = "ShadeTrend")]
        [Category("4. Вид"), DisplayName("Подсвечивать «безоткатно»")]
        public bool ShadeTrend
        {
            get => _shadeTrend;
            set { if (value == _shadeTrend) return; _shadeTrend = value; Touch(); OnPropertyChanged(); }
        }

        private bool _shadeChop;
        [DataMember(Name = "ShadeChop")]
        [Category("4. Вид"), DisplayName("Подсвечивать «запил»")]
        public bool ShadeChop
        {
            get => _shadeChop;
            set { if (value == _shadeChop) return; _shadeChop = value; Touch(); OnPropertyChanged(); }
        }

        private XColor _trendColor;
        [DataMember(Name = "TrendColor")]
        [Category("4. Вид"), DisplayName("Цвет «безоткатно» (с прозрачностью)")]
        public XColor TrendColor
        {
            get => _trendColor;
            set { if (value == _trendColor) return; _trendColor = value; _trendBrush = null; Touch(); OnPropertyChanged(); }
        }

        private XColor _chopColor;
        [DataMember(Name = "ChopColor")]
        [Category("4. Вид"), DisplayName("Цвет «запил» (с прозрачностью)")]
        public XColor ChopColor
        {
            get => _chopColor;
            set { if (value == _chopColor) return; _chopColor = value; _chopBrush = null; Touch(); OnPropertyChanged(); }
        }

        // =========================== Служебное ===========================

        private RegimeData _data;
        private RegimeData Data => _data ?? (_data = new RegimeData());

        private XBrush _trendBrush, _chopBrush;
        private XBrush TrendBrush => _trendBrush ?? (_trendBrush = new XBrush(_trendColor));
        private XBrush ChopBrush => _chopBrush ?? (_chopBrush = new XBrush(_chopColor));

        private int _touch;
        private int _calcVersion;
        private long _renderedVersion;

        [Browsable(false)] public override bool ShowIndicatorValues => false;
        [Browsable(false)] public override bool ShowIndicatorLabels => false;

        [Browsable(false)]
        public override IndicatorCalculation Calculation => IndicatorCalculation.OnPriceChange;

        public RegimeShadeIndicator()
        {
            ShowIndicatorTitle = false;

            Window = 12; Smooth = 1; Source = RegimeShadeSource.Er; VrLookback = 96;

            ThresholdMode = RegimeThresholdMode.Auto;
            AutoLookback = 500; LowPct = 15; HighPct = 85;

            ErLow = 0.42; ErHigh = 1.49;
            RrLow = 0.84; RrHigh = 1.22;
            VrLow = 0.85; VrHigh = 1.09;

            ShadeTrend = true; ShadeChop = true;
            TrendColor = Color.FromArgb(28, 60, 190, 90);
            ChopColor = Color.FromArgb(28, 220, 60, 60);
        }

        private static double ClampLevel(double v) => v < 0.01 ? 0.01 : (v > 25 ? 25 : v);

        private void Touch() { _touch++; }
        private long CurrentVersion() => 1L + ((long)_calcVersion << 20) + _touch;

        private bool NeedsVr =>
            Source == RegimeShadeSource.Vr || Source == RegimeShadeSource.ErAndVr;

        private RegimeSettings BuildSettings()
        {
            RegimeSettings s;
            s.Window = Window;
            s.Smooth = Smooth;
            s.NeedVr = NeedsVr;
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

        private void EnsureCalc()
        {
            var dp = DataProvider;
            if (dp == null) return;
            if (Data.Update(Helper, dp.Count, BuildSettings(), Environment.TickCount)) _calcVersion++;
        }

        protected override void Execute()
        {
            try { EnsureCalc(); }
            catch (Exception ex) { RegimeIndicator.Log.Info("shade execute error: " + ex.Message); }
        }

        public override bool CheckNeedRedraw()
        {
            try { return CurrentVersion() != _renderedVersion; }
            catch { return false; }
        }

        /// <summary>Режим бара по выбранной метрике; для пар — только когда обе согласны.</summary>
        private int RegimeAt(int bar)
        {
            switch (Source)
            {
                case RegimeShadeSource.Rr: return Data.RrRegime(bar);
                case RegimeShadeSource.Vr: return Data.VrRegime(bar);
                case RegimeShadeSource.ErAndVr: return Agree(Data.ErRegime(bar), Data.VrRegime(bar));
                case RegimeShadeSource.ErAndRr: return Agree(Data.ErRegime(bar), Data.RrRegime(bar));
                default: return Data.ErRegime(bar);
            }
        }

        private static int Agree(int a, int b) => a == b ? a : 0;

        public override void Render(DxVisualQueue visual)
        {
            try
            {
                EnsureCalc();
                _renderedVersion = CurrentVersion();

                var dp = DataProvider;
                if (dp == null || !Data.HasResult) return;
                if (!ShadeTrend && !ShadeChop) return;

                var rect = Canvas.Rect;
                var w = Canvas.ColumnWidth;
                if (w < 1) w = 1;

                var slots = Canvas.Count;
                var count = Data.Count;

                // Соседние бары одного режима сливаем в одну заливку: иначе на
                // полупрозрачном цвете видны стыки столбиков.
                var runStart = double.NaN;
                var runEnd = 0.0;
                var runRegime = 0;

                for (var i = 0; i < slots; i++)
                {
                    var idx = Canvas.GetIndex(i);
                    var regime = idx >= 0 && idx < count ? RegimeAt(idx) : 0;
                    if (regime > 0 && !ShadeTrend) regime = 0;
                    if (regime < 0 && !ShadeChop) regime = 0;

                    if (regime != runRegime)
                    {
                        Flush(visual, rect, runRegime, runStart, runEnd);
                        runRegime = regime;
                        runStart = double.NaN;
                    }
                    if (regime == 0) continue;

                    var x = Canvas.GetX(idx);
                    if (double.IsNaN(runStart)) runStart = x - w / 2.0;
                    runEnd = x + w / 2.0;
                }
                Flush(visual, rect, runRegime, runStart, runEnd);
            }
            catch (Exception ex)
            {
                _renderedVersion = CurrentVersion();
                RegimeIndicator.Log.Info("shade render error: " + ex.Message);
            }
        }

        private void Flush(DxVisualQueue visual, Rect rect, int regime, double left, double right)
        {
            if (regime == 0 || double.IsNaN(left) || right <= left) return;
            if (left < rect.Left) left = rect.Left;
            if (right > rect.Right) right = rect.Right;
            if (right <= left) return;
            visual.FillRectangle(regime > 0 ? TrendBrush : ChopBrush,
                new Rect(left, rect.Top, right - left, rect.Height));
        }

        public override void CopyTemplate(IndicatorBase indicator, bool style)
        {
            var i = (RegimeShadeIndicator)indicator;
            Window = i.Window; Smooth = i.Smooth; Source = i.Source; VrLookback = i.VrLookback;
            ThresholdMode = i.ThresholdMode; AutoLookback = i.AutoLookback;
            LowPct = i.LowPct; HighPct = i.HighPct;
            ErLow = i.ErLow; ErHigh = i.ErHigh;
            RrLow = i.RrLow; RrHigh = i.RrHigh;
            VrLow = i.VrLow; VrHigh = i.VrHigh;
            ShadeTrend = i.ShadeTrend; ShadeChop = i.ShadeChop;
            TrendColor = i.TrendColor; ChopColor = i.ChopColor;
            base.CopyTemplate(indicator, style);
        }
    }
}
