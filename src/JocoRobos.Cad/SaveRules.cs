using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JocoRobos.Cad
{
    internal enum SaveCommand { None, Save, SaveAll, SaveAs }

    internal enum DocKind { Part, Assembly, Drawing }

    /// <summary>One open document as a save sees it: SOLIDWORKS' state plus what the last status check knows about the team file.</summary>
    internal sealed class OpenDocState
    {
        internal string Path;           // null: never saved
        internal DocKind Kind;
        internal bool Dirty;
        internal bool ReadOnly;         // opened read-only in SOLIDWORKS (a team file nobody here has locked yet)
        internal bool Active;           // the window the command was given in
        internal bool Intent;           // the student opened a feature or sketch to edit, or ran a modeling command, in this document
        internal bool InTeam;           // in the current robot or the Library (not an archived season, not outside)
        internal bool Mine;             // this computer holds its lock
        internal string LockedBy;       // someone else holds it (or the same account on another computer: MineElsewhere)
        internal bool MineElsewhere;
        internal string NewerFrom;      // a teammate submitted a version this computer doesn't have
        internal string Name { get { return Path == null ? "(never saved)" : System.IO.Path.GetFileNameWithoutExtension(Path); } }
    }

    internal enum SaveStep
    {
        SaveNow,        // writable already: just save it
        LockThenSave,   // a team file the student is clearly working on: lock it (outside SOLIDWORKS' event), then save the original
        AskThenLock,    // a read-only assembly saved on purpose (Ctrl+S in its window): ask once, then lock and save
        Skip,           // left unsaved and still open (nothing is lost), with the reason
        Blocked,        // can't go into the robot now, with the reason and what to do instead
    }

    internal sealed class SavePlanItem
    {
        internal OpenDocState Doc;
        internal SaveStep Step;
        internal string Reason = "";
    }

    /// <summary>
    /// What a save did: saved, locked on the way, left alone on purpose (only rebuilt, or an assembly the student chose not to lock),
    /// and what couldn't be saved and why. Everything not saved is still open with its changes.
    /// </summary>
    internal sealed class SaveOutcome
    {
        internal readonly List<string> Saved = new List<string>();
        internal readonly List<string> Locked = new List<string>();
        internal readonly List<Tuple<string, string>> Skipped = new List<Tuple<string, string>>();
        internal readonly List<Tuple<string, string>> NotSaved = new List<Tuple<string, string>>();
    }

    /// <summary>
    /// One save at a time. Pressing Ctrl+S again while CAD Hub is still locking and saving doesn't start a second one, and CAD Hub's
    /// own saves (the ones it makes after locking) are never taken over again by its own command handler.
    /// </summary>
    internal sealed class SaveGate
    {
        private bool running;
        internal bool Running { get { return running; } }
        internal bool TryBegin() { if (running) return false; running = true; return true; }
        internal void End() { running = false; }
    }

    /// <summary>
    /// What Ctrl+S, Save All and Save As mean for team files, verified in SOLIDWORKS 2026 (see docs/EDITING.md): SOLIDWORKS turns a
    /// save of a changed read-only file into Save As (or its read-only files window for Save All), so CAD Hub stops the command before
    /// it runs, locks what the student is working on outside SOLIDWORKS' event, and saves the originals itself. Pure, so it's tested.
    /// </summary>
    internal static class SaveRules
    {
        internal const int SaveCommandId = 2, SaveAllCommandId = 19, SaveAsCommandId = 620;
        internal const int SketchCommandId = 45, EditSketchCommandId = 859, EditFeatureCommandId = 623;

        // Commands that start changing a part: running one in a read-only team part is clear evidence the student means to edit it.
        // Rebuilds, views, selection and measuring aren't here, and a plain "changed" is never evidence (rebuilds cause it too).
        internal static readonly HashSet<int> ModelingCommands = new HashSet<int>
        {
            SketchCommandId, EditSketchCommandId, EditFeatureCommandId,
            8,   // Extruded Boss/Base
            10,  // Extruded Cut
            392, // Extrude
            9,   // Fillet
            39,  // Hole Wizard
        };

        internal static SaveCommand Classify(int command)
        {
            switch (command)
            {
                case SaveCommandId: return SaveCommand.Save;
                case SaveAllCommandId: return SaveCommand.SaveAll;
                case SaveAsCommandId: return SaveCommand.SaveAs;
                default: return SaveCommand.None;
            }
        }

        /// <summary>
        /// What a Save or Save All would write, document by document. Save covers the active window, and for an assembly every changed
        /// document in it as well (SOLIDWORKS saves changed components with their assembly); Save All covers every changed document.
        /// </summary>
        internal static List<SavePlanItem> Plan(SaveCommand command, IList<OpenDocState> documents)
        {
            var active = documents.FirstOrDefault(d => d.Active);
            IEnumerable<OpenDocState> scope;
            if (command == SaveCommand.SaveAll) scope = documents.Where(d => d.Dirty);
            else if (command == SaveCommand.Save && active != null)
                scope = active.Kind == DocKind.Assembly ? documents.Where(d => d.Dirty) : new[] { active }.Where(d => d.Dirty);
            else scope = Enumerable.Empty<OpenDocState>();
            return scope.Select(d => Decide(command, d)).ToList();
        }

        private static SavePlanItem Decide(SaveCommand command, OpenDocState d)
        {
            var item = new SavePlanItem { Doc = d };
            if (d.Path == null) { item.Step = SaveStep.Skip; item.Reason = "never saved: use File → Save As to give it a name and folder"; return item; }
            if (!d.ReadOnly) { item.Step = SaveStep.SaveNow; return item; }
            if (!d.InTeam) { item.Step = SaveStep.Skip; item.Reason = "read-only, and not in your robot: use File → Save As to keep a copy"; return item; }
            if (d.MineElsewhere)
            {
                item.Step = SaveStep.Blocked;
                item.Reason = "you're editing it on another computer: Submit it there, or ask a mentor to release it";
                return item;
            }
            if (d.LockedBy != null)
            {
                item.Step = SaveStep.Blocked;
                item.Reason = d.LockedBy + " is editing it: Ask " + d.LockedBy + " for it, or File → Save As → Experimental copy to keep your changes";
                return item;
            }
            if (d.NewerFrom != null && !d.Mine)
            {
                item.Step = SaveStep.Blocked;
                item.Reason = d.NewerFrom + " submitted a newer version: File → Save As → Experimental copy keeps your changes; then Update and redo them";
                return item;
            }
            if (d.Kind == DocKind.Assembly)
            {
                // Rebuilding marks assemblies changed by itself: only lock one the student saves on purpose (Ctrl+S in its own window).
                if (command == SaveCommand.Save && d.Active) item.Step = SaveStep.AskThenLock;
                else
                {
                    item.Step = SaveStep.Skip;
                    item.Reason = "read-only; its changes may only be from a rebuild. If you changed it, click Edit on it, then save";
                }
                return item;
            }
            // Parts and drawings: the window the student pressed Ctrl+S in, or one they were clearly editing.
            if (d.Active || d.Intent || d.Mine) { item.Step = SaveStep.LockThenSave; return item; }
            item.Step = SaveStep.Skip;
            item.Reason = "read-only, and CAD Hub can't tell you changed it (a rebuild can do that). If you did, open it and press Ctrl+S, or click Edit";
            return item;
        }

        /// <summary>
        /// Whether CAD Hub must stop the command: only when SOLIDWORKS would otherwise turn it into Save As or its read-only files window,
        /// that is when a changed read-only team file is in it. Everything else (writable files, files outside the robot) saves normally.
        /// </summary>
        internal static bool MustTakeOver(IList<SavePlanItem> plan)
        {
            return plan.Any(i => i.Doc.ReadOnly && i.Doc.Dirty && i.Doc.InTeam);
        }

        /// <summary>The one-line outcome, e.g. "Saved 3 files" or "Saved 2 files · 1 not saved".</summary>
        internal static string Summary(int saved, int notSaved)
        {
            string text = saved == 0 ? "Nothing saved" : "Saved " + saved + (saved == 1 ? " file" : " files");
            return notSaved == 0 ? text : text + " · " + notSaved + " not saved";
        }

        /// <summary>
        /// Carries out a plan. Every step that can fail (the student's answer, the lock, the save) is passed in, so the add-in uses
        /// SVN and SOLIDWORKS and the tests can make any of them fail. A file whose lock or save fails stays open, changed and as it
        /// was: nothing is discarded, and nothing else is stopped by it. Never throws.
        /// </summary>
        internal static SaveOutcome Run(IList<SavePlanItem> plan, Func<OpenDocState, bool> ask, Action<OpenDocState> lockForEditing, Func<OpenDocState, string> save)
        {
            var outcome = new SaveOutcome();
            var toSave = new List<OpenDocState>();
            foreach (var item in plan)
            {
                switch (item.Step)
                {
                    case SaveStep.SaveNow:
                        toSave.Add(item.Doc);
                        break;
                    case SaveStep.AskThenLock:
                    case SaveStep.LockThenSave:
                        if (item.Step == SaveStep.AskThenLock && !Safely(() => ask(item.Doc), false))
                        {
                            outcome.Skipped.Add(Tuple.Create(item.Doc.Name, "not locked (you chose not to); still open with its changes"));
                            break;
                        }
                        try
                        {
                            if (!item.Doc.Mine || item.Doc.ReadOnly) lockForEditing(item.Doc);
                            outcome.Locked.Add(item.Doc.Name);
                            toSave.Add(item.Doc);
                        }
                        catch (Exception exception)
                        {
                            outcome.NotSaved.Add(Tuple.Create(item.Doc.Name, exception.Message.Trim() + " (still open with your changes)"));
                        }
                        break;
                    case SaveStep.Skip:
                        outcome.Skipped.Add(Tuple.Create(item.Doc.Name, item.Reason));
                        break;
                    default:
                        outcome.NotSaved.Add(Tuple.Create(item.Doc.Name, item.Reason));
                        break;
                }
            }
            foreach (var doc in toSave)
            {
                string problem = Safely(() => save(doc), "couldn't be saved");
                if (problem == null) outcome.Saved.Add(doc.Name);
                else outcome.NotSaved.Add(Tuple.Create(doc.Name, problem + " (still open with your changes)"));
            }
            return outcome;
        }

        private static T Safely<T>(Func<T> step, T failed)
        {
            try { return step(); }
            catch (Exception) { return failed; }
        }

        // ---------- Save As: experimental copies and new team parts ----------

        internal const string ExperimentsFolder = "Experiments";

        /// <summary>
        /// Where an experimental copy goes by default: C:\JOCO-ROBOS\Experiments\Plate (experiment).SLDPRT, numbered when taken
        /// ("Plate (experiment 2)"). Its own name, so SOLIDWORKS never confuses it with the team's file (it can't open two files with
        /// the same name), and outside every robot season, so nothing in the robot ever points at it.
        /// </summary>
        internal static string ExperimentPath(string baseFolder, string original, Func<string, bool> exists)
        {
            string folder = Path.Combine(baseFolder, ExperimentsFolder), name = Path.GetFileNameWithoutExtension(original), extension = Path.GetExtension(original);
            for (int i = 1; ; i++)
            {
                string candidate = Path.Combine(folder, name + (i == 1 ? " (experiment)" : " (experiment " + i + ")") + extension);
                if (!exists(candidate)) return candidate;
            }
        }

        internal static bool IsExperiment(string baseFolder, string path)
        {
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(baseFolder)) return false;
            string folder = Path.GetFullPath(Path.Combine(baseFolder, ExperimentsFolder)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            try { return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
        }

        /// <summary>
        /// Why an experimental copy can't go to this path, or null. Never inside a robot season (it would become team CAD), never over an
        /// existing file, never the name of a file SOLIDWORKS has open (it can't hold two files with one name).
        /// </summary>
        internal static string WhyNotExperiment(string target, string baseFolder, Func<string, bool> exists, IEnumerable<string> openNames)
        {
            if (String.IsNullOrWhiteSpace(target)) return "Choose where to save the copy.";
            string extension = Path.GetExtension(target);
            if (!WorkspacePolicy.IsCad(target)) return "Keep the SOLIDWORKS file type (" + extension + ").";
            string season, root;
            if (Recovery.Locate(baseFolder, target, out season, out root))
                return "That's inside " + season + ". An experimental copy goes outside the robot, so it never becomes team CAD (to add a part to the robot, choose New team part).";
            if (exists(target)) return Path.GetFileName(target) + " already exists there. Choose another name; CAD Hub never saves over a file.";
            if (openNames.Any(n => String.Equals(n, Path.GetFileName(target), StringComparison.OrdinalIgnoreCase)))
                return "A file named " + Path.GetFileName(target) + " is open in SOLIDWORKS, which can't hold two files with one name. Choose another name.";
            if (WorkspacePolicy.TooLong(target)) return "That path is too long for SOLIDWORKS. Choose a shorter folder or name.";
            return null;
        }

        /// <summary>
        /// Why a new team part can't have this name and folder, or null. Inside the robot season it belongs to, a name no file anywhere in
        /// that robot already has (SOLIDWORKS mixes up same-named files, even in different folders), and never over an existing file.
        /// </summary>
        internal static string WhyNotNewTeamFile(string name, string folder, string seasonRoot, string extension, ILookup<string, string> robotNames, Func<string, bool> exists)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Type a name for the new part.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".")) return "A file name can't contain \\ / : * ? \" < > | or end with a dot.";
            if (name.Contains("^")) return "Leave out ^ (SOLIDWORKS uses it for parts saved inside an assembly).";
            string target;
            try
            {
                target = Path.Combine(folder, name + extension);
                WorkspacePolicy.RequireInside(seasonRoot, target);
            }
            catch (Exception) { return "Choose a folder inside the robot."; }
            var taken = robotNames[name + extension].FirstOrDefault();
            if (taken != null)
            {
                string where = Path.GetDirectoryName(taken);
                where = where.Length > seasonRoot.Length ? where.Substring(seasonRoot.TrimEnd(Path.DirectorySeparatorChar).Length + 1) : "the robot folder";
                return "The robot already has a file named " + name + extension + " (in " + where + "). Choose a name nothing else uses.";
            }
            if (exists(target)) return name + extension + " already exists in that folder. Choose another name.";
            if (WorkspacePolicy.TooLong(target)) return "That path is too long for SOLIDWORKS. Choose a shorter name.";
            return null;
        }
    }
}
