using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace JocoRobos.Cad
{
    /// <summary>The electrical tools take almost anything you can click on a device: faces, edges, corners, sketch points.</summary>
    internal abstract class ElectricalTool : CadHubTool
    {
        internal override bool Cuts { get { return false; } }
        internal override bool MakesFeature { get { return false; } }
        internal override bool NeedsSelection { get { return true; } }
        internal override bool WorksInAssembly { get { return true; } }
        internal override Body2 Build(SldWorks application, FeatureParams p, bool preview) { return null; }

        internal override int[] SelectionFilters
        {
            get
            {
                return new[] { (int)swSelectType_e.swSelFACES, (int)swSelectType_e.swSelEDGES, (int)swSelectType_e.swSelVERTICES,
                    (int)swSelectType_e.swSelSKETCHPOINTS, (int)swSelectType_e.swSelEXTSKETCHPOINTS };
            }
        }

        internal override bool Accepts(object selection) { return selection != null; }
    }

    /// <summary>Connector: a named connection point on a device (CAN IN, Power In…), used by Route Wire and the network checks.</summary>
    internal sealed class ConnectorTool : ElectricalTool
    {
        internal override string Kind { get { return "connector"; } }
        internal override string Title { get { return "Connector"; } }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "where", Label = "Click the connector on the device (a face, edge, corner or point)", Kind = FieldKind.Selection },
                new ToolField { Key = "type", Label = "Connector", Kind = FieldKind.Choice, Items = Wiring.ConnectorTypes.Select(c => c.Name).ToList() },
                new ToolField { Key = "device", Label = "Device name (blank: the part's name)", Kind = FieldKind.Text,
                    Tip = "How reports name it, like roboRIO, PDH, or Elevator SPARK MAX." },
            };
        }

        internal override string Result(FeatureParams p, TeamStandards standards, object selection)
        {
            return selection == null ? "Click where the connector is." : "✓ adds " + Wiring.Connector((int)p.Number("type", 0)).Name + " there.";
        }

        internal override string Apply(SldWorks application, ModelDoc2 doc, FeatureParams p, object selection, TeamStandards standards) { return null; }

        internal override string ApplyAll(SldWorks application, ModelDoc2 doc, FeatureParams p, IList<object> picks, IList<double[]> points, TeamStandards standards)
        {
            return Addin.AddConnector(application, doc, picks[0], points.FirstOrDefault(), (int)p.Number("type", 0), (p["device"] ?? "").Trim());
        }
    }

    /// <summary>Route Wire: through the picked points in order; its type, ends and lengths are kept for the reports.</summary>
    internal sealed class RouteWireTool : ElectricalTool
    {
        internal override string Kind { get { return "wire"; } }
        internal override string Title { get { return "Route Wire"; } }
        internal override bool MultipleSelections { get { return true; } }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "path", Label = "Click the start, any bends in order (zip ties, points, faces), then the end", Kind = FieldKind.Selection },
                new ToolField { Key = "type", Label = "Wire", Kind = FieldKind.Choice, Items = Wiring.WireTypes.Select(w => w.Name + (w.Gauge.Length > 0 && w.Gauge != w.Name ? " (" + w.Gauge + ")" : "")).ToList(), DefaultItem = 5 },
                new ToolField { Key = "slack", Label = "Slack (%)", Kind = FieldKind.Number, Min = 0, Max = 100, Default = 10 },
                new ToolField { Key = "id", Label = "Wire ID (blank: the next W number)", Kind = FieldKind.Text },
            };
        }

        internal override string Result(FeatureParams p, TeamStandards standards, object selection)
        {
            int picks = (int)p.Number("picks", 0);
            return picks < 2 ? "Click at least a start and an end (" + picks + " so far)." : picks + " points: ✓ routes the wire through them in this order.";
        }

        internal override string ApplyAll(SldWorks application, ModelDoc2 doc, FeatureParams p, IList<object> picks, IList<double[]> points, TeamStandards standards)
        {
            return Addin.RouteWire(application, doc, picks, points, (int)p.Number("type", 5), p.Number("slack", 10) / 100, (p["id"] ?? "").Trim());
        }
    }

    /// <summary>Zip Tie / Mount Point: points where the wiring is held, counted in the report and usable as wire bends.</summary>
    internal sealed class ZipTieTool : ElectricalTool
    {
        internal override string Kind { get { return "zip-tie"; } }
        internal override string Title { get { return "Zip Tie"; } }
        internal override bool MultipleSelections { get { return true; } }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "where", Label = "Click where each zip tie, clip or mount goes", Kind = FieldKind.Selection },
                new ToolField { Key = "kind", Label = "Kind", Kind = FieldKind.Choice, Items = new List<string> { "Zip tie", "Cable clip", "Wire anchor", "Printed guide" } },
            };
        }

        internal override string Result(FeatureParams p, TeamStandards standards, object selection)
        {
            int picks = (int)p.Number("picks", 0);
            return picks == 0 ? "Click where they go." : "✓ adds " + picks + " point" + (picks == 1 ? "" : "s") + " (Route Wire can use them as bends).";
        }

        internal override string ApplyAll(SldWorks application, ModelDoc2 doc, FeatureParams p, IList<object> picks, IList<double[]> points, TeamStandards standards)
        {
            string[] kinds = { "Zip tie", "Cable clip", "Wire anchor", "Printed guide" };
            return Addin.AddZipTies(application, doc, picks, points, kinds[Math.Max(0, Math.Min(3, (int)p.Number("kind", 0)))]);
        }
    }

    /// <summary>Harness or bundle: a named group of routed wires.</summary>
    internal sealed class HarnessTool : ElectricalTool
    {
        internal override string Kind { get { return "harness"; } }
        internal override string Title { get { return "Harness"; } }
        internal override bool MultipleSelections { get { return true; } }

        internal override int[] SelectionFilters
        {
            get { return new[] { (int)swSelectType_e.swSelEXTSKETCHSEGS, (int)swSelectType_e.swSelSKETCHSEGS, (int)swSelectType_e.swSelSKETCHES }; }
        }

        internal override List<ToolField> Fields(TeamStandards standards)
        {
            return new List<ToolField>
            {
                new ToolField { Key = "wires", Label = "Click the wires (in the graphics or the feature tree)", Kind = FieldKind.Selection },
                new ToolField { Key = "name", Label = "Name, like Elevator Harness", Kind = FieldKind.Text },
                new ToolField { Key = "kind", Label = "Kind", Kind = FieldKind.Choice, Items = new List<string> { "Harness (wires made up together)", "Bundle (wires run together for part of the way)" } },
            };
        }

        internal override string Problem(FeatureParams p)
        {
            return String.IsNullOrWhiteSpace(p["name"]) ? "Type a name for it." : null;
        }

        internal override string Result(FeatureParams p, TeamStandards standards, object selection)
        {
            int picks = (int)p.Number("picks", 0);
            return picks == 0 ? "Click the wires that go in it." : picks + " picked.";
        }

        internal override string ApplyAll(SldWorks application, ModelDoc2 doc, FeatureParams p, IList<object> picks, IList<double[]> points, TeamStandards standards)
        {
            return Addin.MakeHarness(doc, picks, p["name"].Trim(), p.Number("kind", 0) == 1);
        }
    }
}
