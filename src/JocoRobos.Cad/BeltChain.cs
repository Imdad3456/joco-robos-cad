using System;
using System.Collections.Generic;

namespace JocoRobos.Cad
{
    internal sealed class DriveKind
    {
        internal string Name;
        internal double Pitch;      // inches
        internal bool Chain;        // chain: sprocket pitch diameter p/sin(180°/z), lengths in links; belt: z·p/π, lengths in teeth
        internal string Unit { get { return Chain ? "links" : "teeth"; } }
    }

    /// <summary>
    /// The belt and chain calculator: center distance from the two tooth counts and the belt (teeth) or chain (links) length,
    /// and the nearest lengths for a center distance you want. Pure math, inches.
    /// </summary>
    internal static class BeltChain
    {
        internal static readonly List<DriveKind> Kinds = new List<DriveKind>
        {
            new DriveKind { Name = "HTD 5 mm belt", Pitch = 5 / 25.4 },
            new DriveKind { Name = "GT2 3 mm belt", Pitch = 3 / 25.4 },
            new DriveKind { Name = "#25 chain", Pitch = 0.25, Chain = true },
            new DriveKind { Name = "#35 chain", Pitch = 0.375, Chain = true },
        };

        internal static double PitchDiameter(DriveKind kind, int teeth)
        {
            return kind.Chain ? kind.Pitch / Math.Sin(Math.PI / teeth) : teeth * kind.Pitch / Math.PI;
        }

        /// <summary>Length of the belt or chain around two pulleys (pitch diameters d1, d2) at center distance c.</summary>
        internal static double Length(double c, double d1, double d2)
        {
            double big = Math.Max(d1, d2), small = Math.Min(d1, d2), half = (big - small) / 2;
            double straight = Math.Sqrt(Math.Max(0, c * c - half * half));
            double angle = Math.Asin(Math.Min(1, half / c));
            return 2 * straight + Math.PI * (big + small) / 2 + (big - small) * angle;
        }

        /// <summary>Center distance for a belt with this many teeth (chain: links), or NaN if it can't reach around both.</summary>
        internal static double CenterDistance(DriveKind kind, int teeth1, int teeth2, int length)
        {
            double d1 = PitchDiameter(kind, teeth1), d2 = PitchDiameter(kind, teeth2), target = length * kind.Pitch;
            double low = Math.Max(Math.Abs(d1 - d2) / 2, (d1 + d2) / 2) + 1e-6, high = target / 2;
            if (low >= high || Length(low, d1, d2) > target) return double.NaN; // Too short: the pulleys would overlap.
            for (int i = 0; i < 200; i++)
            {
                double mid = (low + high) / 2;
                if (Length(mid, d1, d2) < target) low = mid; else high = mid;
            }
            return (low + high) / 2;
        }

        /// <summary>
        /// The belt's path around two pulleys (pitch diameters d1 at the origin, d2 at (center, 0)): the top straight run, the arc
        /// around pulley 2, the bottom run, and the arc around pulley 1 (counterclockwise arcs). Null if they overlap.
        /// </summary>
        internal static List<PocketSegment> Path(double d1, double d2, double center)
        {
            double r1 = d1 / 2, r2 = d2 / 2;
            if (center <= Math.Abs(r1 - r2) || center <= 0) return null;
            // The straight runs touch both pitch circles where the normal makes this angle with the line of centers.
            double phi = Math.Acos((r1 - r2) / center), c = Math.Cos(phi), s = Math.Sin(phi);
            double[] top1 = { r1 * c, r1 * s }, top2 = { center + r2 * c, r2 * s }, bottom1 = { r1 * c, -r1 * s }, bottom2 = { center + r2 * c, -r2 * s };
            return new List<PocketSegment>
            {
                new PocketSegment { X1 = top1[0], Y1 = top1[1], X2 = top2[0], Y2 = top2[1] },
                new PocketSegment { Arc = true, Clockwise = true, X1 = top2[0], Y1 = top2[1], X2 = bottom2[0], Y2 = bottom2[1], Cx = center, Cy = 0 },
                new PocketSegment { X1 = bottom2[0], Y1 = bottom2[1], X2 = bottom1[0], Y2 = bottom1[1] },
                new PocketSegment { Arc = true, Clockwise = true, X1 = bottom1[0], Y1 = bottom1[1], X2 = top1[0], Y2 = top1[1], Cx = 0, Cy = 0 },
            };
        }

        /// <summary>The lengths just shorter and just longer than what a center distance needs (chains: even link counts).</summary>
        internal static Tuple<int, int> NearestLengths(DriveKind kind, int teeth1, int teeth2, double center)
        {
            double exact = Length(center, PitchDiameter(kind, teeth1), PitchDiameter(kind, teeth2)) / kind.Pitch;
            int step = kind.Chain ? 2 : 1;
            int below = (int)Math.Floor(exact / step) * step;
            return Tuple.Create(below, below + step);
        }
    }
}
