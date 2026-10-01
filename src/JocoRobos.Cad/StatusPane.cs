using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    /// <summary>What the panel's buttons and links do; all of it lives in the add-in.</summary>
    internal sealed class PaneActions
    {
        internal Action OpenRobot, Edit, Submit, CloseAndUpdate, InstallUpdate, Refresh, ReleaseUnchanged, History, Diagnostics;
        internal Func<NetworkCredential> Login;
        internal Action<FrcItem, Dictionary<string, string>> InsertFrc;
        internal Func<string, List<string>> SearchTeam;
        internal Action<string> InsertTeam;
        // Robot tab file browser: open, show in Explorer, File History for a robot file.
        internal Action<string> OpenFile, RevealFile, FileHistoryOf;
        internal Action BrowseTeam, ImportDownloaded;
    }

    /// <summary>
    /// The JOCO panel: a status card that answers "what can I do right now?" with one obvious action per section,
    /// and the Library tab for every way of getting a part. Everything else lives in Tools → JOCO ROBOS CAD.
    /// </summary>
    internal sealed class StatusPane : UserControl
    {
        private readonly Label version = Caption(8.5f, FontStyle.Regular, "JOCO ROBOS CAD " + Updater.Current + (Updater.IsDevelopmentBuild ? " (dev build)" : ""), SystemColors.GrayText);
        private readonly Label update = Caption(9f, FontStyle.Bold, "", Color.RoyalBlue);
        private readonly Button install = Action("Install update", null);
        private readonly Label flash = Caption(10f, FontStyle.Bold, "", Color.ForestGreen);
        private readonly Label warning = Caption(9.5f, FontStyle.Bold, "", Color.Firebrick);
        private readonly LinkLabel history = new LinkLabel { Text = "History of this file", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        private readonly LinkLabel diagnostics = new LinkLabel { Text = "Copy diagnostics for a mentor", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        private readonly Label working = Caption(9.5f, FontStyle.Italic, "", SystemColors.GrayText);
        private readonly Label robot = Caption(12f, FontStyle.Bold);
        private readonly Label sync = Caption(10f, FontStyle.Bold);
        private readonly Label details = Caption(8.5f, FontStyle.Regular, "", SystemColors.GrayText);
        private readonly Button open = Action("Open Robot", "Open Robot");
        private readonly Button closeUpdate = Action("Close & Update", "Update");
        private readonly Label activeFile = Caption(10f, FontStyle.Bold);
        private readonly Label activeStatus = Caption(9.5f, FontStyle.Regular);
        private readonly Button edit = Action("Edit", "Edit");
        private readonly Label pending = Caption(10f, FontStyle.Bold, "", Color.DarkOrange);
        private readonly Button submit = Action("Submit", "Submit");
        private readonly Label locks = Caption(8.5f, FontStyle.Regular, "", SystemColors.GrayText);
        private readonly LinkLabel release = new LinkLabel { Text = "Give back the ones I didn't change", AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        // The status card takes the height it needs; the robot file browser fills the rest of the Robot tab.
        private readonly FlowLayoutPanel layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 10, 12, 4) };
        private RobotFilesPanel files;
        // Gaps that only show with the section after them, so hidden sections don't leave holes.
        private readonly Control submitGap = Spacer(), locksGap = Spacer();
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TabPage libraryTab = new TabPage("Library") { BackColor = SystemColors.Window };
        private readonly FrcLibraryPanel library;

        internal StatusPane(PaneActions actions)
        {
            BackColor = SystemColors.Window;
            install.Click += (s, e) => actions.InstallUpdate();
            open.Click += (s, e) => actions.OpenRobot();
            closeUpdate.Click += (s, e) => actions.CloseAndUpdate();
            edit.Click += (s, e) => actions.Edit();
            submit.Click += (s, e) => actions.Submit();
            release.LinkClicked += (s, e) => actions.ReleaseUnchanged();
            var check = new LinkLabel { Text = "Check now", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            check.LinkClicked += (s, e) => actions.Refresh();
            history.LinkClicked += (s, e) => actions.History();
            diagnostics.LinkClicked += (s, e) => actions.Diagnostics();
            foreach (var control in new Control[] { version, update, install, warning, flash, working, robot, sync, details, open, closeUpdate, Spacer(),
                activeFile, activeStatus, edit, history, submitGap, pending, submit, locksGap, locks, release, check, diagnostics })
                layout.Controls.Add(control);
            var robotTab = new TabPage("Robot") { BackColor = SystemColors.Window };
            files = new RobotFilesPanel(actions.OpenFile, actions.RevealFile, actions.FileHistoryOf) { Dock = DockStyle.Fill };
            var robotGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
            robotGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            robotGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            robotGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            robotGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Empty space below.
            files.WantsHeight += height => { if ((int)robotGrid.RowStyles[1].Height != height) robotGrid.RowStyles[1].Height = height; };
            robotGrid.Controls.Add(layout, 0, 0);
            robotGrid.Controls.Add(files, 0, 1);
            robotTab.Controls.Add(robotGrid);
            library = new FrcLibraryPanel(actions) { Dock = DockStyle.Fill };
            libraryTab.Controls.Add(library);
            tabs.TabPages.Add(robotTab);
            tabs.TabPages.Add(libraryTab);
            Controls.Add(tabs);
            layout.Resize += (s, e) =>
            {
                // Stretch buttons and wrap text to the pane's width.
                int width = Math.Max(150, layout.ClientSize.Width - layout.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
                foreach (Control control in layout.Controls)
                {
                    if (control is Button) control.Width = width;
                    else if (control is Label) control.MaximumSize = new Size(width, 0);
                }
            };
            Show(new PaneState());
        }

        /// <summary>The robot file browser follows the robot's latest status check (and the robot chosen).</summary>
        internal void ShowRobotFiles(WorkspaceSnapshot robot)
        {
            files.Show(robot);
        }

        internal void ShowLibrary()
        {
            tabs.SelectedTab = libraryTab;
            library.FocusSearch();
        }

        private static Label Caption(float size, FontStyle style, string text = "", Color? color = null)
        {
            return new Label { AutoSize = true, MaximumSize = new Size(230, 0), Text = text, Margin = new Padding(0, 2, 0, 2),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, size, style), ForeColor = color ?? SystemColors.ControlText };
        }

        private static Button Action(string text, string icon)
        {
            return new Button { Text = "  " + text, Width = 200, Height = 38, Margin = new Padding(0, 6, 0, 2), Image = icon == null ? null : ButtonIcon(icon),
                ImageAlign = ContentAlignment.MiddleLeft, TextImageRelation = TextImageRelation.ImageBeforeText, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0), AutoEllipsis = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.5f, FontStyle.Bold) };
        }

        // Same artwork as the toolbar (Icons\button_*.png); buttons without a file just show text.
        internal static Image ButtonIcon(string label)
        {
            string name = label.ToLowerInvariant().Replace(' ', '-');
            if (name == "library" || name == "insert-from-library") name = "insert-library";
            string path = System.IO.Path.Combine(Addin.IconFolder, "button_" + name + ".png");
            try { return File.Exists(path) ? Image.FromFile(path) : null; }
            catch (OutOfMemoryException) { return null; } // Unreadable image file.
        }

        private static Control Spacer() { return new Panel { Height = 10, Width = 10 }; }

        private static Color ColorOf(Tone tone)
        {
            switch (tone)
            {
                case Tone.Muted: return SystemColors.GrayText;
                case Tone.Good: return Color.ForestGreen;
                case Tone.Warn: return Color.DarkOrange;
                case Tone.Bad: return Color.Firebrick;
                case Tone.Info: return Color.RoyalBlue;
                default: return SystemColors.ControlText;
            }
        }

        internal void Show(PaneState state)
        {
            layout.SuspendLayout();
            update.Text = state.Update ?? "";
            update.Visible = install.Visible = state.Update != null;
            flash.Text = state.Flash ?? "";
            flash.Visible = !String.IsNullOrEmpty(state.Flash);
            working.Text = state.Working ?? "";
            working.Visible = !String.IsNullOrEmpty(state.Working);
            robot.Text = state.Robot;
            robot.Visible = state.Robot.Length > 0;
            sync.Text = state.Sync;
            sync.ForeColor = ColorOf(state.SyncTone);
            details.Text = state.Details;
            details.Visible = state.Details.Length > 0;
            open.Visible = state.ShowOpen;
            closeUpdate.Visible = state.ShowCloseAndUpdate;
            activeFile.Text = state.ActiveFile;
            activeFile.Visible = state.ActiveFile.Length > 0;
            activeStatus.Text = state.ActiveStatus;
            activeStatus.ForeColor = ColorOf(state.ActiveTone);
            activeStatus.Visible = state.ActiveStatus.Length > 0;
            edit.Visible = state.EditTarget != null;
            edit.Text = "  " + (String.IsNullOrEmpty(state.EditTarget) ? "Edit" : "Edit " + state.EditTarget);
            pending.Text = state.Pending;
            pending.Visible = state.Pending.Length > 0;
            submit.Visible = state.SubmitCount > 0 || state.InterruptedSubmit;
            submit.Text = "  Submit" + (state.SubmitCount > 0 ? " " + state.SubmitCount : "");
            warning.Text = state.Warning ?? "";
            warning.Visible = !String.IsNullOrEmpty(state.Warning);
            history.Visible = state.ShowHistory;
            locks.Text = state.Locks;
            locks.Visible = state.Locks.Length > 0;
            release.Visible = state.HasLocks;
            submitGap.Visible = state.Pending.Length > 0 || state.SubmitCount > 0 || state.InterruptedSubmit;
            locksGap.Visible = state.Locks.Length > 0;
            layout.ResumeLayout();
        }
    }
}
