using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JocoRobos.Cad
{
    /// <summary>Inch helpers and the hole layout along a tube (Hole Pattern).</summary>
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

        /// <summary>Hole centers along a length: start, start + spacing, … while a whole hole still fits before the far end.</summary>
        internal static List<double> HolePositions(double length, double start, double spacing, double diameter)
        {
            var positions = new List<double>();
            if (spacing <= 0) return positions;
            for (double x = start; x + diameter / 2 <= length - Math.Min(start, 0.05) + 1e-9; x += spacing) positions.Add(Math.Round(x, 6));
            return positions;
        }

        /// <summary>Row offsets across a face, centered: 1 row → 0; 2 rows 0.5 apart → −0.25, 0.25; 3 → −0.5, 0, 0.5.</summary>
        internal static double[] RowOffsets(int rows, double rowSpacing)
        {
            return Enumerable.Range(0, Math.Max(1, rows)).Select(i => Math.Round((i - (rows - 1) / 2.0) * rowSpacing, 6)).ToArray();
        }

        /// <summary>
        /// As many rows as fit across a face on the row spacing, centered, each hole's edge at least `edge` from the face's sides:
        /// on a 0.5" grid, 3 rows on a 2" face, 2 on 1.5", 1 on 1".
        /// </summary>
        internal static double[] FillRows(double faceWidth, double rowSpacing, double diameter, double edge = 0.25)
        {
            int rows = 1;
            while (rowSpacing > 0 && rows < 20 && rows * rowSpacing / 2 + diameter / 2 <= faceWidth / 2 - edge + 1e-9) rows++;
            return RowOffsets(rows, rowSpacing);
        }
    }
}
