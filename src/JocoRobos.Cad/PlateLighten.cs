using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace JocoRobos.Cad
{
    internal sealed class LightenSettings
    {
        internal double Rib = 0.15;          // width of the ribs between pockets
        internal double Border = 0.25;       // solid material kept along the plate's edge
        internal double Ring = 0.15;         // solid material kept around every hole, beyond its edge
        internal double CornerRadius = 0.0625; // the router bit's radius: pockets get rounded corners at least this big
        internal double MinPocket = 0.35;    // pockets narrower than this are left solid
        internal double MaxPocket = 3;     // pockets bigger across than about this are split by another rib junction
    }

    internal sealed class Circle2
    {
        internal double X, Y, R;
    }

    /// <summary>One piece of a pocket's outline: a line, or an arc around (Cx, Cy), counterclockwise unless Clockwise.</summary>
    internal sealed class PocketSegment
    {
        internal bool Arc, Clockwise;
        internal double X1, Y1, X2, Y2, Cx, Cy;
    }

    /// <summary>One closed loop of a pocket. A loop inside another (an island left around a hole) has negative Area.</summary>
    internal sealed class Pocket
    {
        internal readonly List<PocketSegment> Segments = new List<PocketSegment>();
        internal double Area;
    }

    internal sealed class LightenPlan
    {
        internal readonly List<Pocket> Pockets = new List<Pocket>();
        internal double PlateArea, RemovedArea;
        internal double Percent { get { return PlateArea <= 0 ? 0 : 100 * RemovedArea / PlateArea; } }
        internal int Count { get { return Pockets.Count(p => p.Area > 0); } }
    }

    /// <summary>
    /// "Lighten Plate", 2D, the way the Onshape lightening scripts do it: the area that may be cut is the plate shrunk by the
    /// border, minus a ring around every hole and cutout (true offsets, so it follows arcs and curved edges). Ribs join the holes,
    /// the plate's corners and points along its edge in triangles; what's left between the ribs are the pockets, with rounded
    /// corners, and anything narrower than the smallest pocket left solid. Pure geometry in the plate's plane, inches; the add-in
    /// reads the face and sketches the result as lines and arcs.
    /// </summary>
    internal static class PlateLighten
    {
        private const int Precision = 5;          // Clipper works to 0.00001"
        private const double ArcTolerance = 0.0002; // how closely offsets follow a curve
        private const double FitTolerance = 0.001;  // how far a sketched line or arc may stray from the computed outline
        private const double MinAngle = 22 * Math.PI / 180; // triangles sharper than this don't get their longest rib

        internal static LightenPlan Plan(IList<double[]> outline, IList<Circle2> holes, IList<double[]> extraPoints, LightenSettings settings,
            IList<List<double[]>> cutouts = null)
        {
            var plan = new LightenPlan();
            var border = outline.Select(p => new[] { p[0], p[1] }).ToList();
            if (SignedArea(border) < 0) border.Reverse();
            cutouts = cutouts ?? new List<List<double[]>>();
            plan.PlateArea = SignedArea(border) - holes.Sum(h => Math.PI * h.R * h.R) - cutouts.Sum(c => Math.Abs(SignedArea(c)));

            // Where pockets may go: inside the border, outside every hole's and cutout's ring.
            var keepOut = new PathsD();
            foreach (var h in holes) keepOut.Add(CirclePath(h.X, h.Y, h.R + settings.Ring));
            if (cutouts.Count > 0)
                keepOut.AddRange(Clipper.InflatePaths(new PathsD(cutouts.Select(Path)), settings.Ring, JoinType.Round, EndType.Polygon, 2, Precision, ArcTolerance));
            var region = Clipper.InflatePaths(new PathsD { Path(border) }, -settings.Border, JoinType.Round, EndType.Polygon, 2, Precision, ArcTolerance);
            region = Clipper.Difference(region, keepOut, FillRule.NonZero, Precision);
            // Gaps too narrow for a pocket (between close holes, or a hole and the edge) stay solid: shrink, then grow back.
            region = Clipper.InflatePaths(Clipper.InflatePaths(region, -settings.MinPocket / 2, JoinType.Round, EndType.Polygon, 2, Precision, ArcTolerance),
                settings.MinPocket / 2, JoinType.Round, EndType.Polygon, 2, Precision, ArcTolerance);
            if (region.Count == 0) return plan;

            // Where ribs meet: each hole, or one point for a cluster of holes too close for pockets between them (a bolt pattern,
            // a bearing and its screws), unless it's right at the edge; each cutout; the plate's corners and points along its edge
            // about a pocket apart; points the student added; then more points inside wherever a pocket would be too big, so
            // pockets come out even-sized.
            var nodes = new List<double[]>();
            var spokes = new PathsD(); // a rib from each hole of a cluster to the cluster's junction, so no hole's ring hangs loose
            var group = HoleGroups(holes, settings);
            foreach (var members in Enumerable.Range(0, holes.Count).GroupBy(i => group[i]))
            {
                double weight = members.Sum(i => holes[i].R * holes[i].R);
                var center = new[] { members.Sum(i => holes[i].X * holes[i].R * holes[i].R) / weight, members.Sum(i => holes[i].Y * holes[i].R * holes[i].R) / weight };
                bool atEdge = members.All(i => DistanceToPolygon(new[] { holes[i].X, holes[i].Y }, border) < holes[i].R + settings.Ring + settings.Border + settings.MinPocket);
                if (atEdge) continue;
                nodes.Add(center);
                foreach (int i in members)
                    if (Distance(center, new[] { holes[i].X, holes[i].Y }) > 1e-6) spokes.Add(Path(new List<double[]> { center, new[] { holes[i].X, holes[i].Y } }));
            }
            nodes.AddRange(cutouts.Where(c => c.Count > 0).Select(c => new[] { c.Average(p => p[0]), c.Average(p => p[1]) }));
            Func<double[], bool> fresh = p => nodes.All(n => Distance(n, p) > 1e-6);
            Func<double[], bool> clear = p => holes.All(h => Distance(p, new[] { h.X, h.Y }) > h.R + settings.Ring + settings.Rib + settings.MinPocket);
            var corners = Corners(border);
            var edgeNodes = new List<double[]>();
            foreach (var p in EdgePoints(border, settings.MaxPocket)) if (fresh(p) && (corners.Contains(p) || clear(p))) { nodes.Add(p); edgeNodes.Add(p); }
            foreach (var p in extraPoints ?? new List<double[]>()) if (fresh(p)) nodes.Add(new[] { p[0], p[1] });
            for (int round = 0; round < 8 && nodes.Count < 800; round++)
            {
                var added = new List<double[]>();
                foreach (var triangle in Triangulate(nodes))
                {
                    double longest = Enumerable.Range(0, 3).Max(k => Distance(triangle[k], triangle[(k + 1) % 3]));
                    if (longest <= settings.MaxPocket * 1.5) continue;
                    var middle = new[] { triangle.Average(q => q[0]), triangle.Average(q => q[1]) };
                    // Only inside the area that's cut, comfortably clear of its edge, and not crowding another junction.
                    if (Depth(region, middle) < settings.MinPocket) continue;
                    if (nodes.Concat(added).All(n => Distance(n, middle) > settings.MaxPocket * 0.45)) added.Add(middle);
                }
                if (added.Count == 0) break;
                nodes.AddRange(added);
            }

            // Ribs along the edges of the triangles; what's left of the region between them are the pockets. A thin triangle would
            // only make a sliver: its longest side gets no rib, so it joins its neighbor in one pocket.
            var edges = new PathsD(spokes);
            var seen = new HashSet<string>();
            var skipped = new HashSet<string>();
            Func<double[], double[], string> key = (a, b) => Compare(a, b) > 0 ? b[0] + "," + b[1] + "," + a[0] + "," + a[1] : a[0] + "," + a[1] + "," + b[0] + "," + b[1];
            // Neighbors along the plate's edge get no rib between them: the edge's border is the rib there, and a straight rib
            // across a curved edge would flatten the pockets beside it.
            var alongEdge = edgeNodes.OrderBy(p => AlongBorder(border, p)).ToList();
            for (int i = 0; i < alongEdge.Count && alongEdge.Count > 2; i++) skipped.Add(key(alongEdge[i], alongEdge[(i + 1) % alongEdge.Count]));
            var triangles = Triangulate(nodes);
            foreach (var triangle in triangles)
            {
                double smallestAngle = Enumerable.Range(0, 3).Min(k => Math.PI - Math.Abs(Turn(triangle[(k + 2) % 3], triangle[k], triangle[(k + 1) % 3])));
                if (smallestAngle >= MinAngle) continue;
                int longest = Enumerable.Range(0, 3).OrderByDescending(k => Distance(triangle[k], triangle[(k + 1) % 3])).First();
                skipped.Add(key(triangle[longest], triangle[(longest + 1) % 3]));
            }
            foreach (var triangle in triangles)
                for (int k = 0; k < 3; k++)
                {
                    double[] a = triangle[k], b = triangle[(k + 1) % 3];
                    string id = key(a, b);
                    if (!skipped.Contains(id) && seen.Add(id)) edges.Add(Path(new List<double[]> { a, b }));
                }
            var ribs = Clipper.InflatePaths(edges, settings.Rib / 2, JoinType.Round, EndType.Round, 2, Precision, ArcTolerance);
            var pieces = Clipper.Difference(region, ribs, FillRule.NonZero, Precision);

            // Too narrow anywhere for the smallest pocket: left solid. Then rounded corners for the router bit (shrink, then grow
            // back by the corner radius), which also trims thin tails off pockets.
            var kept = new PathsD();
            foreach (var piece in Pieces(pieces))
            {
                if (Clipper.InflatePaths(piece, -settings.MinPocket / 2, JoinType.Round, EndType.Polygon, 2, Precision, ArcTolerance).Count == 0) continue;
                kept.AddRange(piece);
            }
            double radius = Math.Max(settings.CornerRadius, 0.001);
            var rounded = Clipper.InflatePaths(Clipper.InflatePaths(kept, -radius, JoinType.Round, EndType.Polygon, 2, Precision, ArcTolerance),
                radius, JoinType.Round, EndType.Polygon, 2, Precision, ArcTolerance);
            double smallest = Math.PI * settings.MinPocket * settings.MinPocket / 4;
            foreach (var piece in Pieces(rounded))
            {
                if (Clipper.Area(piece) < smallest) continue;
                foreach (var loop in piece)
                {
                    var pocket = new Pocket { Area = Clipper.Area(loop) };
                    pocket.Segments.AddRange(Fit(loop.Select(p => new[] { p.x, p.y }).ToList(), FitTolerance));
                    if (pocket.Segments.Count == 0) continue;
                    plan.Pockets.Add(pocket);
                    plan.RemovedArea += pocket.Area;
                }
            }
            return plan;
        }

        // How far along the outline (from its first point) the point nearest p is.
        private static double AlongBorder(List<double[]> border, double[] p)
        {
            double best = double.MaxValue, at = 0, result = 0;
            for (int i = 0; i < border.Count; i++)
            {
                double[] a = border[i], b = border[(i + 1) % border.Count];
                double length = Distance(a, b), d = SegmentDistance(p, a, b);
                if (d < best)
                {
                    best = d;
                    result = at + (length < 1e-12 ? 0 : Math.Max(0, Math.Min(length, ((p[0] - a[0]) * (b[0] - a[0]) + (p[1] - a[1]) * (b[1] - a[1])) / length)));
                }
                at += length;
            }
            return result;
        }

        // Holes close enough that no pocket fits between them share a group id.
        private static int[] HoleGroups(IList<Circle2> holes, LightenSettings s)
        {
            var group = Enumerable.Range(0, holes.Count).ToArray();
            Func<int, int> find = null;
            find = i => group[i] == i ? i : (group[i] = find(group[i]));
            for (int i = 0; i < holes.Count; i++)
                for (int j = i + 1; j < holes.Count; j++)
                {
                    double gap = Distance(new[] { holes[i].X, holes[i].Y }, new[] { holes[j].X, holes[j].Y }) - holes[i].R - holes[j].R - 2 * s.Ring;
                    if (gap < s.Rib + s.MinPocket) group[find(i)] = find(j);
                }
            return Enumerable.Range(0, holes.Count).Select(find).ToArray();
        }

        // Splits a boolean result into separate pockets: each outer loop with the islands inside it.
        private static List<PathsD> Pieces(PathsD paths)
        {
            var outers = paths.Where(p => Clipper.Area(p) > 0).Select(p => new PathsD { p }).ToList();
            foreach (var island in paths.Where(p => Clipper.Area(p) < 0))
            {
                var at = new[] { island[0].x, island[0].y };
                var owner = outers.Where(o => Inside(o[0].Select(q => new[] { q.x, q.y }).ToList(), at)).OrderBy(o => Clipper.Area(o[0])).FirstOrDefault();
                if (owner != null) owner.Add(island);
            }
            return outers;
        }

        // How far a point is inside the region (0 outside it).
        private static double Depth(PathsD region, double[] p)
        {
            bool inside = false;
            double nearest = double.MaxValue;
            foreach (var path in region)
            {
                var points = path.Select(q => new[] { q.x, q.y }).ToList();
                if (Inside(points, p)) inside = !inside;
                nearest = Math.Min(nearest, DistanceToPolygon(p, points));
            }
            return inside ? nearest : 0;
        }

        // ---------- turning a computed outline back into lines and arcs ----------

        /// <summary>
        /// A closed outline (many short pieces) as the fewest lines and arcs that stay within `tolerance` of it. Arcs go exactly
        /// through their end points, and neighbors share end points, so the sketch closes.
        /// </summary>
        internal static List<PocketSegment> Fit(List<double[]> points, double tolerance)
        {
            var result = new List<PocketSegment>();
            var p = new List<double[]>();
            foreach (var q in points) if (p.Count == 0 || Distance(p[p.Count - 1], q) > 1e-7) p.Add(q);
            while (p.Count > 1 && Distance(p[0], p[p.Count - 1]) < 1e-7) p.RemoveAt(p.Count - 1);
            int n = p.Count;
            if (n < 3) return result;
            // Start at the sharpest corner, so an arc isn't split where the loop happens to begin.
            int start = 0;
            double sharpest = -1;
            for (int i = 0; i < n; i++)
            {
                double turn = Turn(p[(i + n - 1) % n], p[i], p[(i + 1) % n]);
                if (Math.Abs(turn) > sharpest) { sharpest = Math.Abs(turn); start = i; }
            }
            var loop = Enumerable.Range(0, n + 1).Select(i => p[(start + i) % n]).ToList(); // closed: the last point is the first
            int at = 0;
            while (at < n)
            {
                int line = at + 1;
                while (line < n && LineFits(loop, at, line + 1, tolerance)) line++;
                int arc = at;
                double[] center = null, best = null;
                for (int j = at + 3; j <= n; j++)
                {
                    center = ArcFits(loop, at, j, tolerance);
                    if (center == null) break;
                    arc = j; best = center;
                }
                if (best != null && arc > line)
                {
                    double[] a = loop[at], b = loop[arc], m = loop[(at + arc) / 2];
                    bool clockwise = (m[0] - a[0]) * (b[1] - m[1]) - (m[1] - a[1]) * (b[0] - m[0]) < 0;
                    result.Add(new PocketSegment { Arc = true, Clockwise = clockwise, X1 = a[0], Y1 = a[1], X2 = b[0], Y2 = b[1], Cx = best[0], Cy = best[1] });
                    at = arc;
                }
                else
                {
                    result.Add(new PocketSegment { X1 = loop[at][0], Y1 = loop[at][1], X2 = loop[line][0], Y2 = loop[line][1] });
                    at = line;
                }
            }
            return result;
        }

        private static bool LineFits(List<double[]> loop, int from, int to, double tolerance)
        {
            for (int k = from + 1; k < to; k++) if (SegmentDistance(loop[k], loop[from], loop[to]) > tolerance) return false;
            return true;
        }

        // The center of a circle through loop[from], the middle point and loop[to] that every point between follows
        // (turning one way, less than a full circle), or null.
        private static double[] ArcFits(List<double[]> loop, int from, int to, double tolerance)
        {
            double[] a = loop[from], m = loop[(from + to) / 2], b = loop[to];
            var center = Circumcenter(a, m, b);
            if (center == null) return null;
            double r = Distance(center, a);
            if (r > 100 || r < 0.005) return null;
            double sign = 0, sweep = 0;
            for (int k = from; k < to; k++)
            {
                double[] p = loop[k], q = loop[k + 1];
                if (Math.Abs(Distance(center, q) - r) > tolerance) return null;
                var mid = new[] { (p[0] + q[0]) / 2, (p[1] + q[1]) / 2 };
                if (Math.Abs(Distance(center, mid) - r) > tolerance) return null;
                double step = Math.Atan2((p[0] - center[0]) * (q[1] - center[1]) - (p[1] - center[1]) * (q[0] - center[0]),
                    (p[0] - center[0]) * (q[0] - center[0]) + (p[1] - center[1]) * (q[1] - center[1]));
                if (Math.Abs(step) > Math.PI / 4) return null;
                if (sign == 0) sign = Math.Sign(step);
                else if (step * sign < -1e-9) return null;
                sweep += Math.Abs(step);
            }
            return sweep < 2 * Math.PI - 0.1 ? center : null;
        }

        private static double[] Circumcenter(double[] a, double[] b, double[] c)
        {
            double d = 2 * (a[0] * (b[1] - c[1]) + b[0] * (c[1] - a[1]) + c[0] * (a[1] - b[1]));
            if (Math.Abs(d) < 1e-12) return null;
            double a2 = a[0] * a[0] + a[1] * a[1], b2 = b[0] * b[0] + b[1] * b[1], c2 = c[0] * c[0] + c[1] * c[1];
            return new[] { (a2 * (b[1] - c[1]) + b2 * (c[1] - a[1]) + c2 * (a[1] - b[1])) / d, (a2 * (c[0] - b[0]) + b2 * (a[0] - c[0]) + c2 * (b[0] - a[0])) / d };
        }

        // Signed turning angle at b going a → b → c.
        private static double Turn(double[] a, double[] b, double[] c)
        {
            double ux = b[0] - a[0], uy = b[1] - a[1], vx = c[0] - b[0], vy = c[1] - b[1];
            return Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        }

        // ---------- where ribs meet along the edge ----------

        // The plate's corners, plus points along its edges about `spacing` apart (curves are followed, not sampled point by point).
        private static List<double[]> EdgePoints(List<double[]> border, double spacing)
        {
            var corners = Corners(border);
            var points = new List<double[]>(corners);
            if (spacing <= 0) return points;
            double since = 0;
            for (int i = 0; i < border.Count; i++)
            {
                double[] v = border[i], next = border[(i + 1) % border.Count];
                if (corners.Contains(v)) since = 0;
                double length = Distance(v, next), at = 0;
                while (since + (length - at) >= spacing)
                {
                    at += spacing - since;
                    points.Add(new[] { v[0] + (next[0] - v[0]) * at / length, v[1] + (next[1] - v[1]) * at / length });
                    since = 0;
                }
                since += length - at;
            }
            // A point just before a corner would make a sliver: drop points crowding a corner.
            return points.Where(p => corners.Contains(p) || corners.All(c => Distance(c, p) > spacing * 0.4)).ToList();
        }

        // Real corners: where the outline turns by more than 30° (points along an arc turn a little at a time and don't count).
        private static List<double[]> Corners(List<double[]> border)
        {
            var corners = new List<double[]>();
            for (int i = 0; i < border.Count; i++)
                if (Math.Abs(Turn(border[(i + border.Count - 1) % border.Count], border[i], border[(i + 1) % border.Count])) > Math.PI / 6) corners.Add(border[i]);
            return corners;
        }

        // ---------- geometry ----------

        private static PathD Path(IEnumerable<double[]> points)
        {
            var path = new PathD();
            foreach (var p in points) path.Add(new PointD(p[0], p[1]));
            return path;
        }

        private static PathD CirclePath(double x, double y, double r)
        {
            // Enough sides that no side strays more than ArcTolerance from the true circle.
            int sides = (int)Math.Max(24, Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - ArcTolerance / r))));
            var path = new PathD();
            for (int i = 0; i < sides; i++) path.Add(new PointD(x + r * Math.Cos(2 * Math.PI * i / sides), y + r * Math.Sin(2 * Math.PI * i / sides)));
            return path;
        }

        private static int Compare(double[] a, double[] b) { return a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1].CompareTo(b[1]); }

        /// <summary>The narrowest width of a convex polygon: for each side, how far the farthest corner is from it; the smallest of those.</summary>
        internal static double MinimumWidth(IList<double[]> polygon)
        {
            double best = double.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                double[] a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                double dx = b[0] - a[0], dy = b[1] - a[1], length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-9) continue;
                double far = polygon.Max(p => Math.Abs(dx * (p[1] - a[1]) - dy * (p[0] - a[0])) / length);
                best = Math.Min(best, far);
            }
            return best;
        }

        // Bowyer–Watson Delaunay triangulation (fine for the few hundred points a plate has).
        internal static List<List<double[]>> Triangulate(List<double[]> points)
        {
            var result = new List<List<double[]>>();
            if (points.Count < 3) return result;
            double minX = points.Min(p => p[0]), minY = points.Min(p => p[1]), maxX = points.Max(p => p[0]), maxY = points.Max(p => p[1]);
            double size = Math.Max(maxX - minX, maxY - minY) * 20 + 1, midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;
            var all = points.ToList();
            int s0 = all.Count;
            all.Add(new[] { midX - size, midY - size });
            all.Add(new[] { midX + size, midY - size });
            all.Add(new[] { midX, midY + size });
            var triangles = new List<int[]> { new[] { s0, s0 + 1, s0 + 2 } };
            for (int i = 0; i < s0; i++)
            {
                var p = all[i];
                var bad = triangles.Where(t => InCircumcircle(p, all[t[0]], all[t[1]], all[t[2]])).ToList();
                var edges = new List<int[]>();
                foreach (var t in bad)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = t[k], b = t[(k + 1) % 3];
                        int shared = bad.Count(o => o != t && o.Contains(a) && o.Contains(b));
                        if (shared == 0) edges.Add(new[] { a, b });
                    }
                triangles.RemoveAll(t => bad.Contains(t));
                foreach (var e in edges) triangles.Add(new[] { e[0], e[1], i });
            }
            foreach (var t in triangles)
                if (t.All(k => k < s0)) result.Add(new List<double[]> { all[t[0]], all[t[1]], all[t[2]] });
            return result;
        }

        private static bool InCircumcircle(double[] p, double[] a, double[] b, double[] c)
        {
            if (SignedArea(new List<double[]> { a, b, c }) < 0) { var swap = b; b = c; c = swap; }
            double ax = a[0] - p[0], ay = a[1] - p[1], bx = b[0] - p[0], by = b[1] - p[1], cx = c[0] - p[0], cy = c[1] - p[1];
            return (ax * ax + ay * ay) * (bx * cy - cx * by) - (bx * bx + by * by) * (ax * cy - cx * ay) + (cx * cx + cy * cy) * (ax * by - bx * ay) > 1e-12;
        }

        internal static double SignedArea(IList<double[]> polygon)
        {
            double sum = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var p = polygon[i]; var q = polygon[(i + 1) % polygon.Count];
                sum += p[0] * q[1] - q[0] * p[1];
            }
            return sum / 2;
        }

        internal static bool Inside(IList<double[]> polygon, double[] p)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                double[] a = polygon[i], b = polygon[j];
                if ((a[1] > p[1]) != (b[1] > p[1]) && p[0] < (b[0] - a[0]) * (p[1] - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
            }
            return inside;
        }

        private static double DistanceToPolygon(double[] p, IList<double[]> polygon)
        {
            double best = double.MaxValue;
            for (int i = 0; i < polygon.Count; i++) best = Math.Min(best, SegmentDistance(p, polygon[i], polygon[(i + 1) % polygon.Count]));
            return best;
        }

        private static double SegmentDistance(double[] p, double[] a, double[] b)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], length2 = dx * dx + dy * dy;
            double t = length2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / length2));
            return Distance(p, new[] { a[0] + dx * t, a[1] + dy * t });
        }

        private static double Distance(double[] a, double[] b) { return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1])); }
    }
}
