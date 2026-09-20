// Заглушки API терминала — ТОЛЬКО для проверки индикатора вне Windows
// (типы, прогон «десериализованного инстанса», запись вызовов отрисовки).
// В сборку DLL не входят: см. RegimeMeter.csproj.
using System;
using System.Collections.Generic;

namespace System.Windows
{
    public struct Point
    {
        public double X, Y;
        public Point(double x, double y) { X = x; Y = y; }
    }

    public struct Rect
    {
        public double Left, Top, Width, Height;
        public Rect(double x, double y, double w, double h) { Left = x; Top = y; Width = w; Height = h; }
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }
}

namespace System.Windows.Media
{
    public struct Color
    {
        public byte A, R, G, B;
        public static Color FromArgb(byte a, byte r, byte g, byte b) =>
            new Color { A = a, R = r, G = g, B = b };
    }
}

namespace TigerTrade.Dx.Enums
{
    public enum XDashStyle { Solid, Dash, Dot, DashDot }
    public enum XTextAlignment { Left, Center, Right }
}

namespace TigerTrade.Dx
{
    using System.Windows;
    using System.Windows.Media;
    using TigerTrade.Dx.Enums;

    public struct XColor
    {
        public byte A, R, G, B;
        public static implicit operator XColor(Color c) => new XColor { A = c.A, R = c.R, G = c.G, B = c.B };
        public static bool operator ==(XColor a, XColor b) => a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;
        public static bool operator !=(XColor a, XColor b) => !(a == b);
        public override bool Equals(object o) => o is XColor && (XColor)o == this;
        public override int GetHashCode() => (A << 24) | (R << 16) | (G << 8) | B;
        public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    }

    public class XBrush
    {
        public readonly XColor C;
        public XBrush(XColor c) { C = c; }
    }

    public class XPen
    {
        public readonly XBrush Brush;
        public readonly double W;
        public readonly XDashStyle Style;
        public XPen(XBrush brush, double width, XDashStyle style) { Brush = brush; W = width; Style = style; }
    }

    public class XFont
    {
        public double GetWidth(string text) => (text ?? "").Length * 6.5;
        public double GetHeight() => 14;
    }

    public class DxVisualQueue
    {
        public readonly List<string> Ops = new List<string>();
        public void DrawLine(XPen pen, Point a, Point b) { Ops.Add($"line {a.X:F0},{a.Y:F0}->{b.X:F0},{b.Y:F0}"); }
        public void DrawRectangle(XPen pen, Rect r) { Ops.Add("rect"); }
        public void FillRectangle(XBrush brush, Rect r) { Ops.Add($"fill {brush.C} x=[{r.Left:F0};{r.Right:F0}] y=[{r.Top:F0};{r.Bottom:F0}]"); }
        public void DrawString(string text, XFont font, XBrush brush, Rect r, XTextAlignment a) { Ops.Add($"text '{text}'"); }
    }
}

namespace TigerTrade.Chart.Base { public static class Marker { } }
namespace TigerTrade.Chart.Alerts { public static class Marker { } }

namespace TigerTrade.Core.UI.Converters
{
    public sealed class EnumDescriptionTypeConverter : System.ComponentModel.TypeConverter
    {
        public EnumDescriptionTypeConverter(Type type) { }
    }
}

namespace TigerTrade.Chart.Indicators.Enums
{
    public enum IndicatorCalculation { OnBarClose, OnPriceChange, OnEachTick }
}

namespace TigerTrade.Chart.Indicators.Common
{
    using System.Windows;
    using TigerTrade.Dx;
    using TigerTrade.Chart.Indicators.Enums;

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class IndicatorAttribute : Attribute
    {
        public IndicatorAttribute(string id, string name, bool overlay) { }
        public Type Type { get; set; }
    }

    public sealed class IndicatorValueInfo
    {
        public readonly string Text;
        public IndicatorValueInfo(string text, XBrush brush) { Text = text; }
    }

    public sealed class IndicatorLabelInfo
    {
        public readonly double Value;
        public IndicatorLabelInfo(double value, XColor color) { Value = value; }
    }

