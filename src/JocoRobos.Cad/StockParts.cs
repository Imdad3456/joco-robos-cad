using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JocoRobos.Cad
{
    /// <summary>Inch helpers: showing and reading lengths the way students type them.</summary>
    internal static class StockParts
    {
        internal static string Inches(double value)
        {
            return Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>Reads a length the way students type it: 23.75, 23.75", 23 3/4, 3/4. Null if it isn't a number.</summary>
        internal static double? ParseInches(string text)
        {
            text = (text ?? "").Trim().TrimEnd('"').Replace("in", "").Trim();
            if (text.Length == 0) return null;
            double whole = 0, value;
            var parts = text.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out whole)) return null;
            string last = parts[parts.Length - 1];
            if (parts.Length > 2) return null;
            int slash = last.IndexOf('/');
            if (slash > 0)
            {
                double top, bottom;
                if (!double.TryParse(last.Substring(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out top) ||
                    !double.TryParse(last.Substring(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out bottom) || bottom == 0) return null;
                value = whole + top / bottom;
            }
            else if (parts.Length == 1 && double.TryParse(last, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) { }
            else return null;
            return value;
        }
    }
}
