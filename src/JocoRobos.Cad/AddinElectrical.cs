using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace JocoRobos.Cad
{
    /// <summary>
    /// Robot wiring in SOLIDWORKS: connectors on devices, wires routed as 3D sketches through picked points, zip ties, harnesses,
    /// and the wiring report. Everything is kept in the assembly's custom properties ("CADHub Wire W3 | type" …), so it goes to the
    /// team with the assembly; wire lengths are read from their sketches every time, so moving a wire updates the report.
    /// </summary>
    public sealed partial class Addin
    {
        private const string Records = "CADHub ";

        // ---------- stored records ----------

        private static Dictionary<string, Dictionary<string, string>> ReadRecords(ModelDoc2 doc, string kind)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var properties = doc.Extension.CustomPropertyManager[""];
            string prefix = Records + kind + " ";
            foreach (string name in ((properties.GetNames() as object[]) ?? new object[0]).OfType<string>())
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                int bar = name.LastIndexOf(" | ", StringComparison.Ordinal);
                if (bar <= prefix.Length) continue;
                string id = name.Substring(prefix.Length, bar - prefix.Length), field = name.Substring(bar + 3);
                Dictionary<string, string> record;
                if (!result.TryGetValue(id, out record)) result[id] = record = new Dictionary<string, string>();
                string value, resolved;
                bool wasResolved, linked;
                properties.Get6(name, false, out value, out resolved, out wasResolved, out linked);
                record[field] = value ?? "";
            }
            return result;
        }

        private static void SaveRecord(ModelDoc2 doc, string kind, string id, Dictionary<string, string> fields)
        {
            var properties = doc.Extension.CustomPropertyManager[""];
            foreach (var field in fields)
            {
                string value = field.Value ?? "";
                if (value.Length > 250) value = value.Substring(0, 250);
                properties.Add3(Records + kind + " " + id + " | " + field.Key, (int)swCustomInfoType_e.swCustomInfoText, value,
                    (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            }
        }

        private static string Field(Dictionary<string, string> record, string field)
        {
            string value;
            return record.TryGetValue(field, out value) ? value : "";
        }

        private static double Number(Dictionary<string, string> record, string field, double fallback)
        {
            double value;
            return double.TryParse(Field(record, field), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static string Text(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }

        private static string NextNumber(IEnumerable<string> used, string letter)
        {
            var taken = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
            for (int n = 1; ; n++) if (!taken.Contains(letter + n)) return letter + n;
        }

        // ---------- where a pick is ----------

        /// <summary>
        /// The model point (meters) a pick stands for, and the component it belongs to: a corner's point, a round edge's center,
        /// a 3D sketch point, otherwise where it was clicked.
        /// </summary>
        private static double[] PickPoint(SldWorks app, object pick, double[] click, out Component2 component)
        {
            component = (pick as Entity)?.GetComponent() as Component2;
            double[] local = null;
            var vertex = pick as Vertex;
            if (vertex != null) local = vertex.GetPoint() as double[];
            var curve = (pick as Edge)?.GetCurve() as Curve;
            if (curve != null && curve.IsCircle()) { var circle = (double[])curve.CircleParams; local = new[] { circle[0], circle[1], circle[2] }; }
            var point = pick as SketchPoint;
            if (point != null && (point.GetSketch() as Sketch)?.Is3D() == true) local = new[] { point.X, point.Y, point.Z };
            if (local == null)
            {
                if (click == null) throw new InvalidOperationException("Couldn't tell where that pick is: click a face, a corner, a round edge or a point.");
                return click;
            }
            if (component == null) return local;
            // Geometry of a part in an assembly comes in the part's own coordinates: move it to where the part sits. (Checked
            // against the click, which is always in the assembly's coordinates.)
            var math = (MathUtility)app.GetMathUtility();
            var placed = (double[])((MathPoint)((MathPoint)math.CreatePoint(local)).MultiplyTransform(component.Transform2)).ArrayData;
            if (click != null && Gap(placed, click) > Gap(local, click)) return local;
            return placed;
        }

        private static double[] ToComponent(SldWorks app, Component2 component, double[] point)
        {
            if (component == null) return point;
            var math = (MathUtility)app.GetMathUtility();
            return (double[])((MathPoint)((MathPoint)math.CreatePoint(point)).MultiplyTransform((MathTransform)component.Transform2.Inverse())).ArrayData;
        }

        private sealed class PlacedConnector
        {
            internal string Id, Device;
            internal int Type;
            internal double[] At; // model point, meters
            internal string Name { get { return Device + ": " + Wiring.Connector(Type).Name; } }
        }

        // Every connector where it is now (its device may have moved since).
        private static List<PlacedConnector> Connectors(SldWorks app, ModelDoc2 doc)
        {
            var result = new List<PlacedConnector>();
            var math = (MathUtility)app.GetMathUtility();
            foreach (var record in ReadRecords(doc, "Connector"))
            {
                double[] local = { Number(record.Value, "x", 0), Number(record.Value, "y", 0), Number(record.Value, "z", 0) };
                string componentName = Field(record.Value, "component");
                double[] at = local;
                if (componentName.Length > 0)
                {
                    var component = (doc as AssemblyDoc)?.GetComponentByName(componentName) as Component2;
                    if (component == null) continue; // The device was deleted from the assembly.
                    at = (double[])((MathPoint)((MathPoint)math.CreatePoint(local)).MultiplyTransform(component.Transform2)).ArrayData;
                }
                result.Add(new PlacedConnector { Id = record.Key, Device = Field(record.Value, "device"), Type = (int)Number(record.Value, "type", 0), At = at });
            }
            return result;
        }

        // A 3D sketch of points (a connector, zip ties), named.
        private static Feature PointSketch(ModelDoc2 doc, IEnumerable<double[]> points, string name)
        {
            doc.ClearSelection2(true);
            doc.SketchManager.Insert3DSketch(true);
            var sketch = doc.SketchManager.ActiveSketch as Feature;
            doc.SketchManager.AddToDB = true;
            try { foreach (var p in points) doc.SketchManager.CreatePoint(p[0], p[1], p[2]); }
            finally { doc.SketchManager.AddToDB = false; }
            doc.SketchManager.Insert3DSketch(true);
            if (sketch != null) try { sketch.Name = name; } catch (Exception) { }
            return sketch;
        }

        // ---------- the tools ----------

        internal static string AddConnector(SldWorks app, ModelDoc2 doc, object pick, double[] click, int type, string device)
        {
            Component2 component;
            var at = PickPoint(app, pick, click, out component);
            if (component == null && doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Click on the device itself (a part in the assembly), so the connector moves with it.");
            if (device.Length == 0) device = component != null ? Path.GetFileNameWithoutExtension(component.GetPathName() ?? "") : doc.GetTitle();
            if (device.Length == 0) device = component?.Name2 ?? "Device";
            var local = ToComponent(app, component, at);
            string id = NextNumber(ReadRecords(doc, "Connector").Keys, "C");
            SaveRecord(doc, "Connector", id, new Dictionary<string, string>
            {
                { "component", component?.Name2 ?? "" }, { "device", device }, { "type", type.ToString(CultureInfo.InvariantCulture) },
                { "x", Text(local[0]) }, { "y", Text(local[1]) }, { "z", Text(local[2]) },
            });
            PointSketch(doc, new[] { at }, "Connector " + Wiring.Connector(type).Name + " (" + device + ")");
            return "✓ " + Wiring.Connector(type).Name + " connector on " + device + ". Route Wire snaps wire ends to it.";
        }

        internal static string RouteWire(SldWorks app, ModelDoc2 doc, IList<object> picks, IList<double[]> clicks, int type, double slack, string id)
        {
            if (picks.Count < 2) throw new InvalidOperationException("Click at least a start and an end.");
            var points = new List<double[]>();
            var components = new List<Component2>();
            for (int i = 0; i < picks.Count; i++)
            {
                Component2 component;
                points.Add(PickPoint(app, picks[i], i < clicks.Count ? clicks[i] : null, out component));
                components.Add(component);
            }
            var existing = ReadRecords(doc, "Wire");
            if (id.Length == 0) id = Wiring.NextId(existing.Keys);
            else if (existing.ContainsKey(id)) throw new InvalidOperationException("There's already a wire " + id + ". Pick another ID, or leave it blank.");
            // Ends: the nearest connector within an inch, else the device clicked.
            var connectors = Connectors(app, doc);
            Func<int, PlacedConnector> near = i => connectors.Where(c => Gap(c.At, points[i]) < 0.0254).OrderBy(c => Gap(c.At, points[i])).FirstOrDefault();
            Func<int, string> device = i => components[i] != null ? Path.GetFileNameWithoutExtension(components[i].GetPathName() ?? "") : "";
            var start = near(0);
            var end = near(points.Count - 1);
            string from = start?.Name ?? device(0), to = end?.Name ?? device(points.Count - 1);
            string fromDevice = start?.Device ?? device(0), toDevice = end?.Device ?? device(points.Count - 1);

            doc.ClearSelection2(true);
            doc.SketchManager.Insert3DSketch(true);
            var sketch = doc.SketchManager.ActiveSketch as Feature;
            SketchSegment segment;
            doc.SketchManager.AddToDB = true;
            try
            {
                segment = points.Count == 2
                    ? doc.SketchManager.CreateLine(points[0][0], points[0][1], points[0][2], points[1][0], points[1][1], points[1][2])
                    : doc.SketchManager.CreateSpline2(points.SelectMany(p => p).ToArray(), true) as SketchSegment;
            }
            finally { doc.SketchManager.AddToDB = false; }
            double routed = segment != null ? segment.GetLength() / Meters : 0;
            doc.SketchManager.Insert3DSketch(true);
            if (segment == null || sketch == null) throw new InvalidOperationException("SOLIDWORKS couldn't draw the wire through those points.");
            var kind = Wiring.Type(type);
            try { sketch.Name = "Wire " + id + " (" + kind.Name + ")"; } catch (Exception) { }
            SaveRecord(doc, "Wire", id, new Dictionary<string, string>
            {
                { "type", type.ToString(CultureInfo.InvariantCulture) }, { "slack", Text(slack) }, { "from", from }, { "to", to },
                { "fromDevice", fromDevice }, { "toDevice", toDevice }, { "sketch", sketch.Name }, { "length", Text(routed) },
            });
            var wire = new WireInfo { Routed = routed, Slack = slack };
            return "✓ Wire " + id + " (" + kind.Name + "): " + routed.ToString("0.0", CultureInfo.InvariantCulture) + " in routed, cut " +
                wire.CutLength.ToString("0.0", CultureInfo.InvariantCulture) + " in" + (from.Length > 0 || to.Length > 0 ? ", " + (from.Length > 0 ? from : "?") + " → " + (to.Length > 0 ? to : "?") : "") + ".";
        }

        internal static string AddZipTies(SldWorks app, ModelDoc2 doc, IList<object> picks, IList<double[]> clicks, string kind)
        {
            var points = new List<double[]>();
            for (int i = 0; i < picks.Count; i++)
            {
                Component2 component;
                points.Add(PickPoint(app, picks[i], i < clicks.Count ? clicks[i] : null, out component));
            }
            if (points.Count == 0) throw new InvalidOperationException("Click where they go.");
            string id = NextNumber(ReadRecords(doc, "ZipTie").Keys, "Z");
            var sketch = PointSketch(doc, points, kind + "s " + id.Substring(1));
            SaveRecord(doc, "ZipTie", id, new Dictionary<string, string> { { "kind", kind }, { "count", points.Count.ToString(CultureInfo.InvariantCulture) }, { "sketch", sketch?.Name ?? "" } });
            return "✓ " + points.Count + " " + kind.ToLowerInvariant() + (points.Count == 1 ? "" : "s") + " placed. Pick their points as bends in Route Wire.";
        }

        internal static string MakeHarness(ModelDoc2 doc, IList<object> picks, string name, bool bundle)
        {
            var wires = ReadRecords(doc, "Wire");
            var bySketch = wires.ToDictionary(w => Field(w.Value, "sketch"), w => w.Key, StringComparer.OrdinalIgnoreCase);
            var chosen = new List<string>();
            foreach (var pick in picks)
            {
                var sketch = pick as Feature ?? ((pick as SketchSegment)?.GetSketch() as Feature);
                string id;
                if (sketch != null && bySketch.TryGetValue(sketch.Name, out id) && !chosen.Contains(id)) chosen.Add(id);
            }
            if (chosen.Count == 0) throw new InvalidOperationException("None of those are wires made with Route Wire.");
            string kind = bundle ? "Bundle" : "Harness";
            SaveRecord(doc, kind, name, new Dictionary<string, string> { { "wires", String.Join(",", chosen) } });
            if (!bundle) foreach (var id in chosen) SaveRecord(doc, "Wire", id, new Dictionary<string, string> { { "harness", name } });
            double diameter = Wiring.BundleDiameter(chosen.Select(id => Wiring.Type((int)Number(wires[id], "type", 9))));
            return "✓ " + kind + " " + name + ": " + chosen.Count + " wire" + (chosen.Count == 1 ? "" : "s") + " (" + String.Join(", ", chosen) + "), about " +
                diameter.ToString("0.00", CultureInfo.InvariantCulture) + " in across.";
        }

        // ---------- the wiring report ----------

        public void Connector() { ShowTool(CadHubTool.Find("connector")); }
        public void RouteWire() { ShowTool(CadHubTool.Find("wire")); }
        public void ZipTie() { ShowTool(CadHubTool.Find("zip-tie")); }
        public void Harness() { ShowTool(CadHubTool.Find("harness")); }

        public void WiringReport()
        {
            Execute(() =>
            {
                var doc = application.ActiveDoc as ModelDoc2;
                if (doc == null || doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
                    throw new InvalidOperationException("Open the robot (or the assembly with the wiring), then Wiring Report.");
                var wires = new List<WireInfo>();
                foreach (var record in ReadRecords(doc, "Wire"))
                {
                    // The length from the wire's sketch as it is now; the length when it was routed if the sketch is gone.
                    double routed = Number(record.Value, "length", 0);
                    string sketchName = Field(record.Value, "sketch");
                    var sketch = (doc is AssemblyDoc ? ((AssemblyDoc)doc).FeatureByName(sketchName) : (doc as PartDoc)?.FeatureByName(sketchName)) as Feature;
                    var segments = ((sketch?.GetSpecificFeature2() as Sketch)?.GetSketchSegments() as object[])?.OfType<SketchSegment>().ToList();
                    if (segments != null && segments.Count > 0) routed = segments.Sum(s => s.GetLength()) / Meters;
                    wires.Add(new WireInfo
                    {
                        Id = record.Key, Type = (int)Number(record.Value, "type", 9), Slack = Number(record.Value, "slack", 0.1), Routed = routed,
                        From = Field(record.Value, "from"), To = Field(record.Value, "to"), FromDevice = Field(record.Value, "fromDevice"),
                        ToDevice = Field(record.Value, "toDevice"), Harness = Field(record.Value, "harness"),
                    });
                }
                var connectors = ReadRecords(doc, "Connector").Values.Select(r => new ConnectorInfo { Device = Field(r, "device"), Type = (int)Number(r, "type", 0) }).ToList();
                var groups = new List<string[]>();
                foreach (var kind in new[] { "Harness", "Bundle" })
                    foreach (var record in ReadRecords(doc, kind))
                    {
                        var ids = Field(record.Value, "wires").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        var members = wires.Where(w => ids.Contains(w.Id)).ToList();
                        groups.Add(new[] { record.Key, kind, String.Join(", ", ids), members.Sum(w => w.CutLength).ToString("0.0", CultureInfo.InvariantCulture),
                            Wiring.BundleDiameter(members.Select(w => Wiring.Type(w.Type))).ToString("0.00", CultureInfo.InvariantCulture) });
                    }
                int zipTies = ReadRecords(doc, "ZipTie").Values.Sum(r => (int)Number(r, "count", 0));
                using (var form = new WiringReportForm(doc.GetTitle(), Wiring.TableRows(wires), groups, Wiring.CanReport(connectors, wires),
                    Wiring.PowerReport(connectors, wires), zipTies, connectors.Count))
                    form.ShowDialog(new SolidWorksWindow());
            });
        }
    }

    /// <summary>The wiring report: the harness table (copy or save as CSV), harnesses and bundles, and the CAN and power checks.</summary>
    internal sealed class WiringReportForm : Form
    {
        internal WiringReportForm(string title, List<string[]> rows, List<string[]> groups, List<string> can, List<string> power, int zipTies, int connectors)
        {
            Text = "Wiring Report — " + title;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(980, 560);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowIcon = false;
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(Table("Wires (" + rows.Count + ")", Wiring.TableColumns, rows));
            tabs.TabPages.Add(Table("Harnesses and bundles (" + groups.Count + ")", new[] { "Name", "Kind", "Wires", "Total cut length (in)", "About (in across)" }, groups));
            tabs.TabPages.Add(Lines("CAN network", can));
            tabs.TabPages.Add(Lines("Power network", power));
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
            var save = new Button { Text = "Save CSV…", AutoSize = true };
            var copy = new Button { Text = "Copy table", AutoSize = true };
            var note = new Label { AutoSize = true, Margin = new Padding(0, 8, 24, 0), ForeColor = SystemColors.GrayText,
                Text = connectors + " connector" + (connectors == 1 ? "" : "s") + " · " + zipTies + " zip tie" + (zipTies == 1 ? "" : "s") + " and mounts" };
            string csv = Wiring.Csv(rows);
            copy.Click += (s, e) => { Clipboard.SetText(csv); copy.Text = "Copied ✓"; };
            save.Click += (s, e) =>
            {
                using (var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = Path.GetFileNameWithoutExtension(title) + " wiring.csv" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, csv);
            };
            bottom.Controls.AddRange(new Control[] { close, save, copy, note });
            Controls.Add(tabs);
            Controls.Add(bottom);
            AcceptButton = close;
        }

        private static TabPage Table(string title, string[] columns, List<string[]> rows)
        {
            var page = new TabPage(title);
            var list = new ListView { Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true, GridLines = true };
            foreach (var column in columns) list.Columns.Add(column, -2);
            foreach (var row in rows) list.Items.Add(new ListViewItem(row));
            if (rows.Count == 0) list.Items.Add(new ListViewItem(new[] { "(none yet)" }));
            foreach (ColumnHeader column in list.Columns) column.Width = -2;
            page.Controls.Add(list);
            return page;
        }

        private static TabPage Lines(string title, List<string> lines)
        {
            var page = new TabPage(title);
            page.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = String.Join("\r\n\r\n", lines),
                BackColor = SystemColors.Window, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10f) });
            return page;
        }
    }
}
