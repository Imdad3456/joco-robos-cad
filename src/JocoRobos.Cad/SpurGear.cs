using System;
using System.Collections.Generic;

namespace JocoRobos.Cad
{
    /// <summary>
    /// A standard involute spur gear's outline, in inches, centered on the origin: what the gear generator sketches and extrudes.
    /// Diametral pitch (teeth per inch of pitch diameter: FRC's 20 DP and 32 DP gears) and pressure angle (14.5° or 20°).
    /// </summary>
    internal static class SpurGear
    {
        internal static double PitchDiameter(int teeth, double diametralPitch) { return teeth / diametralPitch; }
        internal static double OutsideDiameter(int teeth, double diametralPitch) { return (teeth + 2) / diametralPitch; }

        internal static string Problem(int teeth, double diametralPitch, double pressureDegrees)
        {
            if (teeth < 8 || teeth > 200) return "Gears from 8 to 200 teeth.";
            if (diametralPitch < 4 || diametralPitch > 64) return "Diametral pitch from 4 to 64.";
            if (pressureDegrees < 14 || pressureDegrees > 25) return "Pressure angle 14.5° or 20°.";
            return null;
        }

        /// <summary>The closed outline: for each tooth, root, one flank, tip, the other flank. pointsPerFlank sets smoothness.</summary>
        internal static List<double[]> Outline(int teeth, double diametralPitch, double pressureDegrees, int pointsPerFlank = 8)
        {
            double phi = pressureDegrees * Math.PI / 180;
            double r = teeth / (2 * diametralPitch), rb = r * Math.Cos(phi);
            double ra = r + 1 / diametralPitch, rf = Math.Max(0.05, r - 1.25 / diametralPitch);
            double involuteAtPitch = Math.Tan(phi) - phi;
            // Half the tooth's angular width at the base circle: half the pitch-circle thickness angle plus the involute angle.
            double halfBase = Math.PI / (2 * teeth) + involuteAtPitch;
            double tMax = Math.Sqrt(ra * ra / (rb * rb) - 1);
            double tMin = rf > rb ? Math.Sqrt(rf * rf / (rb * rb) - 1) : 0;
            // One flank (the tooth's right side, centered on angle 0): points from the root up to the tip.
            var flank = new List<double[]>();
            if (rf < rb) flank.Add(Polar(rf, -halfBase));
            for (int i = 0; i <= pointsPerFlank; i++)
            {
                double t = tMin + (tMax - tMin) * i / pointsPerFlank;
                double x = rb * (Math.Cos(t) + t * Math.Sin(t)), y = rb * (Math.Sin(t) - t * Math.Cos(t));
                double radius = Math.Sqrt(x * x + y * y), angle = Math.Atan2(y, x) - halfBase;
                flank.Add(Polar(radius, angle));
            }
            var outline = new List<double[]>();
            for (int k = 0; k < teeth; k++)
            {
                double turn = 2 * Math.PI * k / teeth;
                foreach (var p in flank) outline.Add(Rotate(p, turn));                                   // up the right flank
                for (int i = flank.Count - 1; i >= 0; i--) outline.Add(Rotate(new[] { flank[i][0], -flank[i][1] }, turn)); // down the left flank
            }
            // Each tooth runs from its right flank to its left, and teeth follow in order: the whole outline is counterclockwise.
            // Straight segments join the teeth at the root and cross each tip: close enough to the arcs to cut and to mesh.
            return outline;
        }

        private static double[] Polar(double radius, double angle) { return new[] { radius * Math.Cos(angle), radius * Math.Sin(angle) }; }

        private static double[] Rotate(double[] p, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return new[] { p[0] * c - p[1] * s, p[0] * s + p[1] * c };
        }
    }
}
