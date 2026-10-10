using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JocoRobos.Cad
{
    /// <summary>One status check of a workspace, read-only (SvnWorkspace.Snapshot).</summary>
    internal sealed class WorkspaceSnapshot
    {
        internal WorkspaceInfo Info;
        internal long Local, Head;
        internal readonly List<string> Incoming = new List<string>();
        // Files those incoming submits change (full local paths) → who changed them last.
        internal readonly Dictionary<string, string> IncomingFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Locks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> New = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, DateTime> LockedSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // A Submit was interrupted (connection lost, SOLIDWORKS closed): the next Submit settles it.
        internal bool PendingSubmit;
    }

    /// <summary>One submit that changed a file (File History).</summary>
    internal sealed class FileVersion
    {
        internal long Revision;
        internal string Author;
        internal DateTime Time;
        internal string Comment;
    }

    // Every tone also has its own icon or word on screen, so nothing is told by color alone.
    internal enum Tone { Normal, Muted, Good, Warn, Bad, Info }

    /// <summary>The one thing the sync line can offer to do.</summary>
    internal enum SyncAction { None, CloseAndUpdate, ReviewRecovery }

    /// <summary>The one thing the open file's row can offer to do.</summary>
    internal enum FileAction { None, Edit, Ask }

    internal enum NoticeKind { Request, Freed }

    /// <summary>
    /// Something to act on, kept under the panel's bell until it's handled: a teammate waiting for one of this student's files
    /// (gone once they Submit or give it back, or say "Not yet"), or a file they asked for that's free now (gone once opened or
    /// dismissed, or if someone else takes it first).
    /// </summary>
    internal sealed class Notice
    {
        internal NoticeKind Kind;
        internal string Path;
        internal string Who;
        internal string RequestId;  // Request only: what "Not yet" answers
        internal string Text;
    }

    /// <summary>
    /// What the last save of a My work file showed, from the same SOLIDWORKS checks Submit uses: its What's Wrong count, and for an
    /// assembly, the files it uses that aren't on this computer. A heads-up on the file's row, never a block (Submit decides that).
    /// </summary>
    internal sealed class FileHealth
    {
        internal int RebuildProblems;
        internal readonly List<string> Missing = new List<string>();

        internal string Summary
        {
            get
            {
                var parts = new List<string>();
                if (RebuildProblems > 0) parts.Add(RebuildProblems + " rebuild " + (RebuildProblems == 1 ? "problem" : "problems"));
                if (Missing.Count > 0) parts.Add(Missing.Count + " missing " + (Missing.Count == 1 ? "file" : "files"));
                return String.Join(", ", parts);
            }
        }

        internal string Advice
        {
            get
            {
                var lines = new List<string>();
                if (RebuildProblems > 0) lines.Add("Rebuild problems travel with the file: teammates open it to the same red flags. Tools → Evaluate → What's Wrong shows them.");
                if (Missing.Count > 0)
                    lines.Add("It uses files that aren't on this computer: " + String.Join(", ", Missing.Take(4).Select(System.IO.Path.GetFileName)) + (Missing.Count > 4 ? ", …" : "") +
                        ". Teammates would see them missing too. Fix or remove those components.");
                return String.Join("\n", lines);
            }
        }
    }

    /// <summary>One line of My work: a file this student is editing, changed, or added.</summary>
    internal sealed class WorkItem
    {
        internal string Path;
        internal string Name;
        internal string Mark;       // ✎ editing, ● new, ⚠ changed without a lock
        internal string State;      // unsaved · ready · new · unchanged · not locked
        internal Tone Tone;
        internal string WaitingFor; // teammates who asked for it ("sarah", "sarah, ben"), or null
        internal FileHealth Health; // what its last save showed, when there's something to look at
    }

    /// <summary>
    /// What the panel shows. A fixed top: the sync line (an icon and a state word, a short note saying what to do, at most one
    /// action), the open file in one row (status and one action; a second line only for a problem), and My work (one summary
    /// line, expandable to every file). The robot's files fill the rest. Built from server snapshots plus SOLIDWORKS' open
    /// documents; pure, so it's tested without SOLIDWORKS.
    /// </summary>
    internal sealed class PaneState
    {
        internal string Robot = "";
        internal string Sync = "Checking…";
        internal Tone SyncTone = Tone.Muted;
        // One short line under the sync state: what to do about it (empty when nothing).
        internal string SyncNote = "";
        // On hover over the sync line: when it was checked, what's new, the full error.
        internal string SyncTip = "";
        internal SyncAction SyncAction;
        // Download/open the robot: the one big button, only when there's no robot to look at.
        internal bool ShowOpen;
        // True when nothing stands in the way of getting teammates' changes: the add-in does it by itself.
        internal bool CanAutoUpdate;

        internal string ActiveFile = "";
        internal string ActiveStatus = "";
        internal Tone ActiveTone = Tone.Normal;
        internal string ActiveTip = "";
        // A second line, only for a problem (or an answer the student is waiting on).
        internal string ActiveHint = "";
        internal FileAction ActiveAction;
        // For Edit: the file's name ("" for an assembly, where Edit may lock the selected part instead).
        internal string EditTarget;
        // For Ask: who is editing it.
        internal string AskOwner;

        internal readonly List<WorkItem> Work = new List<WorkItem>();
        // "1 unsaved · 2 ready · 1 new · 2 unchanged": My work while collapsed.
        internal string WorkSummary = "";
        // Teammates waiting for files in My work, shown even while it's collapsed.
        internal string Requests = "";
        internal int SubmitCount;
        // Locked files with no changes, saved or not: "Give back N unchanged".
        internal int Unchanged;
        internal bool HasLocks;
        internal bool InterruptedSubmit;

        internal string Update;
        internal string Flash;
        internal string Working;
        // A problem that stops editing on this computer (for example the wrong SOLIDWORKS version).
        internal string Warning;

        internal bool AnyoneWaiting { get { return Requests.Length > 0; } }

        // The bell: requests first (someone is waiting on this student), then freed files.
        internal readonly List<Notice> Notices = new List<Notice>();

        /// <summary>
        /// Recovery copies hold work the robot doesn't (after a crash): that comes first on the sync line until it's reviewed. Updates
        /// wait meanwhile, so the robot files being compared with the copies don't change underneath the student.
        /// </summary>
        internal void ShowRecovered(int files)
        {
            if (files <= 0) return;
            Sync = "⚠ Recovered work: " + files + (files == 1 ? " file" : " files");
            SyncTone = Tone.Bad;
            SyncNote = "Saved from before SOLIDWORKS closed. Review it before you keep editing those files.";
            SyncAction = SyncAction.ReviewRecovery;
            CanAutoUpdate = false;
        }

        internal static PaneState Describe(string user, WorkspaceSnapshot robotSnapshot, WorkspaceSnapshot librarySnapshot,
            string activePath, bool activeReadOnly, string error, DateTime checkedAt, bool activeDirty = false, bool robotOpen = false,
            IEnumerable<EditRequests.Request> requests = null, ICollection<string> unsaved = null, IDictionary<string, string> freed = null,
            IDictionary<string, FileHealth> health = null)
        {
            var state = new PaneState();
            var now = DateTime.Now;
            if (user == null)
            {
                state.Sync = "Not set up yet";
                state.SyncNote = "Open Robot signs you in (or sets up your account with a mentor's code) and downloads the robot.";
                state.ShowOpen = true;
                return state;
            }
            if (robotSnapshot == null)
            {
                if (error == null) return state; // Checking…
                state.Sync = "⚠ Offline";
                state.SyncTone = Tone.Warn;
                state.SyncNote = "Keep working on files you've locked. Submit and updates wait until you're back online.";
                state.SyncTip = "Can't reach the team server: " + error;
                return state;
            }
            var info = robotSnapshot.Info;
            state.Robot = info.Name + (info.Archived ? " (archived, read-only)" : "");
            var snapshots = new[] { robotSnapshot, librarySnapshot }.Where(x => x != null).ToList();
            DescribeSync(state, robotSnapshot, librarySnapshot, error, checkedAt, robotOpen);
            if (robotSnapshot.Local > 0 && activePath == null && !robotOpen) state.ShowOpen = true;
            if (activePath != null) DescribeActive(state, user, snapshots, activePath, activeReadOnly, activeDirty, now);
            DescribeWork(state, snapshots, requests, unsaved, activePath, activeDirty, health);
            DescribeNotices(state, user, snapshots, requests, freed);
            state.InterruptedSubmit = snapshots.Any(x => x.PendingSubmit);
            return state;
        }

        // Only what the student can act on now, from the latest check: nothing here is a history.
        private static void DescribeNotices(PaneState state, string user, List<WorkspaceSnapshot> snapshots, IEnumerable<EditRequests.Request> requests,
            IDictionary<string, string> freed)
        {
            foreach (var r in requests ?? Enumerable.Empty<EditRequests.Request>())
            {
                // Still theirs to wait for only while this student holds the file.
                string path = snapshots.SelectMany(x => x.Mine)
                    .FirstOrDefault(m => m.Replace('\\', '/').EndsWith("/" + r.Season + "/" + r.Path, StringComparison.OrdinalIgnoreCase));
                if (path == null) continue;
                state.Notices.Add(new Notice { Kind = NoticeKind.Request, Path = path, Who = r.From, RequestId = r.Id,
                    Text = r.From + " is waiting for " + Path.GetFileNameWithoutExtension(path) });
            }
            foreach (var f in freed ?? new Dictionary<string, string>())
            {
                // Someone else took it meanwhile: nothing to act on any more.
                string owner;
                var snapshot = snapshots.FirstOrDefault(x => x.Info.Contains(f.Key));
                if (snapshot == null || (snapshot.Locks.TryGetValue(f.Key, out owner) && owner != user)) continue;
                state.Notices.Add(new Notice { Kind = NoticeKind.Freed, Path = f.Key, Who = f.Value,
                    Text = Path.GetFileNameWithoutExtension(f.Key) + " is free now (" + f.Value + " gave it back)" });
            }
        }

        // Up to date · updates waiting (and why, and what to do) · offline. Recovered work is added by the add-in, which knows about it.
        private static void DescribeSync(PaneState state, WorkspaceSnapshot robotSnapshot, WorkspaceSnapshot librarySnapshot, string error,
            DateTime checkedAt, bool robotOpen)
        {
            bool localWork = robotSnapshot.Changed.Count + robotSnapshot.New.Count > 0;
            // The robot and the Library each get news on their own: a mentor's new Library part must come down too.
            var library = librarySnapshot != null && librarySnapshot.Local > 0 ? librarySnapshot : null;
            bool libraryWork = library != null && library.Changed.Count + library.New.Count > 0;
            int robotNews = robotSnapshot.Local == 0 ? 0 : robotSnapshot.Incoming.Count, libraryNews = library == null ? 0 : library.Incoming.Count;
            string checkedText = "Checked " + checkedAt.ToString("h:mm tt") + ". ↻ checks now.";
            state.SyncTip = checkedText;
            if (robotSnapshot.Local == 0)
            {
                state.Sync = "Not downloaded yet";
                state.SyncTone = Tone.Warn;
                state.SyncNote = "Open Robot downloads it (the first time takes a few minutes).";
                state.ShowOpen = true;
            }
            else if (robotNews + libraryNews == 0)
            {
                state.Sync = "✓ Up to date";
                state.SyncTone = Tone.Good;
            }
            else
            {
                int count = robotNews + libraryNews;
                string updates = count + (count == 1 ? " update" : " updates");
                state.SyncTip = updates + " from teammates:\n" +
                    String.Join("\n", robotSnapshot.Incoming.AsEnumerable().Reverse().Take(6)
                        .Concat(library == null ? Enumerable.Empty<string>() : library.Incoming.AsEnumerable().Reverse().Take(3).Select(x => "Library " + x))) +
                    "\n\n" + checkedText;
                bool robotBlocked = robotNews > 0 && localWork, libraryBlocked = libraryNews > 0 && libraryWork;
                bool canGet = (robotNews > 0 && !robotBlocked) || (libraryNews > 0 && !libraryBlocked);
                state.SyncTone = Tone.Info;
                if (canGet && !robotOpen)
                {
                    state.Sync = "⬇ Getting " + updates + "…";
                    state.CanAutoUpdate = true;
                }
                else
                {
                    state.Sync = "⬇ " + updates + " waiting";
                    if (canGet)
                    {
                        state.SyncNote = "Close the robot's files to get them.";
                        state.SyncAction = SyncAction.CloseAndUpdate;
                    }
                    else state.SyncNote = robotBlocked ? "Submit your work first, then they come in." :
                        "Submit your Library changes first, then the new Library parts come in.";
                }
            }
            if (error != null)
            {
                state.Sync = "⚠ Offline";
                state.SyncTone = Tone.Warn;
                state.SyncNote = "Keep working on files you've locked. Submit and updates wait until you're back online.";
                state.SyncTip = "Can't reach the team server: " + error + "\nShowing the check from " + checkedAt.ToString("h:mm tt") + ".";
                state.SyncAction = SyncAction.None;
                state.CanAutoUpdate = false;
            }
        }

        private static void DescribeActive(PaneState state, string user, List<WorkspaceSnapshot> snapshots, string activePath, bool readOnly, bool dirty, DateTime now)
        {
            state.ActiveFile = Path.GetFileNameWithoutExtension(activePath);
            bool assembly = activePath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
            var owner = snapshots.FirstOrDefault(x => x.Info.Contains(activePath));
            string lockedBy, newer = null; // newer: who submitted a version this computer doesn't have yet
            if (owner != null) owner.IncomingFiles.TryGetValue(activePath, out newer);
            DateTime since;
            string when = owner != null && owner.LockedSince.TryGetValue(activePath, out since) ? " since " + RobotFileStatus.Since(since, now) : "";
            Action edit = () =>
            {
                state.ActiveAction = FileAction.Edit;
                state.EditTarget = assembly ? "" : Path.GetFileNameWithoutExtension(activePath);
            };
            state.ActiveTip = Path.GetFileName(activePath) + "\nRight-click it in Robot files for File History and Where Used.";
            if (owner == null && SaveRules.IsExperiment(WorkspaceInfo.BaseFolder, activePath))
            {
                state.ActiveStatus = "Experimental copy · not part of the robot";
                state.ActiveTip = "Teammates can't see it, and no robot file uses it. To use these changes in the robot, open the team's file and redo them, " +
                    "or File → Save As → Create a new team part from it.";
                state.ActiveTone = Tone.Muted;
            }
            else if (owner == null)
            {
                state.ActiveStatus = "⚠ Not in the robot folder";
                state.ActiveHint = "Teammates can't see it. Save it into the robot folder, or use Library → Import a downloaded CAD file.";
                state.ActiveTone = Tone.Warn;
            }
            else if (owner.Mine.Contains(activePath))
            {
                state.ActiveStatus = "✎ Editing" + (dirty ? " · unsaved" : owner.Changed.Contains(activePath) ? " · saved" : readOnly ? " · locked for you" : "");
                state.ActiveTone = Tone.Good;
                // Locked (when the student started editing) but SOLIDWORKS still has it read-only: Ctrl+S switches it and saves.
                // (Saving an assembly whose read-only parts a rebuild marked changed needs no tip any more: CAD Hub saves the assembly and
                // leaves those parts alone, without SOLIDWORKS' read-only files window.)
                if (readOnly) state.ActiveTip = "Locked for you. Ctrl+S saves it into the robot.";
            }
            else if (owner.Locks.TryGetValue(activePath, out lockedBy) && lockedBy == user)
            {
                state.ActiveStatus = "⚠ Locked by you on another computer";
                state.ActiveHint = "Submit it there, or ask a mentor to release it.";
                state.ActiveTone = Tone.Warn;
            }
            else if (owner.Locks.TryGetValue(activePath, out lockedBy))
            {
                state.ActiveStatus = "Locked by " + lockedBy;
                state.ActiveTip = lockedBy + " is editing it" + when + ". You can still look, measure, and reference it.";
                state.ActiveTone = Tone.Bad;
                state.ActiveAction = FileAction.Ask;
                state.AskOwner = lockedBy;
                if (readOnly && dirty)
                {
                    state.ActiveHint = "⚠ Your changes here can't be saved into the robot. Undo them, or Save As a copy outside the robot folder.";
                    state.ActiveTone = Tone.Warn;
                }
            }
            else if (owner.New.Contains(activePath))
            {
                state.ActiveStatus = "● New · not submitted yet";
                state.ActiveTone = Tone.Info;
            }
            else if (owner.Info.Archived)
            {
                state.ActiveStatus = "Read-only (archived season)";
                state.ActiveTone = Tone.Muted;
            }
            else if (readOnly && dirty && newer == null)
            {
                // Not locked yet: CAD Hub can't tell a rebuild from an edit, so it locks when the student saves (or clicks Edit).
                state.ActiveStatus = "Changed · Ctrl+S locks it";
                state.ActiveTip = "Nobody's editing it. Ctrl+S locks it for you and saves it into the robot; Edit locks it now.";
                state.ActiveTone = Tone.Info;
                edit();
            }
            else if (newer != null)
            {
                state.ActiveStatus = "⬇ Newer version from " + newer;
                state.ActiveTip = newer + " submitted a newer version. It comes in when the robot's files are closed; get it before you change this.";
                state.ActiveTone = Tone.Info;
                if (readOnly) edit();
            }
            else
            {
                state.ActiveStatus = "Free to edit";
                state.ActiveTip = assembly ? "Nobody's editing it. Select a part and click Edit to change that part, or Edit with nothing selected to change this assembly." :
                    "Nobody's editing it. Start changing it and it's locked for you, or click Edit.";
                state.ActiveTone = Tone.Normal;
                if (readOnly) edit();
            }
        }

        // Everything this student is responsible for: locked, changed, or new. Unsaved first, then what Submit sends, then the rest.
        private static void DescribeWork(PaneState state, List<WorkspaceSnapshot> snapshots, IEnumerable<EditRequests.Request> requests,
            ICollection<string> unsaved, string activePath, bool activeDirty, IDictionary<string, FileHealth> health)
        {
            var dirty = new HashSet<string>(unsaved ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (activePath != null && activeDirty) dirty.Add(activePath);
            var asked = (requests ?? Enumerable.Empty<EditRequests.Request>()).ToList();
            foreach (var snapshot in snapshots)
                foreach (string path in snapshot.Mine.Concat(snapshot.Changed).Concat(snapshot.New).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    bool mine = snapshot.Mine.Contains(path), isNew = snapshot.New.Contains(path), changed = snapshot.Changed.Contains(path);
                    var item = new WorkItem { Path = path, Name = Path.GetFileName(path), Mark = isNew ? "●" : mine ? "✎" : "⚠" };
                    if (dirty.Contains(path)) { item.State = "unsaved"; item.Tone = Tone.Warn; }
                    else if (isNew) { item.State = "new"; item.Tone = Tone.Info; }
                    else if (changed && mine) { item.State = "ready"; item.Tone = Tone.Good; }
                    else if (changed) { item.State = "not locked"; item.Tone = Tone.Warn; }
                    else { item.State = "unchanged"; item.Tone = Tone.Muted; }
                    var waiting = asked.Where(r => path.Replace('\\', '/').EndsWith("/" + r.Season + "/" + r.Path, StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.From).Distinct().ToList();
                    if (waiting.Count > 0) item.WaitingFor = String.Join(", ", waiting);
                    FileHealth found;
                    if (health != null && health.TryGetValue(path, out found) && found.Summary.Length > 0) item.Health = found;
                    state.Work.Add(item);
                }
            var order = new[] { "unsaved", "ready", "new", "not locked", "unchanged" };
            state.Work.Sort((a, b) =>
            {
                int byState = Array.IndexOf(order, a.State).CompareTo(Array.IndexOf(order, b.State));
                return byState != 0 ? byState : String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            state.WorkSummary = String.Join(" · ", order.Select(s => new { s, n = state.Work.Count(w => w.State == s) }).Where(x => x.n > 0).Select(x => x.n + " " + x.s));
            int toCheck = state.Work.Count(w => w.Health != null);
            if (toCheck > 0) state.WorkSummary = "⚠ " + toCheck + " to check · " + state.WorkSummary;
            var wanted = state.Work.Where(w => w.WaitingFor != null).ToList();
            if (wanted.Count == 1)
                state.Requests = "✋ " + wanted[0].WaitingFor + (wanted[0].WaitingFor.Contains(",") ? " are" : " is") + " waiting for " +
                    System.IO.Path.GetFileNameWithoutExtension(wanted[0].Name);
            else if (wanted.Count > 1)
                state.Requests = "✋ Teammates are waiting for " + wanted.Count + " of your files";
            state.SubmitCount = snapshots.Sum(x => x.Changed.Count + x.New.Count);
            state.HasLocks = snapshots.Any(x => x.Mine.Count > 0);
            state.Unchanged = state.Work.Count(w => w.State == "unchanged");
        }
    }
}
