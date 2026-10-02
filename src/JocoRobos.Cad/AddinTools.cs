using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace JocoRobos.Cad
{
    /// <summary>
    /// CAD Hub's modeling tools: Lighten Plate and the belt and chain calculator (and rebuilding CAD Hub gear and bearing hole
    /// features made by earlier versions). The math lives in PlateLighten and BeltChain (tested without SOLIDWORKS).
    /// </summary>
    public sealed partial class Addin
    {
        private const double Meters = 0.0254; // SOLIDWORKS' API works in meters; CAD Hub's tools in inches.

        private static Feature Extrude(ModelDoc2 doc, double inches, bool cut = false)
        {
            doc.SketchManager.InsertSketch(true);
            var feature = cut
                ? doc.FeatureManager.FeatureCut4(true, false, false, (int)swEndConditions_e.swEndCondBlind, 0, inches * Meters, 0, false, false, false, false, 0, 0,
                    false, false, false, false, false, true, true, true, true, false, (int)swStartConditions_e.swStartSketchPlane, 0, false, false)
                : doc.FeatureManager.FeatureExtrusion3(true, false, false, (int)swEndConditions_e.swEndCondBlind, 0, inches * Meters, 0, false, false, false, false, 0, 0,
                    false, false, false, false, true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
            if (feature == null) throw new InvalidOperationException("SOLIDWORKS couldn't make the " + (cut ? "cut" : "extrusion") + ".");
            return feature;
        }

        private static Feature CutThroughBoth(ModelDoc2 doc)
        {
            doc.SketchManager.InsertSketch(true);
            var feature = doc.FeatureManager.FeatureCut4(false, false, false, (int)swEndConditions_e.swEndCondThroughAll, (int)swEndConditions_e.swEndCondThroughAll,
                0, 0, false, false, false, false, 0, 0, false, false, false, false, false, true, true, true, true, false, (int)swStartConditions_e.swStartSketchPlane, 0, false, false);
            if (feature == null) throw new InvalidOperationException("SOLIDWORKS couldn't cut the holes.");
            return feature;
        }

        // ---------- the PropertyManager tools (framework in ToolPage and CadHubTools) ----------

        // The add-in that's running, for CAD Hub features' Edit Feature (SOLIDWORKS creates those objects itself).
        internal static Addin Instance;

        private TeamStandards Standards { get { return paneCatalog?.Standards ?? TeamStandards.Defaults; } }

        internal static void EditFeature(SldWorks app, ModelDoc2 doc, Feature feature, CadHubTool tool, FeatureParams values)
        {
            var addin = Instance;
            Action<string> report = text => { if (addin != null) addin.ShowFlash(text); };
            try { new ToolPage(app, doc, tool, addin?.Standards ?? TeamStandards.Defaults, values, feature, report).Show(); }
            catch (InvalidOperationException busy) { report("✗ " + busy.Message); }
        }

        // Opens a tool's page in the PropertyManager (the left side panel): Lighten Plate on the open part, Belt and Chain on any
        // document. A read-only team part needs Edit first.
        private void ShowTool(CadHubTool tool)
        {
            try
            {
                var doc = application.ActiveDoc as ModelDoc2;
                if (doc == null && tool.AnyDocument)
                {
                    // Nothing open to show a side panel in: the calculator in its own window.
                    using (var dialog = new BeltChainDialog(SetSelectedDimension)) dialog.ShowDialog(new SolidWorksWindow());
                    return;
                }
                if (doc == null) throw new InvalidOperationException("Open the part first, then " + tool.Title + ".");
                if (!tool.AnyDocument)
                {
                    if (doc.GetType() != (int)swDocumentTypes_e.swDocPART)
                        throw new InvalidOperationException(tool.Title + " works in a part. Open the part (right-click it → Open Part), then try again.");
                    if (doc.IsOpenedReadOnly()) throw new InvalidOperationException("Click Edit on " + doc.GetTitle() + " first, so it can be changed.");
                }
                new ToolPage(application, doc, tool, Standards, null, null, text => ShowFlash(text)).Show();
            }
            catch (Exception exception)
            {
                ErrorLog.Write(tool.Title, exception);
                Message(exception.Message, MessageBoxIcon.Warning);
            }
        }

        // ---------- belt and chain calculator ----------

        public void BeltChainCalculator() { ShowTool(CadHubTool.Find("belt-chain")); }

        // "Use for the selected dimension": the calculator's center distance onto the dimension selected in SOLIDWORKS.
        private string SetSelectedDimension(double inches)
        {
            var doc = application.ActiveDoc as ModelDoc2;
            var shown = (doc?.SelectionManager as SelectionMgr)?.GetSelectedObject6(1, -1) as DisplayDimension;
            if (shown == null) return "Select the center-distance dimension in SOLIDWORKS first (click it once), then try again.";
            shown.GetDimension2(0).SetSystemValue3(inches * Meters, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
            doc.EditRebuild3();
            return null;
        }

        // ---------- lighten plate ----------

        public void LightenPlate() { ShowTool(CadHubTool.Find("lighten")); }

        private static void ReadPlate(Face2 face, out double[] frame, out List<double[]> outline, out List<Circle2> holes, out List<List<double[]>> cutouts)
        {
            var surface = face?.GetSurface() as Surface;
            if (surface == null || !surface.IsPlane()) throw new InvalidOperationException("Click the plate's flat face.");
            frame = PlaneFrame(surface, face);
            outline = new List<double[]>();
            holes = new List<Circle2>();
            cutouts = new List<List<double[]>>();
            ReadFace(face, frame, outline, holes, cutouts);
            if (outline.Count < 3) throw new InvalidOperationException("Couldn't read the outline of that face.");
        }

        internal static string CutPockets(SldWorks app, ModelDoc2 doc, Face2 face, LightenSettings settings, double depth)
        {
            double[] frame;
            List<double[]> outline;
            List<Circle2> holes;
            List<List<double[]>> cutouts;
            ReadPlate(face, out frame, out outline, out holes, out cutouts);
            var plan = PlateLighten.Plan(outline, holes, null, settings, cutouts);
            if (plan.Pockets.Count == 0)
                throw new InvalidOperationException("No pockets fit with these settings. Try narrower ribs, a smaller border or ring, or a smaller smallest pocket.");
            double before = doc.Extension.CreateMassProperty().Mass;
            doc.ClearSelection2(true);
            ((Entity)face).Select4(false, null);
            doc.SketchManager.InsertSketch(true);
            var sketch = doc.SketchManager.ActiveSketch;
            if (sketch == null) throw new InvalidOperationException("SOLIDWORKS didn't start a sketch on that face. Try again.");
            var toSketch = sketch.ModelToSketchTransform;
            var math = app.GetMathUtility() as MathUtility;
            Func<double, double, double[]> map = (x, y) =>
            {
                var model = new[] { frame[0] + x * frame[3] + y * frame[6], frame[1] + x * frame[4] + y * frame[7], frame[2] + x * frame[5] + y * frame[8] };
                var point = ((MathPoint)math.CreatePoint(model)).MultiplyTransform(toSketch) as MathPoint;
                return (double[])point.ArrayData;
            };
            // The sketch's axes may be mirrored relative to the plane frame: then counterclockwise arcs become clockwise.
            var o = map(0, 0); var ux = map(Meters, 0); var vy = map(0, Meters);
            short direction = (short)(((ux[0] - o[0]) * (vy[1] - o[1]) - (ux[1] - o[1]) * (vy[0] - o[0])) >= 0 ? 1 : -1);
            doc.SketchManager.AddToDB = true;
            doc.SketchManager.DisplayWhenAdded = false;
            try
            {
                foreach (var pocket in plan.Pockets)
                    foreach (var segment in pocket.Segments)
                    {
                        var a = map(segment.X1 * Meters, segment.Y1 * Meters);
                        var b = map(segment.X2 * Meters, segment.Y2 * Meters);
                        if (!segment.Arc) doc.SketchManager.CreateLine(a[0], a[1], 0, b[0], b[1], 0);
                        else
                        {
                            var c = map(segment.Cx * Meters, segment.Cy * Meters);
                            doc.SketchManager.CreateArc(c[0], c[1], 0, a[0], a[1], 0, b[0], b[1], 0, (short)(segment.Clockwise ? -direction : direction));
                        }
                    }
            }
            finally
            {
                doc.SketchManager.AddToDB = false;
                doc.SketchManager.DisplayWhenAdded = true;
            }
            if (depth <= 0) CutThroughBoth(doc); else Extrude(doc, depth, true);
            double after = doc.Extension.CreateMassProperty().Mass;
            double saved = (before - after) * 2.20462;
            return "✓ Lightened " + doc.GetTitle() + ": " + plan.Count + " pockets, −" + saved.ToString("0.00") + " lb (" +
                (before > 0 ? (100 * (before - after) / before).ToString("0") : "?") + "%). It's an ordinary cut: edit or delete it like any feature.";
        }

        // The face's plane as origin (0..2), u axis (3..5), v axis (6..8), in meters: u along its longest straight edge.
        private static double[] PlaneFrame(Surface surface, Face2 face)
        {
            var plane = (double[])surface.PlaneParams; // normal x,y,z then a point x,y,z
            double[] normal = { plane[0], plane[1], plane[2] }, origin = { plane[3], plane[4], plane[5] };
            double[] u = null;
            double longest = 0;
            foreach (Edge edge in ((object[])face.GetEdges() ?? new object[0]).OfType<Edge>())
            {
                var start = (double[])((Vertex)edge.GetStartVertex())?.GetPoint();
                var end = (double[])((Vertex)edge.GetEndVertex())?.GetPoint();
                if (start == null || end == null) continue;
                double dx = end[0] - start[0], dy = end[1] - start[1], dz = end[2] - start[2], length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length > longest) { longest = length; u = new[] { dx / length, dy / length, dz / length }; }
            }
            if (u == null) u = Math.Abs(normal[0]) < 0.9 ? Normalize(Cross(normal, new[] { 1.0, 0, 0 })) : Normalize(Cross(normal, new[] { 0.0, 1, 0 }));
            var v = Normalize(Cross(normal, u));
            return new[] { origin[0], origin[1], origin[2], u[0], u[1], u[2], v[0], v[1], v[2] };
        }

        // The face's outline (as points in inches, in the frame), its round holes, and any other cutouts (slots, shapes) as outlines.
        private static void ReadFace(Face2 face, double[] frame, List<double[]> outline, List<Circle2> holes, List<List<double[]>> cutouts = null)
        {
            Func<double[], double[]> flat = p =>
            {
                double x = p[0] - frame[0], y = p[1] - frame[1], z = p[2] - frame[2];
                return new[] { (x * frame[3] + y * frame[4] + z * frame[5]) / Meters, (x * frame[6] + y * frame[7] + z * frame[8]) / Meters };
            };
            foreach (Loop2 loop in ((object[])face.GetLoops() ?? new object[0]).OfType<Loop2>())
            {
                var edges = ((object[])loop.GetEdges() ?? new object[0]).OfType<Edge>().ToList();
                if (!loop.IsOuter() && edges.Count == 1 && ((Curve)edges[0].GetCurve()).IsCircle())
                {
                    var circle = (double[])((Curve)edges[0].GetCurve()).CircleParams; // center x,y,z, axis x,y,z, radius
                    var center = flat(new[] { circle[0], circle[1], circle[2] });
                    holes.Add(new Circle2 { X = center[0], Y = center[1], R = circle[6] / Meters });
                    continue;
                }
                var points = ChainEdges(edges).Select(flat).ToList();
                if (loop.IsOuter()) { outline.AddRange(points); continue; }
                if (points.Count >= 3 && cutouts != null) cutouts.Add(points);
            }
        }

        // A loop's edges as one chain of points: lines by their ends, curves sampled finely enough to follow arcs.
        private static List<double[]> ChainEdges(List<Edge> edges)
        {
            var pieces = new List<List<double[]>>();
            foreach (var edge in edges)
            {
                var start = (double[])((Vertex)edge.GetStartVertex())?.GetPoint();
                var end = (double[])((Vertex)edge.GetEndVertex())?.GetPoint();
                if (start == null || end == null) continue;
                var curve = (Curve)edge.GetCurve();
                var piece = new List<double[]>();
                if (curve.IsLine()) { piece.Add(start); piece.Add(end); }
                else
                {
                    var tess = curve.GetTessPts(0.00001, 0.002, start, end) as double[];
                    if (tess == null || tess.Length < 6) { piece.Add(start); piece.Add(end); }
                    else for (int i = 0; i + 2 < tess.Length; i += 3) piece.Add(new[] { tess[i], tess[i + 1], tess[i + 2] });
                }
                pieces.Add(piece);
            }
            var chain = new List<double[]>();
            while (pieces.Count > 0)
            {
                int index = 0;
                bool reverse = false;
                if (chain.Count > 0)
                {
                    var last = chain[chain.Count - 1];
                    double best = double.MaxValue;
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        double toStart = Gap(last, pieces[i][0]), toEnd = Gap(last, pieces[i][pieces[i].Count - 1]);
                        if (toStart < best) { best = toStart; index = i; reverse = false; }
                        if (toEnd < best) { best = toEnd; index = i; reverse = true; }
                    }
                }
                var next = pieces[index];
                pieces.RemoveAt(index);
                if (reverse) next.Reverse();
                chain.AddRange(chain.Count > 0 ? next.Skip(1) : next);
            }
            if (chain.Count > 1 && Gap(chain[0], chain[chain.Count - 1]) < 1e-7) chain.RemoveAt(chain.Count - 1);
            return chain;
        }

        private static double Gap(double[] a, double[] b) { return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2])); }
        private static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        private static double[] Normalize(double[] a) { double l = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); return new[] { a[0] / l, a[1] / l, a[2] / l }; }
    }
}
