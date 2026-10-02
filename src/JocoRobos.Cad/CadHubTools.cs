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
        /// <summary>The result line once something is selected (Lighten counts its pockets on the chosen face).</summary>
        internal virtual string Result(FeatureParams p, TeamStandards standards, object selection) { return Result(p, standards); }
        internal virtual string Problem(FeatureParams p) { return null; }
        /// <summary>True: the feature cuts the selected body. False: it adds a new body.</summary>
        internal abstract bool Cuts { get; }
        internal virtual bool NeedsSelection { get { return false; } }
        /// <summary>A selection box that may stay empty (Belt and Chain: the dimension to set is optional).</summary>
        internal virtual bool SelectionOptional { get { return false; } }
        internal virtual int[] SelectionFilters { get { return new[] { (int)swSelectType_e.swSelFACES, (int)swSelectType_e.swSelEDGES }; } }
        /// <summary>False: on OK the tool does its work directly (a sketch and a cut, a dimension) instead of adding a CAD Hub feature.</summary>
        internal virtual bool MakesFeature { get { return true; } }
        /// <summary>Works in assemblies and drawings too (only a calculator).</summary>
        internal virtual bool AnyDocument { get { return false; } }
        /// <summary>For tools that don't make a feature: do the work, and say what was done.</summary>
        internal virtual string Apply(SldWorks application, ModelDoc2 doc, FeatureParams p, object selection, TeamStandards standards) { return null; }
        /// <summary>Called on OK: turn the selection into stored geometry (center, axis…) and freeze preset-derived numbers.</summary>
        internal virtual string Capture(object selection, double[] pickPoint, FeatureParams p, TeamStandards standards) { return null; }
        /// <summary>The body to add, or the tool to cut with, in meters.</summary>
        internal abstract Body2 Build(SldWorks application, FeatureParams p, bool preview);
        /// <summary>All the bodies it makes (a planetary set makes several).</summary>
        internal virtual List<Body2> BuildBodies(SldWorks application, FeatureParams p, bool preview)
        {
            var body = Build(application, p, preview);
            return body == null ? new List<Body2>() : new List<Body2> { body };
        }
        /// <summary>Used from an assembly: the part's file name (without .SLDPRT) and its folder under 90_COTS/Stock.</summary>
        internal virtual string PartName(FeatureParams p) { return Title; }
        internal virtual string Folder { get { return Title + "s"; } }

        internal static readonly List<CadHubTool> All = new List<CadHubTool> { new SpurGearTool(), new SprocketTool(), new PulleyTool(), new ShaftTool(), new PlanetaryTool(),
            new GearRatioTool(), new LightenTool(), new BeltChainTool(), new BearingHoleTool() };
        // Bearing Hole is no longer on the tab: kept so the CAD Hub bearing holes earlier versions made still rebuild and edit.
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

        /// <summary>Adds a bore to a profile: round bores as true circles, hex and keyed ones as outlines.</summary>
        protected static void AddBore(int index, List<List<double[]>> loops, List<double[]> circles)
        {
            switch (index)
            {
                case 2: circles.Add(new[] { 0, 0, (0.5 + 0.002) / 2 }); break;
                case 3: circles.Add(new[] { 0, 0, (8 / 25.4 + 0.002) / 2 }); break;
                default: var loop = Bores.Loop(index); if (loop != null) loops.Add(loop); break;
            }
        }

        /// <summary>One body from two (a gear and its hub, a pulley and its flanges); null parts are skipped.</summary>
        protected static Body2 Join(Body2 a, Body2 b)
        {
            if (a == null) return b;
            if (b == null) return a;
            int error;
            var joined = a.Operations2((int)swBodyOperationType_e.SWBODYADD, b, out error) as object[];
            var body = joined?.OfType<Body2>().FirstOrDefault();
            if (body == null) throw new InvalidOperationException("SOLIDWORKS couldn't join the pieces (error " + error + ").");
            return body;
        }

        /// <summary>A round hub (with the same bore) on the back, from z to z + length; null when there's no hub.</summary>
        protected static Body2 Hub(SldWorks application, FeatureParams p, double z)
        {
            double diameter = p.Number("hub", 0), length = p.Number("hubLength", 0);
            if (diameter <= 0 || length <= 0) return null;
            var loops = new List<List<double[]>>();
            var circles = new List<double[]> { new[] { 0, 0, diameter / 2 } };
            AddBore((int)p.Number("bore", 0), loops, circles);
            return Extrude(application, loops, circles, new[] { 0.0, 0, z * M }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }, length);
        }

        /// <summary>The usual hub fields (Advanced): 0 means no hub.</summary>
        protected static IEnumerable<ToolField> HubFields()
        {
            yield return new ToolField { Key = "hub", Label = "Hub diameter (0: no hub)", Kind = FieldKind.Length, Min = 0, Max = 6, Default = 0, Step = 0.125, Advanced = true };
            yield return new ToolField { Key = "hubLength", Label = "Hub length", Kind = FieldKind.Length, Min = 0, Max = 3, Default = 0.25, Step = 0.0625, Advanced = true };
        }

        protected static string HubProblem(FeatureParams p)
        {
            double hub = p.Number("hub", 0);
            return hub > 0 && hub <= Bores.Size((int)p.Number("bore", 0)) + 0.1 ? "The hub must be bigger than the bore." : null;
        }

        /// <summary>A short code for these exact settings, so two different parts never share a file name.</summary>
        protected static string Code(FeatureParams p)
        {
            uint hash = 2166136261;
            foreach (char c in p.Encode()) hash = (hash ^ c) * 16777619;
            return (hash % 0x10000).ToString("x4");
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
        internal override string Folder { get { return "Gears"; } }

        private static readonly string[] Pitches = { "20 DP (most FRC gears)", "32 DP", "10 DP" };
        private static readonly double[] PitchValues = { 20, 32, 10 };

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            var fields = new List<ToolField>
            {
                new ToolField { Key = "teeth", Label = "Teeth", Kind = FieldKind.Number, Min = 8, Max = 200, Default = 36 },
                new ToolField { Key = "pitch", Label = "Diametral pitch", Kind = FieldKind.Choice, Items = Pitches.ToList() },
                new ToolField { Key = "width", Label = "Thickness", Kind = FieldKind.Length, Min = 0.05, Max = 4, Default = 0.5, Step = 0.125 },
                new ToolField { Key = "bore", Label = "Bore", Kind = FieldKind.Choice, Items = Bores.Names.ToList() },
                new ToolField { Key = "pressure", Label = "Pressure angle", Kind = FieldKind.Choice, Items = new List<string> { "20°", "14.5°" }, Advanced = true,
                    Tip = "Match the gear it meshes with (vendors list it)." },
                new ToolField { Key = "backlash", Label = "Backlash", Kind = FieldKind.Length, Min = 0, Max = 0.03, Default = 0.004, Step = 0.001, Advanced = true,
                    Tip = "The gap the pair runs with: each tooth is thinned by half of it. 0.004\" suits printed and machined gears." },
            };
            fields.AddRange(HubFields());
            return fields;
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

        internal override string Problem(FeatureParams p) { return SpurGear.Problem((int)p.Number("teeth", 36), Pitch(p), Pressure(p)) ?? HubProblem(p); }

        internal override string PartName(FeatureParams p)
        {
            return "Spur Gear " + StockParts.Inches(Pitch(p)) + "DP " + StockParts.Inches(Pressure(p)) + "PA " + (int)p.Number("teeth", 36) + "T " +
                Bores.FileNames[Math.Max(0, Math.Min(Bores.FileNames.Length - 1, (int)p.Number("bore", 0)))] + " " + StockParts.Inches(p.Number("width", 0.5)) + " FW " + Code(p);
        }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview)
        {
            // Backlash and hub are missing from gears made before 1.11: those rebuild exactly as they were.
            var outline = SpurGear.Outline((int)p.Number("teeth", 36), Pitch(p), Pressure(p), preview ? 4 : 8, p.Number("backlash", 0));
            var loops = new List<List<double[]>> { outline };
            var circles = new List<double[]>();
            AddBore((int)p.Number("bore", 0), loops, circles);
            double width = p.Number("width", 0.5);
            // On the Front plane at the origin, like a part modeled from scratch.
            var gear = Extrude(application, loops, circles, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }, width);
            return Join(gear, Hub(application, p, width));
        }
    }

    /// <summary>A roller chain sprocket (#25 or #35), as an editable CAD Hub feature.</summary>
    internal sealed class SprocketTool : CadHubTool
    {
        internal override string Kind { get { return "sprocket"; } }
        internal override string Title { get { return "Sprocket"; } }
        internal override bool Cuts { get { return false; } }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            var fields = new List<ToolField>
            {
                new ToolField { Key = "chain", Label = "Chain", Kind = FieldKind.Choice, Items = Powertrain.Chains.Select(c => c.Name).ToList(), DefaultItem = 1 },
                new ToolField { Key = "teeth", Label = "Teeth", Kind = FieldKind.Number, Min = 8, Max = 120, Default = 22 },
                new ToolField { Key = "bore", Label = "Bore", Kind = FieldKind.Choice, Items = Bores.Names.ToList() },
                new ToolField { Key = "width", Label = "Thickness (0: standard for the chain)", Kind = FieldKind.Length, Min = 0, Max = 1, Default = 0, Step = 0.0625, Advanced = true,
                    Tip = "Single-strand tooth width: 0.110\" for #25, 0.168\" for #35." },
            };
            fields.AddRange(HubFields());
            return fields;
        }

        private static Powertrain.Chain Chain(FeatureParams p) { return Powertrain.Chains[Math.Max(0, Math.Min(Powertrain.Chains.Count - 1, (int)p.Number("chain", 1)))]; }
        private static double Width(FeatureParams p) { double w = p.Number("width", 0); return w > 0 ? w : Chain(p).Thickness; }

        internal override string Problem(FeatureParams p) { return Powertrain.SprocketProblem((int)p.Number("teeth", 22)) ?? HubProblem(p); }

        internal override string Result(FeatureParams p, TeamStandards standards)
        {
            var chain = Chain(p);
            int teeth = (int)p.Number("teeth", 22);
            return "Pitch diameter " + StockParts.Inches(Math.Round(Powertrain.SprocketPitchDiameter(chain, teeth), 3)) + " in · outside " +
                StockParts.Inches(Math.Round(Powertrain.SprocketOutsideDiameter(chain, teeth), 3)) + " in · " + StockParts.Inches(Width(p)) + " in thick. Chain length: Belt and Chain.";
        }

        internal override string PartName(FeatureParams p)
        {
            return "Sprocket " + Chain(p).Name.Replace(" chain", "") + " " + (int)p.Number("teeth", 22) + "T " +
                Bores.FileNames[Math.Max(0, Math.Min(Bores.FileNames.Length - 1, (int)p.Number("bore", 0)))] + " " + Code(p);
        }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview)
        {
            var loops = new List<List<double[]>> { Powertrain.SprocketOutline(Chain(p), (int)p.Number("teeth", 22)) };
            var circles = new List<double[]>();
            AddBore((int)p.Number("bore", 0), loops, circles);
            double width = Width(p);
            var sprocket = Extrude(application, loops, circles, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }, width);
            return Join(sprocket, Hub(application, p, width));
        }
    }

    /// <summary>A timing pulley (HTD 5 mm or GT2 3 mm) with optional flanges, as an editable CAD Hub feature.</summary>
    internal sealed class PulleyTool : CadHubTool
    {
        internal override string Kind { get { return "pulley"; } }
        internal override string Title { get { return "Timing Pulley"; } }
        internal override bool Cuts { get { return false; } }
        internal override string Folder { get { return "Pulleys"; } }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            var fields = new List<ToolField>
            {
                new ToolField { Key = "belt", Label = "Belt", Kind = FieldKind.Choice, Items = Powertrain.Belts.Select(b => b.Name).ToList() },
                new ToolField { Key = "teeth", Label = "Teeth", Kind = FieldKind.Number, Min = 10, Max = 120, Default = 24 },
                new ToolField { Key = "beltWidth", Label = "Belt width (0: 9 mm HTD, 6 mm GT2)", Kind = FieldKind.Length, Min = 0, Max = 2, Default = 0, Step = 0.0625 },
                new ToolField { Key = "flanges", Label = "Flanges", Kind = FieldKind.Choice, Items = new List<string> { "Both sides", "None" } },
                new ToolField { Key = "bore", Label = "Bore", Kind = FieldKind.Choice, Items = Bores.Names.ToList() },
                new ToolField { Key = "flangeHeight", Label = "Flange height above the teeth", Kind = FieldKind.Length, Min = 0.02, Max = 0.5, Default = 0.08, Step = 0.01, Advanced = true },
                new ToolField { Key = "flangeThickness", Label = "Flange thickness", Kind = FieldKind.Length, Min = 0.02, Max = 0.5, Default = 0.0625, Step = 0.0625, Advanced = true },
            };
            fields.AddRange(HubFields());
            return fields;
        }

        private static Powertrain.Belt Belt(FeatureParams p) { return Powertrain.Belts[Math.Max(0, Math.Min(Powertrain.Belts.Count - 1, (int)p.Number("belt", 0)))]; }
        // The toothed width: the belt plus 1 mm, so it runs free between the flanges.
        private static double ToothWidth(FeatureParams p) { double w = p.Number("beltWidth", 0); return (w > 0 ? w : Belt(p).Width) + 1 / 25.4; }
        private static bool Flanged(FeatureParams p) { return p.Number("flanges", 0) == 0; }

        internal override string Problem(FeatureParams p) { return Powertrain.PulleyProblem(Belt(p), (int)p.Number("teeth", 24)) ?? HubProblem(p); }

        internal override string Result(FeatureParams p, TeamStandards standards)
        {
            var belt = Belt(p);
            int teeth = (int)p.Number("teeth", 24);
            double total = ToothWidth(p) + (Flanged(p) ? 2 * p.Number("flangeThickness", 0.0625) : 0);
            return "Pitch diameter " + StockParts.Inches(Math.Round(Powertrain.PulleyPitchDiameter(belt, teeth), 3)) + " in · outside " +
                StockParts.Inches(Math.Round(Powertrain.PulleyOutsideDiameter(belt, teeth), 3)) + " in · " + StockParts.Inches(Math.Round(total, 3)) +
                " in wide. Printed-pulley tooth shape.";
        }

        internal override string PartName(FeatureParams p)
        {
            return "Pulley " + Belt(p).Name.Replace(" belt", "").Replace(" ", "") + " " + (int)p.Number("teeth", 24) + "T " +
                Bores.FileNames[Math.Max(0, Math.Min(Bores.FileNames.Length - 1, (int)p.Number("bore", 0)))] + " " + Code(p);
        }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview)
        {
            var belt = Belt(p);
            int teeth = (int)p.Number("teeth", 24);
            int bore = (int)p.Number("bore", 0);
            double flange = Flanged(p) ? p.Number("flangeThickness", 0.0625) : 0, width = ToothWidth(p);
            Func<List<double[]>, List<double[]>, double, double, Body2> slab = (outline, circle, z, depth) =>
            {
                var loops = new List<List<double[]>>();
                var circles = new List<double[]>();
                if (outline != null) loops.Add(outline); else circles.Add(circle[0]);
                AddBore(bore, loops, circles);
                return Extrude(application, loops, circles, new[] { 0.0, 0, z * M }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }, depth);
            };
            var body = slab(Powertrain.PulleyOutline(belt, teeth), null, flange, width);
            if (flange > 0)
            {
                var disc = new List<double[]> { new[] { 0, 0, Powertrain.PulleyOutsideDiameter(belt, teeth) / 2 + p.Number("flangeHeight", 0.08) } };
                body = Join(body, slab(null, disc, 0, flange));
                body = Join(body, slab(null, disc, flange + width, flange));
            }
            return Join(body, Hub(application, p, 2 * flange + width));
        }
    }

    /// <summary>A hex or round shaft, plain or with turned ends, as an editable CAD Hub feature.</summary>
    internal sealed class ShaftTool : CadHubTool
    {
        internal override string Kind { get { return "shaft"; } }
        internal override string Title { get { return "Shaft"; } }
        internal override bool Cuts { get { return false; } }

        private static readonly string[] Types = { "1/2\" hex", "3/8\" hex", "1/2\" round", "3/8\" round", "8 mm round" };
        private static readonly double[] Sizes = { 0.5, 0.375, 0.5, 0.375, 8 / 25.4 };

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "type", Label = "Shaft", Kind = FieldKind.Choice, Items = Types.ToList() },
                new ToolField { Key = "length", Label = "Length", Kind = FieldKind.Length, Min = 0.25, Max = 72, Default = 6, Step = 0.25 },
                new ToolField { Key = "ends", Label = "Ends", Kind = FieldKind.Choice, Items = new List<string> { "Plain", "Turned, both ends", "Turned, one end" } },
                new ToolField { Key = "turned", Label = "Turned diameter", Kind = FieldKind.Length, Min = 0.1, Max = 2, Default = 0.5, Step = 0.0625,
                    Tip = "For a 1/2\" hex shaft in round 1/2\" bearings: 0.5." },
                new ToolField { Key = "turnedLength", Label = "Turned length", Kind = FieldKind.Length, Min = 0.05, Max = 6, Default = 0.5, Step = 0.125 },
            };
        }

        private static int Type(FeatureParams p) { return Math.Max(0, Math.Min(Types.Length - 1, (int)p.Number("type", 0))); }
        private static bool Hex(FeatureParams p) { return Type(p) <= 1; }
        private static int Ends(FeatureParams p) { return (int)p.Number("ends", 0); }

        internal override string Problem(FeatureParams p)
        {
            if (Ends(p) == 0) return null;
            double size = Sizes[Type(p)], corners = Hex(p) ? size / Math.Cos(Math.PI / 6) : size;
            if (p.Number("turned", 0.5) > corners + 1e-9) return "The turned diameter can't be bigger than the shaft (" + StockParts.Inches(Math.Round(corners, 3)) + " in across).";
            if (p.Number("turnedLength", 0.5) * (Ends(p) == 1 ? 2 : 1) >= p.Number("length", 6)) return "The turned ends are longer than the shaft.";
            return null;
        }

        internal override string Result(FeatureParams p, TeamStandards standards)
        {
            return Types[Type(p)] + " × " + StockParts.Inches(p.Number("length", 6)) + " in" +
                (Ends(p) == 0 ? "" : ", turned to " + StockParts.Inches(p.Number("turned", 0.5)) + " for " + StockParts.Inches(p.Number("turnedLength", 0.5)) + " in at " + (Ends(p) == 1 ? "both ends" : "one end")) + ".";
        }

        internal override string PartName(FeatureParams p)
        {
            return (Hex(p) ? "Hex" : "Round") + " Shaft " + StockParts.Inches(Sizes[Type(p)]) + " x " + StockParts.Inches(p.Number("length", 6)) + (Ends(p) == 0 ? "" : " Turned") + " " + Code(p);
        }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview)
        {
            double size = Sizes[Type(p)], length = p.Number("length", 6), turned = p.Number("turned", 0.5), turnedLength = p.Number("turnedLength", 0.5);
            int ends = Ends(p);
            double start = ends == 0 ? 0 : turnedLength, end = ends == 1 ? length - turnedLength : length;
            Func<double, double, bool, double, Body2> piece = (z, depth, full, diameter) =>
            {
                var loops = new List<List<double[]>>();
                var circles = new List<double[]>();
                if (full && Hex(p)) loops.Add(Hexagon(size)); else circles.Add(new[] { 0, 0, (full ? size : diameter) / 2 });
                return Extrude(application, loops, circles, new[] { 0.0, 0, z * M }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }, depth);
            };
            // Along the Z axis from the origin.
            var shaft = piece(start, end - start, true, size);
            if (ends != 0) shaft = Join(piece(0, turnedLength, false, turned), shaft);
            if (ends == 1) shaft = Join(shaft, piece(length - turnedLength, turnedLength, false, turned));
            return shaft;
        }
    }

    /// <summary>A planetary gearset: sun, planets and ring as separate bodies, checked to mesh.</summary>
    internal sealed class PlanetaryTool : CadHubTool
    {
        internal override string Kind { get { return "planetary"; } }
        internal override string Title { get { return "Planetary Gearset"; } }
        internal override bool Cuts { get { return false; } }
        internal override string Folder { get { return "Gears"; } }

        private static readonly string[] Pitches = { "20 DP", "32 DP", "10 DP" };
        private static readonly double[] PitchValues = { 20, 32, 10 };

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "sun", Label = "Sun teeth", Kind = FieldKind.Number, Min = 8, Max = 100, Default = 12 },
                new ToolField { Key = "planet", Label = "Planet teeth", Kind = FieldKind.Number, Min = 8, Max = 100, Default = 18 },
                new ToolField { Key = "planets", Label = "Planets", Kind = FieldKind.Number, Min = 2, Max = 8, Default = 3 },
                new ToolField { Key = "pitch", Label = "Diametral pitch", Kind = FieldKind.Choice, Items = Pitches.ToList() },
                new ToolField { Key = "width", Label = "Thickness", Kind = FieldKind.Length, Min = 0.05, Max = 4, Default = 0.375, Step = 0.125 },
                new ToolField { Key = "bore", Label = "Sun bore", Kind = FieldKind.Choice, Items = Bores.Names.ToList() },
                new ToolField { Key = "planetBore", Label = "Planet bore", Kind = FieldKind.Choice, Items = Bores.Names.ToList(), DefaultItem = 3 },
                new ToolField { Key = "rim", Label = "Ring wall outside the teeth", Kind = FieldKind.Length, Min = 0.05, Max = 2, Default = 0.25, Step = 0.0625, Advanced = true },
                new ToolField { Key = "backlash", Label = "Backlash", Kind = FieldKind.Length, Min = 0, Max = 0.03, Default = 0.004, Step = 0.001, Advanced = true },
            };
        }

        private static double Pitch(FeatureParams p) { return PitchValues[Math.Max(0, Math.Min(PitchValues.Length - 1, (int)p.Number("pitch", 0)))]; }
        private static int Sun(FeatureParams p) { return (int)p.Number("sun", 12); }
        private static int Planet(FeatureParams p) { return (int)p.Number("planet", 18); }
        private static int Ring(FeatureParams p) { return Sun(p) + 2 * Planet(p); }
        private static int Count(FeatureParams p) { return (int)p.Number("planets", 3); }

        internal override string Problem(FeatureParams p) { return Powertrain.PlanetaryProblem(Sun(p), Planet(p), Ring(p), Count(p)); }

        internal override string Result(FeatureParams p, TeamStandards standards)
        {
            double dp = Pitch(p);
            return "Ring " + Ring(p) + " teeth · " + StockParts.Inches(Math.Round(Powertrain.PlanetaryRatio(Sun(p), Ring(p)), 3)) + ":1 (sun in, ring held, carrier out) · planets " +
                StockParts.Inches(Math.Round((Sun(p) + Planet(p)) / (2 * dp), 4)) + " in from the center · ring outside " +
                StockParts.Inches(Math.Round((Ring(p) + 2.5) / dp + 2 * p.Number("rim", 0.25), 3)) + " in.";
        }

        internal override string PartName(FeatureParams p)
        {
            return "Planetary " + Sun(p) + "-" + Planet(p) + "-" + Ring(p) + " x" + Count(p) + " " + StockParts.Inches(Pitch(p)) + "DP " + Code(p);
        }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview) { return BuildBodies(application, p, preview).FirstOrDefault(); }

        internal override List<Body2> BuildBodies(SldWorks application, FeatureParams p, bool preview)
        {
            double dp = Pitch(p), width = p.Number("width", 0.375), backlash = p.Number("backlash", 0.004);
            int sun = Sun(p), planet = Planet(p), ring = Ring(p), detail = preview ? 4 : 8;
            var bodies = new List<Body2>();
            Func<List<double[]>, int, Body2> gear = (outline, bore) =>
            {
                var loops = new List<List<double[]>> { outline };
                var circles = new List<double[]>();
                AddBore(bore, loops, circles);
                return Extrude(application, loops, circles, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }, width);
            };
            bodies.Add(gear(SpurGear.Outline(sun, dp, 20, detail, backlash), (int)p.Number("bore", 0)));
            int planetBore = (int)p.Number("planetBore", 3);
            foreach (var at in Powertrain.Planets(sun, planet, Count(p), dp))
            {
                var loops = new List<List<double[]>> { Place(SpurGear.Outline(planet, dp, 20, detail, backlash), at[2], at[0], at[1]) };
                var circles = new List<double[]>();
                var bore = new List<List<double[]>>();
                var boreCircles = new List<double[]>();
                AddBore(planetBore, bore, boreCircles);
                loops.AddRange(bore.Select(l => Place(l, at[2], at[0], at[1])));
                circles.AddRange(boreCircles.Select(c => new[] { c[0] + at[0], c[1] + at[1], c[2] }));
                bodies.Add(Extrude(application, loops, circles, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }, width));
            }
            // The ring: its teeth point inward; the outline of its tooth spaces is an outward gear with addendum and dedendum swapped.
            double turn = Powertrain.RingTurn(sun, planet, ring, Count(p));
            var cavity = Place(SpurGear.Outline(ring, dp, 20, detail, -backlash, 1.25, 1), turn, 0, 0);
            double outside = ring / dp / 2 + 1.25 / dp + p.Number("rim", 0.25);
            bodies.Add(Extrude(application, new List<List<double[]>> { cavity }, new List<double[]> { new[] { 0, 0, outside } },
                new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }, width));
            return bodies;
        }

        private static List<double[]> Place(List<double[]> points, double turn, double x, double y)
        {
            double c = Math.Cos(turn), s = Math.Sin(turn);
            return points.Select(q => new[] { q[0] * c - q[1] * s + x, q[0] * s + q[1] * c + y }).ToList();
        }
    }

    /// <summary>The gear ratio calculator: motor, up to three stages, and a wheel; nothing is made.</summary>
    internal sealed class GearRatioTool : CadHubTool
    {
        internal override string Kind { get { return "ratio"; } }
        internal override string Title { get { return "Gear Ratio"; } }
        internal override bool Cuts { get { return false; } }
        internal override bool MakesFeature { get { return false; } }
        internal override bool AnyDocument { get { return true; } }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            var fields = new List<ToolField> { new ToolField { Key = "motor", Label = "Motor", Kind = FieldKind.Choice, Items = Powertrain.Motors.Select(m => m.Name + " (" + m.FreeSpeed + " rpm)").ToList() } };
            for (int stage = 1; stage <= 3; stage++)
            {
                fields.Add(new ToolField { Key = "in" + stage, Label = "Stage " + stage + ": driving teeth" + (stage > 1 ? " (0: none)" : ""), Kind = FieldKind.Number, Min = 0, Max = 200, Default = stage == 1 ? 12 : 0 });
                fields.Add(new ToolField { Key = "out" + stage, Label = "Stage " + stage + ": driven teeth", Kind = FieldKind.Number, Min = 0, Max = 200, Default = stage == 1 ? 60 : 0 });
            }
            fields.Add(new ToolField { Key = "wheel", Label = "Wheel diameter (0: no wheel)", Kind = FieldKind.Length, Min = 0, Max = 12, Default = 4, Step = 0.25 });
            return fields;
        }

        internal override string Result(FeatureParams p, TeamStandards standards)
        {
            var motor = Powertrain.Motors[Math.Max(0, Math.Min(Powertrain.Motors.Count - 1, (int)p.Number("motor", 0)))];
            var stages = Enumerable.Range(1, 3).Select(s => new[] { (int)p.Number("in" + s, 0), (int)p.Number("out" + s, 0) }).ToList();
            double ratio = Powertrain.Ratio(stages), wheel = p.Number("wheel", 0);
            string text = "Ratio " + Math.Round(ratio, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":1 → " + Math.Round(motor.FreeSpeed / ratio) + " rpm free speed";
            if (wheel > 0) text += " · " + Math.Round(Powertrain.WheelSpeed(motor.FreeSpeed, ratio, wheel), 1).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " ft/s with a " + StockParts.Inches(wheel) + " in wheel (free speed; about 80% under load)";
            return text + ".";
        }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview) { return null; }
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

    /// <summary>Lighten Plate on the PropertyManager: pick the plate's face, see how many pockets, OK cuts them.</summary>
    internal sealed class LightenTool : CadHubTool
    {
        internal override string Kind { get { return "lighten"; } }
        internal override string Title { get { return "Lighten Plate"; } }
        internal override bool Cuts { get { return true; } }
        internal override bool NeedsSelection { get { return true; } }
        internal override bool MakesFeature { get { return false; } }
        internal override int[] SelectionFilters { get { return new[] { (int)swSelectType_e.swSelFACES }; } }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "face", Label = "Plate face: click the flat face to lighten", Kind = FieldKind.Selection },
                new ToolField { Key = "size", Label = "Pocket size", Kind = FieldKind.Length, Min = 0.75, Max = 12, Default = 2, Step = 0.25,
                    Tip = "About how big across each pocket gets. Smaller: more ribs, stronger and heavier." },
                new ToolField { Key = "rib", Label = "Rib width", Kind = FieldKind.Length, Min = 0.05, Max = 1, Default = 0.15, Step = 0.025 },
                new ToolField { Key = "border", Label = "Edge border", Kind = FieldKind.Length, Min = 0.05, Max = 2, Default = 0.25, Step = 0.025 },
                new ToolField { Key = "ring", Label = "Ring around holes", Kind = FieldKind.Length, Min = 0.03, Max = 1, Default = 0.15, Step = 0.025 },
                new ToolField { Key = "corner", Label = "Corner radius (router bit)", Kind = FieldKind.Length, Min = 0.01, Max = 0.5, Default = 0.0625, Step = 0.0625, Advanced = true },
                new ToolField { Key = "smallest", Label = "Smallest pocket", Kind = FieldKind.Length, Min = 0.1, Max = 2, Default = 0.35, Step = 0.05, Advanced = true },
                new ToolField { Key = "depth", Label = "Pocket depth (0: through)", Kind = FieldKind.Length, Min = 0, Max = 4, Default = 0, Step = 0.0625, Advanced = true },
            };
        }

        internal static LightenSettings Settings(FeatureParams p)
        {
            return new LightenSettings { MaxPocket = p.Number("size", 2), Rib = p.Number("rib", 0.15), Border = p.Number("border", 0.25), Ring = p.Number("ring", 0.15),
                CornerRadius = p.Number("corner", 0.0625), MinPocket = p.Number("smallest", 0.35) };
        }

        // Kept light: the pockets are worked out once, on ✓ (the flash then says how many and the weight saved).
        internal override string Result(FeatureParams p, TeamStandards standards, object selection)
        {
            return selection is Face2 ? "Ready: ✓ cuts the pockets (about " + StockParts.Inches(p.Number("size", 2)) + " in across)." : "Click the plate's flat face.";
        }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview) { return null; }

        internal override string Apply(SldWorks application, ModelDoc2 doc, FeatureParams p, object selection, TeamStandards standards)
        {
            return Addin.CutPockets(application, doc, selection as Face2, Settings(p), p.Number("depth", 0));
        }
    }

    /// <summary>Belt and Chain on the PropertyManager: the center distance as you change the numbers; OK can set a dimension.</summary>
    internal sealed class BeltChainTool : CadHubTool
    {
        internal override string Kind { get { return "belt-chain"; } }
        internal override string Title { get { return "Belt and Chain"; } }
        internal override bool Cuts { get { return false; } }
        internal override bool NeedsSelection { get { return true; } }
        internal override bool SelectionOptional { get { return true; } }
        internal override bool MakesFeature { get { return false; } }
        internal override bool AnyDocument { get { return true; } }
        internal override int[] SelectionFilters
        {
            get { return new[] { (int)swSelectType_e.swSelDIMENSIONS, (int)swSelectType_e.swSelFACES, (int)swSelectType_e.swSelDATUMPLANES }; }
        }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "kind", Label = "Belt or chain", Kind = FieldKind.Choice, Items = BeltChain.Kinds.Select(k => k.Name).ToList() },
                new ToolField { Key = "teeth1", Label = "Pulley/sprocket 1 (teeth)", Kind = FieldKind.Number, Min = 8, Max = 120, Default = 18 },
                new ToolField { Key = "teeth2", Label = "Pulley/sprocket 2 (teeth)", Kind = FieldKind.Number, Min = 8, Max = 120, Default = 36 },
                new ToolField { Key = "length", Label = "Belt length (teeth) or chain length (links)", Kind = FieldKind.Number, Min = 10, Max = 2000, Default = 100 },
                new ToolField { Key = "wanted", Label = "Or: the center distance you want (0: off)", Kind = FieldKind.Length, Min = 0, Max = 100, Default = 0, Step = 0.25 },
                new ToolField { Key = "dimension", Label = "Optional, on ✓: click a center-distance dimension to set it, or a flat face or plane to draw the layout sketch there", Kind = FieldKind.Selection },
            };
        }

        private static double Center(FeatureParams p)
        {
            var drive = BeltChain.Kinds[Math.Max(0, Math.Min(BeltChain.Kinds.Count - 1, (int)p.Number("kind", 0)))];
            return BeltChain.CenterDistance(drive, (int)p.Number("teeth1", 18), (int)p.Number("teeth2", 36), (int)p.Number("length", 100));
        }

        internal override string Result(FeatureParams p, TeamStandards standards)
        {
            var drive = BeltChain.Kinds[Math.Max(0, Math.Min(BeltChain.Kinds.Count - 1, (int)p.Number("kind", 0)))];
            int a = (int)p.Number("teeth1", 18), b = (int)p.Number("teeth2", 36);
            double wanted = p.Number("wanted", 0);
            string answer;
            if (wanted > 0)
            {
                var near = BeltChain.NearestLengths(drive, a, b, wanted);
                answer = "For " + StockParts.Inches(wanted) + " in: " + near.Item1 + " " + drive.Unit + " (center " + Show(BeltChain.CenterDistance(drive, a, b, near.Item1)) +
                    ") or " + near.Item2 + " " + drive.Unit + " (center " + Show(BeltChain.CenterDistance(drive, a, b, near.Item2)) + "). ";
            }
            else answer = "";
            double center = Center(p);
            return answer + (double.IsNaN(center) ? "Belt/chain too short to go around both." :
                "Center distance: " + Show(center) + " in (" + (center * 25.4).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " mm).");
        }

        private static string Show(double inches) { return double.IsNaN(inches) ? "—" : inches.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture); }

        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview) { return null; }

        internal override string Apply(SldWorks application, ModelDoc2 doc, FeatureParams p, object selection, TeamStandards standards)
        {
            var dimension = selection as DisplayDimension;
            double center = Center(p);
            if (selection == null || double.IsNaN(center)) return null;
            if (dimension == null)
            {
                var drive = BeltChain.Kinds[Math.Max(0, Math.Min(BeltChain.Kinds.Count - 1, (int)p.Number("kind", 0)))];
                int a = (int)p.Number("teeth1", 18), b = (int)p.Number("teeth2", 36);
                return Addin.DrawBeltLayout(doc, selection, BeltChain.PitchDiameter(drive, a), BeltChain.PitchDiameter(drive, b), center,
                    drive.Name.Replace(" belt", "").Replace(" chain", "") + " " + a + "T-" + b + "T " + (int)p.Number("length", 100) + (drive.Chain ? "L" : "T") + " layout");
            }
            dimension.GetDimension2(0).SetSystemValue3(center * M, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
            doc.EditRebuild3();
            return "✓ Set the dimension to " + Show(center) + " in.";
        }
    }
}
