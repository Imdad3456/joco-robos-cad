using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace JocoRobos.Cad
{
    /// <summary>
    /// The native PropertyManager page every CAD Hub modeling tool uses: its fields (common first, Advanced collapsed), a line
    /// with the exact result, a live preview, and the green check / red X. New: inserts a CAD Hub feature. Edit: updates it.
    /// SOLIDWORKS calls this over COM, so nothing may throw out of a callback.
    /// </summary>
    // SOLIDWORKS takes the handler as IDispatch: it needs the automatic dispatch interface (ClassInterfaceType.None has none, and
    // CreatePropertyManagerPage then fails to marshal it).
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class ToolPage : IPropertyManagerPage2Handler9
    {
        internal const string ProgId = "JocoRobos.Cad.Feature";
        internal const string ParameterName = "cadhub";

        private readonly SldWorks application;
        private readonly ModelDoc2 doc;
        private readonly CadHubTool tool;
        private readonly TeamStandards standards;
        private readonly FeatureParams values;
        private readonly Feature editing;
        private readonly Action<string> report;
        private readonly List<ToolField> fields;
        private readonly Dictionary<int, ToolField> byId = new Dictionary<int, ToolField>();
        private readonly Dictionary<int, object> controls = new Dictionary<int, object>();
        private PropertyManagerPage2 page;
        private PropertyManagerPageLabel result;
        private List<Body2> preview = new List<Body2>();
        /// <summary>Set when the page was opened from an assembly on a new part: called after ✓ (true) or ✗ (false).</summary>
        internal Action<bool> Finished;
        internal FeatureParams Values { get { return values; } }
        private bool okay;
        private object chosen;
        private double[] chosenPoint;
        // Every pick in the order they were clicked (Route Wire's start, bends and end), with where each was clicked.
        private List<object> picks = new List<object>();
        private List<double[]> pickPoints = new List<double[]>();
        private const int SelectionMark = 1, ResultId = 900;

        internal ToolPage(SldWorks application, ModelDoc2 doc, CadHubTool tool, TeamStandards standards, FeatureParams start, Feature editing, Action<string> report)
        {
            this.application = application;
            this.doc = doc;
            this.tool = tool;
            this.standards = standards ?? TeamStandards.Defaults;
            this.editing = editing;
            this.report = report;
            values = start ?? Remembered(tool.Kind);
            values["kind"] = tool.Kind;
            fields = tool.Fields(this.standards);
            foreach (var field in fields.Where(f => values[f.Key] == null))
                if (field.Kind == FieldKind.Choice) values.Set(field.Key, field.DefaultItem);
                else if (field.Kind != FieldKind.Selection) values.Set(field.Key, field.Default);
        }

        private const string SavedKey = @"Software\JOCO ROBOS\CAD\Tools";
        // Where the student picked the selection: never remembered, it belongs to one part.
        private static readonly string[] Placement = { "cx", "cy", "cz", "ax", "ay", "az", "ux", "uy", "uz", "bore", "picks" };

        // Each tool opens with the settings it was last used with on this computer.
        private static FeatureParams Remembered(string kind)
        {
            try
            {
                using (var saved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SavedKey))
                {
                    var values = FeatureParams.Decode(saved?.GetValue(kind) as string ?? "");
                    foreach (var key in Placement) values.Values.Remove(key);
                    return values;
                }
            }
            catch (Exception) { return new FeatureParams(); }
        }

        private void Remember()
        {
            try
            {
                var copy = FeatureParams.Decode(values.Encode());
                foreach (var key in Placement) copy.Values.Remove(key);
                using (var saved = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SavedKey)) saved.SetValue(tool.Kind, copy.Encode());
            }
            catch (Exception) { } // A convenience only.
        }

        // The page on screen, if any: SOLIDWORKS can crash when a second PropertyManager page is opened over one that's still up.
        private static ToolPage open;
        private object preselected;

        internal void Show()
        {
            if (open != null)
                throw new InvalidOperationException(open.tool.Title + " is already open on the left. Finish it with ✓ (or cancel with ✗) first.");
            // Something already selected that this tool takes (the plate's face, the tube's side): it starts in the box.
            ErrorLog.Step("opening " + tool.Title);
            preselected = null;
            try
            {
                var manager = (SelectionMgr)doc.SelectionManager;
                if (tool.NeedsSelection && manager.GetSelectedObjectCount2(-1) > 0 && tool.SelectionFilters.Contains(manager.GetSelectedObjectType3(1, -1)))
                    preselected = manager.GetSelectedObject6(1, -1);
            }
            catch (Exception) { }
            int errors = 0;
            page = application.CreatePropertyManagerPage((editing != null ? "Edit " : "") + "CAD Hub " + tool.Title,
                (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton | swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton),
                this, ref errors) as PropertyManagerPage2;
            if (page == null) throw new InvalidOperationException("SOLIDWORKS couldn't open the " + tool.Title + " panel (error " + errors + ").");
            var main = (PropertyManagerPageGroup)page.AddGroupBox(1, tool.Title,
                (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded));
            var advanced = fields.Any(f => f.Advanced) ? (PropertyManagerPageGroup)page.AddGroupBox(2, "Advanced", (int)swAddGroupBoxOptions_e.swGroupBoxOptions_Visible) : null;
            int id = 10;
            foreach (var field in fields)
            {
                var group = field.Advanced ? advanced : main;
                short align = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge;
                int options = (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled);
                group.AddControl2(id + 1, (short)swPropertyManagerPageControlType_e.swControlType_Label, field.Label, align, options, field.Tip);
                object control;
                switch (field.Kind)
                {
                    case FieldKind.Choice:
                        var combo = (PropertyManagerPageCombobox)group.AddControl2(id, (short)swPropertyManagerPageControlType_e.swControlType_Combobox, field.Label, align, options, field.Tip);
                        combo.AddItems(field.Items.ToArray());
                        combo.Height = 140;
                        combo.CurrentSelection = (short)Math.Max(0, Math.Min(field.Items.Count - 1, (int)values.Number(field.Key, 0)));
                        control = combo;
                        break;
                    case FieldKind.Text:
                        var text = (PropertyManagerPageTextbox)group.AddControl2(id, (short)swPropertyManagerPageControlType_e.swControlType_Textbox, field.Label, align, options, field.Tip);
                        text.Text = values[field.Key] ?? "";
                        control = text;
                        break;
                    case FieldKind.Selection:
                        var box = (PropertyManagerPageSelectionbox)group.AddControl2(id, (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox, field.Label, align, options, field.Tip);
                        box.SingleEntityOnly = !tool.MultipleSelections;
                        box.Mark = SelectionMark;
                        box.Height = (short)(tool.MultipleSelections ? 90 : 30);
                        box.SetSelectionFilters(tool.SelectionFilters);
                        control = box;
                        break;
                    default:
                        var number = (PropertyManagerPageNumberbox)group.AddControl2(id, (short)swPropertyManagerPageControlType_e.swControlType_Numberbox, field.Label, align, options, field.Tip);
                        bool length = field.Kind == FieldKind.Length;
                        bool whole = !length && field.Step >= 1;
                        double scale = length ? 0.0254 : 1;
                        number.SetRange2(length ? (int)swNumberboxUnitType_e.swNumberBox_Length : whole ? (int)swNumberboxUnitType_e.swNumberBox_UnitlessInteger : (int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble,
                            field.Min * scale, field.Max * scale, true, field.Step * scale, field.Step * scale * 4, field.Step * scale / 4);
                        number.Value = values.Number(field.Key, field.Default) * scale;
                        control = number;
                        break;
                }
                controls[id] = control;
                byId[id] = field;
                id += 2;
            }
            result = (PropertyManagerPageLabel)main.AddControl2(ResultId, (short)swPropertyManagerPageControlType_e.swControlType_Label, " ",
                (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled), "");
            page.Show2(0);
            open = this;
            ErrorLog.Step(tool.Title + " shown");
        }

        // New values: update the result line and the preview body.
        private void Refresh()
        {
            try
            {
                if (tool.NeedsSelection && chosen != null) tool.Capture(chosen, chosenPoint, values, standards);
                string problem = tool.Problem(values);
                if (result != null) result.Caption = problem ?? tool.Result(values, standards, chosen);
                ClearPreview();
                if (problem != null) return;
                preview = tool.BuildBodies(application, values, true);
                foreach (var body in preview) body.Display3(null, 0x2090F0, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone); // orange-ish, like SOLIDWORKS previews
            }
            catch (Exception exception) { ErrorLog.Write(tool.Title + " preview", exception); }
        }

        private void ClearPreview()
        {
            foreach (var body in preview)
                try { body.Hide(doc as PartDoc); } catch (Exception) { }
            preview = new List<Body2>();
        }

        // ---------- PropertyManager callbacks ----------

        public void OnNumberboxChanged(int Id, double Value)
        {
            ToolField field;
            if (!byId.TryGetValue(Id, out field)) return;
            values.Set(field.Key, field.Kind == FieldKind.Length ? Value / 0.0254 : Value);
            Refresh();
        }

        public void OnComboboxSelectionChanged(int Id, int Item)
        {
            ToolField field;
            if (!byId.TryGetValue(Id, out field)) return;
            values.Set(field.Key, Item);
            Refresh();
        }

        public bool OnSubmitSelection(int Id, object Selection, int SelType, ref string ItemText)
        {
            try
            {
                if (!tool.SelectionFilters.Contains(SelType) || !tool.Accepts(Selection)) return false;
                chosen = Selection;
                var manager = (SelectionMgr)doc.SelectionManager;
                chosenPoint = null;
                // The click point arrives just after this call; read it then (Refresh runs again from OnSelectionboxListChanged).
                return true;
            }
            catch (Exception exception) { ErrorLog.Write(tool.Title + " selection", exception); return false; }
        }

        public void OnSelectionboxListChanged(int Id, int Count)
        {
            try
            {
                var manager = (SelectionMgr)doc.SelectionManager;
                picks = Enumerable.Range(1, Count).Select(i => manager.GetSelectedObject6(i, SelectionMark)).ToList();
                pickPoints = Enumerable.Range(1, Count).Select(i => manager.GetSelectionPoint2(i, SelectionMark) as double[]).ToList();
                chosen = picks.FirstOrDefault();
                chosenPoint = pickPoints.FirstOrDefault();
                values.Set("picks", Count);
                if (chosen == null) { foreach (var key in new[] { "cx", "cy", "cz", "ax", "ay", "az" }) values.Values.Remove(key); }
                Refresh();
            }
            catch (Exception exception) { ErrorLog.Write(tool.Title + " selection", exception); }
        }

        public void OnClose(int Reason)
        {
            if (open == this) open = null;
            okay = Reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay;
            try
            {
                // Selections are gone after closing: freeze them now.
                if (okay && tool.NeedsSelection && tool.SelectionOptional) { }
                else if (okay && tool.NeedsSelection)
                {
                    string problem = chosen == null && values["cx"] == null ? "Select where it goes first." : chosen != null ? tool.Capture(chosen, chosenPoint, values, standards) : null;
                    if (problem != null) { okay = false; report("✗ " + tool.Title + ": " + problem); }
                }
                else if (okay) tool.Capture(null, null, values, standards);
                string invalid = okay ? tool.Problem(values) : null;
                if (invalid != null) { okay = false; report("✗ " + tool.Title + ": " + invalid); }
            }
            catch (Exception exception) { okay = false; ErrorLog.Write(tool.Title + " closing", exception); }
        }

        public void AfterClose()
        {
            ClearPreview();
            if (open == this) open = null;
            if (!okay) { Finished?.Invoke(false); return; }
            Remember();
            if (!tool.AnyDocument && doc.IsOpenedReadOnly())
            {
                report("✗ " + doc.GetTitle() + " is read-only: click Edit on it first, then " + tool.Title + " again (your settings are remembered).");
                return;
            }
            try
            {
                if (!tool.MakesFeature)
                {
                    ErrorLog.Step(tool.Title + ": applying");
                    string done = tool.ApplyAll(application, doc, values, picks.Count > 0 ? picks : new List<object> { chosen }, pickPoints.Count > 0 ? pickPoints : new List<double[]> { chosenPoint }, standards);
                    ErrorLog.Step(tool.Title + ": done");
                    if (!String.IsNullOrEmpty(done)) report(done);
                }
                else if (editing != null) { Update(); report("✓ Updated " + editing.Name + "."); }
                else
                {
                    var feature = Insert();
                    if (Finished != null) Finished(true);
                    else report("✓ Added " + feature.Name + ". Right-click it → Edit Feature to change it.");
                }
            }
            catch (Exception exception)
            {
                ErrorLog.Write(tool.Title, exception);
                report("✗ " + tool.Title + " didn't work: " + exception.Message);
            }
        }

        private Feature Insert()
        {
            object editBodies = null;
            if (tool.Cuts)
            {
                var body = (chosen as Face2)?.GetBody() as Body2 ?? ((chosen as Edge)?.GetTwoAdjacentFaces2() as object[])?.OfType<Face2>().Select(f => f.GetBody() as Body2).FirstOrDefault();
                if (body == null) throw new InvalidOperationException("Couldn't find the body to cut.");
                editBodies = new object[] { body };
            }
            var feature = doc.FeatureManager.InsertMacroFeature3("CAD Hub " + tool.Title, ProgId, null, new[] { ParameterName },
                new[] { (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString }, new[] { values.Encode() }, null, null, editBodies, null,
                (int)swMacroFeatureOptions_e.swMacroFeatureByDefault);
            if (feature == null) throw new InvalidOperationException("SOLIDWORKS didn't accept the feature. Check the settings and the selection.");
            return feature;
        }

        private void Update()
        {
            var data = (MacroFeatureData)editing.GetDefinition();
            data.AccessSelections(doc, null);
            data.SetStringByName(ParameterName, values.Encode());
            if (!editing.ModifyDefinition(data, doc, null))
            {
                data.ReleaseSelectionAccess();
                throw new InvalidOperationException("SOLIDWORKS didn't accept the new settings.");
            }
        }

        // ---------- the rest of the handler interface (unused) ----------
        // The page is fully up: now the face already selected goes into its box (not while SOLIDWORKS is still building the page).
        public void AfterActivation()
        {
            try
            {
                if (preselected != null && tool.Accepts(preselected))
                {
                    var data = (SelectData)((SelectionMgr)doc.SelectionManager).CreateSelectData();
                    data.Mark = SelectionMark;
                    doc.ClearSelection2(true);
                    var entity = preselected as Entity;
                    if (entity != null) entity.Select4(true, data);
                    else if (preselected is Feature) ((Feature)preselected).Select2(true, SelectionMark);
                    else ((preselected as DisplayDimension)?.GetAnnotation() as Annotation)?.Select3(true, data);
                    if (chosen == null) { chosen = preselected; picks = new List<object> { preselected }; pickPoints = new List<double[]> { null }; } // In case SOLIDWORKS doesn't report it to the box.
                }
                preselected = null;
                Refresh();
            }
            catch (Exception exception) { ErrorLog.Write(tool.Title + " opening", exception); }
        }
        public bool OnHelp() { return false; }
        public bool OnPreviousPage() { return false; }
        public bool OnNextPage() { return false; }
        public bool OnPreview() { return false; }
        public void OnWhatsNew() { }
        public void OnUndo() { }
        public void OnRedo() { }
        public bool OnTabClicked(int Id) { return true; }
        public void OnGroupExpand(int Id, bool Expanded) { }
        public void OnGroupCheck(int Id, bool Checked) { }
        public void OnCheckboxCheck(int Id, bool Checked) { }
        public void OnOptionCheck(int Id) { }
        public void OnButtonPress(int Id) { }
        public void OnTextboxChanged(int Id, string Text)
        {
            ToolField field;
            if (!byId.TryGetValue(Id, out field)) return;
            values[field.Key] = Text ?? "";
            Refresh();
        }
        public void OnComboboxEditChanged(int Id, string Text) { }
        public void OnListboxSelectionChanged(int Id, int Item) { }
        public void OnSelectionboxFocusChanged(int Id) { }
        public void OnSelectionboxCalloutCreated(int Id) { }
        public void OnSelectionboxCalloutDestroyed(int Id) { }
        public int OnActiveXControlCreated(int Id, bool Status) { return 0; }
        public void OnSliderPositionChanged(int Id, double Value) { }
        public void OnSliderTrackingCompleted(int Id, double Value) { }
        public bool OnKeystroke(int Wparam, int Message, int Lparam, int Id) { return false; }
        public void OnPopupMenuItem(int Id) { }
        public void OnPopupMenuItemUpdate(int Id, ref int retval) { }
        public void OnGainedFocus(int Id) { }
        public void OnLostFocus(int Id) { }
        public int OnWindowFromHandleControlCreated(int Id, bool Status) { return 0; }
        public void OnListboxRMBUp(int Id, int PosX, int PosY) { }
        public void OnNumberBoxTrackingCompleted(int Id, double Value) { }
    }

    /// <summary>
    /// The CAD Hub feature in the tree ("CAD Hub Spur Gear1"): rebuilds its body from the settings stored in the part,
    /// and Edit Feature reopens its page. Registered by the installer like the add-in itself (RegAsm /codebase).
    /// </summary>
    [ComVisible(true)]
    [Guid("6C2E9A41-5919-4B7E-9D3A-2F1C0B8E5A17")]
    [ProgId(ToolPage.ProgId)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class CadHubFeature : ISwComFeature
    {
        public object Regenerate(object app, object modelDoc, object feature)
        {
            try
            {
                var data = (MacroFeatureData)((Feature)feature).GetDefinition();
                string stored = "";
                data.GetStringByName(ToolPage.ParameterName, out stored);
                var values = FeatureParams.Decode(stored);
                var tool = CadHubTool.Find(values["kind"]);
                if (tool == null) return "This CAD Hub feature needs a newer CAD Hub.";
                var bodies = tool.BuildBodies((SldWorks)app, values, false);
                if (bodies.Count == 0) return "Nothing to build: edit the feature and check its settings.";
                // Always as a list, even of one: Planetary's list of bodies was accepted, a single body wasn't (1.13.2).
                if (!tool.Cuts) return bodies.Cast<object>().ToArray();
                var body = bodies[0];
                var target = data.EditBody ?? ((data.EditBodies as object[]) ?? new object[0]).OfType<Body2>().FirstOrDefault();
                if (target == null) return "The body this feature cuts is missing.";
                int error = 0;
                var cut = target.Operations2((int)swBodyOperationType_e.SWBODYCUT, body, out error) as object[];
                if (cut == null || cut.Length == 0) return "The cut didn't work (SOLIDWORKS error " + error + "). Edit the feature and pick the face again.";
                return cut;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("CAD Hub feature rebuild", exception);
                return "CAD Hub couldn't rebuild this feature: " + exception.Message;
            }
        }

        public object Edit(object app, object modelDoc, object feature)
        {
            try
            {
                var data = (MacroFeatureData)((Feature)feature).GetDefinition();
                string stored = "";
                data.GetStringByName(ToolPage.ParameterName, out stored);
                var values = FeatureParams.Decode(stored);
                var tool = CadHubTool.Find(values["kind"]);
                if (tool == null) return false;
                Addin.EditFeature((SldWorks)app, (ModelDoc2)modelDoc, (Feature)feature, tool, values);
                return true;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("CAD Hub feature edit", exception);
                return false;
            }
        }

        public object Security(object app, object modelDoc, object feature) { return null; }
    }
}
