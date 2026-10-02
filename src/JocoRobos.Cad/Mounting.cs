using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JocoRobos.Cad
{
    /// <summary>One electronics or motor mounting pattern, from the vendor's published numbers (inches).</summary>
    internal sealed class MountPattern
    {
        internal string Name, Source;
        internal int Count;          // holes on the bolt circle (0: a grid)
        internal double Circle;      // bolt circle diameter
        internal double Center;      // the pattern's own center hole (a motor's pilot), 0: none
        internal double Grid;        // grid pattern: the square's size, holes every 1/2"
    }

    /// <summary>Mounting patterns for motors, gearboxes and REV/ThriftyBot grid parts: where the holes go, relative to the center.</summary>
    internal static class Mounting
    {
        // Stored by index in remembered settings: only ever add to the end. Sources: WCP Kraken X60 docs (11 #10-32 holes every
        // 30° on a 2" circle; the 3/4" pilot "the CIM/MiniCIM/NEO/Falcon has"), REV NEO docs (four 10-32 on a 2" circle), CTRE
        // Falcon 500 (six #10-32 on a 2" circle), VEX VersaPlanetary (face holes 2" apart, 10-32), REV MAX pattern (#10 clearance
        // on a 1/2" grid; MAXSpline bearings are 1.125" across), ThriftyBot (#10 holes 1/2" apart).
        internal static readonly List<MountPattern> Patterns = new List<MountPattern>
        {
            new MountPattern { Name = "Motor face, 4 holes (NEO, NEO Vortex, Kraken X60)", Count = 4, Circle = 2, Center = 0.76, Source = "REV NEO and WCP Kraken X60 docs: #10-32 on a 2\" circle, 3/4\" pilot" },
            new MountPattern { Name = "Motor face, 6 holes (Falcon 500, Kraken X60)", Count = 6, Circle = 2, Center = 0.76, Source = "CTRE Falcon 500 and WCP Kraken X60 docs: #10-32 on a 2\" circle, 3/4\" pilot" },
            new MountPattern { Name = "Motor face, 2 holes (CIM, MiniCIM)", Count = 2, Circle = 2, Center = 0.76, Source = "CIM face: #10-32 2\" apart, 3/4\" pilot" },
            new MountPattern { Name = "VersaPlanetary face (2 holes)", Count = 2, Circle = 2, Center = 0, Source = "VEX VersaPlanetary: face holes 2\" apart, 10-32 (add the output's hole as a custom center)" },
            new MountPattern { Name = "1/2\" grid, 2\" square (MAX pattern, ThriftyBot)", Grid = 2, Center = 1.125, Source = "REV MAX pattern and ThriftyBot: #10 holes on a 1/2\" grid; center fits MAXSpline bearings (1.125\"), not the spline" },
            new MountPattern { Name = "1/2\" grid, 3\" square", Grid = 3, Center = 0, Source = "#10 holes on a 1/2\" grid" },
            new MountPattern { Name = "Custom bolt circle", Count = 4, Circle = 2, Center = 0, Source = "your numbers" },
        };

        internal static readonly string[] HoleSizes = { "#10 clearance, close (0.196)", "#10 clearance, free (0.201)", "#10-32 tapped (0.159 drill)", "#8 clearance (0.177)" };
        internal static readonly double[] HoleDiameters = { 0.196, 0.201, 0.159, 0.177 };

        internal static MountPattern Pattern(int index) { return Patterns[Math.Max(0, Math.Min(Patterns.Count - 1, index))]; }

        /// <summary>
        /// The hole centers (x, y, relative to the pattern's center) turned by `degrees`. A bolt circle starts at 0° (along the
        /// sketch's X axis). A grid keeps clear of a center hole of `center` diameter.
        /// </summary>
        internal static List<double[]> Holes(MountPattern pattern, double degrees, int count, double circle, double center, double hole)
        {
            var points = new List<double[]>();
            if (pattern.Grid > 0)
            {
                int steps = (int)Math.Round(pattern.Grid / 0.5);
                for (int i = 0; i <= steps; i++)
                    for (int j = 0; j <= steps; j++)
                    {
                        double x = -pattern.Grid / 2 + 0.5 * i, y = -pattern.Grid / 2 + 0.5 * j;
                        // Keep a wall of at least 0.03" between a grid hole and the center hole.
                        if (center > 0 && Math.Sqrt(x * x + y * y) - hole / 2 < center / 2 + 0.03) continue;
                        points.Add(new[] { x, y });
                    }
            }
            else
                for (int k = 0; k < count; k++)
                {
                    double a = 2 * Math.PI * k / count;
                    points.Add(new[] { circle / 2 * Math.Cos(a), circle / 2 * Math.Sin(a) });
                }
            double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            return points.Select(p => new[] { Math.Round(p[0] * c - p[1] * s, 6), Math.Round(p[0] * s + p[1] * c, 6) }).ToList();
        }

        /// <summary>What will be cut, in words, for the side panel.</summary>
        internal static string Describe(MountPattern pattern, double degrees, int count, double circle, double center, double hole, int holes)
        {
            string size = hole.ToString("0.000", CultureInfo.InvariantCulture);
            string text = pattern.Grid > 0
                ? holes + " × ⌀" + size + " on a 1/2\" grid in a " + StockParts.Inches(pattern.Grid) + "\" square"
                : count + " × ⌀" + size + " on a " + StockParts.Inches(circle) + "\" circle, every " + StockParts.Inches(Math.Round(360.0 / count, 2)) + "° from " + StockParts.Inches(degrees) + "°";
            if (center > 0) text += " · center ⌀" + center.ToString("0.000", CultureInfo.InvariantCulture);
            return text + ". From: " + pattern.Source + ".";
        }
    }
}
