using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace JocoRobos.Cad
{
    /// <summary>A bore through a gear, sprocket, pulley or hub: its loops (points, inches, centered on the origin).</summary>
    internal static class Bores
    {
        // Stored by index in CAD Hub features: only ever add to the end.
        internal static readonly string[] Names = { "1/2\" hex", "3/8\" hex", "1/2\" round", "8 mm round", "No bore", "1/2\" round, 1/8\" keyway", "8 mm round, 2 mm keyway" };
        internal static readonly string[] FileNames = { "0.5 Hex Bore", "0.375 Hex Bore", "0.5 Round Bore", "8mm Round Bore", "No Bore", "0.5 Keyed Bore", "8mm Keyed Bore" };

        /// <summary>The bore's outline as one loop (a hexagon, a circle, or a circle with its keyway), or null for no bore.</summary>
        internal static List<double[]> Loop(int index)
        {
            switch (index)
            {
                case 0: return Hexagon(0.5 + 0.004);
                case 1: return Hexagon(0.375 + 0.004);
                case 2: return Circle((0.5 + 0.002) / 2);
                case 3: return Circle((8 / 25.4 + 0.002) / 2);
                case 5: return Keyed((0.5 + 0.002) / 2, 0.125, 0.0625);
                case 6: return Keyed((8 / 25.4 + 0.002) / 2, 2 / 25.4, 1 / 25.4);
                default: return null;
            }
        }

        /// <summary>Across the flats (hex) or diameter: the size a hub has to be bigger than.</summary>
        internal static double Size(int index)
        {
            var loop = Loop(index);
            return loop == null ? 0 : 2 * loop.Max(p => Math.Sqrt(p[0] * p[0] + p[1] * p[1]));
        }

        internal static List<double[]> Hexagon(double acrossFlats)
        {
            double r = acrossFlats / Math.Sqrt(3); // corner radius of a hexagon with these flats
            return Enumerable.Range(0, 6).Select(i => new[] { r * Math.Cos(Math.PI / 3 * i + Math.PI / 6), r * Math.Sin(Math.PI / 3 * i + Math.PI / 6) }).ToList();
        }

        internal static List<double[]> Circle(double radius, int sides = 72)
        {
            return Enumerable.Range(0, sides).Select(i => new[] { radius * Math.Cos(2 * Math.PI * i / sides), radius * Math.Sin(2 * Math.PI * i / sides) }).ToList();
        }

        // A round bore with a keyway of this width cut this deep beyond the bore, at the top.
        private static List<double[]> Keyed(double radius, double width, double depth)
        {
            var round = new PathD(Circle(radius).Select(p => new PointD(p[0], p[1])));
            var key = new PathD { new PointD(-width / 2, 0), new PointD(width / 2, 0), new PointD(width / 2, radius + depth), new PointD(-width / 2, radius + depth) };
            var union = Clipper.Union(new PathsD { round }, new PathsD { key }, FillRule.NonZero, 5);
            return union.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First().Select(p => new[] { p.x, p.y }).ToList();
        }
    }

    /// <summary>
    /// The powertrain shapes and numbers, in inches: roller chain sprockets, timing pulleys, internal (ring) gears and planetary
    /// sets, and gear ratios. Pure geometry, tested without SOLIDWORKS; the CAD Hub tools extrude these outlines.
    /// </summary>
    internal static class Powertrain
    {
        private const int Precision = 5;

        // ---------- roller chain sprockets ----------

        internal sealed class Chain
        {
            internal string Name;
            internal double Pitch, Roller, Thickness; // inches: pitch, roller diameter, sprocket tooth width for single-strand chain
        }

        // Stored by index in CAD Hub features: only ever add to the end.
        internal static readonly List<Chain> Chains = new List<Chain>
        {
            new Chain { Name = "#25 chain", Pitch = 0.25, Roller = 0.130, Thickness = 0.110 },
            new Chain { Name = "#35 chain", Pitch = 0.375, Roller = 0.200, Thickness = 0.168 },
        };

        internal static double SprocketPitchDiameter(Chain chain, int teeth) { return chain.Pitch / Math.Sin(Math.PI / teeth); }
        internal static double SprocketOutsideDiameter(Chain chain, int teeth) { return chain.Pitch * (0.6 + 1 / Math.Tan(Math.PI / teeth)); }

        /// <summary>
        /// An ANSI roller chain sprocket's outline: a seat for each roller on the pitch circle (1.005 × roller + 0.003") wrapping its
        /// lower half, and tooth flanks that are arcs around the neighboring rollers (radius pitch − seat), trimmed to the outside diameter.
        /// </summary>
        internal static List<double[]> SprocketOutline(Chain chain, int teeth)
        {
            double pitchRadius = SprocketPitchDiameter(chain, teeth) / 2, seat = (1.005 * chain.Roller + 0.003) / 2;
            double outside = SprocketOutsideDiameter(chain, teeth) / 2, flank = chain.Pitch - seat;
            Func<int, double[]> roller = k => new[] { pitchRadius * Math.Cos(2 * Math.PI * k / teeth), pitchRadius * Math.Sin(2 * Math.PI * k / teeth) };
            // Solid up to the pitch circle, so each seat wraps the bottom half of its roller.
            var body = new PathsD { Disc(0, 0, pitchRadius) };
            var rim = new PathsD { Disc(0, 0, outside) };
            for (int k = 0; k < teeth; k++)
            {
                double[] a = roller(k), b = roller(k + 1);
                var tooth = Clipper.Intersect(new PathsD { Disc(a[0], a[1], flank) }, new PathsD { Disc(b[0], b[1], flank) }, FillRule.NonZero, Precision);
                body = Clipper.Union(body, Clipper.Intersect(tooth, rim, FillRule.NonZero, Precision), FillRule.NonZero, Precision);
            }
            var seats = new PathsD(Enumerable.Range(0, teeth).Select(k => Disc(roller(k)[0], roller(k)[1], seat)));
            return Largest(Clipper.Difference(body, seats, FillRule.NonZero, Precision));
        }

        internal static string SprocketProblem(int teeth)
        {
            return teeth < 8 || teeth > 120 ? "Sprockets from 8 to 120 teeth." : null;
        }

        // ---------- timing pulleys ----------

        internal sealed class Belt
        {
            internal string Name;
            internal double Pitch, Offset, Depth, Groove, Tip, Width; // inches: pitch, pitch-line offset (per side), groove depth,
                                                                        // groove radius, tip rounding, usual belt width
        }

        // Stored by index in CAD Hub features: only ever add to the end. Groove shapes are close to HTD and GT2 (round-bottomed
        // grooves of the belt's tooth depth): right for printed pulleys; buy machined pulleys for high loads.
        internal static readonly List<Belt> Belts = new List<Belt>
        {
            new Belt { Name = "HTD 5 mm belt", Pitch = 5 / 25.4, Offset = 0.5715 / 25.4, Depth = 2.06 / 25.4, Groove = 1.6 / 25.4, Tip = 0.4 / 25.4, Width = 9 / 25.4 },
            new Belt { Name = "GT2 3 mm belt", Pitch = 3 / 25.4, Offset = 0.381 / 25.4, Depth = 1.14 / 25.4, Groove = 0.85 / 25.4, Tip = 0.25 / 25.4, Width = 6 / 25.4 },
        };

        internal static double PulleyPitchDiameter(Belt belt, int teeth) { return teeth * belt.Pitch / Math.PI; }
        internal static double PulleyOutsideDiameter(Belt belt, int teeth) { return PulleyPitchDiameter(belt, teeth) - 2 * belt.Offset; }

        /// <summary>A timing pulley's toothed outline: the outside circle with a round-bottomed groove for every belt tooth.</summary>
        internal static List<double[]> PulleyOutline(Belt belt, int teeth)
        {
            double outside = PulleyOutsideDiameter(belt, teeth) / 2, center = outside - (belt.Depth - belt.Groove);
            var grooves = new PathsD();
            for (int k = 0; k < teeth; k++)
            {
                double angle = 2 * Math.PI * (k + 0.5) / teeth;
                grooves.Add(Disc(center * Math.Cos(angle), center * Math.Sin(angle), belt.Groove));
            }
            var shape = Clipper.Difference(new PathsD { Disc(0, 0, outside) }, grooves, FillRule.NonZero, Precision);
            // Round the sharp tooth tips, like a real pulley (shrink, then grow back).
            shape = Clipper.InflatePaths(Clipper.InflatePaths(shape, -belt.Tip, JoinType.Round, EndType.Polygon, 2, Precision, 0.0002), belt.Tip, JoinType.Round,
                EndType.Polygon, 2, Precision, 0.0002);
            return Largest(shape);
        }

        internal static string PulleyProblem(Belt belt, int teeth)
        {
            if (teeth < 10 || teeth > 120) return "Pulleys from 10 to 120 teeth.";
            return null;
        }

        // ---------- planetary sets ----------

        /// <summary>Why these tooth counts don't make a working planetary, or null.</summary>
        internal static string PlanetaryProblem(int sun, int planet, int ring, int planets)
        {
            if (sun < 8 || planet < 8) return "Sun and planets need at least 8 teeth.";
            if (ring != sun + 2 * planet) return "The ring needs sun + 2 × planet teeth: " + (sun + 2 * planet) + ".";
            if (planets < 2 || planets > 8) return "2 to 8 planets.";
            if ((sun + ring) % planets != 0) return "With " + planets + " planets evenly spaced, sun + ring teeth (" + (sun + ring) + ") must divide by " + planets + ".";
            // Neighboring planets must not hit each other: their tips need room between them.
            double centers = (sun + planet) / 2.0, tip = (planet + 2) / 2.0;
            if (2 * centers * Math.Sin(Math.PI / planets) <= 2 * tip) return "The planets would hit each other: fewer planets or a bigger sun.";
            return null;
        }

        /// <summary>Sun in, ring held, carrier out: the reduction.</summary>
        internal static double PlanetaryRatio(int sun, int ring) { return 1 + (double)ring / sun; }

        /// <summary>Where each planet sits (center, inches) and how far it's turned so its teeth mesh with the sun's.</summary>
        internal static List<double[]> Planets(int sun, int planet, int planets, double diametralPitch)
        {
            double centers = (sun + planet) / (2 * diametralPitch);
            var result = new List<double[]>();
            for (int k = 0; k < planets; k++)
            {
                double theta = 2 * Math.PI * k / planets;
                // The sun has a tooth at angle 0; facing it, each planet needs a space where the sun has a tooth.
                double sunPhase = theta * sun / (2 * Math.PI);
                double turn = theta + Math.PI - Math.PI / planet * (1 - 2 * (sunPhase - Math.Floor(sunPhase)));
                result.Add(new[] { centers * Math.Cos(theta), centers * Math.Sin(theta), turn });
            }
            return result;
        }

        /// <summary>How far the ring is turned so planet 0 (at angle 0) meshes with it.</summary>
        internal static double RingTurn(int sun, int planet, int ring, int planets)
        {
            var first = Planets(sun, planet, planets, 1)[0];
            // The planet's tooth pattern facing outward (angle 0), from its center; the ring needs a space where the planet has a tooth.
            double planetPhase = (0 - first[2]) * planet / (2 * Math.PI);
            return -(2 * Math.PI / ring) * (planetPhase + 0.5) + Math.PI / ring;
        }

        // ---------- gear ratios ----------

        internal sealed class Motor
        {
            internal string Name;
            internal double FreeSpeed; // rpm, the vendor's published free speed
        }

        internal static readonly List<Motor> Motors = new List<Motor>
        {
            new Motor { Name = "Kraken X60", FreeSpeed = 6000 },
            new Motor { Name = "Kraken X44", FreeSpeed = 7530 },
            new Motor { Name = "NEO Vortex", FreeSpeed = 6784 },
            new Motor { Name = "NEO", FreeSpeed = 5676 },
            new Motor { Name = "NEO 550", FreeSpeed = 11000 },
            new Motor { Name = "Falcon 500", FreeSpeed = 6380 },
            new Motor { Name = "CIM", FreeSpeed = 5330 },
            new Motor { Name = "775pro", FreeSpeed = 18730 },
        };

        /// <summary>The overall reduction of stages (driving teeth, driven teeth); stages with a 0 are skipped.</summary>
        internal static double Ratio(IEnumerable<int[]> stages)
        {
            double ratio = 1;
            foreach (var stage in stages) if (stage[0] > 0 && stage[1] > 0) ratio *= (double)stage[1] / stage[0];
            return ratio;
        }

        /// <summary>Free speed of a wheel in feet per second.</summary>
        internal static double WheelSpeed(double motorRpm, double ratio, double wheelDiameter)
        {
            return motorRpm / ratio * Math.PI * wheelDiameter / 12 / 60;
        }

        // ---------- helpers ----------

        internal static PathD Disc(double x, double y, double r)
        {
            int sides = (int)Math.Max(48, Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - 0.0002 / Math.Max(r, 1e-6)))));
            var path = new PathD();
            for (int i = 0; i < sides; i++) path.Add(new PointD(x + r * Math.Cos(2 * Math.PI * i / sides), y + r * Math.Sin(2 * Math.PI * i / sides)));
            return path;
        }

        private static List<double[]> Largest(PathsD paths)
        {
            if (paths.Count == 0) return new List<double[]>();
            // Fewer, longer pieces (within 0.0005"): each one becomes a curve in SOLIDWORKS.
            var best = Clipper.SimplifyPath(paths.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First(), 0.0005);
            var points = best.Select(p => new[] { p.x, p.y }).ToList();
            if (PlateLighten.SignedArea(points) < 0) points.Reverse();
            return points;
        }
    }
}