    public sealed class ChartTheme
    {
        public XBrush ChartBackBrush => new XBrush(default(XColor));
        public XBrush ChartFontBrush => new XBrush(default(XColor));
        public XColor ChartAxisColor => default(XColor);
    }

    public sealed class ChartSymbol
    {
        public string Name => "BTCUSDT";
        public string Exchange => "BINANCE-FUT";
    }

    public sealed class ChartDataProvider
    {
        public static int Bars = 300;
        public ChartSymbol Symbol => new ChartSymbol();
        public int Count => Bars;
        public double Step => 0.1;
    }

    public sealed class ChartCanvas
    {
        public static int Slots = 120;
        public static int FirstBar = 100;
        public Rect Rect => new Rect(0, 0, 960, 200);
        public int Count => Slots;
        public int Start => 0;
        public double ColumnWidth => 8;
        public int GetIndex(int slot) => FirstBar + slot;
        public double GetX(int barIndex) => (barIndex - FirstBar) * 8.0 + 4.0;
        public XFont ChartFont => new XFont();
        public ChartTheme Theme => new ChartTheme();
        public string FormatValue(double v) => v.ToString("0.00");
    }

    /// <summary>
    /// Стенд-двойник Helper: массивы подставляет тест. Свойства ищутся рефлексией
    /// (ChartArrays), поэтому здесь важны именно имена High/Low/Close.
    /// </summary>
    public sealed class ChartHelper
    {
        public static double[] HighData, LowData, CloseData;
        public static decimal[] HighDec, LowDec;
        /// <summary>true — прятать Close, чтобы проверить путь Price(enum)/(H+L)/2.</summary>
        public static bool HideClose;
        public static bool UseDecimal;
        public static bool ExposePrice;

        public object High => UseDecimal ? (object)HighDec : HighData;
        public object Low => UseDecimal ? (object)LowDec : LowData;
        public object Close => HideClose ? null : (object)CloseData;

        public enum PriceKind { Open, High, Low, Close }
        public double[] Price(PriceKind kind) => ExposePrice && kind == PriceKind.Close ? CloseData : null;
    }

    public abstract class IndicatorBase
    {
        public bool ShowIndicatorTitle { get; set; }
        public virtual bool ShowIndicatorValues => true;
        public virtual bool ShowIndicatorLabels => true;
        public virtual IndicatorCalculation Calculation => IndicatorCalculation.OnBarClose;

        private static readonly ChartCanvas TheCanvas = new ChartCanvas();
        private static readonly ChartDataProvider TheProvider = new ChartDataProvider();
        private static readonly ChartHelper TheHelper = new ChartHelper();

        /// <summary>null, чтобы тест мог проверить поведение снятого индикатора.</summary>
        public static bool ProviderAvailable = true;

        protected ChartCanvas Canvas => TheCanvas;
        protected ChartDataProvider DataProvider => ProviderAvailable ? TheProvider : null;
        protected ChartHelper Helper => ProviderAvailable ? TheHelper : null;

        /// <summary>Панель: значение → пиксель. Шкала берётся из последнего GetMinMax.</summary>
        public static double ScaleMin = 0, ScaleMax = 2;
        protected double GetY(double value)
        {
            var span = ScaleMax - ScaleMin;
            if (span <= 0) span = 1;
            return 200.0 - (value - ScaleMin) / span * 200.0;
        }

        protected void OnPropertyChanged(string name = null) { }

        protected virtual void Execute() { }
        public virtual bool CheckNeedRedraw() => false;
        public virtual void Render(DxVisualQueue visual) { }
        public virtual bool GetMinMax(out double min, out double max) { min = 0; max = 1; return false; }
        public virtual List<IndicatorValueInfo> GetValues(int cursorPos) => new List<IndicatorValueInfo>();
        public virtual void GetLabels(ref List<IndicatorLabelInfo> labels) { }
        public virtual void CopyTemplate(IndicatorBase indicator, bool style) { }
    }
}
