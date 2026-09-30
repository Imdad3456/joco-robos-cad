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
        internal readonly Dictionary<string, string> Locks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> New = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal enum Tone { Normal, Muted, Good, Warn, Bad, Info }

    /// <summary>
    /// What the panel shows: a status card with at most one obvious next action per section, instead of every command.
    /// Built from server snapshots plus the active SOLIDWORKS document; pure, so it's tested without SOLIDWORKS.
    /// </summary>
    internal sealed class PaneState
    {
        internal string Robot = "";
        internal string Sync = "Checking…";
        internal Tone SyncTone = Tone.Muted;
        internal string Details = "";
        // Robot actions: download/open the robot, or close robot documents so teammates' changes can come in.
        internal bool ShowOpen;
        internal bool ShowCloseAndUpdate;
        // True when nothing stands in the way of getting teammates' changes: the add-in does it by itself.
        internal bool CanAutoUpdate;
        internal string ActiveFile = "";
        internal string ActiveStatus = "";
        internal Tone ActiveTone = Tone.Normal;
        // Non-null: show an Edit button, naming this file ("" for an assembly, where Edit may lock the selected part instead).
        internal string EditTarget;
        internal int SubmitCount;
        internal string Pending = "";
        internal string Locks = "";
        internal bool HasLocks;
        internal string Update;
        internal string Flash;
        internal string Working;

        internal static PaneState Describe(string user, WorkspaceSnapshot robotSnapshot, WorkspaceSnapshot librarySnapshot,
            string activePath, bool activeReadOnly, string error, DateTime checkedAt, bool activeDirty = false, bool robotOpen = false)
        {
            var state = new PaneState();
            if (user == null)
            {
                state.Sync = "Not set up yet";
                state.Details = "Open Robot signs you in (or sets up your account with a mentor's code) and downloads the robot.";
                state.ShowOpen = true;
                return state;
            }
            if (robotSnapshot == null)
            {
                state.Sync = error == null ? "Checking…" : "Can't reach the server";
                state.SyncTone = error == null ? Tone.Muted : Tone.Warn;
                state.Details = error == null ? "" : error + "\nYou can keep working on files you've already locked, and Submit later.";
                return state;
            }
            var info = robotSnapshot.Info;
            state.Robot = info.Name + (info.Archived ? " (archived, read-only)" : "");
            var snapshots = new[] { robotSnapshot, librarySnapshot }.Where(x => x != null).ToList();
            bool localWork = robotSnapshot.Changed.Count + robotSnapshot.New.Count > 0;
            if (robotSnapshot.Local == 0)
            {
                state.Sync = "Not downloaded yet";
                state.SyncTone = Tone.Warn;
                state.Details = "Open Robot downloads it (the first time takes a few minutes).";
                state.ShowOpen = true;
            }
            else if (robotSnapshot.Incoming.Count == 0)
            {
                state.Sync = "✓ Up to date";
                state.SyncTone = Tone.Good;
            }
            else
            {
                int count = robotSnapshot.Incoming.Count;
                state.Sync = "⬇ " + count + (count == 1 ? " teammate change" : " teammate changes");
                state.SyncTone = Tone.Info;
                state.Details = String.Join("\n", robotSnapshot.Incoming.AsEnumerable().Reverse().Take(3)) + "\n";
                if (localWork)
                    state.Details += "You'll get them after you Submit your own changes.";
                else if (robotOpen)
                {
                    state.Details += "Close your robot documents to get them.";
                    state.ShowCloseAndUpdate = true;
                }
                else
                {
                    state.Details += "Getting them now…";
                    state.CanAutoUpdate = true;
                }
            }
            state.Details += (state.Details.Length > 0 && !state.Details.EndsWith("\n") ? "\n" : "") + "Checked " + checkedAt.ToString("h:mm tt") +
                (error != null ? " — offline: " + error : "");
            if (robotSnapshot.Local > 0 && activePath == null && !robotOpen) state.ShowOpen = true;

            if (activePath != null)
            {
                state.ActiveFile = Path.GetFileName(activePath);
                bool assembly = activePath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
                var owner = snapshots.FirstOrDefault(x => x.Info.Contains(activePath));
                string lockedBy;
                if (owner == null)
                {
                    state.ActiveStatus = "Not in the robot folder, so teammates can't see it. Save it into the robot folder (or use Import a downloaded CAD file in the Library tab).";
                    state.ActiveTone = Tone.Warn;
                }
                else if (owner.Mine.Contains(activePath))
                {
                    state.ActiveStatus = "✎ You're editing this" + (activeDirty ? " (unsaved changes)" : owner.Changed.Contains(activePath) ? " (saved)" : "") +
                        (activeReadOnly ? "\nStill read-only in SOLIDWORKS: click Edit again." : "");
                    state.ActiveTone = Tone.Good;
                    if (activeReadOnly) state.EditTarget = assembly ? "" : Path.GetFileNameWithoutExtension(activePath);
                }
                else if (owner.Locks.TryGetValue(activePath, out lockedBy) && lockedBy == user)
                {
                    state.ActiveStatus = "🔒 You're editing this on another computer.\nSubmit it there, or ask a mentor to release it.";
                    state.ActiveTone = Tone.Warn;
                }
                else if (owner.Locks.TryGetValue(activePath, out lockedBy))
                {
                    state.ActiveStatus = "🔒 " + lockedBy + " is editing this.\nYou can still look, measure, and reference it." +
                        (activeReadOnly && activeDirty ? "\nYour unsaved changes here can't be saved into the robot: undo them, or Save As a copy outside the robot folder." : "");
                    state.ActiveTone = activeDirty ? Tone.Warn : Tone.Bad;
                }
                else if (owner.New.Contains(activePath))
                {
                    state.ActiveStatus = "New file: it goes to the team with your next Submit.";
                    state.ActiveTone = Tone.Info;
                }
                else if (owner.Info.Archived)
                {
                    state.ActiveStatus = "Read-only (archived season).";
                    state.ActiveTone = Tone.Muted;
                }
                else
                {
                    state.ActiveStatus = activeReadOnly && activeDirty
                        ? "⚠ Unsaved changes in a read-only file. Edit keeps them. (Don't press Ctrl+S here: it makes a copy.)"
                        : "Read-only. Nobody else is editing it." + (assembly ? "\nEdit changes this assembly; select a part in it first to edit that part." :
                            "\nStart changing it and it's locked for you, or click Edit.");
                    state.ActiveTone = activeDirty ? Tone.Warn : Tone.Normal;
                    if (activeReadOnly) state.EditTarget = assembly ? "" : Path.GetFileNameWithoutExtension(activePath);
                }
            }

            int unsubmitted = snapshots.Sum(x => x.Changed.Count + x.New.Count);
            state.SubmitCount = unsubmitted;
            if (unsubmitted > 0) state.Pending = unsubmitted + (unsubmitted == 1 ? " change" : " changes") + " waiting";
            var mine = snapshots.SelectMany(x => x.Mine).ToList();
            state.HasLocks = mine.Count > 0;
            state.Locks = mine.Count == 0 ? "" : "You're editing " + (mine.Count == 1 ? Path.GetFileName(mine[0]) : mine.Count + " files: " +
                String.Join(", ", mine.Take(4).Select(Path.GetFileName)) + (mine.Count > 4 ? ", …" : "")) + ".";
            return state;
        }
    }
}
