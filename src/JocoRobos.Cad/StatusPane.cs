using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    /// <summary>What the pane shows; built from server snapshots plus the active SOLIDWORKS document.</summary>
    internal sealed class PaneState
    {
        internal string Robot = "";
        internal string Sync = "Checking…";
        internal Color SyncColor = SystemColors.GrayText;
        internal string Details = "";
        internal string ActiveFile = "";
        internal string ActiveStatus = "";
        internal Color ActiveColor = SystemColors.ControlText;
        internal string Locks = "";
        internal string Update;
        internal string Pending = "";
    }

    internal sealed class StatusPane : UserControl
    {
        private readonly Label robot = Caption(11f, FontStyle.Bold);
        private readonly Label sync = Caption(10f, FontStyle.Bold);
        private readonly Label details = Caption(8.5f, FontStyle.Regular);
        private readonly Label activeFile = Caption(9.5f, FontStyle.Bold);
        private readonly Label activeStatus = Caption(9.5f, FontStyle.Regular);
        private readonly Label locks = Caption(8.5f, FontStyle.Regular);
        private readonly Label pending = Caption(9.5f, FontStyle.Bold, "", Color.DarkOrange);
        private readonly Label update = Caption(9f, FontStyle.Bold, "", Color.RoyalBlue);
        private readonly Button install = new Button { Text = "Install update", Width = 200, Height = 30, FlatStyle = FlatStyle.System };
        private readonly FlowLayoutPanel layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(10) };

        internal StatusPane(IEnumerable<KeyValuePair<string, Action>> buttons, Action refresh, Action installUpdate)
        {
            BackColor = SystemColors.Window;
            layout.Controls.Add(Caption(9f, FontStyle.Bold, "JOCO ROBOS CAD " + Updater.Current + (Updater.IsDevelopmentBuild ? " (dev build)" : ""), SystemColors.GrayText));
            layout.Controls.Add(update);
            install.Click += (s, e) => installUpdate();
            layout.Controls.Add(install);
            layout.Controls.Add(robot);
            layout.Controls.Add(sync);
            layout.Controls.Add(details);
            layout.Controls.Add(pending);
            layout.Controls.Add(Spacer());
            layout.Controls.Add(activeFile);
            layout.Controls.Add(activeStatus);
            layout.Controls.Add(Spacer());
            foreach (var pair in buttons)
            {
                var button = new Button { Text = pair.Key, Width = 200, Height = 30, Margin = new Padding(0, 2, 0, 2), FlatStyle = FlatStyle.System };
                Action action = pair.Value;
                button.Click += (s, e) => action();
                layout.Controls.Add(button);
            }
            layout.Controls.Add(Spacer());
            layout.Controls.Add(locks);
            var check = new LinkLabel { Text = "Check now", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            check.LinkClicked += (s, e) => refresh();
            layout.Controls.Add(check);
            Controls.Add(layout);
            Show(new PaneState());
        }

        private static Label Caption(float size, FontStyle style, string text = "", Color? color = null)
        {
            return new Label { AutoSize = true, MaximumSize = new Size(230, 0), Text = text, Margin = new Padding(0, 2, 0, 2),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, size, style), ForeColor = color ?? SystemColors.ControlText };
        }

        private static Control Spacer() { return new Panel { Height = 8, Width = 10 }; }

        internal void Show(PaneState state)
        {
            robot.Text = state.Robot;
            sync.Text = state.Sync;
            sync.ForeColor = state.SyncColor;
            details.Text = state.Details;
            details.Visible = state.Details.Length > 0;
            activeFile.Text = state.ActiveFile;
            activeStatus.Text = state.ActiveStatus;
            activeStatus.ForeColor = state.ActiveColor;
            locks.Text = state.Locks;
            pending.Text = state.Pending;
            pending.Visible = state.Pending.Length > 0;
            update.Text = state.Update ?? "";
            update.Visible = install.Visible = state.Update != null;
        }

        /// <summary>Plain-language status for the pane. Pure so it can be reasoned about without SOLIDWORKS.</summary>
        internal static PaneState Describe(string user, WorkspaceSnapshot robotSnapshot, WorkspaceSnapshot librarySnapshot,
            string activePath, bool activeReadOnly, string error, DateTime checkedAt, bool activeDirty = false)
        {
            var state = new PaneState();
            if (user == null)
            {
                state.Sync = "Not signed in";
                state.Details = "Click Open Robot to sign in.";
                return state;
            }
            if (robotSnapshot == null)
            {
                state.Sync = error == null ? "Checking…" : "Can't reach the server";
                state.SyncColor = error == null ? SystemColors.GrayText : Color.DarkOrange;
                state.Details = error ?? "";
                return state;
            }
            var info = robotSnapshot.Info;
            state.Robot = info.Name + (info.Archived ? " (archived, read-only)" : "");
            if (robotSnapshot.Local == 0)
            {
                state.Sync = "Not downloaded yet";
                state.SyncColor = Color.DarkOrange;
                state.Details = "Click Open Robot.";
            }
            else if (robotSnapshot.Incoming.Count == 0)
            {
                state.Sync = "✓ Up to date";
                state.SyncColor = Color.ForestGreen;
            }
            else
            {
                int count = robotSnapshot.Incoming.Count;
                state.Sync = "⬇ " + count + (count == 1 ? " update" : " updates") + " available";
                state.SyncColor = Color.DarkOrange;
                state.Details = String.Join("\n", robotSnapshot.Incoming.AsEnumerable().Reverse().Take(3)) +
                    "\nSave, close your documents, and click Update.";
            }
            state.Details += (state.Details.Length > 0 ? "\n" : "") + "Checked " + checkedAt.ToString("h:mm tt") +
                (error != null ? " — offline: " + error : "");

            var snapshots = new[] { robotSnapshot, librarySnapshot }.Where(x => x != null).ToList();
            if (activePath == null)
            {
                state.ActiveFile = "No document open";
            }
            else
            {
                state.ActiveFile = Path.GetFileName(activePath);
                var owner = snapshots.FirstOrDefault(x => x.Info.Contains(activePath));
                string lockedBy;
                if (owner == null)
                {
                    state.ActiveStatus = "Not in the robot folder. Teammates can't see this file.";
                    state.ActiveColor = Color.DarkOrange;
                }
                else if (owner.Mine.Contains(activePath))
                {
                    state.ActiveStatus = "✎ You are editing this" + (owner.Changed.Contains(activePath) ? " (changes not submitted)" : "") +
                        (activeReadOnly ? "\nStill read-only in SOLIDWORKS: click Edit again." : "");
                    state.ActiveColor = Color.ForestGreen;
                }
                else if (owner.Locks.TryGetValue(activePath, out lockedBy) && lockedBy == user)
                {
                    state.ActiveStatus = "🔒 Locked by you on another computer.\nSubmit it there, or ask a mentor to release it.";
                    state.ActiveColor = Color.DarkOrange;
                }
                else if (activeReadOnly && activeDirty)
                {
                    // Changes the student can't save yet: stay visible until they lock or undo.
                    state.ActiveStatus = owner.Locks.TryGetValue(activePath, out lockedBy)
                        ? "⚠ Unsaved changes, but " + lockedBy + " is editing this file.\nUndo them, or Save As a copy outside the robot folder."
                        : "⚠ Unsaved changes in a read-only file.\nClick Edit to lock it and keep them.";
                    state.ActiveColor = Color.DarkOrange;
                }
                else if (owner.Locks.TryGetValue(activePath, out lockedBy))
                {
                    state.ActiveStatus = "🔒 Locked by " + lockedBy + "\nYou can look, measure, and reference it.";
                    state.ActiveColor = Color.Firebrick;
                }
                else if (owner.New.Contains(activePath))
                {
                    state.ActiveStatus = "New file: Submit adds it to " + owner.Info.Label + ".";
                    state.ActiveColor = Color.RoyalBlue;
                }
                else
                {
                    state.ActiveStatus = owner.Info.Archived ? "Read-only (archived season)" : "Read-only. Click Edit to change it" +
                        (owner.Info.IsLibrary ? " in the Library." : ".");
                }
            }
            int unsubmitted = snapshots.Sum(x => x.Changed.Count + x.New.Count);
            if (unsubmitted > 0)
                state.Pending = "⚠ " + unsubmitted + (unsubmitted == 1 ? " saved change" : " saved changes") + " not submitted.\nClick Submit so teammates get them.";
            var mine = snapshots.SelectMany(x => x.Mine).ToList();
            state.Locks = mine.Count == 0 ? "You have no files locked." :
                "Your locked files (" + mine.Count + "):\n" + String.Join("\n", mine.Take(8).Select(Path.GetFileName)) +
                (mine.Count > 8 ? "\n…" : "") + "\nSubmit when done so others can edit them.";
            return state;
        }
    }
}
