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
    /// CAD Hub's modeling tools: stock parts built in SOLIDWORKS (one master per kind, a configuration per size), spur gears,
    /// the belt and chain calculator, Lighten Plate, the FRC hole pattern, and tube profiles for Structural Members.
    /// The math lives in StockParts, SpurGear, BeltChain and PlateLighten (tested without SOLIDWORKS); this part only drives SOLIDWORKS.
    /// </summary>
    public sealed partial class Addin
    {
        private const double Meters = 0.0254; // SOLIDWORKS' API works in meters; CAD Hub's tools in inches.
        private const string DimensionsProperty = "CADHub.Dimensions";

        // ---------- stock parts ----------

        public void MakeStockPart()
        {
            using (var dialog = new StockDialog())
            {
                if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                InsertStock(dialog.Type, dialog.Length, dialog.PlateWidth, dialog.Copies);
            }
        }

        private static string StockMasterPath(WorkspaceInfo robot, StockType type)
        {
            return Path.Combine(robot.Root, "90_COTS", "Stock", type.Group, type.FileName + ".SLDPRT");
        }

        private void InsertStock(StockType type, double? length, double? width, int copies)
        {
            Execute(() =>
            {
                string problem = StockParts.Problem(type, length, width);
                if (problem != null) throw new InvalidOperationException(problem);
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireCurrentAddin(login, catalog);
                var robot = catalog.Robot;
                if (robot.Archived) throw new InvalidOperationException(robot.Name + " is archived and read-only.");
                var svn = new SvnWorkspace(login, robot);
                if (!svn.IsCheckedOut) throw new InvalidOperationException("Click Open Robot first, so the part has a robot to go into.");
                string configuration = StockParts.ConfigurationName(type, length ?? 0, width ?? 0);
                bool cancelled;
                var assembly = InsertTarget(catalog, type.Label, out cancelled);
                if (cancelled) return;
                string master = StockMasterPath(robot, type);
                if (!File.Exists(master))
                {
                    if (OperationDialog.Run("Checking the robot…", () => svn.OnServer(master)))
                        throw new InvalidOperationException("A teammate already made " + type.FileName + ". Get their changes first (Close & Update in the panel), then insert again.");
                    SaveNewFile(BuildStock(type, length ?? 0, width ?? 0, configuration), master);
                }
                else if (!HasConfiguration(master, configuration))
                    AddStockSize(type, master, configuration, length ?? 0, width ?? 0, svn);
                DeliverConfigured(assembly, master, configuration, Math.Max(1, copies), type.FileName + (configuration == "Default" ? "" : " " + configuration));
            });
        }

        private bool HasConfiguration(string path, string configuration)
        {
            var names = application.GetConfigurationNames(path) as object[];
            return names != null && names.OfType<string>().Any(n => String.Equals(n, configuration, StringComparison.OrdinalIgnoreCase));
        }

        // A teammate's size request a moment ago may hold the lock for a few seconds: wait for it instead of failing.
        private void LockForSize(SvnWorkspace svn, string master)
        {
            OperationDialog.Run("Adding the size to the team's stock part…", () =>
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { return SvnWorkspace.Exclusive(() => svn.Edit(master)); }
                    catch (InvalidOperationException busy) when (attempt < 5 && busy.Message.StartsWith("Locked by", StringComparison.Ordinal))
                    {
                        System.Threading.Thread.Sleep(3000);
                    }
                }
            });
        }

        // New size of an existing master: the same as Edit (it locks the part and checks it's the newest), then add the configuration
        // and save. It goes to the team with this student's next Submit, like any edit.
        private void AddStockSize(StockType type, string master, string configuration, double length, double width, SvnWorkspace svn)
        {
            LockForSize(svn, master);
            File.SetAttributes(master, File.GetAttributes(master) & ~FileAttributes.ReadOnly);
            var doc = FindOpen(master);
            bool opened = doc == null;
            int errors = 0, warnings = 0;
            if (opened)
            {
                application.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
                try { doc = application.OpenDoc6(master, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings); }
                finally { application.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART); }
                if (doc == null) throw new InvalidOperationException("SOLIDWORKS couldn't open " + Path.GetFileName(master) + " (error " + errors + ").");
            }
            try
            {
                if (doc.IsOpenedReadOnly() && !doc.SetReadOnlyState(false))
                    throw new InvalidOperationException("SOLIDWORKS couldn't make " + Path.GetFileName(master) + " editable. Close it and try again.");
                if (doc.AddConfiguration3(configuration, "", "", 0) == null)
                    throw new InvalidOperationException("SOLIDWORKS couldn't add the size " + configuration + " to " + Path.GetFileName(master) + ".");
                SetStockSize(doc, type, configuration, length, width);
                if (!doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                    throw new InvalidOperationException("Couldn't save " + Path.GetFileName(master) + " (error " + errors + ").");
            }
            finally
            {
                if (opened) application.CloseDoc(doc.GetTitle());
            }
        }

        // A new file (a stock master, a gear) saved into the robot: like any new part, it goes to the team with the student's
        // next Submit (where they can still uncheck it).
        private void SaveNewFile(ModelDoc2 doc, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            int errors = 0, warnings = 0;
            try
            {
                if (!doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings) || !File.Exists(path))
                    throw new InvalidOperationException("Couldn't save " + Path.GetFileName(path) + " (error " + errors + ").");
            }
            finally { application.CloseDoc(doc.GetTitle()); }
        }

        // Inserts a configuration of a part, as many times as asked; without an assembly, opens it in that configuration.
        private void DeliverConfigured(ModelDoc2 assemblyDoc, string path, string configuration, int copies, string name)
        {
            if (assemblyDoc == null)
            {
                int errors = 0, warnings = 0;
                var doc = application.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, configuration, ref errors, ref warnings);
                if (doc == null) throw new InvalidOperationException(name + " is in the robot at\n" + path + "\nbut SOLIDWORKS couldn't open it (error " + errors + ").");
                doc.ShowConfiguration2(configuration);
                ShowFlash("✓ " + name + " is open (90_COTS/Stock). Drag it into any assembly; it goes to the team with your next Submit.");
                return;
            }
            for (int i = 0; i < copies; i++) AddToAssembly(assemblyDoc, path, configuration, i * 0.05);
            ShowFlash("✓ Inserted " + (copies > 1 ? copies + "× " : "") + name + ". Mate it, save, and Submit (new stock parts and sizes go with it).");
        }

        // ---------- building stock geometry ----------

        private ModelDoc2 NewPart()
        {
            string template = application.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            var doc = application.NewDocument(template, 0, 0, 0) as ModelDoc2;
            if (doc == null) throw new InvalidOperationException("SOLIDWORKS couldn't start a new part. Check Tools → Options → Default Templates.");
            doc.Extension.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0, (int)swUnitSystem_e.swUnitSystem_IPS);
            doc.SketchManager.AddToDB = true;
            doc.SketchManager.DisplayWhenAdded = false;
            return doc;
        }

        // The standard planes by position (Front, Top, Right), so it works in every SOLIDWORKS language.
        private static void SelectPlane(ModelDoc2 doc, int which)
        {
            int seen = 0;
            for (var feature = doc.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
            {
                if (feature.GetTypeName2() != "RefPlane") continue;
                if (seen++ == which) { doc.ClearSelection2(true); feature.Select2(false, 0); return; }
            }
            throw new InvalidOperationException("The part template has no standard planes.");
        }

        private const int Front = 0, Top = 1, Right = 2;

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

        private static void Hexagon(ModelDoc2 doc, double acrossFlats)
        {
            // Inscribed: the circle touches the flats, so its radius is half the size across flats.
            doc.SketchManager.CreatePolygon(0, 0, 0, acrossFlats / 2 * Meters, 0, 0, 6, true);
        }

        private static void Rectangle(ModelDoc2 doc, double width, double height)
        {
            doc.SketchManager.CreateCenterRectangle(0, 0, 0, width / 2 * Meters, height / 2 * Meters, 0);
        }

        // Builds a stock part's master at its first size, named as that size's configuration.
        private ModelDoc2 BuildStock(StockType type, double length, double width, string configuration)
        {
            var doc = NewPart();
            var dimensions = new List<string>();
            switch (type.Shape)
            {
                case StockShape.BoxTube:
                    SelectPlane(doc, Right);
                    doc.SketchManager.InsertSketch(true);
                    Rectangle(doc, type.Width, type.Height);
                    Rectangle(doc, type.Width - 2 * type.Wall, type.Height - 2 * type.Wall);
                    dimensions.Add("D1@" + Extrude(doc, length).Name);
                    break;
                case StockShape.HexShaft:
                case StockShape.RoundShaft:
                    SelectPlane(doc, Right);
                    doc.SketchManager.InsertSketch(true);
                    if (type.Shape == StockShape.HexShaft) Hexagon(doc, type.Size); else doc.SketchManager.CreateCircleByRadius(0, 0, 0, type.Size / 2 * Meters);
                    dimensions.Add("D1@" + Extrude(doc, length).Name);
                    break;
                case StockShape.HexSpacer:
                case StockShape.RoundSpacer:
                    SelectPlane(doc, Front);
                    doc.SketchManager.InsertSketch(true);
                    doc.SketchManager.CreateCircleByRadius(0, 0, 0, type.Size / 2 * Meters);
                    if (type.Shape == StockShape.HexSpacer) Hexagon(doc, type.Bore); else doc.SketchManager.CreateCircleByRadius(0, 0, 0, type.Bore / 2 * Meters);
                    dimensions.Add("D1@" + Extrude(doc, length).Name);
                    break;
                case StockShape.Plate:
                    SelectPlane(doc, Top);
                    doc.SketchManager.InsertSketch(true);
                    var sides = (doc.SketchManager.CreateCenterRectangle(0, 0, 0, width / 2 * Meters, length / 2 * Meters, 0) as object[] ?? new object[0])
                        .OfType<SketchLine>().ToList();
                    string sketchName = ((Feature)doc.SketchManager.ActiveSketch).Name;
                    // Dimension one horizontal side (width) and one vertical side (length), so every size sets its own.
                    Func<SketchLine, bool> horizontal = line => Math.Abs(((SketchPoint)line.GetStartPoint2()).Y - ((SketchPoint)line.GetEndPoint2()).Y) < 1e-9;
                    foreach (var side in new[] { sides.FirstOrDefault(horizontal), sides.FirstOrDefault(l => !horizontal(l)) })
                    {
                        if (side == null) throw new InvalidOperationException("SOLIDWORKS didn't draw the plate's rectangle.");
                        ((SketchSegment)side).Select4(false, null);
                        var shown = doc.AddDimension2(0, 0, 0) as DisplayDimension;
                        if (shown == null) throw new InvalidOperationException("SOLIDWORKS couldn't dimension the plate.");
                        dimensions.Add(shown.GetDimension2(0).Name + "@" + sketchName);
                    }
                    Extrude(doc, type.Thickness);
                    break;
                case StockShape.Bearing:
                    SelectPlane(doc, Front);
                    doc.SketchManager.InsertSketch(true);
                    doc.SketchManager.CreateCircleByRadius(0, 0, 0, type.Size / 2 * Meters);
                    if (type.BoreHex) Hexagon(doc, type.Bore); else doc.SketchManager.CreateCircleByRadius(0, 0, 0, type.Bore / 2 * Meters);
                    Extrude(doc, type.Thickness);
                    if (type.FlangeDiameter > 0)
                    {
                        SelectPlane(doc, Front);
                        doc.SketchManager.InsertSketch(true);
                        doc.SketchManager.CreateCircleByRadius(0, 0, 0, type.FlangeDiameter / 2 * Meters);
                        doc.SketchManager.CreateCircleByRadius(0, 0, 0, type.Size / 2 * Meters);
                        Extrude(doc, type.FlangeThickness);
                    }
                    break;
            }
            doc.SketchManager.AddToDB = false;
            doc.SketchManager.DisplayWhenAdded = true;
            doc.Extension.CustomPropertyManager[""].Add3(DimensionsProperty, (int)swCustomInfoType_e.swCustomInfoText, String.Join(";", dimensions),
                (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            var active = doc.ConfigurationManager.ActiveConfiguration;
            if (active != null) active.Name = configuration;
            SetStockSize(doc, type, configuration, length, width);
            return doc;
        }

        // Sets one configuration's size (its own length, and width for plates), material and description.
        private static void SetStockSize(ModelDoc2 doc, StockType type, string configuration, double length, double width)
        {
            string stored = doc.Extension.CustomPropertyManager[""].Get(DimensionsProperty) ?? "";
            var names = stored.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var values = type.Shape == StockShape.Plate ? new[] { width, length } : new[] { length };
            for (int i = 0; i < names.Length && i < values.Length; i++)
            {
                var dimension = doc.Parameter(names[i]) as Dimension;
                if (dimension == null) throw new InvalidOperationException("The stock part is missing its size dimension (" + names[i] + "). Ask a mentor.");
                dimension.SetSystemValue3(values[i] * Meters, (int)swSetValueInConfiguration_e.swSetValue_InSpecificConfigurations, new[] { configuration });
            }
            var part = doc as PartDoc;
            if (part != null) part.SetMaterialPropertyName2(configuration, "SOLIDWORKS Materials", type.Material);
            var properties = doc.Extension.CustomPropertyManager[configuration];
            properties.Add3("Description", (int)swCustomInfoType_e.swCustomInfoText, StockParts.Description(type, configuration), (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            properties.Add3("Vendor", (int)swCustomInfoType_e.swCustomInfoText, "Stock", (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            doc.ShowConfiguration2(configuration);
            doc.ForceRebuild3(false);
        }

        // ---------- the PropertyManager tools (framework in ToolPage and CadHubTools) ----------

        // The add-in that's running, for CAD Hub features' Edit Feature (SOLIDWORKS creates those objects itself).
        internal static Addin Instance;

        private TeamStandards Standards { get { return paneCatalog?.Standards ?? TeamStandards.Defaults; } }

        internal static void EditFeature(SldWorks app, ModelDoc2 doc, Feature feature, CadHubTool tool, FeatureParams values)
        {
            var addin = Instance;
            Action<string> report = text => { if (addin != null) addin.ShowFlash(text); };
            new ToolPage(app, doc, tool, addin?.Standards ?? TeamStandards.Defaults, values, feature, report).Show();
        }

        // Opens a tool's page in the PropertyManager (the left side panel). The gear starts a new part when nothing is open; the
        // others work on the open part (Belt and Chain on any document). A read-only team part needs Edit first.
        private void ShowTool(CadHubTool tool)
        {
            try
            {
                var doc = application.ActiveDoc as ModelDoc2;
                if (doc == null && tool.Kind == "gear") doc = NewPart();
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

        public void BearingHole() { ShowTool(CadHubTool.Find("bearing-hole")); }

        // A bearing bore: a circle on the face (with its diameter dimension, so it can be changed later) and a cut through.
        internal static string CutBearingHole(SldWorks app, ModelDoc2 doc, object selection, double[] center, double[] axis, double bore, string bearing)
        {
            var face = selection as Face2;
            if (face == null && selection is Edge)
                // A round edge (resizing a hole): the flat face it lies on.
                face = (((Edge)selection).GetTwoAdjacentFaces2() as object[] ?? new object[0]).OfType<Face2>()
                    .FirstOrDefault(f => (f.GetSurface() as Surface)?.IsPlane() == true);
            if (face == null) throw new InvalidOperationException("Click the flat face where the bearing goes.");
            doc.ClearSelection2(true);
            ((Entity)face).Select4(false, null);
            doc.SketchManager.InsertSketch(true);
            var sketch = doc.SketchManager.ActiveSketch;
            if (sketch == null) throw new InvalidOperationException("SOLIDWORKS didn't start a sketch on that face. Try again.");
            var math = (MathUtility)app.GetMathUtility();
            var at = (double[])((MathPoint)((MathPoint)math.CreatePoint(center)).MultiplyTransform(sketch.ModelToSketchTransform)).ArrayData;
            doc.SketchManager.AddToDB = true;
            SketchSegment circle;
            try { circle = doc.SketchManager.CreateCircleByRadius(at[0], at[1], 0, bore / 2 * Meters); }
            finally { doc.SketchManager.AddToDB = false; }
            if (circle == null) throw new InvalidOperationException("SOLIDWORKS couldn't draw the bore.");
            doc.ClearSelection2(true);
            circle.Select4(false, null);
            doc.AddDimension2(at[0] + bore * Meters, at[1] + bore * Meters, 0);
            doc.ClearSelection2(true);
            var feature = CutThroughBoth(doc);
            string name = "Bearing Hole (" + bearing + ")";
            for (int n = 1; n < 50 && !TryRename(feature, n == 1 ? name : name + " " + n); n++) { }
            return "✓ " + feature.Name + ": ⌀" + bore.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) +
                " in, through. To change the size later, edit its sketch's diameter.";
        }

        private static bool TryRename(Feature feature, string name)
        {
            try { feature.Name = name; return feature.Name == name; }
            catch (Exception) { return false; }
        }

        // ---------- spur gears ----------

        public void MakeGear()
        {
            // In a part: the native page and an editable feature. In an assembly: a gear part made for the robot and inserted.
            var active = application.ActiveDoc as ModelDoc2;
            if (active == null || active.GetType() == (int)swDocumentTypes_e.swDocPART) { ShowTool(CadHubTool.Find("gear")); return; }
            using (var dialog = new GearDialog())
            {
                if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                Execute(() =>
                {
                    var login = GetLogin(false);
                    if (login == null) return;
                    var catalog = LoadCatalog(login);
                    RequireCurrentAddin(login, catalog);
                    var robot = catalog.Robot;
                    var svn = new SvnWorkspace(login, robot);
                    if (robot.Archived || !svn.IsCheckedOut) throw new InvalidOperationException("Click Open Robot first, so the gear has a robot to go into.");
                    string name = "Spur Gear " + StockParts.Inches(dialog.Pitch) + "DP " + StockParts.Inches(dialog.Pressure) + "PA " + dialog.Teeth + "T " +
                        dialog.BoreName + " " + StockParts.Inches(dialog.FaceWidth) + " FW";
                    string path = Path.Combine(robot.Root, "90_COTS", "Stock", "Gears", name + ".SLDPRT");
                    bool cancelled;
                    var assembly = InsertTarget(catalog, name, out cancelled);
                    if (cancelled) return;
                    if (!File.Exists(path))
                    {
                        if (OperationDialog.Run("Checking the robot…", () => svn.OnServer(path)))
                            throw new InvalidOperationException("A teammate already made " + name + ". Get their changes first (Close & Update in the panel), then insert again.");
                        var doc = NewPart();
                        SelectPlane(doc, Front);
                        doc.SketchManager.InsertSketch(true);
                        var outline = SpurGear.Outline(dialog.Teeth, dialog.Pitch, dialog.Pressure);
                        for (int i = 0; i < outline.Count; i++)
                        {
                            var a = outline[i]; var b = outline[(i + 1) % outline.Count];
                            doc.SketchManager.CreateLine(a[0] * Meters, a[1] * Meters, 0, b[0] * Meters, b[1] * Meters, 0);
                        }
                        if (dialog.BoreHex) Hexagon(doc, dialog.Bore);
                        else if (dialog.Bore > 0) doc.SketchManager.CreateCircleByRadius(0, 0, 0, dialog.Bore / 2 * Meters);
                        Extrude(doc, dialog.FaceWidth);
                        doc.SketchManager.AddToDB = false;
                        doc.SketchManager.DisplayWhenAdded = true;
                        ((PartDoc)doc).SetMaterialPropertyName2("", "SOLIDWORKS Materials", "6061 Alloy");
                        doc.Extension.CustomPropertyManager[""].Add3("Description", (int)swCustomInfoType_e.swCustomInfoText, name, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                        SaveNewFile(doc, path);
                    }
                    DeliverConfigured(assembly, path, "Default", dialog.Copies, name);
                });
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

        // The pockets for a face (for the page's result line, and the cut).
        internal static LightenPlan PlanPockets(Face2 face, LightenSettings settings)
        {
            double[] frame;
            List<double[]> outline;
            List<Circle2> holes;
            List<List<double[]>> cutouts;
            ReadPlate(face, out frame, out outline, out holes, out cutouts);
            return PlateLighten.Plan(outline, holes, null, settings, cutouts);
        }

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

        // ---------- FRC hole pattern on a tube face ----------

        public void AddHolePattern() { ShowTool(CadHubTool.Find("holes")); }

        // Where the holes go on a tube's side: positions along it, row offsets across it, the face's frame and the cut depth.
        private static string HoleLayout(Face2 face, FeatureParams p, out double[] frame, out List<double> along, out double[] rows, out double minX, out double middle, out double depth)
        {
            frame = null; along = null; rows = null; minX = middle = depth = 0;
            var surface = face?.GetSurface() as Surface;
            if (surface == null || !surface.IsPlane()) return "Click a long flat side of the tube.";
            frame = PlaneFrame(surface, face);
            var outline = new List<double[]>();
            ReadFace(face, frame, outline, new List<Circle2>());
            if (outline.Count < 3) return "Couldn't read that face.";
            minX = outline.Min(q => q[0]);
            double maxX = outline.Max(q => q[0]), minY = outline.Min(q => q[1]), maxY = outline.Max(q => q[1]);
            double length = maxX - minX, width = maxY - minY;
            middle = (minY + maxY) / 2;
            if (width > length) return "That's the end of the tube: click a long side.";
            double diameter = p.Number("diameter", 0.196), rowSpacing = p.Number("rowSpacing", 0.5);
            int count = (int)p.Number("rows", 0);
            rows = count == 0 ? StockParts.FillRows(width, rowSpacing, diameter) : StockParts.RowOffsets(count, rowSpacing);
            if (rows.Max() + diameter / 2 > width / 2) return "Those rows don't fit across a " + StockParts.Inches(width) + "\" side.";
            along = StockParts.HolePositions(length, p.Number("start", 0.25), p.Number("spacing", 0.5), diameter);
            // How deep: the body's size across the face, so the holes go through both walls but nothing behind the tube.
            var box = (double[])((Body2)face.GetBody()).GetBodyBox();
            double[] normal = Normalize(Cross(new[] { frame[3], frame[4], frame[5] }, new[] { frame[6], frame[7], frame[8] }));
            depth = (Math.Abs(normal[0]) * (box[3] - box[0]) + Math.Abs(normal[1]) * (box[4] - box[1]) + Math.Abs(normal[2]) * (box[5] - box[2])) / Meters;
            return null;
        }

        internal static string PlanHoles(Face2 face, FeatureParams p, out int count, out int rowCount)
        {
            double[] frame, rows;
            List<double> along;
            double minX, middle, depth;
            string problem = HoleLayout(face, p, out frame, out along, out rows, out minX, out middle, out depth);
            count = problem == null ? along.Count * rows.Length : 0;
            rowCount = problem == null ? rows.Length : 0;
            return problem;
        }

        internal static string CutHolePattern(SldWorks app, ModelDoc2 doc, Face2 face, FeatureParams p)
        {
            double[] frame, rows;
            List<double> along;
            double minX, middle, depth;
            string problem = HoleLayout(face, p, out frame, out along, out rows, out minX, out middle, out depth);
            if (problem != null) throw new InvalidOperationException(problem);
            double diameter = p.Number("diameter", 0.196);
            doc.ClearSelection2(true);
            ((Entity)face).Select4(false, null);
            doc.SketchManager.InsertSketch(true);
            var sketch = doc.SketchManager.ActiveSketch;
            if (sketch == null) throw new InvalidOperationException("SOLIDWORKS didn't start a sketch on that face. Try again.");
            var toSketch = sketch.ModelToSketchTransform;
            var math = app.GetMathUtility() as MathUtility;
            doc.SketchManager.AddToDB = true;
            doc.SketchManager.DisplayWhenAdded = false;
            int count = 0;
            try
            {
                foreach (double x in along)
                    foreach (double row in rows)
                    {
                        double px = minX + x, py = middle + row;
                        var model = new[] { frame[0] + (px * frame[3] + py * frame[6]) * Meters, frame[1] + (px * frame[4] + py * frame[7]) * Meters, frame[2] + (px * frame[5] + py * frame[8]) * Meters };
                        var point = (double[])((MathPoint)((MathPoint)math.CreatePoint(model)).MultiplyTransform(toSketch)).ArrayData;
                        doc.SketchManager.CreateCircleByRadius(point[0], point[1], 0, diameter / 2 * Meters);
                        count++;
                    }
            }
            finally
            {
                doc.SketchManager.AddToDB = false;
                doc.SketchManager.DisplayWhenAdded = true;
            }
            Extrude(doc, depth + 0.01, true);
            return "✓ Added " + count + " holes in " + rows.Length + (rows.Length == 1 ? " row" : " rows") + " (⌀" + StockParts.Inches(diameter) + "\" every " +
                StockParts.Inches(p.Number("spacing", 0.5)) + "\"). It's an ordinary cut: edit or delete it like any feature.";
        }
    }
}
