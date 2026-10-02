using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JocoRobos.Cad
{
    internal enum StockShape { BoxTube, HexShaft, RoundShaft, HexSpacer, RoundSpacer, Plate, Bearing }

    /// <summary>
    /// One kind of stock part CAD Hub builds itself (no Onshape): its shape and dimensions in inches. Each kind is one master
    /// part in the robot (90_COTS/Stock), with one configuration per size. Dimensions come from vendors' published drawings;
    /// check one of each against the real part before cutting.
    /// </summary>
    internal sealed class StockType
    {
        internal string Id, Label, FileName, Group, Material = "6061 Alloy";
        internal StockShape Shape;
        // Box tube: outside width (the wide face) and height, wall, outside corner radius. Plain: vendors' hole patterns differ,
        // so holes are added with Hole Pattern (or use FRCDesignLib's vendor tubes for an exact pattern).
        internal double Width, Height, Wall, Corner;
        // Hex: across flats. Round: diameter. Spacers: outside diameter and bore (hex across flats, or round diameter).
        internal double Size, Bore;
        // Plates: thickness. Bearings: outside diameter, width, flange diameter and thickness (0: none), bore is hex when BoreHex.
        internal double Thickness, FlangeDiameter, FlangeThickness;
        internal bool BoreHex;
        internal double MinLength = 0.01, MaxLength = 72;
        internal bool HasLength = true, HasWidth;

    }

    internal static class StockParts
    {
        internal static readonly List<StockType> Types = new List<StockType>
        {
            Tube("tube-2x1-0625", "2×1 box tube, 1/16\" wall", "2x1 Box Tube 0.0625 wall", 2, 1, 0.0625),
            Tube("tube-2x1-125", "2×1 box tube, 1/8\" wall", "2x1 Box Tube 0.125 wall", 2, 1, 0.125),
            Tube("tube-1x1-0625", "1×1 box tube, 1/16\" wall", "1x1 Box Tube 0.0625 wall", 1, 1, 0.0625),
            Tube("tube-15x15-0625", "1.5×1.5 box tube, 1/16\" wall", "1.5x1.5 Box Tube 0.0625 wall", 1.5, 1.5, 0.0625),
            Tube("tube-2x2-125", "2×2 box tube, 1/8\" wall", "2x2 Box Tube 0.125 wall", 2, 2, 0.125),
            new StockType { Id = "hex-500", Label = "1/2\" hex shaft", FileName = "Hex Shaft 0.5", Group = "Shafts", Shape = StockShape.HexShaft, Size = 0.5, MaxLength = 48 },
            new StockType { Id = "hex-375", Label = "3/8\" hex shaft", FileName = "Hex Shaft 0.375", Group = "Shafts", Shape = StockShape.HexShaft, Size = 0.375, MaxLength = 48 },
            new StockType { Id = "round-500", Label = "1/2\" round shaft", FileName = "Round Shaft 0.5", Group = "Shafts", Shape = StockShape.RoundShaft, Size = 0.5, MaxLength = 48 },
            new StockType { Id = "round-375", Label = "3/8\" round shaft", FileName = "Round Shaft 0.375", Group = "Shafts", Shape = StockShape.RoundShaft, Size = 0.375, MaxLength = 48 },
            new StockType { Id = "spacer-hex500-0625", Label = "1/2\" hex-bore spacer, 0.625\" OD", FileName = "Spacer 0.5 Hex Bore 0.625 OD", Group = "Spacers",
                Shape = StockShape.HexSpacer, Size = 0.625, Bore = 0.5 + 0.006, MaxLength = 6 },
            new StockType { Id = "spacer-hex500-075", Label = "1/2\" hex-bore spacer, 0.75\" OD", FileName = "Spacer 0.5 Hex Bore 0.75 OD", Group = "Spacers",
                Shape = StockShape.HexSpacer, Size = 0.75, Bore = 0.5 + 0.006, MaxLength = 6 },
            new StockType { Id = "spacer-round10-0375", Label = "#10 round spacer, 0.375\" OD", FileName = "Spacer 10 Round Bore 0.375 OD", Group = "Spacers",
                Shape = StockShape.RoundSpacer, Size = 0.375, Bore = 0.196, MaxLength = 6 },
            new StockType { Id = "spacer-round500-0625", Label = "1/2\" round-bore spacer, 0.625\" OD", FileName = "Spacer 0.5 Round Bore 0.625 OD", Group = "Spacers",
                Shape = StockShape.RoundSpacer, Size = 0.625, Bore = 0.5 + 0.006, MaxLength = 6 },
            Plate("plate-al-125", "Aluminum plate 1/8\"", "Plate Aluminum 0.125", 0.125, "6061 Alloy"),
            Plate("plate-al-1875", "Aluminum plate 3/16\"", "Plate Aluminum 0.1875", 0.1875, "6061 Alloy"),
            Plate("plate-al-25", "Aluminum plate 1/4\"", "Plate Aluminum 0.25", 0.25, "6061 Alloy"),
            Plate("plate-pc-125", "Polycarbonate 1/8\"", "Plate Polycarbonate 0.125", 0.125, "PC High Viscosity"),
            Plate("plate-pc-25", "Polycarbonate 1/4\"", "Plate Polycarbonate 0.25", 0.25, "PC High Viscosity"),
            Bearing("bearing-hex500-flanged", "1/2\" hex bore bearing, flanged (1.125\" OD)", "Bearing 0.5 Hex Flanged 1.125 OD", 1.125, 0.3125, 0.5 + 0.004, true, 1.25, 0.0625),
            Bearing("bearing-fr8zz", "FR8ZZ, 1/2\" round bore, flanged", "Bearing FR8ZZ", 1.125, 0.3125, 0.5, false, 1.25, 0.0625),
            Bearing("bearing-r8", "R8, 1/2\" round bore", "Bearing R8", 1.125, 0.3125, 0.5, false, 0, 0),
            Bearing("bearing-hex375-flanged", "3/8\" hex bore bearing, flanged (0.875\" OD)", "Bearing 0.375 Hex Flanged 0.875 OD", 0.875, 0.28, 0.375 + 0.004, true, 1.0, 0.0625),
        };

        private static StockType Tube(string id, string label, string file, double width, double height, double wall)
        {
            return new StockType { Id = id, Label = label + ", plain", FileName = file, Group = "Box tube", Shape = StockShape.BoxTube, Width = width, Height = height, Wall = wall,
                Corner = wall, MaxLength = 72 };
        }

        private static StockType Plate(string id, string label, string file, double thickness, string material)
        {
            return new StockType { Id = id, Label = label, FileName = file, Group = "Plates", Shape = StockShape.Plate, Thickness = thickness, Material = material,
                HasWidth = true, MinLength = 0.25, MaxLength = 48 };
        }

        private static StockType Bearing(string id, string label, string file, double od, double width, double bore, bool hex, double flange, double flangeThickness)
        {
            return new StockType { Id = id, Label = label, FileName = file, Group = "Bearings", Shape = StockShape.Bearing, Size = od, Thickness = width, Bore = bore,
                BoreHex = hex, FlangeDiameter = flange, FlangeThickness = flangeThickness, HasLength = false, Material = "Chrome Stainless Steel" };
        }

        internal static StockType Find(string id)
        {
            return Types.FirstOrDefault(t => t.Id == id);
        }

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

        /// <summary>The configuration a size is stored as: "23.75 in", plates "6 x 12 in", bearings "Default".</summary>
        internal static string ConfigurationName(StockType type, double length, double width)
        {
            if (!type.HasLength) return "Default";
            return type.HasWidth ? Inches(width) + " x " + Inches(length) + " in" : Inches(length) + " in";
        }

        /// <summary>Why this size can't be made, or null.</summary>
        internal static string Problem(StockType type, double? length, double? width)
        {
            if (!type.HasLength) return null;
            if (length == null) return "Type a length in inches, like 23.75 or 23 3/4.";
            if (length < type.MinLength || length > type.MaxLength)
                return type.Label + " lengths go from " + Inches(type.MinLength) + " to " + Inches(type.MaxLength) + " in.";
            if (type.HasWidth && (width == null || width < 0.25 || width > 48)) return "Type a width from 0.25 to 48 in.";
            return null;
        }

        internal static string Description(StockType type, string configuration)
        {
            return type.FileName + (configuration == "Default" ? "" : ", " + configuration);
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
