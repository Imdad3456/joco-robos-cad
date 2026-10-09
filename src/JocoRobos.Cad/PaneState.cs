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

    internal enum Tone { Normal, Muted, Good, Warn, Bad, Info }

    /// <summary>One line of My work: a file this student is editing, changed, or added.</summary>
    internal sealed class WorkItem
    {
        internal string Path;
        internal string Name;
        internal string Mark;       // ✎ editing, ● new, ⚠ changed without a lock
        internal string State;      // unsaved · saved · new · not changed · …
        internal Tone Tone;
        internal string WaitingFor; // teammates who asked for it ("sarah", "sarah, ben"), or null
    }

    /// <summary>
    /// What the panel shows, in three parts: where the robot stands (one line, details on hover), the open file (one status
    /// line, one action, a hint only when it applies), and My work (every file this student is responsible for, with Submit).
    /// Built from server snapshots plus SOLIDWORKS' open documents; pure, so it's tested without SOLIDWORKS.
    /// </summary>
    internal sealed class PaneState
    {
        internal string Robot = "";
        internal string Sync = "Checking…";
        internal Tone SyncTone = Tone.Muted;
        // On hover over the sync line: when it was checked and what's new.
        internal string SyncTip = "";
        // Only what needs reading now: why something can't happen, or what's happening.
        internal string Details = "";
        // Robot actions: download/open the robot, or close robot documents so teammates' changes can come in.
        internal bool ShowOpen;
        internal bool ShowCloseAndUpdate;
        // True when nothing stands in the way of getting teammates' changes: the add-in does it by itself.
        internal bool CanAutoUpdate;
        internal string ActiveFile = "";
        internal string ActiveStatus = "";
        internal string ActiveHint = "";
        internal Tone ActiveTone = Tone.Normal;
        // Non-null: show an Edit button, naming this file ("" for an assembly, where Edit may lock the selected part instead).
        internal string EditTarget;
        internal bool ShowHistory;
        // The active file is someone else's: who to ask for it (null: nothing to ask).
        internal string AskOwner;
        internal readonly List<WorkItem> Work = new List<WorkItem>();
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

        internal bool AnyoneWaiting { get { return Work.Any(w => w.WaitingFor != null); } }

        private static string Since(WorkspaceSnapshot owner, string path, DateTime now)
        {
            DateTime since;
            return owner.LockedSince.TryGetValue(path, out since) ? " · since " + RobotFileStatus.Since(since, now) : "";
        }

        internal static PaneState Describe(string user, WorkspaceSnapshot robotSnapshot, WorkspaceSnapshot librarySnapshot,
            string activePath, bool activeReadOnly, string error, DateTime checkedAt, bool activeDirty = false, bool robotOpen = false,
            IEnumerable<EditRequests.Request> requests = null, ICollection<string> unsaved = null)
        {
            var state = new PaneState();
            var now = DateTime.Now;
            if (user == null)
            {
                state.Sync = "Not set up yet";
                state.Details = "Open Robot signs you in (or sets up your account with a mentor's code) and downloads the robot.";
                state.ShowOpen = true;
                return state;
            }
            if (robotSnapshot == null)
            {
                state.Sync = error == null ? "Checking…" : "⚠ Can't reach the server";
                state.SyncTone = error == null ? Tone.Muted : Tone.Warn;
                state.Details = error == null ? "" : error + "\nYou can keep working on files you've already locked, and Submit later.";
                return state;
            }
            var info = robotSnapshot.Info;
            state.Robot = info.Name + (info.Archived ? " (archived, read-only)" : "");
            var snapshots = new[] { robotSnapshot, librarySnapshot }.Where(x => x != null).ToList();
            bool localWork = robotSnapshot.Changed.Count + robotSnapshot.New.Count > 0;
            // The robot and the Library each get news on their own: a mentor's new Library part must come down too.
            var library = librarySnapshot != null && librarySnapshot.Local > 0 ? librarySnapshot : null;
            bool libraryWork = library != null && library.Changed.Count + library.New.Count > 0;
            int robotNews = robotSnapshot.Local == 0 ? 0 : robotSnapshot.Incoming.Count, libraryNews = library == null ? 0 : library.Incoming.Count;
            var details = new List<string>();
            state.SyncTip = "Checked " + checkedAt.ToString("h:mm tt") + ". Click ↻ to check now.";
            if (robotSnapshot.Local == 0)
            {
                state.Sync = "Not downloaded yet";
                state.SyncTone = Tone.Warn;
                details.Add("Open Robot downloads it (the first time takes a few minutes).");
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
                state.Sync = "⬇ " + count + " new";
                state.SyncTone = Tone.Info;
                state.SyncTip = count + (count == 1 ? " new change" : " new changes") + " from teammates:\n" +
                    String.Join("\n", robotSnapshot.Incoming.AsEnumerable().Reverse().Take(6)
                        .Concat(library == null ? Enumerable.Empty<string>() : library.Incoming.AsEnumerable().Reverse().Take(3).Select(x => "Library " + x))) +
                    "\n\n" + state.SyncTip;
                bool robotBlocked = robotNews > 0 && localWork, libraryBlocked = libraryNews > 0 && libraryWork;
                if (robotBlocked || libraryBlocked)
                    details.Add(robotBlocked ? "Teammates' changes come in after you Submit yours." : "New Library parts come in after you Submit your Library changes.");
                if ((robotNews > 0 && !robotBlocked) || (libraryNews > 0 && !libraryBlocked))
                {
                    if (robotOpen)
                    {
                        details.Add("Close the robot's files to get " + (robotBlocked || libraryBlocked ? "the rest." : "them."));
                        state.ShowCloseAndUpdate = true;
                    }
                    else
                    {
                        details.Add("Getting them now…");
                        state.CanAutoUpdate = true;
                    }
                }
            }
            if (error != null)
            {
                state.Sync = "⚠ Offline";
                state.SyncTone = Tone.Warn;
                details.Add("Showing the check from " + checkedAt.ToString("h:mm tt") + ": " + error);
            }
            state.Details = String.Join("\n", details);
            if (robotSnapshot.Local > 0 && activePath == null && !robotOpen) state.ShowOpen = true;

            if (activePath != null) DescribeActive(state, user, snapshots, activePath, activeReadOnly, activeDirty, now);
            DescribeWork(state, snapshots, requests, unsaved, activePath, activeDirty);
            state.InterruptedSubmit = snapshots.Any(x => x.PendingSubmit);
            return state;
        }

        private static void DescribeActive(PaneState state, string user, List<WorkspaceSnapshot> snapshots, string activePath, bool readOnly, bool dirty, DateTime now)
        {
            state.ActiveFile = Path.GetFileName(activePath);
            bool assembly = activePath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
            var owner = snapshots.FirstOrDefault(x => x.Info.Contains(activePath));
            string lockedBy, newer = null; // newer: who submitted a version this computer doesn't have yet
            if (owner != null) owner.IncomingFiles.TryGetValue(activePath, out newer);
            state.ShowHistory = owner != null && !owner.New.Contains(activePath);
            if (owner == null)
            {
                state.ActiveStatus = "⚠ Not in the robot folder";
                state.ActiveHint = "Teammates can't see it. Save it into the robot folder, or use Library → Import a downloaded CAD file.";
                state.ActiveTone = Tone.Warn;
            }
            else if (owner.Mine.Contains(activePath))
            {
                state.ActiveStatus = "✎ You're editing this" + (dirty ? " · unsaved" : owner.Changed.Contains(activePath) ? " · saved" : "");
                state.ActiveTone = Tone.Good;
                if (readOnly)
                {
                    state.ActiveHint = "Still read-only in SOLIDWORKS: click Edit again.";
                    state.EditTarget = assembly ? "" : Path.GetFileNameWithoutExtension(activePath);
                }
                // SOLIDWORKS marks the read-only parts inside an assembly changed just from rebuilding, then offers to save them.
                else if (assembly && dirty)
                    state.ActiveHint = "When you save: if SOLIDWORKS lists read-only files, tick \"Do not save read-only documents\", then Save All. Only your changes are saved.";
            }
            else if (owner.Locks.TryGetValue(activePath, out lockedBy) && lockedBy == user)
            {
                state.ActiveStatus = "You're editing this on another computer" + Since(owner, activePath, now);
                state.ActiveHint = "Submit it there, or ask a mentor to release it.";
                state.ActiveTone = Tone.Warn;
            }
            else if (owner.Locks.TryGetValue(activePath, out lockedBy))
            {
                state.AskOwner = lockedBy;
                state.ActiveStatus = lockedBy + " is editing this" + Since(owner, activePath, now);
                state.ActiveHint = readOnly && dirty
                    ? "Your changes here can't be saved into the robot: undo them, or Save As a copy outside the robot folder."
                    : "You can still look, measure, and reference it.";
                state.ActiveTone = dirty ? Tone.Warn : Tone.Bad;
            }
            else if (owner.New.Contains(activePath))
            {
                state.ActiveStatus = "● New file · goes to the team with your next Submit";
                state.ActiveTone = Tone.Info;
            }
            else if (owner.Info.Archived)
            {
                state.ActiveStatus = "Read-only (archived season)";
                state.ActiveTone = Tone.Muted;
            }
            else
            {
                if (readOnly && dirty)
                {
                    state.ActiveStatus = "⚠ Unsaved changes in a read-only file";
                    state.ActiveHint = "Click Edit to keep them. (Don't press Ctrl+S here: it makes a copy.)";
                    state.ActiveTone = Tone.Warn;
                }
                else if (newer != null)
                {
                    state.ActiveStatus = "⬇ " + newer + " submitted a newer version";
                    state.ActiveHint = "You get it when the robot's files are closed (Close & Update), before you change it.";
                    state.ActiveTone = Tone.Info;
                }
                else
                {
                    state.ActiveStatus = "Read-only · nobody's editing it";
                    state.ActiveHint = assembly ? "Select a part and click Edit to change that part; Edit with nothing selected changes this assembly." :
                        "Start changing it and it's locked for you, or click Edit.";
                    state.ActiveTone = Tone.Normal;
                }
                if (readOnly) state.EditTarget = assembly ? "" : Path.GetFileNameWithoutExtension(activePath);
            }
        }

        // Everything this student is responsible for: locked, changed, or new. Unsaved first, then what Submit sends, then the rest.
        private static void DescribeWork(PaneState state, List<WorkspaceSnapshot> snapshots, IEnumerable<EditRequests.Request> requests,
            ICollection<string> unsaved, string activePath, bool activeDirty)
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
                    else if (changed && mine) { item.State = "saved"; item.Tone = Tone.Good; }
                    else if (changed) { item.State = "changed, not locked"; item.Tone = Tone.Warn; }
                    else { item.State = "not changed"; item.Tone = Tone.Muted; }
                    var waiting = asked.Where(r => path.Replace('\\', '/').EndsWith("/" + r.Season + "/" + r.Path, StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.From).Distinct().ToList();
                    if (waiting.Count > 0) item.WaitingFor = String.Join(", ", waiting);
                    state.Work.Add(item);
                }
            var order = new[] { "unsaved", "saved", "new", "changed, not locked", "not changed" };
            state.Work.Sort((a, b) =>
            {
                int byState = Array.IndexOf(order, a.State).CompareTo(Array.IndexOf(order, b.State));
                return byState != 0 ? byState : String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            state.SubmitCount = snapshots.Sum(x => x.Changed.Count + x.New.Count);
            state.HasLocks = snapshots.Any(x => x.Mine.Count > 0);
            state.Unchanged = state.Work.Count(w => w.State == "not changed");
        }
    }
}
