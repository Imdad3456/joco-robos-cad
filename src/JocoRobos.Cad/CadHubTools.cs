using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace JocoRobos.Cad
{
    internal enum FieldKind { Number, Length, Choice, Selection }

    /// <summary>One setting on a tool's PropertyManager page. Lengths are inches; Advanced ones sit in a collapsed group.</summary>
    internal sealed class ToolField
    {
        internal string Key, Label, Tip = "";
        internal FieldKind Kind;
        internal bool Advanced;
        internal double Min, Max, Default, Step = 1;
        internal List<string> Items = new List<string>();
        internal int DefaultItem;
    }

    /// <summary>
    /// A CAD Hub modeling tool built on the shared framework: a native PropertyManager page (preset first, Advanced collapsed,
    /// the exact result shown), a live preview, and an editable feature in the tree that rebuilds from its stored settings.
    /// Geometry is made with SOLIDWORKS' modeler from numbers stored in the feature, so a rebuild never depends on today's presets.
    /// </summary>
    internal abstract class CadHubTool
    {
        internal abstract string Kind { get; }
        internal abstract string Title { get; }
        internal abstract List<ToolField> Fields(TeamStandards standards);
        /// <summary>A line under the settings with what will be made, e.g. "Resulting bore 1.1265 in".</summary>
        internal virtual string Result(FeatureParams p, TeamStandards standards) { return ""; }
        internal virtual string Problem(FeatureParams p) { return null; }
        /// <summary>True: the feature cuts the selected body. False: it adds a new body.</summary>
        internal abstract bool Cuts { get; }
        internal virtual bool NeedsSelection { get { return false; } }
        /// <summary>Called on OK: turn the selection into stored geometry (center, axis…) and freeze preset-derived numbers.</summary>
        internal virtual string Capture(object selection, double[] pickPoint, FeatureParams p, TeamStandards standards) { return null; }
        /// <summary>The body to add, or the tool to cut with, in meters.</summary>
        internal abstract Body2 Build(SldWorks application, FeatureParams p, bool preview);

        internal static readonly List<CadHubTool> All = new List<CadHubTool> { new SpurGearTool(), new BearingHoleTool() };
        internal static CadHubTool Find(string kind) { return All.FirstOrDefault(t => t.Kind == kind); }

        // ---------- shared geometry ----------

        protected const double M = 0.0254;

        /// <summary>A flat closed profile (loops of points in inches, on a plane through origin with normal n and x-axis u) extruded by depth.</summary>
        protected static Body2 Extrude(SldWorks application, List<List<double[]>> loops, List<double[]> circles, double[] origin, double[] n, double[] u, double depth)
        {
            var modeler = (Modeler)application.GetModeler();
            var math = (MathUtility)application.GetMathUtility();
            var v = new[] { n[1] * u[2] - n[2] * u[1], n[2] * u[0] - n[0] * u[2], n[0] * u[1] - n[1] * u[0] };
            Func<double, double, double[]> at = (x, y) => new[] { origin[0] + (x * u[0] + y * v[0]) * M, origin[1] + (x * u[1] + y * v[1]) * M, origin[2] + (x * u[2] + y * v[2]) * M };
            var curves = new List<Curve>();
            foreach (var loop in loops)
                for (int i = 0; i < loop.Count; i++)
                {
                    double[] a = at(loop[i][0], loop[i][1]), b = at(loop[(i + 1) % loop.Count][0], loop[(i + 1) % loop.Count][1]);
                    var direction = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
                    if (Math.Abs(direction[0]) + Math.Abs(direction[1]) + Math.Abs(direction[2]) < 1e-12) continue;
                    var line = (Curve)modeler.CreateLine(a, direction);
                    curves.Add(line.CreateTrimmedCurve2(a[0], a[1], a[2], b[0], b[1], b[2]));
                }
            foreach (var circle in circles ?? new List<double[]>()) // [x, y, radius] in inches: two half arcs
            {
                var center = at(circle[0], circle[1]);
                double[] east = at(circle[0] + circle[2], circle[1]), west = at(circle[0] - circle[2], circle[1]);
                foreach (var half in new[] { new[] { east, west }, new[] { west, east } })
                {
                    var arc = (Curve)modeler.CreateArc(center, n, circle[2] * M, half[0], half[1]);
                    curves.Add(arc.CreateTrimmedCurve2(half[0][0], half[0][1], half[0][2], half[1][0], half[1][1], half[1][2]));
                }
            }
            var plane = (Surface)modeler.CreatePlanarSurface2(origin, n, u);
            var sheet = plane.CreateTrimmedSheet4(curves.ToArray(), true) as Body2;
            if (sheet == null) throw new InvalidOperationException("SOLIDWORKS couldn't make the profile.");
            var body = modeler.CreateExtrudedBody(sheet, (MathVector)math.CreateVector(n), depth * M);
            if (body == null) throw new InvalidOperationException("SOLIDWORKS couldn't extrude the profile.");
            return body;
        }

        protected static List<double[]> Hexagon(double acrossFlats)
        {
            double r = acrossFlats / Math.Sqrt(3); // corner radius of a hexagon with these flats
            return Enumerable.Range(0, 6).Select(i => new[] { r * Math.Cos(Math.PI / 3 * i + Math.PI / 6), r * Math.Sin(Math.PI / 3 * i + Math.PI / 6) }).ToList();
        }
    }

    internal sealed class SpurGearTool : CadHubTool
    {
        internal override string Kind { get { return "gear"; } }
        internal override string Title { get { return "Spur Gear"; } }
        internal override bool Cuts { get { return false; } }

        private static readonly string[] Pitches = { "20 DP (most FRC gears)", "32 DP", "10 DP" };
        private static readonly double[] PitchValues = { 20, 32, 10 };
        internal static readonly string[] Bores = { "1/2\" hex", "3/8\" hex", "1/2\" round", "8 mm round", "No bore" };

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "teeth", Label = "Teeth", Kind = FieldKind.Number, Min = 8, Max = 200, Default = 36 },
                new ToolField { Key = "pitch", Label = "Diametral pitch", Kind = FieldKind.Choice, Items = Pitches.ToList() },
                new ToolField { Key = "width", Label = "Thickness", Kind = FieldKind.Length, Min = 0.05, Max = 4, Default = 0.5, Step = 0.125 },
                new ToolField { Key = "bore", Label = "Bore", Kind = FieldKind.Choice, Items = Bores.ToList() },
                new ToolField { Key = "pressure", Label = "Pressure angle", Kind = FieldKind.Choice, Items = new List<string> { "20°", "14.5°" }, Advanced = true,
                    Tip = "Match the gear it meshes with (vendors list it)." },
            };
        }

        internal static double Pitch(FeatureParams p) { return PitchValues[Math.Max(0, Math.Min(PitchValues.Length - 1, (int)p.Number("pitch", 0)))]; }
        internal static double Pressure(FeatureParams p) { return p.Number("pressure", 0) == 1 ? 14.5 : 20; }

        internal override string Result(FeatureParams p, TeamStandards standards)
        {
            int teeth = (int)p.Number("teeth", 36);
            double dp = Pitch(p);
            return "Pitch diameter " + StockParts.Inches(SpurGear.PitchDiameter(teeth, dp)) + " in · outside " + StockParts.Inches(SpurGear.OutsideDiameter(teeth, dp)) +
                " in. Two gears mesh at (teeth₁ + teeth₂) ÷ (2 × " + StockParts.Inches(dp) + ") apart.";
        }

        internal override string Problem(FeatureParams p) { return SpurGear.Problem((int)p.Number("teeth", 36), Pitch(p), Pressure(p)); }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview)
        {
            var outline = SpurGear.Outline((int)p.Number("teeth", 36), Pitch(p), Pressure(p), preview ? 4 : 8);
            var loops = new List<List<double[]>> { outline };
            var circles = new List<double[]>();
            switch ((int)p.Number("bore", 0))
            {
                case 0: loops.Add(Hexagon(0.5 + 0.004)); break;
                case 1: loops.Add(Hexagon(0.375 + 0.004)); break;
                case 2: circles.Add(new[] { 0, 0, (0.5 + 0.002) / 2 }); break;
                case 3: circles.Add(new[] { 0, 0, (8 / 25.4 + 0.002) / 2 }); break;
            }
            // On the Front plane at the origin, like a part modeled from scratch.
            return Extrude(application, loops, circles, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }, p.Number("width", 0.5));
        }
    }

    internal sealed class BearingHoleTool : CadHubTool
    {
        internal override string Kind { get { return "bearing-hole"; } }
        internal override string Title { get { return "Bearing Hole"; } }
        internal override bool Cuts { get { return true; } }
        internal override bool NeedsSelection { get { return true; } }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "where", Label = "Where: click the face at the hole's center, or an existing round edge to resize", Kind = FieldKind.Selection },
                new ToolField { Key = "bearing", Label = "Bearing", Kind = FieldKind.Choice, Items = BearingHoles.Presets(standards).Select(b => b.Name).ToList() },
                new ToolField { Key = "fit", Label = "Fit", Kind = FieldKind.Choice, Items = BearingHoles.Fits.ToList() },
                new ToolField { Key = "extra", Label = "Extra clearance (in)", Kind = FieldKind.Number, Min = -0.01, Max = 0.05, Default = 0, Step = 0.0005, Advanced = true,
                    Tip = "Added on top of the team's fit, for one-off adjustments." },
            };
        }

        private static double Bore(FeatureParams p, TeamStandards standards)
        {
            var presets = BearingHoles.Presets(standards);
            var bearing = presets[Math.Max(0, Math.Min(presets.Count - 1, (int)p.Number("bearing", 0)))];
            string fit = BearingHoles.Fits[Math.Max(0, Math.Min(BearingHoles.Fits.Length - 1, (int)p.Number("fit", 0)))];
            return Math.Round(BearingHoles.Bore(bearing.OutsideDiameter, fit, standards) + p.Number("extra", 0), 4);
        }

        internal override string Result(FeatureParams p, TeamStandards standards)
        {
            return "Resulting bore " + Bore(p, standards).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + " in (through)";
        }

        internal override string Capture(object selection, double[] pickPoint, FeatureParams p, TeamStandards standards)
        {
            double[] center = null, axis = null;
            var edge = selection as Edge;
            var curve = edge?.GetCurve() as Curve;
            if (curve != null && curve.IsCircle())
            {
                var circle = (double[])curve.CircleParams;
                center = new[] { circle[0], circle[1], circle[2] };
                axis = new[] { circle[3], circle[4], circle[5] };
            }
            var face = selection as Face2;
            var surface = face?.GetSurface() as Surface;
            if (surface != null && surface.IsPlane() && pickPoint != null)
            {
                var plane = (double[])surface.PlaneParams;
                axis = new[] { plane[0], plane[1], plane[2] };
                // The clicked point, moved onto the face's plane.
                double offset = (pickPoint[0] - plane[3]) * axis[0] + (pickPoint[1] - plane[4]) * axis[1] + (pickPoint[2] - plane[5]) * axis[2];
                center = new[] { pickPoint[0] - offset * axis[0], pickPoint[1] - offset * axis[1], pickPoint[2] - offset * axis[2] };
            }
            if (center == null) return "Click a flat face where the bearing goes (or a round edge to resize an existing hole).";
            string[] names = { "cx", "cy", "cz", "ax", "ay", "az" };
            var values = center.Concat(axis).ToArray();
            for (int i = 0; i < 6; i++) p.Set(names[i], values[i]);
            // Frozen at creation: a later change to the team's fits doesn't silently move existing holes.
            p.Set("bore", Bore(p, standards));
            return null;
        }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview)
        {
            if (p["cx"] == null) return null; // Nothing selected yet: nothing to preview.
            double[] center = { p.Number("cx", 0), p.Number("cy", 0), p.Number("cz", 0) }, axis = { p.Number("ax", 0), p.Number("ay", 0), p.Number("az", 1) };
            double radius = p.Number("bore", 1.1265) / 2 * M, length = preview ? 0.05 : 1.0;
            var modeler = (Modeler)application.GetModeler();
            // Through: a cylinder centered on the face, long enough to pass through the plate both ways.
            var start = new[] { center[0] - axis[0] * length / 2, center[1] - axis[1] * length / 2, center[2] - axis[2] * length / 2 };
            return modeler.CreateBodyFromCyl(new[] { start[0], start[1], start[2], axis[0], axis[1], axis[2], radius, length }) as Body2;
        }
    }
}
