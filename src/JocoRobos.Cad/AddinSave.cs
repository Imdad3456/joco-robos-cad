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
    /// Editing and saving team files the way SOLIDWORKS users expect: change a part, press Ctrl+S, and the team's file is saved.
    /// Verified in SOLIDWORKS 2026 (docs/EDITING.md): a save of a changed read-only file becomes Save As (Ctrl+S) or SOLIDWORKS'
    /// read-only files window (Save All), so CAD Hub stops those commands before they run, then, after SOLIDWORKS' event has finished,
    /// locks what the student is working on and saves the originals. The rules are in SaveRules (tested).
    /// </summary>
    public sealed partial class Addin
    {
        private readonly SaveGate saveGate = new SaveGate();
        // Our own saves and copies: never taken over again by our own handlers.
        private bool ownSave;
        // Documents with clear evidence the student is editing them (opened a feature or sketch, ran a modeling command).
        private readonly HashSet<string> editIntent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> intentHandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void StartSaveHandling()
        {
            application.CommandOpenPreNotify += OnCommandStarting;
            watcher.SaveAsStarting = OnSaveAsStarting;
            watcher.EditIntent = doc => OnUi("edit intent", () => OnEditIntent(doc));
        }

        private void StopSaveHandling()
        {
            try { if (application != null) application.CommandOpenPreNotify -= OnCommandStarting; }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO save handling: " + exception); }
        }

        // SOLIDWORKS asks before running any command; 1 stops it. Must answer at once and never throw: the work happens later.
        private int OnCommandStarting(int command, int userCommand)
        {
            try
            {
                if (application == null || ownSave) return 0;
                var kind = SaveRules.Classify(command);
                var active = application.ActiveDoc as ModelDoc2;
                if (kind == SaveCommand.None)
                {
                    if (SaveRules.ModelingCommands.Contains(command)) NoteIntent(EditTarget(active));
                    return 0;
                }
                if (kind == SaveCommand.SaveAs)
                {
                    if (active == null || !IsCurrentTeamFile(active.GetPathName())) return 0; // not a team file: SOLIDWORKS' own Save As
                    OnUi("save as", () => ChooseSaveAs(active));
                    return 1;
                }
                if (saveGate.Running) { OnUi("still saving", () => ShowFlash("Still saving… one moment")); return 1; }
                var plan = SaveRules.Plan(kind, OpenDocStates());
                if (!SaveRules.MustTakeOver(plan)) return 0; // nothing read-only in it: SOLIDWORKS saves as usual
                OnUi("save", () => SaveTeamFiles(kind, null));
                return 1;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("save command check", exception);
                return 0;
            }
        }

        // The pre-Save-As event: a backstop for read-only team files that reach Save As some other way than Ctrl+S or Save All.
        private int OnSaveAsStarting(ModelDoc2 doc)
        {
            try
            {
                if (ownSave || application == null || !doc.IsOpenedReadOnly() || !IsCurrentTeamFile(doc.GetPathName())) return 0;
                string path = Path.GetFullPath(doc.GetPathName());
                OnUi("save (backstop)", () => SaveTeamFiles(SaveCommand.Save, path));
                return 1;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("save as check", exception);
                return 0;
            }
        }

        // In an assembly window, the part being edited in place (Edit Part); otherwise the window's own document.
        private static ModelDoc2 EditTarget(ModelDoc2 active)
        {
            if (active == null || active.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return active;
            try { return ((AssemblyDoc)active).GetEditTarget() as ModelDoc2 ?? active; }
            catch (Exception) { return active; }
        }

        private void NoteIntent(ModelDoc2 doc)
        {
            try
            {
                if (doc == null || doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY || !doc.IsOpenedReadOnly() || !IsCurrentTeamFile(doc.GetPathName())) return;
                OnUi("edit intent", () => OnEditIntent(doc));
            }
            catch (Exception) { } // Closing right now.
        }

        // Clear evidence of editing a read-only team part or drawing: take its lock now, in the background, so a teammate can't take it
        // while the student works. Only the SVN lock: SOLIDWORKS' own read-only state is left alone while the feature or sketch editor
        // opens, and switched at Ctrl+S (verified to keep unsaved changes). Once per file per session; a teammate's lock is warned about.
        private void OnEditIntent(ModelDoc2 doc)
        {
            string path;
            try { path = Path.GetFullPath(doc.GetPathName()); if (!doc.IsOpenedReadOnly()) return; }
            catch (Exception) { return; }
            editIntent.Add(path);
            if (!intentHandled.Add(path) || WarnIfSomeoneElsesFile(doc, path)) return;
            var login = CredentialStore.Read();
            var season = paneCatalog?.Owning(path);
            if (login == null || season == null || busy) return; // Ctrl+S locks it instead
            string name = Path.GetFileNameWithoutExtension(path);
            System.Threading.Tasks.Task.Run(() => SvnWorkspace.Exclusive(() => new SvnWorkspace(login, season).Edit(path))).ContinueWith(task => OnUi("lock on intent", () =>
            {
                if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                {
                    try { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); } catch (Exception) { }
                    ShowFlash("✎ Locked " + name + " for you. Ctrl+S saves it to the robot.");
                    RefreshStatus();
                }
                else
                {
                    // Not now (offline, a teammate was quicker, a newer version): nothing changed; Ctrl+S tries again and says why.
                    string why = task.Exception?.GetBaseException().Message.Split('\n')[0] ?? "unknown";
                    ErrorLog.Step("lock on intent for " + path + " didn't happen: " + why);
                    ShowFlash("Couldn't lock " + name + " yet: " + why);
                }
            }));
        }

        private bool IsCurrentTeamFile(string path)
        {
            if (String.IsNullOrEmpty(path) || !WorkspacePolicy.IsSubmittableCad(path)) return false;
            var catalog = paneCatalog;
            var season = catalog?.Owning(Path.GetFullPath(path));
            return season != null && !season.Archived && (season.IsLibrary || season.Name == catalog.Robot.Name);
        }

        // Every open document as the save rules see it (fast: SOLIDWORKS' own state plus the last status check).
        private List<OpenDocState> OpenDocStates()
        {
            var states = new List<OpenDocState>();
            var active = application.ActiveDoc as ModelDoc2;
            string activePath = active?.GetPathName();
            foreach (var doc in OpenDocuments())
            {
                string path = doc.GetPathName();
                if (!String.IsNullOrEmpty(path) && WorkspacePolicy.IsVirtualComponent(path)) continue; // saved inside its assembly
                var state = new OpenDocState
                {
                    Path = String.IsNullOrEmpty(path) ? null : Path.GetFullPath(path),
                    Kind = doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? DocKind.Assembly : doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING ? DocKind.Drawing : DocKind.Part,
                    Dirty = doc.GetSaveFlag(),
                    ReadOnly = doc.IsOpenedReadOnly(),
                    Active = ReferenceEquals(doc, active) || (path != null && String.Equals(path, activePath, StringComparison.OrdinalIgnoreCase)),
                };
                if (state.Path != null)
                {
                    state.Intent = editIntent.Contains(state.Path);
                    state.InTeam = IsCurrentTeamFile(state.Path);
                    var snapshot = new[] { robotSnapshot, librarySnapshot }.FirstOrDefault(x => x != null && x.Info.Contains(state.Path));
                    if (snapshot != null)
                    {
                        string owner, newer;
                        state.Mine = snapshot.Mine.Contains(state.Path);
                        if (!state.Mine && snapshot.Locks.TryGetValue(state.Path, out owner))
                        {
                            state.LockedBy = owner;
                            state.MineElsewhere = owner == paneUser;
                        }
                        if (snapshot.IncomingFiles.TryGetValue(state.Path, out newer)) state.NewerFrom = newer;
                    }
                }
                states.Add(state);
            }
            return states;
        }

        /// <summary>
        /// Ctrl+S or Save All taken over (or, with onlyPath, one file's original): lock what's being worked on, then save the originals.
        /// Runs after SOLIDWORKS' event has finished; one at a time; a file whose lock or save fails stays open with its changes.
        /// </summary>
        private void SaveTeamFiles(SaveCommand command, string onlyPath)
        {
            if (!saveGate.TryBegin()) { ShowFlash("Still saving… one moment"); return; }
            // Another CAD Hub task is running: SOLIDWORKS' save was already stopped, so say so (nothing was saved, nothing is lost).
            if (busy) { saveGate.End(); ShowFlash("⚠ Not saved yet: CAD Hub is busy. Press Ctrl+S again in a moment."); return; }
            try
            {
                Execute(() =>
                {
                    var states = OpenDocStates();
                    if (onlyPath != null)
                    {
                        states = states.Where(d => String.Equals(d.Path, onlyPath, StringComparison.OrdinalIgnoreCase)).ToList();
                        foreach (var d in states) d.Active = true;
                    }
                    var plan = SaveRules.Plan(command, states);
                    if (plan.Count == 0) return;
                    Catalog catalog = null;
                    Func<Catalog> teamCatalog = () =>
                    {
                        if (catalog != null) return catalog;
                        var login = GetLogin(false);
                        if (login == null) throw new InvalidOperationException("Sign in first (Tools → CAD Hub → Sign In).");
                        return catalog = LoadCatalog(login);
                    };
                    var outcome = SaveRules.Run(plan,
                        d => MessageBox.Show(new SolidWorksWindow(), "Lock the assembly " + d.Name + " and save it?\n\nIt's read-only until you lock it. Locking keeps your changes, " +
                            "and nobody else can edit it until you Submit.\n\n(If you didn't mean to change it, choose No: it stays as it is, still open.)",
                            Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes,
                        d => LockOpenDocument(d.Path, teamCatalog()),
                        d => SaveOpenDocument(d.Path));
                    ReportSave(outcome);
                });
            }
            finally { saveGate.End(); }
        }

        // Lock one open team file and make it editable, keeping its unsaved changes (verified: SetReadOnlyState(false) keeps them).
        // A recovery copy is taken first, so even an unexpected reload can't lose the work.
        private void LockOpenDocument(string path, Catalog catalog)
        {
            var doc = FindOpen(path);
            if (doc == null) throw new InvalidOperationException("it was closed");
            if (doc.GetSaveFlag()) TakeRecoveryCopy(doc);
            try { EditDocument(doc, false, catalog, true); }
            catch (Exception exception) { throw new InvalidOperationException(exception.Message.Split(new[] { "\n\n" }, StringSplitOptions.None)[0], exception); }
            if (doc.IsOpenedReadOnly()) throw new InvalidOperationException("locked for you, but SOLIDWORKS couldn't make it editable; click Edit on it");
        }

        private string SaveOpenDocument(string path)
        {
            var doc = FindOpen(path);
            if (doc == null) return "it was closed";
            int errors = 0, warnings = 0;
            ownSave = true;
            try { return doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) ? null : "SOLIDWORKS couldn't save it (error " + errors + ")"; }
            finally { ownSave = false; }
        }

        // Before making a file editable: a copy of its unsaved state in the recovery folder (Review lists it if anything goes wrong).
        private void TakeRecoveryCopy(ModelDoc2 doc)
        {
            try
            {
                string path = doc.GetPathName(), season, root;
                if (!Recovery.Locate(WorkspaceInfo.BaseFolder, path, out season, out root)) return;
                string copy = Recovery.CopyPath(Recovery.DefaultRoot, season, root, path, DateTime.Now);
                ownSave = true;
                try { if (SaveCopy(doc, copy) == 0) Recovery.RecordBase(copy, path); }
                finally { ownSave = false; }
            }
            catch (Exception exception) { ErrorLog.Write("save: recovery copy before locking", exception); }
        }

        // Saved, and anything only left alone on purpose: a quiet line in the panel. A real problem (a teammate's lock, a newer version,
        // a failed lock or save): a message listing each file and what to do, every one still open with its changes.
        private void ReportSave(SaveOutcome outcome)
        {
            string summary = SaveRules.Summary(outcome.Saved.Count, outcome.NotSaved.Count);
            string leftAlone = outcome.Skipped.Count == 0 ? "" : " · left unsaved: " + String.Join(", ", outcome.Skipped.Select(x => x.Item1)) +
                " (not changed by you as far as CAD Hub can tell; Ctrl+S in its own window saves it)";
            if (outcome.NotSaved.Count == 0)
            {
                ShowFlash("✓ " + summary + (outcome.Locked.Count > 0 ? " (locked " + String.Join(", ", outcome.Locked) + " for you)" : "") + leftAlone);
                return;
            }
            Message(summary + ".\n\n" +
                (outcome.Saved.Count > 0 ? "Saved: " + String.Join(", ", outcome.Saved) + "\n\n" : "") +
                "Not saved (still open, nothing lost):\n" + String.Join("\n", outcome.NotSaved.Select(n => "  • " + n.Item1 + ": " + n.Item2)),
                MessageBoxIcon.Warning);
        }

        // ---------- File → Save As on a team file: save the original, an experimental copy, or a new team part ----------

        private void ChooseSaveAs(ModelDoc2 doc)
        {
            string path;
            try { path = Path.GetFullPath(doc.GetPathName()); }
            catch (Exception) { return; }
            var catalog = paneCatalog;
            var season = catalog?.Owning(path);
            if (season == null) return;
            var state = OpenDocStates().FirstOrDefault(d => String.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
            var original = state == null ? null : SaveRules.Plan(SaveCommand.Save, new[] { state }.Select(s => { s.Active = true; s.Dirty = true; return s; }).ToList()).FirstOrDefault();
            string originalProblem = original == null ? null : original.Step == SaveStep.Blocked ? original.Reason : null;
            // Inside Save As, "use File → Save As → Experimental copy" would point at this very window: keep the reason, drop the advice.
            if (originalProblem != null)
            {
                int advice = originalProblem.IndexOf("File → Save As", StringComparison.Ordinal);
                if (advice > 0) originalProblem = System.Text.RegularExpressions.Regex.Replace(originalProblem.Substring(0, advice), @"[\s,:;]*(or)?[\s,:;]*$", "");
            }
            // An open assembly that uses this file, for "use the new part there instead".
            var user = OpenDocuments().Where(d => d.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY && d.Visible &&
                    !String.Equals(d.GetPathName(), path, StringComparison.OrdinalIgnoreCase) && UsesComponent(d, path))
                .FirstOrDefault();
            var robotNames = SubmitCheck.CadByName(season.Root);
            var openNames = OpenDocuments().Select(d => Path.GetFileName(d.GetPathName() ?? "")).ToList();
            using (var dialog = new SaveAsChoiceDialog(path, season.Root, WorkspaceInfo.BaseFolder, originalProblem,
                user == null ? null : Path.GetFileNameWithoutExtension(user.GetPathName()),
                target => SaveRules.WhyNotExperiment(target, WorkspaceInfo.BaseFolder, File.Exists, openNames),
                (name, folder) => SaveRules.WhyNotNewTeamFile(name, folder, season.Root, Path.GetExtension(path), robotNames, File.Exists)))
            {
                if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                switch (dialog.Choice)
                {
                    case SaveAsChoice.Original:
                        SaveTeamFiles(SaveCommand.Save, path);
                        break;
                    case SaveAsChoice.Experiment:
                        SaveExperiment(doc, dialog.Target);
                        break;
                    case SaveAsChoice.NewTeamFile:
                        CreateTeamFile(doc, dialog.Target, dialog.UseInAssembly ? user : null);
                        break;
                }
            }
        }

        private static bool UsesComponent(ModelDoc2 assembly, string path)
        {
            try
            {
                return ((((AssemblyDoc)assembly).GetComponents(false) as object[]) ?? new object[0]).Cast<Component2>()
                    .Any(c => String.Equals(c.GetPathName(), path, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception) { return false; }
        }

        // An independent copy outside the robot. Save As Copy: the window stays the team's file and no assembly is redirected (verified).
        private void SaveExperiment(ModelDoc2 doc, string target)
        {
            Execute(() =>
            {
                string why = SaveRules.WhyNotExperiment(target, WorkspaceInfo.BaseFolder, File.Exists, OpenDocuments().Select(d => Path.GetFileName(d.GetPathName() ?? "")));
                if (why != null) throw new InvalidOperationException(why);
                int errors;
                ownSave = true;
                try { errors = SaveCopy(doc, target); }
                finally { ownSave = false; }
                if (errors != 0) throw new InvalidOperationException("SOLIDWORKS couldn't save the copy (error " + errors + "). Nothing was changed.");
                string name = Path.GetFileNameWithoutExtension(doc.GetPathName());
                if (MessageBox.Show(new SolidWorksWindow(), "Saved the experimental copy:\n" + target + "\n\nIt isn't part of the robot; teammates can't see it. " + name +
                    " itself is unchanged" + (doc.GetSaveFlag() ? " and still has your unsaved changes (close it without saving if you only wanted the copy)" : "") +
                    ".\n\nOpen the copy now?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    OpenOutsideFile(target);
            });
        }

        // A new, uniquely named team file from this one. Save As Copy first (nothing redirected); the assembly uses it only if the
        // student asked, and then only after its own lock, through an explicit component replacement (verified).
        private void CreateTeamFile(ModelDoc2 doc, string target, ModelDoc2 replaceIn)
        {
            Execute(() =>
            {
                var catalog = LoadCatalog(GetLogin(false) ?? throw new InvalidOperationException("Sign in first (Tools → CAD Hub → Sign In)."));
                var season = catalog.Owning(target);
                if (season == null) throw new InvalidOperationException("Choose a folder inside the robot.");
                string why = SaveRules.WhyNotNewTeamFile(Path.GetFileNameWithoutExtension(target), Path.GetDirectoryName(target), season.Root, Path.GetExtension(target),
                    SubmitCheck.CadByName(season.Root), File.Exists);
                if (why != null) throw new InvalidOperationException(why);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                int errors;
                ownSave = true;
                try { errors = SaveCopy(doc, target); }
                finally { ownSave = false; }
                if (errors != 0) throw new InvalidOperationException("SOLIDWORKS couldn't create " + Path.GetFileName(target) + " (error " + errors + "). Nothing was changed.");
                string original = Path.GetFullPath(doc.GetPathName()), replaced = "";
                if (replaceIn != null)
                {
                    try
                    {
                        if (replaceIn.IsOpenedReadOnly()) EditDocument(replaceIn, false, catalog, true);
                        replaceIn.ClearSelection2(true);
                        var instances = ((((AssemblyDoc)replaceIn).GetComponents(false) as object[]) ?? new object[0]).Cast<Component2>()
                            .Where(c => String.Equals(c.GetPathName(), original, StringComparison.OrdinalIgnoreCase)).ToList();
                        foreach (var instance in instances) instance.Select4(true, null, false);
                        bool ok = instances.Count > 0 && ((AssemblyDoc)replaceIn).ReplaceComponents2(target, "", true, 0, true);
                        replaced = ok ? "\n\n" + Path.GetFileNameWithoutExtension(replaceIn.GetPathName()) + " now uses it instead of " + Path.GetFileNameWithoutExtension(original) +
                            ". Save " + Path.GetFileNameWithoutExtension(replaceIn.GetPathName()) + " to keep that."
                            : "\n\n" + Path.GetFileNameWithoutExtension(replaceIn.GetPathName()) + " wasn't changed (SOLIDWORKS couldn't replace the component).";
                    }
                    catch (Exception exception)
                    {
                        replaced = "\n\n" + Path.GetFileNameWithoutExtension(replaceIn.GetPathName()) + " wasn't changed: " + exception.Message.Split('\n')[0];
                    }
                }
                Message("Created " + Path.GetFileName(target) + " in the robot. It goes to the team with your next Submit." + replaced +
                    (doc.GetSaveFlag() ? "\n\n" + Path.GetFileNameWithoutExtension(original) + " still has your unsaved changes; close it without saving if you only wanted the new part." : ""));
                OpenOutsideFile(target);
            });
        }

        private void OpenOutsideFile(string path)
        {
            int errors = 0, warnings = 0;
            var open = FindOpen(path);
            if (open != null) { application.ActivateDoc3(open.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors); return; }
            int type = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY
                : path.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocDRAWING : (int)swDocumentTypes_e.swDocPART;
            application.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
        }
    }
}
