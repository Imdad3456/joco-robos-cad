using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
        internal Action<string> OpenFile, RevealFile, FileHistoryOf, WhereUsedOf;
        // Ask whoever is editing a robot file (from the file tree) to give it back.
        internal Action<string> AskFor;
        internal Action BrowseTeam, ImportDownloaded;
        // Library tab extras: how many copies the next insert adds, and CAD Hub's modeling tools.
        internal Action<int> SetCopies;
        internal Action BeltChain;
        // "Ask for it": ask the person editing the active file; tell teammates who asked you "not yet".
        internal Action AskForFile, DismissRequests;
        // Recovered work after a crash: review it.
        internal Action ReviewRecovery;
        // The bell: answer one request "not yet" (by id); open or put away a file that's free now (by path).
        internal Action<string> DismissRequest, OpenFreed, DismissFreed;
    }

    /// <summary>
    /// The CAD Hub panel. The Robot tab has a fixed top that never scrolls (the robot and its sync state with what to do, the open
    /// file in one row, My work as one summary line that expands), and the robot's files take all the height that's left: the only
    /// part that scrolls. Support links sit in a small footer. The Library tab has every way of getting a part; everything else
    /// lives in Tools → CAD Hub.
    /// </summary>
    internal sealed class StatusPane : UserControl
    {
        internal static readonly Color Hairline = Color.FromArgb(226, 229, 233);
        private const int MaxWorkRows = 6;
        private const string SettingsKey = @"Software\JOCO ROBOS\CAD";

        private readonly PaneActions actions;
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000 };
        // Rare lines: an add-in update, a SOLIDWORKS version problem, a confirmation, background work.
        private readonly Label update = Caption(9f, FontStyle.Bold, "", Color.RoyalBlue);
        private readonly Button install = Action("Install update", null);
        private readonly Label flash = Caption(9f, FontStyle.Bold, "", Color.ForestGreen);
        private readonly Label warning = Caption(9f, FontStyle.Bold, "", Color.Firebrick);
        private readonly Label working = Caption(8.5f, FontStyle.Italic, "", SystemColors.GrayText);
        // The robot and its sync state: an icon and a word, what to do about it, at most one action.
        private readonly Label robot = Caption(11.5f, FontStyle.Bold);
        private readonly Label refresh = new Label { Text = "", AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(6, 3, 0, 0),
            Font = new Font("Segoe MDL2 Assets", 9f), ForeColor = SystemColors.GrayText };
        // The bell and how many things are waiting on the student; hidden when there's nothing to act on.
        private readonly Label bell = new Label { AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(6, 2, 0, 0), ForeColor = Color.DarkOrange,
            Font = new Font("Segoe UI Emoji", 9f, FontStyle.Bold) };
        private readonly FlowLayoutPanel notices = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 6, 0, 0), Padding = new Padding(8, 4, 8, 4), BackColor = Color.FromArgb(250, 246, 238) };
        private bool noticesOpen;
        private string noticesShown;
        private readonly Label sync = Caption(9.5f, FontStyle.Bold);
        private readonly LinkLabel syncAction = Link("", 9f);
        private readonly Label syncNote = Caption(8.5f, FontStyle.Regular, "", SystemColors.GrayText);
        private readonly Button open = Action("Open Robot", "Open Robot");
        // The open file: one row, a second line only for a problem.
        private readonly FileRow active = new FileRow { Margin = new Padding(0, 10, 0, 0) };
        private readonly Label activeHint = Caption(8.5f, FontStyle.Regular);
        // My work: one line (summary and Submit), requests always shown, every file when expanded.
        private readonly Label workToggle = new Label { AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(0, 5, 4, 0),
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.5f, FontStyle.Bold) };
        private readonly Label workSummary = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 6, 0), ForeColor = SystemColors.GrayText,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.5f) };
        private readonly Button submit = new Button { Height = 28, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 0),
            Padding = new Padding(4, 0, 4, 0), Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f, FontStyle.Bold) };
        private readonly TableLayoutPanel workHeader = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        private readonly TableLayoutPanel requestsRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        private readonly Label requests = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0), ForeColor = Color.DarkOrange, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.5f, FontStyle.Bold) };
        private readonly LinkLabel notNow = Link("Not yet", 8.5f);
        private readonly Label interrupted = Caption(8.5f, FontStyle.Bold, "⚠ An earlier Submit was interrupted. Click Submit: it checks what reached the team and finishes safely.", Color.DarkOrange);
        private readonly FlowLayoutPanel workRows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 0) };
        private readonly TableLayoutPanel workFooter = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        private readonly LinkLabel release = Link("Give back unchanged", 8.5f);
        private readonly LinkLabel stopEditing = Link("", 8.5f);
        private bool workExpanded;
        private string workShown;
        private int workCount;
        private readonly TableLayoutPanel header = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0) };
        private readonly TableLayoutPanel syncRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        private readonly FlowLayoutPanel layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0) };
        private readonly TableLayoutPanel robotGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = SystemColors.Window };
        private readonly RobotFilesPanel files;
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TabPage libraryTab = new TabPage("Library") { BackColor = SystemColors.Window };
        private readonly FrcLibraryPanel library;

        internal StatusPane(PaneActions actions)
        {
            this.actions = actions;
            BackColor = SystemColors.Window;
            install.Click += (s, e) => actions.InstallUpdate();
            open.Click += (s, e) => actions.OpenRobot();
            submit.Click += (s, e) => actions.Submit();
            release.LinkClicked += (s, e) => actions.ReleaseUnchanged();
            notNow.LinkClicked += (s, e) => actions.DismissRequests?.Invoke();
            refresh.Click += (s, e) => actions.Refresh();
            refresh.MouseEnter += (s, e) => refresh.ForeColor = SystemColors.HotTrack;
            refresh.MouseLeave += (s, e) => refresh.ForeColor = SystemColors.GrayText;
            tips.SetToolTip(refresh, "Check the server now");
            tips.SetToolTip(release, "Give back the files you locked but didn't change, so teammates can edit them");
            tips.SetToolTip(notNow, "Let them know you're still working on it");
            active.ActionClicked += action => { if (action == FileAction.Edit) actions.Edit(); else if (action == FileAction.Ask) actions.AskForFile?.Invoke(); };
            workToggle.Click += (s, e) => ToggleWork();
            workSummary.Click += (s, e) => ToggleWork();
            workExpanded = ReadExpanded();

            // The robot on the left, ↻ on the right; under it the sync state and its one action.
            header.ColumnCount = 3;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            robot.Margin = new Padding(0);
            header.Controls.Add(robot, 0, 0);
            header.Controls.Add(bell, 1, 0);
            header.Controls.Add(refresh, 2, 0);
            bell.Click += (s, e) => { noticesOpen = !noticesOpen; notices.Visible = noticesOpen && notices.Controls.Count > 0; FitWidth(); };
            syncRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            syncRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            sync.Margin = new Padding(0);
            syncAction.Margin = new Padding(6, 1, 0, 0);
            syncAction.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            syncAction.LinkClicked += (s, e) =>
            {
                if ((SyncAction)syncAction.Tag == SyncAction.CloseAndUpdate) actions.CloseAndUpdate();
                else if ((SyncAction)syncAction.Tag == SyncAction.ReviewRecovery) actions.ReviewRecovery?.Invoke();
            };
            syncRow.Controls.Add(sync, 0, 0);
            syncRow.Controls.Add(syncAction, 1, 0);
            syncNote.Margin = new Padding(0, 1, 0, 0);
            activeHint.Margin = new Padding(0, 2, 0, 0);

            // My work: "▸ My work 6 · 1 unsaved · 2 ready …  [Submit 3]".
            workHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            workHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            workHeader.Controls.Add(workToggle, 0, 0);
            workHeader.Controls.Add(workSummary, 1, 0);
            workHeader.Controls.Add(submit, 2, 0);
            requestsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            requestsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            requestsRow.Controls.Add(requests, 0, 0);
            requestsRow.Controls.Add(notNow, 1, 0);
            workFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workFooter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            workFooter.Controls.Add(release, 0, 0);
            interrupted.Margin = new Padding(0, 3, 0, 0);

            foreach (var control in new Control[] { header, notices, syncRow, syncNote, update, install, warning, flash, working, open, active, activeHint,
                workHeader, requestsRow, interrupted, workRows, workFooter })
                layout.Controls.Add(control);

            // The fixed top, a hairline, the robot's files filling the rest, a hairline, the footer. No scrolling but the tree's.
            files = new RobotFilesPanel(actions.OpenFile, actions.RevealFile, actions.FileHistoryOf, actions.WhereUsedOf, actions.AskFor)
                { Dock = DockStyle.Fill, MinimumSize = new Size(0, 120) };
            robotGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            robotGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            robotGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            robotGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            robotGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            robotGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            robotGrid.Controls.Add(layout, 0, 0);
            robotGrid.Controls.Add(Rule(), 0, 1);
            robotGrid.Controls.Add(files, 0, 2);
            robotGrid.Controls.Add(Rule(), 0, 3);
            robotGrid.Controls.Add(Footer(actions), 0, 4);
            var robotTab = new TabPage("Robot") { BackColor = SystemColors.Window };
            robotTab.Controls.Add(robotGrid);
            library = new FrcLibraryPanel(actions) { Dock = DockStyle.Fill };
            libraryTab.Controls.Add(library);
            tabs.TabPages.Add(robotTab);
            tabs.TabPages.Add(libraryTab);
            Controls.Add(tabs);
            robotGrid.Resize += (s, e) => FitWidth();
            Show(new PaneState());
        }

        // Diagnostics on the left, the version on the right: there when needed, out of the way otherwise.
        private Control Footer(PaneActions actions)
        {
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(12, 4, 12, 5), Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var diagnostics = Link("Diagnostics", 8.25f);
            diagnostics.LinkClicked += (s, e) => actions.Diagnostics();
            tips.SetToolTip(diagnostics, "Something wrong? Sends a report (no passwords) to your mentors, and copies it");
            footer.Controls.Add(diagnostics, 0, 0);
            var version = Caption(8f, FontStyle.Regular, Updater.Current + (Updater.IsDevelopmentBuild ? " (dev)" : ""), SystemColors.GrayText);
            version.Anchor = AnchorStyles.Right;
            tips.SetToolTip(version, "CAD Hub " + Updater.Current);
            footer.Controls.Add(version, 1, 0);
            return footer;
        }

        // Everything in the fixed top fits the pane's width: wrapping text wraps, one-line rows shorten with "…".
        private void FitWidth()
        {
            int width = Math.Max(150, robotGrid.ClientSize.Width - layout.Padding.Horizontal);
            layout.SuspendLayout();
            foreach (Control control in new Control[] { header, syncRow, workHeader, requestsRow, workFooter })
                control.MinimumSize = control.MaximumSize = new Size(width, 0);
            notices.MinimumSize = notices.MaximumSize = new Size(width, 0);
            foreach (Control row in notices.Controls) row.MinimumSize = row.MaximumSize = new Size(width - notices.Padding.Horizontal, 0);
            // A painted row, not a sized-to-fit one: give it its size directly (size limits would squash its height to 0).
            active.Size = new Size(width, active.HeightFor(width));
            robot.MaximumSize = new Size(Math.Max(60, width - refresh.PreferredSize.Width - (bell.Visible ? bell.PreferredSize.Width + 6 : 0) - 8), 0);
            sync.MaximumSize = new Size(Math.Max(60, width - (syncAction.Visible ? syncAction.PreferredSize.Width + 8 : 0)), 0);
            foreach (var label in new[] { syncNote, update, warning, flash, working, activeHint, interrupted })
                label.MaximumSize = new Size(width, 0);
            install.Width = open.Width = width;
            workSummary.Height = requests.Height = Math.Max(workToggle.PreferredSize.Height, 20);
            foreach (Control row in workRows.Controls)
            {
                if (!(row is TableLayoutPanel)) { row.MaximumSize = new Size(width - 14, 0); continue; } // "… and 3 more"
                row.MinimumSize = row.MaximumSize = new Size(width - 14, 0);
            }
            layout.ResumeLayout();
        }

        /// <summary>The robot file browser follows the robot's latest status check, and selects the open document.</summary>
        internal void ShowRobotFiles(WorkspaceSnapshot robot, string user, string activePath)
        {
            files.Show(robot, user);
            files.Follow(activePath);
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

        private static LinkLabel Link(string text, float size = 8.5f)
        {
            return new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 2), LinkBehavior = LinkBehavior.HoverUnderline,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, size) };
        }

        private static Control Rule()
        {
            return new Panel { Dock = DockStyle.Fill, Height = 1, Margin = new Padding(0), BackColor = Hairline };
        }

        private static Button Action(string text, string icon)
        {
            return new Button { Text = "  " + text, Width = 200, Height = 36, Margin = new Padding(0, 8, 0, 2), Image = icon == null ? null : ButtonIcon(icon),
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

        internal static Color ColorOf(Tone tone)
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
            warning.Text = state.Warning ?? "";
            warning.Visible = !String.IsNullOrEmpty(state.Warning);

            robot.Text = state.Robot.Length > 0 ? state.Robot : "CAD Hub";
            refresh.Visible = state.Robot.Length > 0;
            sync.Text = state.Sync;
            sync.ForeColor = ColorOf(state.SyncTone);
            tips.SetToolTip(sync, state.SyncTip);
            tips.SetToolTip(robot, state.SyncTip);
            syncAction.Tag = state.SyncAction;
            syncAction.Text = state.SyncAction == SyncAction.CloseAndUpdate ? "Close & Update" : state.SyncAction == SyncAction.ReviewRecovery ? "Review" : "";
            syncAction.Visible = state.SyncAction != SyncAction.None;
            syncNote.Text = state.SyncNote;
            syncNote.Visible = state.SyncNote.Length > 0;
            open.Visible = state.ShowOpen;

            active.Visible = state.ActiveFile.Length > 0;
            active.Show(state.ActiveFile, state.ActiveStatus, ColorOf(state.ActiveTone), state.ActiveAction,
                state.ActiveAction == FileAction.Ask ? "Ask " + state.AskOwner : state.ActiveAction == FileAction.Edit ? "Edit" : "");
            tips.SetToolTip(active, state.ActiveTip);
            activeHint.Text = state.ActiveHint;
            activeHint.ForeColor = state.ActiveHint.StartsWith("⚠") ? Color.DarkOrange : SystemColors.GrayText;
            activeHint.Visible = active.Visible && state.ActiveHint.Length > 0;

            bool anyWork = state.Work.Count > 0 || state.InterruptedSubmit;
            workCount = state.Work.Count;
            workHeader.Visible = anyWork;
            workToggle.Text = (workExpanded ? "▾" : "▸") + " My work " + state.Work.Count;
            workSummary.Text = state.WorkSummary.Length > 0 ? "· " + state.WorkSummary : "";
            tips.SetToolTip(workSummary, state.WorkSummary.Replace(" · ", "\n") + "\n\nClick to " + (workExpanded ? "hide" : "show") + " each file");
            tips.SetToolTip(workToggle, workExpanded ? "Hide the file list" : "Show each file");
            submit.Visible = state.SubmitCount > 0 || state.InterruptedSubmit;
            submit.Text = "Submit" + (state.SubmitCount > 0 ? " " + state.SubmitCount : "");
            submit.Image = ButtonIcon("Submit");
            submit.TextImageRelation = TextImageRelation.ImageBeforeText;
            requests.Text = state.Requests;
            tips.SetToolTip(requests, String.Join("\n", state.Work.Where(w => w.WaitingFor != null).Select(w => w.WaitingFor + " is waiting for " + w.Name)) +
                "\nSubmit (or give it back) when you're done with it.");
            requestsRow.Visible = state.AnyoneWaiting;
            interrupted.Visible = state.InterruptedSubmit;
            release.Text = "Give back " + state.Unchanged + " unchanged";
            workFooter.Visible = workExpanded && state.Unchanged > 0;
            ShowWork(state.Work);
            workRows.Visible = workExpanded && state.Work.Count > 0;
            ShowNotices(state.Notices);
            layout.ResumeLayout();
            FitWidth();
        }

        // The bell's list: one row per thing to act on, each with its own answer. New ones only change the count; the list
        // opens when the student clicks the bell, so nothing jumps around while they model.
        private void ShowNotices(List<Notice> items)
        {
            bell.Text = "\U0001F514 " + items.Count;
            bell.Visible = items.Count > 0;
            tips.SetToolTip(bell, items.Count == 0 ? "" : String.Join("\n", items.Select(n => n.Text)) + "\n\nClick to " + (noticesOpen ? "hide" : "answer") + " them here");
            string shown = String.Join("|", items.Select(n => n.Kind + ":" + n.Path + ":" + n.RequestId));
            if (shown != noticesShown)
            {
                noticesShown = shown;
                notices.SuspendLayout();
                foreach (Control old in notices.Controls.Cast<Control>().ToList()) { notices.Controls.Remove(old); old.Dispose(); }
                foreach (var notice in items) notices.Controls.Add(NoticeRow(notice));
                notices.ResumeLayout();
            }
            if (items.Count == 0) noticesOpen = false;
            notices.Visible = noticesOpen && items.Count > 0;
        }

        // ✋ sarah is waiting for Shooter Hood ........ Not yet
        // ✓ Gearbox Plate is free now (sarah gave it back) ........ Open  ×
        private Control NoticeRow(Notice notice)
        {
            var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 1, 0, 1) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bool request = notice.Kind == NoticeKind.Request;
            var text = new Label { Text = (request ? "✋ " : "✓ ") + notice.Text, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 20,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0), ForeColor = request ? Color.DarkOrange : Color.ForestGreen,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.5f, FontStyle.Bold) };
            tips.SetToolTip(text, notice.Text + (request ? "\nSubmit it (or give it back) when you're done, or tell them Not yet." : "\nOpen it and click Edit."));
            row.Controls.Add(text, 0, 0);
            string path = notice.Path, id = notice.RequestId;
            if (request)
            {
                var notYet = Link("Not yet", 8.5f);
                notYet.Margin = new Padding(6, 2, 0, 0);
                notYet.LinkClicked += (s, e) => actions.DismissRequest?.Invoke(id);
                row.Controls.Add(notYet, 1, 0);
            }
            else
            {
                var openIt = Link("Open", 8.5f);
                openIt.Margin = new Padding(6, 2, 0, 0);
                openIt.LinkClicked += (s, e) => actions.OpenFreed?.Invoke(path);
                var dismiss = Link("×", 9f);
                dismiss.Margin = new Padding(8, 1, 0, 0);
                dismiss.LinkClicked += (s, e) => actions.DismissFreed?.Invoke(path);
                tips.SetToolTip(dismiss, "Remove this from the list");
                row.Controls.Add(openIt, 1, 0);
                row.Controls.Add(dismiss, 2, 0);
            }
            return row;
        }

        private void ToggleWork()
        {
            workExpanded = !workExpanded;
            try { using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey)) key.SetValue("PaneWorkExpanded", workExpanded ? 1 : 0); }
            catch (Exception) { } // A convenience only.
            workToggle.Text = (workExpanded ? "▾" : "▸") + " My work " + workCount;
            workRows.Visible = workExpanded && workRows.Controls.Count > 0;
            workFooter.Visible = workExpanded && release.Text != "Give back 0 unchanged";
            FitWidth();
        }

        private static bool ReadExpanded()
        {
            try { using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey)) return key != null && Convert.ToInt32(key.GetValue("PaneWorkExpanded", 0)) == 1; }
            catch (Exception) { return false; }
        }

        // One row per file: mark and name (click to open), its state on the right in words, who's waiting for it underneath.
        private void ShowWork(List<WorkItem> items)
        {
            string shown = String.Join("|", items.Select(w => w.Path + ":" + w.State + ":" + w.WaitingFor + ":" + w.Health?.Summary));
            if (shown == workShown) return;
            workShown = shown;
            workRows.SuspendLayout();
            foreach (Control old in workRows.Controls.Cast<Control>().ToList()) { workRows.Controls.Remove(old); old.Dispose(); }
            foreach (var item in items.Take(MaxWorkRows))
            {
                var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(14, 0, 0, 0) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var name = new LinkLabel { Text = item.Mark + " " + Path.GetFileNameWithoutExtension(item.Name) + (item.WaitingFor != null ? "  ✋" : ""),
                    AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 20, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0),
                    LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = item.Mark == "⚠" ? Color.DarkOrange : SystemColors.ControlText,
                    Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f) };
                string path = item.Path;
                name.LinkClicked += (s, e) => actions.OpenFile?.Invoke(path);
                tips.SetToolTip(name, item.Name + (item.WaitingFor != null ? "\n" + item.WaitingFor + " is waiting for it" : "") +
                    (item.Health != null ? "\n" + item.Health.Advice : "") + "\nClick to open");
                var state = Caption(8.5f, FontStyle.Regular, item.State, ColorOf(item.Tone));
                state.Anchor = AnchorStyles.Right;
                state.Margin = new Padding(6, 2, 0, 0);
                row.Controls.Add(name, 0, 0);
                row.Controls.Add(state, 1, 0);
                if (item.Health != null)
                {
                    // Under the name: what its last save showed. Hover for what to do; Submit still decides what blocks.
                    var health = new Label { Text = "⚠ " + item.Health.Summary, AutoSize = true, Margin = new Padding(14, 0, 0, 2), ForeColor = Color.DarkOrange,
                        Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.5f) };
                    tips.SetToolTip(health, item.Health.Advice);
                    row.Controls.Add(health, 0, 1);
                    row.SetColumnSpan(health, 2);
                }
                workRows.Controls.Add(row);
            }
            if (items.Count > MaxWorkRows)
            {
                var more = Caption(8.5f, FontStyle.Regular, "… and " + (items.Count - MaxWorkRows) + " more (Submit lists them all)", SystemColors.GrayText);
                more.Margin = new Padding(14, 1, 0, 0);
                workRows.Controls.Add(more);
            }
            workRows.ResumeLayout();
        }

        /// <summary>
        /// The open file in one line: its name (bold), its status (in words, colored), and one action on the right. When the pane is
        /// narrow the name shortens first, so the status and the action stay readable.
        /// </summary>
        private sealed class FileRow : Control
        {
            private string name = "", status = "", action = "";
            private FileAction kind;
            private Color statusColor;
            private Rectangle actionBounds;
            private bool overAction;
            private readonly Font bold = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.5f, FontStyle.Bold);
            internal event Action<FileAction> ActionClicked;

            internal FileRow()
            {
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                Height = LineHeight;
            }

            private const TextFormatFlags Flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            private const int Gap = 14; // " · " between the name and the status

            private int LineHeight { get { return Math.Max(bold.Height, Font.Height) + 6; } }

            /// <summary>One line when the name (or most of it), the status and the action fit; otherwise the status gets its own line.</summary>
            internal int HeightFor(int width) { return OneLine(width) ? LineHeight : LineHeight * 2 - 4; }

            private bool OneLine(int width)
            {
                if (status.Length == 0) return true;
                int nameWidth = Math.Min(Measure(name, bold), Measure("Flywheel Pl…", bold)); // a little of the name is enough
                return nameWidth + Gap + Measure(status, Font) + ActionWidth() <= width;
            }

            private int ActionWidth() { return action.Length == 0 ? 0 : Measure(action, Font) + 10; }

            private static int Measure(string text, Font font) { return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, 100), Flags).Width; }

            internal void Show(string fileName, string fileStatus, Color color, FileAction fileAction, string actionText)
            {
                name = fileName; status = fileStatus; statusColor = color; kind = fileAction; action = actionText;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                int line = LineHeight, actionWidth = ActionWidth();
                actionBounds = actionWidth == 0 ? Rectangle.Empty : new Rectangle(Width - actionWidth + 10, 0, actionWidth - 10, line);
                int room = Width - actionWidth;
                if (OneLine(Width))
                {
                    // Name · status ........ action
                    int nameShown = Math.Min(Measure(name, bold), Math.Max(room / 3, room - (status.Length == 0 ? 0 : Measure(status, Font) + Gap)));
                    TextRenderer.DrawText(e.Graphics, name, bold, new Rectangle(0, 0, nameShown, line), ForeColor, Flags | TextFormatFlags.EndEllipsis);
                    if (status.Length > 0)
                    {
                        int x = nameShown + 4;
                        TextRenderer.DrawText(e.Graphics, "·", Font, new Rectangle(x, 0, Gap - 4, line), SystemColors.GrayText, Flags);
                        TextRenderer.DrawText(e.Graphics, status, Font, new Rectangle(x + Gap - 4, 0, Math.Max(0, room - x - Gap + 4), line), statusColor, Flags | TextFormatFlags.EndEllipsis);
                    }
                }
                else
                {
                    // Name ........ action
                    // status
                    TextRenderer.DrawText(e.Graphics, name, bold, new Rectangle(0, 0, room, line), ForeColor, Flags | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(e.Graphics, status, Font, new Rectangle(0, line - 4, Width, line), statusColor, Flags | TextFormatFlags.EndEllipsis);
                }
                if (actionWidth > 0)
                    using (var link = new Font(Font, overAction ? FontStyle.Underline : FontStyle.Regular))
                        TextRenderer.DrawText(e.Graphics, action, link, actionBounds, SystemColors.HotTrack, Flags);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                bool over = actionBounds.Contains(e.Location);
                if (over == overAction) return;
                overAction = over;
                Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (!overAction) return;
                overAction = false;
                Invalidate();
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (actionBounds.Contains(e.Location) && kind != FileAction.None) ActionClicked?.Invoke(kind);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) bold.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
