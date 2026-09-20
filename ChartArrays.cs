//--------------------------------------------------------------------------------
// ChartArrays — достаём массивы цен из объекта Helper терминала.
//
// Зачем рефлексия. В примерах документации подтверждены только Helper.High,
// Helper.Low и Helper.Date; массива Close ни в одном примере нет, VWAP берёт
// цену через Helper.Price(<enum>) — имя enum'а в документации не показано.
// Чтобы индикатор не развалился на чужой версии терминала, close ищем по
// цепочке и, если не нашли, честно падаем на (H+L)/2 с записью в лог.
//
// Тип элементов массивов тоже не зафиксирован (decimal[] или double[]) —
// конвертируем оба, поэтому всё здесь через object/Array.
//--------------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Reflection;

namespace RegimeMeter
{
    public static class ChartArrays
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>Свойство-массив объекта в double[] длиной count; null, если такого нет.</summary>
        public static double[] Read(object source, string property, int count)
        {
            if (source == null || count <= 0) return null;
            try
            {
                var p = source.GetType().GetProperty(property, Flags);
                if (p == null || p.GetIndexParameters().Length > 0) return null;
                return ToDoubles(p.GetValue(source), count);
            }
            catch { return null; }
        }

        /// <summary>
        /// Цены закрытия: Helper.Close → Helper.Price(<enum>.Close) → null.
        /// how — что именно сработало, для лога.
        /// </summary>
        public static double[] ReadCloses(object helper, int count, out string how)
        {
            how = "none";
            if (helper == null || count <= 0) return null;

            var direct = Read(helper, "Close", count);
            if (direct != null) { how = "Helper.Close"; return direct; }

            try
            {
                foreach (var m in helper.GetType().GetMethods(Flags))
                {
                    if (m.Name != "Price") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;

                    var enumType = ps[0].ParameterType;
                    foreach (var name in Enum.GetNames(enumType))
                    {
                        if (!string.Equals(name, "Close", StringComparison.OrdinalIgnoreCase)) continue;
                        var arr = ToDoubles(m.Invoke(helper, new[] { Enum.Parse(enumType, name) }), count);
                        if (arr == null) continue;
                        how = $"Helper.Price({enumType.Name}.{name})";
                        return arr;
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>Любой числовой массив → double[count]. Хвост добивается последним значением.</summary>
        public static double[] ToDoubles(object array, int count)
        {
            if (count <= 0) return null;

            var asDouble = array as double[];
            if (asDouble != null) return Fit(asDouble, count);

            var asDecimal = array as decimal[];
            if (asDecimal != null)
            {
                var n = Math.Min(count, asDecimal.Length);
                if (n <= 0) return null;
                var res = new double[count];
                for (var i = 0; i < n; i++) res[i] = (double)asDecimal[i];
                return Pad(res, n, count);
            }

            var generic = array as Array;
            if (generic == null) return null;
            try
            {
                var n = Math.Min(count, generic.Length);
                if (n <= 0) return null;
                var res = new double[count];
                for (var i = 0; i < n; i++)
                    res[i] = Convert.ToDouble(generic.GetValue(i), CultureInfo.InvariantCulture);
                return Pad(res, n, count);
            }
            catch { return null; }
        }

        private static double[] Fit(double[] src, int count)
        {
            if (src.Length == count) return src;
            var n = Math.Min(count, src.Length);
            if (n <= 0) return null;
            var res = new double[count];
            Array.Copy(src, res, n);
            return Pad(res, n, count);
        }

        private static double[] Pad(double[] res, int filled, int count)
        {
            var last = filled > 0 ? res[filled - 1] : 0;
            for (var i = filled; i < count; i++) res[i] = last;
            return res;
        }
    }
}
