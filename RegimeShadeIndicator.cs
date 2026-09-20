//--------------------------------------------------------------------------------
// Regime Shade — тот же расчёт, что и в Regime, но рисуется ПОВЕРХ графика цены:
// вертикальная полупрозрачная подсветка баров, попавших в режим «безоткатно»
// или «запил». Отдельный индикатор, потому что панельный (Z_Regime) живёт в своей
// области и до свечей не дотягивается.
//
// Ставится отдельно и не обязателен: нужен, чтобы режим ловился боковым зрением,
// когда смотришь на цену, а не на панель. Настройки расчёта дублируются —
// держите k и пороги такими же, как в панели.
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
        [EnumMember(Value = "Er"), Description("ER — безоткатность")] Er,
        [EnumMember(Value = "Rr"), Description("RR — размах")] Rr,
        [EnumMember(Value = "Both"), Description("Обе: подсвечивать, когда согласны")] Both,
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

        // ======================= 2. Пороги =======================

        private double _chopLevel;
        [DataMember(Name = "ChopLevel")]
        [Category("2. Пороги"), DisplayName("«Запил» — ниже этого")]
        public double ChopLevel
        {
            get => _chopLevel;
            set
            {
                value = value < 0.05 ? 0.05 : (value > 5 ? 5 : value);
                if (value == _chopLevel) return;
                _chopLevel = value; Touch(); OnPropertyChanged();
            }
        }

        private double _trendLevel;
        [DataMember(Name = "TrendLevel")]
        [Category("2. Пороги"), DisplayName("«Безоткатно» — выше этого")]
        public double TrendLevel
        {
            get => _trendLevel;
            set
            {
                value = value < 0.05 ? 0.05 : (value > 25 ? 25 : value);
                if (value == _trendLevel) return;
                _trendLevel = value; Touch(); OnPropertyChanged();
            }
        }

        // ======================= 3. Вид =======================

        private bool _shadeTrend;
        [DataMember(Name = "ShadeTrend")]
        [Category("3. Вид"), DisplayName("Подсвечивать «безоткатно»")]
        public bool ShadeTrend
        {
            get => _shadeTrend;
            set { if (value == _shadeTrend) return; _shadeTrend = value; Touch(); OnPropertyChanged(); }
        }

        private bool _shadeChop;
        [DataMember(Name = "ShadeChop")]
        [Category("3. Вид"), DisplayName("Подсвечивать «запил»")]
        public bool ShadeChop
        {
            get => _shadeChop;
            set { if (value == _shadeChop) return; _shadeChop = value; Touch(); OnPropertyChanged(); }
        }

        private XColor _trendColor;
        [DataMember(Name = "TrendColor")]
        [Category("3. Вид"), DisplayName("Цвет «безоткатно» (с прозрачностью)")]
        public XColor TrendColor
        {
            get => _trendColor;
            set { if (value == _trendColor) return; _trendColor = value; _trendBrush = null; Touch(); OnPropertyChanged(); }
        }

        private XColor _chopColor;
        [DataMember(Name = "ChopColor")]
        [Category("3. Вид"), DisplayName("Цвет «запил» (с прозрачностью)")]
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

            Window = 12; Smooth = 1; Source = RegimeShadeSource.Er;
            ChopLevel = 0.85; TrendLevel = 1.20;
            ShadeTrend = true; ShadeChop = true;
            TrendColor = Color.FromArgb(28, 60, 190, 90);
            ChopColor = Color.FromArgb(28, 220, 60, 60);
        }

        private void Touch() { _touch++; }
        private long CurrentVersion() => 1L + ((long)_calcVersion << 20) + _touch;

        private void EnsureCalc()
        {
            var dp = DataProvider;
            if (dp == null) return;
            if (Data.Update(Helper, dp.Count, Window, Smooth, Environment.TickCount)) _calcVersion++;
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

        /// <summary>Режим бара по выбранной метрике; для «обе» — только когда обе согласны.</summary>
        private int RegimeAt(int bar)
        {
            switch (Source)
            {
                case RegimeShadeSource.Rr:
                    return RegimeCore.Regime(Data.Rr(bar), ChopLevel, TrendLevel);
                case RegimeShadeSource.Both:
                {
                    var a = RegimeCore.Regime(Data.Er(bar), ChopLevel, TrendLevel);
                    var b = RegimeCore.Regime(Data.Rr(bar), ChopLevel, TrendLevel);
                    return a == b ? a : 0;
                }
                default:
                    return RegimeCore.Regime(Data.Er(bar), ChopLevel, TrendLevel);
            }
        }

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
            Window = i.Window; Smooth = i.Smooth; Source = i.Source;
            ChopLevel = i.ChopLevel; TrendLevel = i.TrendLevel;
            ShadeTrend = i.ShadeTrend; ShadeChop = i.ShadeChop;
            TrendColor = i.TrendColor; ChopColor = i.ChopColor;
            base.CopyTemplate(indicator, style);
        }
    }
}
