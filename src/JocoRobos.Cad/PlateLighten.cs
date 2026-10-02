using System;
using System.Collections.Generic;
using System.Linq;

namespace JocoRobos.Cad
{
    internal sealed class LightenSettings
    {
        internal double Rib = 0.15;          // width of the ribs between pockets
        internal double Border = 0.25;       // solid material kept along the plate's edge
        internal double Ring = 0.15;         // solid material kept around every hole, beyond its edge
        internal double CornerRadius = 0.0625; // the router bit's radius: pockets get rounded corners at least this big
        internal double MinPocket = 0.35;    // pockets narrower than this (inscribed diameter) are left solid
        internal double MaxPocket = 3;     // pockets bigger across than about this are split by another rib junction
    }

    internal sealed class Circle2
    {
        internal double X, Y, R;
    }

    /// <summary>One piece of a pocket's outline: a line, or a counterclockwise arc around (Cx, Cy).</summary>
    internal sealed class PocketSegment
    {
        internal bool Arc;
        internal double X1, Y1, X2, Y2, Cx, Cy;
    }

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
    }

    /// <summary>
    /// "Lighten Plate", 2D: ribs connect the holes (and the plate's corners and any points the student adds) in triangles,
    /// and each triangle between ribs becomes a pocket with rounded corners, kept clear of the edge and of every hole.
    /// Pure geometry in the plate's plane, inches; the add-in reads the face and sketches the result.
    /// </summary>
    internal static class PlateLighten
    {
        private const double MinAngle = 22 * Math.PI / 180;

        internal static LightenPlan Plan(IList<double[]> outline, IList<Circle2> holes, IList<double[]> extraPoints, LightenSettings settings)
        {
            var plan = new LightenPlan();
            var border = outline.Select(p => new[] { p[0], p[1] }).ToList();
            if (SignedArea(border) < 0) border.Reverse();
            plan.PlateArea = SignedArea(border) - holes.Sum(h => Math.PI * h.R * h.R);
            // Where ribs meet: every hole; the plate's corners and points along its edge about a pocket apart; points the student
            // added; then more points inside wherever a pocket would be too big, so pockets come out even-sized.
            var group = HoleGroups(holes, settings);
            var nodes = new List<double[]>();
            var groupOf = new Dictionary<double[], int>();
            for (int i = 0; i < holes.Count; i++) { var c = new[] { holes[i].X, holes[i].Y }; nodes.Add(c); groupOf[c] = group[i]; }
            Func<double[], double, bool> clear = (p, margin) => holes.All(h => Distance(p, new[] { h.X, h.Y }) > h.R + settings.Ring + margin) &&
                nodes.All(n => Distance(n, p) > 1e-6);
            foreach (var p in EdgePoints(border, settings.MaxPocket))
                if (clear(p, settings.Rib + settings.MinPocket)) nodes.Add(p);
            foreach (var p in extraPoints ?? new List<double[]>()) if (clear(p, 0)) nodes.Add(new[] { p[0], p[1] });
            for (int round = 0; round < 8 && nodes.Count < 800; round++)
            {
                var added = new List<double[]>();
                foreach (var triangle in Triangulate(nodes))
                {
                    if (!Useful(triangle, border, holes, groupOf, settings)) continue;
                    double longest = Enumerable.Range(0, 3).Max(k => Distance(triangle[k], triangle[(k + 1) % 3]));
                    if (longest <= settings.MaxPocket * 1.5) continue;
                    var middle = new[] { triangle.Average(q => q[0]), triangle.Average(q => q[1]) };
                    bool inside = Inside(border, middle) && border.Select((q, i) => SegmentDistance(middle, q, border[(i + 1) % border.Count])).Min() > settings.Border + settings.MinPocket;
                    if (inside && clear(middle, settings.MinPocket) && nodes.Concat(added).All(n => Distance(n, middle) > settings.MaxPocket * 0.45)) added.Add(middle);
                }
                if (added.Count == 0) break;
                nodes.AddRange(added);
            }
            foreach (var triangle in Triangulate(nodes))
            {
                if (!Useful(triangle, border, holes, groupOf, settings)) continue;
                var pocket = Shape(triangle, border, holes, settings);
                if (pocket == null) continue;
                var rounded = Round(pocket, settings.CornerRadius);
                if (rounded == null) continue;
                plan.Pockets.Add(rounded);
                plan.RemovedArea += rounded.Area;
            }
            return plan;
        }

        // A triangle shrunk by half a rib on each side, then kept off the plate edge and away from holes. Null if too small.
        private static List<double[]> Shape(List<double[]> triangle, List<double[]> border, IList<Circle2> holes, LightenSettings s)
        {
            var polygon = triangle.ToList();
            if (SignedArea(polygon) < 0) polygon.Reverse();
            var corners = polygon.ToList();
            for (int i = 0; i < corners.Count; i++)
                polygon = Clip(polygon, corners[i], corners[(i + 1) % corners.Count], s.Rib / 2);
            // Plate edges near the pocket: keep the border width clear.
            for (int i = 0; i < border.Count && polygon.Count >= 3; i++)
            {
                var a = border[i]; var b = border[(i + 1) % border.Count];
                if (polygon.Any(p => SegmentDistance(p, a, b) < s.Border - 1e-9)) polygon = Clip(polygon, a, b, s.Border);
            }
            // Holes: cut the pocket back by a line facing away from each hole that's too close.
            foreach (var hole in holes)
            {
                if (polygon.Count < 3) break;
                var center = new[] { hole.X, hole.Y };
                double keep = hole.R + s.Ring;
                if (DistanceToPolygon(center, polygon) >= keep - 1e-9 && !Inside(polygon, center)) continue;
                var middle = new[] { polygon.Average(p => p[0]), polygon.Average(p => p[1]) };
                double dx = middle[0] - hole.X, dy = middle[1] - hole.Y, length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-9) return null;
                dx /= length; dy /= length;
                polygon = ClipHalfPlane(polygon, p => (p[0] - hole.X) * dx + (p[1] - hole.Y) * dy - keep);
            }
            if (polygon.Count < 3) return null;
            double area = SignedArea(polygon);
            // Too narrow anywhere (a sliver) or too small: leave it solid.
            if (area <= 0 || MinimumWidth(polygon) < s.MinPocket) return null;
            if (polygon.Any(p => !Inside(border, p) || border.Select((q, i) => SegmentDistance(p, q, border[(i + 1) % border.Count])).Min() < s.Border - 1e-6)) return null;
            if (holes.Any(h => DistanceToPolygon(new[] { h.X, h.Y }, polygon) < h.R + s.Ring - 1e-6)) return null;
            return polygon;
        }

        // Rounded corners: each corner gets a tangent arc (smaller where the corner is too tight for the full radius).
        private static Pocket Round(List<double[]> polygon, double radius)
        {
            int n = polygon.Count;
            var t1 = new double[n][]; var t2 = new double[n][]; var centers = new double[n][];
            double loss = 0;
            for (int i = 0; i < n; i++)
            {
                double[] v = polygon[i], prev = polygon[(i + n - 1) % n], next = polygon[(i + 1) % n];
                double[] u1 = Unit(prev[0] - v[0], prev[1] - v[1]), u2 = Unit(next[0] - v[0], next[1] - v[1]);
                if (u1 == null || u2 == null) return null;
                double theta = Math.Acos(Math.Max(-1, Math.Min(1, u1[0] * u2[0] + u1[1] * u2[1])));
                if (theta < 1e-3 || theta > Math.PI - 1e-3) return null;
                double room = 0.45 * Math.Min(Distance(prev, v), Distance(next, v));
                double r = Math.Min(radius, room * Math.Tan(theta / 2));
                double t = r / Math.Tan(theta / 2);
                t1[i] = new[] { v[0] + u1[0] * t, v[1] + u1[1] * t };
                t2[i] = new[] { v[0] + u2[0] * t, v[1] + u2[1] * t };
                var bisector = Unit(u1[0] + u2[0], u1[1] + u2[1]);
                double toCenter = r / Math.Sin(theta / 2);
                centers[i] = new[] { v[0] + bisector[0] * toCenter, v[1] + bisector[1] * toCenter };
                loss += r * t - 0.5 * r * r * (Math.PI - theta);
            }
            var pocket = new Pocket { Area = SignedArea(polygon) - loss };
            for (int i = 0; i < n; i++)
            {
                pocket.Segments.Add(new PocketSegment { Arc = true, X1 = t1[i][0], Y1 = t1[i][1], X2 = t2[i][0], Y2 = t2[i][1], Cx = centers[i][0], Cy = centers[i][1] });
                var nextStart = t1[(i + 1) % n];
                pocket.Segments.Add(new PocketSegment { X1 = t2[i][0], Y1 = t2[i][1], X2 = nextStart[0], Y2 = nextStart[1] });
            }
            return pocket;
        }

        // Holes close enough that no pocket fits between them share a group id: no pocket is cut between holes of one group.
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

        // A triangle worth pocketing: inside the plate, not inside a hole's ring, and not spanning only holes of one tight group.
        private static bool Useful(List<double[]> triangle, List<double[]> border, IList<Circle2> holes, Dictionary<double[], int> groupOf, LightenSettings s)
        {
            var centroid = new[] { triangle.Average(p => p[0]), triangle.Average(p => p[1]) };
            if (!Inside(border, centroid) || holes.Any(h => Distance(centroid, new[] { h.X, h.Y }) < h.R + s.Ring)) return false;
            // Slivers: a triangle with a very sharp corner only makes a thin wedge of a pocket.
            for (int k = 0; k < 3; k++)
            {
                var u = Unit(triangle[(k + 1) % 3][0] - triangle[k][0], triangle[(k + 1) % 3][1] - triangle[k][1]);
                var w = Unit(triangle[(k + 2) % 3][0] - triangle[k][0], triangle[(k + 2) % 3][1] - triangle[k][1]);
                if (u == null || w == null || Math.Acos(Math.Max(-1, Math.Min(1, u[0] * w[0] + u[1] * w[1]))) < MinAngle) return false;
            }
            int g0, g1, g2;
            return !(groupOf.TryGetValue(triangle[0], out g0) && groupOf.TryGetValue(triangle[1], out g1) && groupOf.TryGetValue(triangle[2], out g2) && g0 == g1 && g1 == g2);
        }

        // The plate's corners, plus points along its edges about `spacing` apart (arcs are followed, not sampled point by point).
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
            {
                double[] prev = border[(i + border.Count - 1) % border.Count], v = border[i], next = border[(i + 1) % border.Count];
                var a = Unit(v[0] - prev[0], v[1] - prev[1]); var b = Unit(next[0] - v[0], next[1] - v[1]);
                if (a == null || b == null) continue;
                double turn = Math.Acos(Math.Max(-1, Math.Min(1, a[0] * b[0] + a[1] * b[1])));
                if (turn > Math.PI / 6) corners.Add(v);
            }
            return corners;
        }

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

        // ---------- geometry ----------

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

        // Keeps the part of a convex polygon at least `distance` to the left of the line a→b.
        private static List<double[]> Clip(List<double[]> polygon, double[] a, double[] b, double distance)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-12) return polygon;
            return ClipHalfPlane(polygon, p => (dx * (p[1] - a[1]) - dy * (p[0] - a[0])) / length - distance);
        }

        // Sutherland–Hodgman against one half-plane: keeps points where side(p) >= 0.
        private static List<double[]> ClipHalfPlane(List<double[]> polygon, Func<double[], double> side)
        {
            var result = new List<double[]>();
            for (int i = 0; i < polygon.Count; i++)
            {
                double[] p = polygon[i], q = polygon[(i + 1) % polygon.Count];
                double sp = side(p), sq = side(q);
                if (sp >= 0) result.Add(p);
                if ((sp >= 0) != (sq >= 0))
                {
                    double t = sp / (sp - sq);
                    result.Add(new[] { p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t });
                }
            }
            return result;
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

        private static double Perimeter(IList<double[]> polygon)
        {
            double sum = 0;
            for (int i = 0; i < polygon.Count; i++) sum += Distance(polygon[i], polygon[(i + 1) % polygon.Count]);
            return sum;
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

        private static double[] Unit(double x, double y)
        {
            double length = Math.Sqrt(x * x + y * y);
            return length < 1e-12 ? null : new[] { x / length, y / length };
        }
    }
}
