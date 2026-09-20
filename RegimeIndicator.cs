//--------------------------------------------------------------------------------
// Regime — отдельная панель под графиком: запильно цена идёт или безоткатно.
//
// Окно k баров = «старшая свеча», сами бары = «младшие». Две линии:
//
//   ER (толстая) = √k · |C[i] − C[i−k]| / Σ|ΔC|   — безоткатность хода
//   RR (тонкая)  = √k · (HH − LL) / Σ TR          — размах старшей свечи
//
// Шкала одна для обеих: 1.0 — как у случайного блуждания, выше — движение
// направленное/безоткатное, ниже — топтание. Потолок √k (для k=12 это 3.46).
// Расхождение линий читается так: RR высоко при низком ER — крупные качели
// без прогресса; оба высоко — чистый импульс.
//
// Математика — в RegimeCore.cs (проверяется тестами без терминала).
//
// ВАЖНО (инцидент 29.08.2026): при загрузке конфигурации терминал создаёт
// индикатор БЕЗ конструктора (DataContractSerializer → GetUninitializedObject)
// и сразу дёргает сеттеры через CopyTemplate. Поэтому: никаких инициализаторов
// полей, сеттеры пишут только своё поле, все runtime-объекты ленивые.
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

        private bool _showEr;
        [DataMember(Name = "ShowEr")]
        [Category("3. Вид"), DisplayName("Линия ER (безоткатность)")]
        public bool ShowEr
        {
            get => _showEr;
            set { if (value == _showEr) return; _showEr = value; Touch(); OnPropertyChanged(); }
        }

        private bool _showRr;
        [DataMember(Name = "ShowRr")]
        [Category("3. Вид"), DisplayName("Линия RR (размах)")]
        public bool ShowRr
        {
            get => _showRr;
            set { if (value == _showRr) return; _showRr = value; Touch(); OnPropertyChanged(); }
        }

        private bool _showFill;
        [DataMember(Name = "ShowFill")]
        [Category("3. Вид"), DisplayName("Заливка между ER и уровнем 1.0")]
        public bool ShowFill
        {
            get => _showFill;
            set { if (value == _showFill) return; _showFill = value; Touch(); OnPropertyChanged(); }
        }

        private bool _showTitle;
        [DataMember(Name = "ShowTitle")]
        [Category("3. Вид"), DisplayName("Строка состояния в углу панели")]
        public bool ShowTitle
        {
            get => _showTitle;
            set { if (value == _showTitle) return; _showTitle = value; Touch(); OnPropertyChanged(); }
        }

        private int _lineWidth;
        [DataMember(Name = "LineWidth")]
        [Category("3. Вид"), DisplayName("Толщина линии ER, px")]
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
        [Category("3. Вид"), DisplayName("Цвет линии ER")]
        public XColor ErColor
        {
            get => _erColor;
            set { if (value == _erColor) return; _erColor = value; _erBrush = null; _erPen = null; Touch(); OnPropertyChanged(); }
        }

        private XColor _rrColor;
        [DataMember(Name = "RrColor")]
        [Category("3. Вид"), DisplayName("Цвет линии RR")]
        public XColor RrColor
        {
            get => _rrColor;
            set { if (value == _rrColor) return; _rrColor = value; _rrBrush = null; _rrPen = null; Touch(); OnPropertyChanged(); }
        }

        private XColor _trendColor;
        [DataMember(Name = "TrendColor")]
        [Category("3. Вид"), DisplayName("Заливка «безоткатно» (с прозрачностью)")]
        public XColor TrendColor
        {
            get => _trendColor;
            set { if (value == _trendColor) return; _trendColor = value; _trendBrush = null; Touch(); OnPropertyChanged(); }
        }

        private XColor _chopColor;
        [DataMember(Name = "ChopColor")]
        [Category("3. Вид"), DisplayName("Заливка «запил» (с прозрачностью)")]
        public XColor ChopColor
        {
            get => _chopColor;
            set { if (value == _chopColor) return; _chopColor = value; _chopBrush = null; Touch(); OnPropertyChanged(); }
        }

        // =========================== Служебное ===========================
        // Никаких «= new» у полей: конструктор при загрузке конфигурации не выполняется.

        private RegimeData _data;
        private RegimeData Data => _data ?? (_data = new RegimeData());

        private XBrush _erBrush, _rrBrush, _trendBrush, _chopBrush;
        private XBrush ErBrush => _erBrush ?? (_erBrush = new XBrush(_erColor));
        private XBrush RrBrush => _rrBrush ?? (_rrBrush = new XBrush(_rrColor));
        private XBrush TrendBrush => _trendBrush ?? (_trendBrush = new XBrush(_trendColor));
        private XBrush ChopBrush => _chopBrush ?? (_chopBrush = new XBrush(_chopColor));

        private XPen _erPen, _rrPen;
        private XPen ErPen => _erPen ?? (_erPen = new XPen(ErBrush, _lineWidth < 1 ? 1 : _lineWidth, XDashStyle.Solid));
        private XPen RrPen => _rrPen ?? (_rrPen = new XPen(RrBrush, 1, XDashStyle.Solid));

        private int _touch;            // версия настроек
        private int _calcVersion;      // версия данных
        private long _renderedVersion; // 0 = ещё не рисовали
        private bool _sourceLogged;

        [Browsable(false)]
        public override IndicatorCalculation Calculation => IndicatorCalculation.OnPriceChange;

        public RegimeIndicator()
        {
            Window = 12;
            Smooth = 1;

            ChopLevel = 0.85;
            TrendLevel = 1.20;

            ShowEr = true; ShowRr = true; ShowFill = true; ShowTitle = true;
            LineWidth = 2;
            ErColor = Color.FromArgb(255, 235, 195, 80);
            RrColor = Color.FromArgb(255, 120, 150, 200);
            TrendColor = Color.FromArgb(55, 60, 190, 90);
            ChopColor = Color.FromArgb(55, 220, 60, 60);
        }

        // ========================= Расчёт =========================

        private void Touch() { _touch++; }

        /// <summary>Версия состояния БЕЗ обращения к DataProvider — для CheckNeedRedraw.</summary>
        private long CurrentVersion() => 1L + ((long)_calcVersion << 20) + _touch;

        /// <summary>Пересчёт рядов. Только из Execute()/Render()/GetMinMax() — колбэков терминала.</summary>
        private void EnsureCalc()
        {
            var dp = DataProvider;
            if (dp == null) return;
            if (!Data.Update(Helper, dp.Count, Window, Smooth, Environment.TickCount)) return;

            _calcVersion++;
            if (!_sourceLogged)
            {
                _sourceLogged = true;
                Log.Info($"close: {Data.CloseSource}; k={Window}, smooth={Smooth}, bars={dp.Count}, ceiling={RegimeCore.Ceiling(Window):F2}");
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
                }
                if (lo > hi) { min = 0; max = ceiling; return true; }

                // Уровень 1.0 и пороги всегда в кадре — иначе шкала врёт на глаз.
                lo = Math.Min(Math.Min(lo, 1.0), ChopLevel);
                hi = Math.Max(Math.Max(hi, 1.0), TrendLevel);

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

                // Уровень «случайного блуждания» и пороги режимов.
                DrawLevel(visual, axisPen, rect, 1.0);
                DrawLevel(visual, dashPen, rect, TrendLevel);
                DrawLevel(visual, dashPen, rect, ChopLevel);

                if (ShowFill && ShowEr) DrawFill(visual, rect);
                if (ShowRr) DrawSeries(visual, RrPen, false);
                if (ShowEr) DrawSeries(visual, ErPen, true);
                if (ShowTitle) DrawTitle(visual, rect);
            }
            catch (Exception ex)
            {
                _renderedVersion = CurrentVersion(); // не зацикливать перерисовку на той же ошибке
                LogError("render", ex);
            }
        }

        private void DrawLevel(DxVisualQueue visual, XPen pen, Rect rect, double value)
        {
            var y = GetY(value);
            if (y < rect.Top || y > rect.Bottom) return;
            visual.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));
        }

        private void DrawSeries(DxVisualQueue visual, XPen pen, bool useEr)
        {
            var slots = Canvas.Count;
            var count = Data.Count;
            double px = 0, py = 0;
            var has = false;

            for (var i = 0; i < slots; i++)
            {
                var idx = Canvas.GetIndex(i);
                if (idx < 0 || idx >= count) { has = false; continue; }

                var v = useEr ? Data.Er(idx) : Data.Rr(idx);
                if (double.IsNaN(v) || double.IsInfinity(v)) { has = false; continue; }

                var x = Canvas.GetX(idx);
                var y = GetY(v);
                if (has) visual.DrawLine(pen, new Point(px, py), new Point(x, y));
                px = x; py = y; has = true;
            }
        }

        /// <summary>Столбики от уровня 1.0 до линии ER: зелёные выше порога, красные ниже.</summary>
        private void DrawFill(DxVisualQueue visual, Rect rect)
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

                var v = Data.Er(idx);
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;

                var regime = RegimeCore.Regime(v, ChopLevel, TrendLevel);
                if (regime == 0) continue;

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

        private void DrawTitle(DxVisualQueue visual, Rect rect)
        {
            var last = Data.Count - 1 - Canvas.Start;
            if (last < 0) return;

            var er = Data.Er(last);
            var rr = Data.Rr(last);
            var regime = RegimeCore.Regime(er, ChopLevel, TrendLevel);

            var text = "k" + Window.ToString(CultureInfo.InvariantCulture);
            if (!double.IsNaN(er)) text += "  ER " + er.ToString("0.00", CultureInfo.InvariantCulture);
            if (!double.IsNaN(rr)) text += "  RR " + rr.ToString("0.00", CultureInfo.InvariantCulture);
            text += regime > 0 ? "  ▲ безоткатно" : (regime < 0 ? "  ▼ запил" : "  · нейтрально");

            var font = Canvas.ChartFont;
            var w = font.GetWidth(text) + 8;
            var h = font.GetHeight() + 2;
            if (w < 8 || w > rect.Width) w = Math.Min(rect.Width, 260);

            var box = new Rect(rect.Left + 4, rect.Top + 2, w, h);
            if (regime > 0) visual.FillRectangle(TrendBrush, box);
            else if (regime < 0) visual.FillRectangle(ChopBrush, box);

            visual.DrawString(text, font, Canvas.Theme.ChartFontBrush,
                new Rect(box.Left + 4, box.Top, box.Width - 8, box.Height), XTextAlignment.Left);
        }

        // ========================= Значения под курсором / метки шкалы =========================

        public override List<IndicatorValueInfo> GetValues(int cursorPos)
        {
            var info = new List<IndicatorValueInfo>();
            try
            {
                if (!Data.HasResult) return info;
                if (ShowEr)
                {
                    var v = Data.Er(cursorPos);
                    if (!double.IsNaN(v)) info.Add(new IndicatorValueInfo("ER " + v.ToString("0.00", CultureInfo.InvariantCulture), ErBrush));
                }
                if (ShowRr)
                {
                    var v = Data.Rr(cursorPos);
                    if (!double.IsNaN(v)) info.Add(new IndicatorValueInfo("RR " + v.ToString("0.00", CultureInfo.InvariantCulture), RrBrush));
                }
            }
            catch (Exception ex) { LogError("values", ex); }
            return info;
        }

        public override void GetLabels(ref List<IndicatorLabelInfo> labels)
        {
            try
            {
                if (!Data.HasResult || labels == null) return;
                var last = Data.Count - 1 - Canvas.Start;
                if (last < 0) return;

                if (ShowEr)
                {
                    var v = Data.Er(last);
                    if (!double.IsNaN(v)) labels.Add(new IndicatorLabelInfo(v, _erColor));
                }
                if (ShowRr)
                {
                    var v = Data.Rr(last);
                    if (!double.IsNaN(v)) labels.Add(new IndicatorLabelInfo(v, _rrColor));
                }
            }
            catch (Exception ex) { LogError("labels", ex); }
        }

        public override void CopyTemplate(IndicatorBase indicator, bool style)
        {
            var i = (RegimeIndicator)indicator;
            Window = i.Window; Smooth = i.Smooth;
            ChopLevel = i.ChopLevel; TrendLevel = i.TrendLevel;
            ShowEr = i.ShowEr; ShowRr = i.ShowRr; ShowFill = i.ShowFill; ShowTitle = i.ShowTitle;
            LineWidth = i.LineWidth;
            ErColor = i.ErColor; RrColor = i.RrColor; TrendColor = i.TrendColor; ChopColor = i.ChopColor;
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
