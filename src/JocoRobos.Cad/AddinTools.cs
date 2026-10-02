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
                InsertStock(dialog.Type, dialog.Length, dialog.Width, dialog.Copies);
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
                    var doc = BuildStock(type, length ?? 0, width ?? 0, configuration);
                    SaveNewTeamFile(doc, master, svn, robot, "Add stock part " + type.FileName + " (" + configuration + ")");
                }
                else if (!HasConfiguration(master, configuration))
                    AddStockSize(type, master, configuration, length ?? 0, width ?? 0, svn, robot);
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

        // New size of an existing master: lock it (Edit checks it's the newest), add the configuration, save, and submit straight away.
        private void AddStockSize(StockType type, string master, string configuration, double length, double width, SvnWorkspace svn, WorkspaceInfo robot)
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
                var item = new SubmitItem { Kind = SubmitKind.Modified, Path = master, Workspace = robot };
                OperationDialog.Run("Sharing the new size with the team…", () => SvnWorkspace.Exclusive(() =>
                    svn.Submit(new List<SubmitItem> { item }, "Add size " + configuration + " to " + type.FileName)));
            }
            finally
            {
                if (opened) application.CloseDoc(doc.GetTitle());
                else doc.SetReadOnlyState(true);
                if (File.Exists(master)) File.SetAttributes(master, File.GetAttributes(master) | FileAttributes.ReadOnly);
            }
        }

        // A brand-new team file (a stock master, a gear): saved into the robot and submitted on its own, so teammates get it and
        // nobody makes a second one under the same name.
        private void SaveNewTeamFile(ModelDoc2 doc, string path, SvnWorkspace svn, WorkspaceInfo robot, string comment)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            int errors = 0, warnings = 0;
            try
            {
                if (!doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings) || !File.Exists(path))
                    throw new InvalidOperationException("Couldn't save " + Path.GetFileName(path) + " (error " + errors + ").");
            }
            finally { application.CloseDoc(doc.GetTitle()); }
            var item = new SubmitItem { Kind = SubmitKind.New, Path = path, Workspace = robot };
            OperationDialog.Run("Sharing " + Path.GetFileNameWithoutExtension(path) + " with the team…", () => SvnWorkspace.Exclusive(() =>
                svn.Submit(new List<SubmitItem> { item }, comment)));
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
                ShowFlash("✓ " + name + " is open (90_COTS/Stock). Drag it into any assembly; pick the size in the configuration list.");
                return;
            }
            for (int i = 0; i < copies; i++) AddToAssembly(assemblyDoc, path, configuration, i * 0.05);
            ShowFlash("✓ Inserted " + (copies > 1 ? copies + "× " : "") + name + ". Mate it, save, and Submit.");
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
                    // Holes for the longest tube: circles past the end of a shorter one cut nothing, so every length gets the pattern.
                    // They follow the tube whichever way SOLIDWORKS extruded it (+X normally).
                    var solid = (((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[])?.OfType<Body2>().FirstOrDefault();
                    double direction = solid == null || ((double[])solid.GetBodyBox())[3] > 0.001 ? 1 : -1;
                    var along = StockParts.HolePositions(type.MaxLength).Select(x => x * direction).ToList();
                    SelectPlane(doc, Top);    // through the wide faces (top and bottom)
                    doc.SketchManager.InsertSketch(true);
                    foreach (double x in along) foreach (double row in type.WideRows) doc.SketchManager.CreateCircleByRadius(x * Meters, row * Meters, 0, StockType.HoleDiameter / 2 * Meters);
                    CutThroughBoth(doc);
                    SelectPlane(doc, Front);  // through the narrow faces (the sides)
                    doc.SketchManager.InsertSketch(true);
                    foreach (double x in along) foreach (double row in type.NarrowRows) doc.SketchManager.CreateCircleByRadius(x * Meters, row * Meters, 0, StockType.HoleDiameter / 2 * Meters);
                    CutThroughBoth(doc);
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

        // ---------- spur gears ----------

        public void MakeGear()
        {
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
                        SaveNewTeamFile(doc, path, svn, robot, "Add " + name);
                    }
                    DeliverConfigured(assembly, path, "Default", dialog.Copies, name);
                });
            }
        }

        // ---------- belt and chain calculator ----------

        public void BeltChainCalculator()
        {
            using (var dialog = new BeltChainDialog(SetSelectedDimension))
                dialog.ShowDialog(new SolidWorksWindow());
        }

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

        public void LightenPlate()
        {
            Execute(() =>
            {
                var doc = application.ActiveDoc as ModelDoc2;
                var face = (doc?.SelectionManager as SelectionMgr)?.GetSelectedObject6(1, -1) as Face2;
                var surface = face?.GetSurface() as Surface;
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART || surface == null || !surface.IsPlane())
                    throw new InvalidOperationException("Open the plate (as a part), click its flat face once, then Lighten Plate.");
                if (doc.IsOpenedReadOnly()) throw new InvalidOperationException("Click Edit on " + doc.GetTitle() + " first, so it can be changed.");
                var frame = PlaneFrame(surface, face);
                var outline = new List<double[]>();
                var holes = new List<Circle2>();
                ReadFace(face, frame, outline, holes);
                if (outline.Count < 3) throw new InvalidOperationException("Couldn't read the outline of that face.");
                using (var dialog = new LightenDialog())
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    var plan = PlateLighten.Plan(outline, holes, null, dialog.Settings);
                    if (plan.Pockets.Count == 0)
                        throw new InvalidOperationException("No pockets fit with these settings. Try narrower ribs, a smaller border or ring, or a smaller minimum pocket.");
                    double before = doc.Extension.CreateMassProperty().Mass;
                    ((Entity)face).Select4(false, null);
                    doc.SketchManager.InsertSketch(true);
                    var sketch = doc.SketchManager.ActiveSketch;
                    var toSketch = sketch.ModelToSketchTransform;
                    var math = application.GetMathUtility() as MathUtility;
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
                                    doc.SketchManager.CreateArc(c[0], c[1], 0, a[0], a[1], 0, b[0], b[1], 0, direction);
                                }
                            }
                    }
                    finally
                    {
                        doc.SketchManager.AddToDB = false;
                        doc.SketchManager.DisplayWhenAdded = true;
                    }
                    if (dialog.Depth <= 0) CutThroughBoth(doc); else Extrude(doc, dialog.Depth, true);
                    double after = doc.Extension.CreateMassProperty().Mass;
                    double saved = (before - after) * 2.20462;
                    ShowFlash("✓ Lightened " + doc.GetTitle() + ": " + plan.Pockets.Count + " pockets, −" + saved.ToString("0.00") + " lb (" +
                        (before > 0 ? (100 * (before - after) / before).ToString("0") : "?") + "%). It's an ordinary cut: edit or delete it like any feature.");
                }
            });
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

        // The face's outline (as points in inches, in the frame) and its holes; a non-round cutout counts as a hole around it.
        private static void ReadFace(Face2 face, double[] frame, List<double[]> outline, List<Circle2> holes)
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
                if (points.Count == 0) continue;
                double cx = points.Average(p => p[0]), cy = points.Average(p => p[1]);
                holes.Add(new Circle2 { X = cx, Y = cy, R = points.Max(p => Math.Sqrt((p[0] - cx) * (p[0] - cx) + (p[1] - cy) * (p[1] - cy))) });
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
                    var tess = curve.GetTessPts(0.0005, 0.005, start, end) as double[];
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

        // Holes every 0.5" along the selected flat face of a tube (for example a Structural Member), through both walls.
        public void AddHolePattern()
        {
            Execute(() =>
            {
                var doc = application.ActiveDoc as ModelDoc2;
                var face = (doc?.SelectionManager as SelectionMgr)?.GetSelectedObject6(1, -1) as Face2;
                var surface = face?.GetSurface() as Surface;
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART || surface == null || !surface.IsPlane())
                    throw new InvalidOperationException("Click the flat side of a tube once (in a part), then Add FRC Hole Pattern.");
                if (doc.IsOpenedReadOnly()) throw new InvalidOperationException("Click Edit on " + doc.GetTitle() + " first, so it can be changed.");
                var frame = PlaneFrame(surface, face);
                var outline = new List<double[]>();
                ReadFace(face, frame, outline, new List<Circle2>());
                if (outline.Count < 3) throw new InvalidOperationException("Couldn't read that face.");
                double minX = outline.Min(p => p[0]), maxX = outline.Max(p => p[0]), minY = outline.Min(p => p[1]), maxY = outline.Max(p => p[1]);
                double length = maxX - minX, width = maxY - minY, middle = (minY + maxY) / 2;
                if (width > length) throw new InvalidOperationException("Select a long side of the tube.");
                var rows = width >= 1.9 ? new[] { -0.5, 0.5 } : width >= 1.4 ? new[] { -0.25, 0.25 } : new[] { 0.0 };
                // How deep: the body's size across the face, so the holes go through both walls but nothing behind the tube.
                var box = (double[])((Body2)face.GetBody()).GetBodyBox();
                double[] normal = Normalize(Cross(new[] { frame[3], frame[4], frame[5] }, new[] { frame[6], frame[7], frame[8] }));
                double depth = (Math.Abs(normal[0]) * (box[3] - box[0]) + Math.Abs(normal[1]) * (box[4] - box[1]) + Math.Abs(normal[2]) * (box[5] - box[2])) / Meters;
                ((Entity)face).Select4(false, null);
                doc.SketchManager.InsertSketch(true);
                var toSketch = doc.SketchManager.ActiveSketch.ModelToSketchTransform;
                var math = application.GetMathUtility() as MathUtility;
                doc.SketchManager.AddToDB = true;
                doc.SketchManager.DisplayWhenAdded = false;
                int count = 0;
                try
                {
                    foreach (double x in StockParts.HolePositions(length))
                        foreach (double row in rows)
                        {
                            double px = minX + x, py = middle + row;
                            var model = new[] { frame[0] + (px * frame[3] + py * frame[6]) * Meters, frame[1] + (px * frame[4] + py * frame[7]) * Meters, frame[2] + (px * frame[5] + py * frame[8]) * Meters };
                            var point = (double[])((MathPoint)((MathPoint)math.CreatePoint(model)).MultiplyTransform(toSketch)).ArrayData;
                            doc.SketchManager.CreateCircleByRadius(point[0], point[1], 0, StockType.HoleDiameter / 2 * Meters);
                            count++;
                        }
                }
                finally
                {
                    doc.SketchManager.AddToDB = false;
                    doc.SketchManager.DisplayWhenAdded = true;
                }
                Extrude(doc, depth + 0.01, true);
                ShowFlash("✓ Added " + count + " holes (⌀0.196\" every 0.5\"). It's an ordinary cut: edit or delete it like any feature.");
            });
        }

        // ---------- tube profiles for Structural Members ----------

        // Makes FRC box tube and hex shaft profiles and adds their folder to SOLIDWORKS' weldment profile locations, so
        // Insert → Structural Member offers "FRC". Only CAD Hub's own folder is added; existing locations stay.
        public void SetUpTubeProfiles()
        {
            Execute(() =>
            {
                string root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "JocoRobos.Cad", "Weldment Profiles");
                int made = 0;
                foreach (var type in StockParts.Types.Where(t => t.Shape == StockShape.BoxTube || t.Shape == StockShape.HexShaft || t.Shape == StockShape.RoundShaft))
                {
                    string folder = Path.Combine(root, "FRC", type.Shape == StockShape.BoxTube ? "Box Tube" : "Shaft");
                    string file = Path.Combine(folder, type.FileName + ".SLDLFP");
                    if (File.Exists(file)) continue;
                    Directory.CreateDirectory(folder);
                    var doc = NewPart();
                    SelectPlane(doc, Front);
                    doc.SketchManager.InsertSketch(true);
                    if (type.Shape == StockShape.BoxTube)
                    {
                        Rectangle(doc, type.Width, type.Height);
                        Rectangle(doc, type.Width - 2 * type.Wall, type.Height - 2 * type.Wall);
                    }
                    else if (type.Shape == StockShape.HexShaft) Hexagon(doc, type.Size);
                    else doc.SketchManager.CreateCircleByRadius(0, 0, 0, type.Size / 2 * Meters);
                    doc.SketchManager.InsertSketch(true);
                    doc.SketchManager.AddToDB = false;
                    doc.SketchManager.DisplayWhenAdded = true;
                    int errors = 0, warnings = 0;
                    try
                    {
                        if (!doc.Extension.SaveAs3(file, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings))
                            throw new InvalidOperationException("Couldn't save the profile " + Path.GetFileName(file) + " (error " + errors + ").");
                    }
                    finally { application.CloseDoc(doc.GetTitle()); }
                    made++;
                }
                int setting = (int)swUserPreferenceStringValue_e.swFileLocationsWeldmentProfiles;
                string locations = application.GetUserPreferenceStringValue(setting) ?? "";
                if (!locations.Split(';').Any(l => String.Equals(l.Trim().TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)))
                    application.SetUserPreferenceStringValue(setting, locations.Length == 0 ? root : locations.TrimEnd(';') + ";" + root);
                Message((made > 0 ? "Made " + made + " FRC profiles. " : "The FRC profiles are set up. ") +
                    "To use them: sketch your frame in a part (lines where the tubes go), then Insert → Weldments → Structural Member, " +
                    "Standard: FRC, Type: Box Tube, and pick the size. Click a tube's side and use Tools → CAD Hub → Add FRC Hole Pattern for the holes.");
            });
        }
    }
}
