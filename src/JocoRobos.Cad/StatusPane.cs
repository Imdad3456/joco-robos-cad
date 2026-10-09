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
    }

    /// <summary>
    /// The CAD Hub panel. The Robot tab reads top to bottom as: where the robot stands (one line; details on hover), the one
    /// thing to do next if there is one, This file (the open document: one status line, one action), My work (every file
    /// this student is responsible for, with Submit), then the robot's files with who's working where. Support links sit in a
    /// small footer. The Library tab has every way of getting a part; everything else lives in Tools → CAD Hub.
    /// </summary>
    internal sealed class StatusPane : UserControl
    {
        internal static readonly Color Hairline = Color.FromArgb(226, 229, 233), CardBack = Color.FromArgb(246, 247, 249);
        private const int MaxWorkRows = 8;

        private readonly PaneActions actions;
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000 };
        private readonly Label update = Caption(9f, FontStyle.Bold, "", Color.RoyalBlue);
        private readonly Button install = Action("Install update", null);
        private readonly Label flash = Caption(9.5f, FontStyle.Bold, "", Color.ForestGreen);
        private readonly Label warning = Caption(9.5f, FontStyle.Bold, "", Color.Firebrick);
        private readonly Label working = Caption(9f, FontStyle.Italic, "", SystemColors.GrayText);
        private readonly Label robot = Caption(12f, FontStyle.Bold);
        private readonly Label sync = Caption(9.5f, FontStyle.Bold);
        private readonly Label refresh = new Label { Text = "", AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(4, 4, 0, 0),
            Font = new Font("Segoe MDL2 Assets", 9f), ForeColor = SystemColors.GrayText };
        private readonly Label details = Caption(8.5f, FontStyle.Regular, "", SystemColors.GrayText);
        private readonly Button open = Action("Open Robot", "Open Robot");
        private readonly Button closeUpdate = Action("Close & Update", "Update");
        // This file: only while a document is open.
        private readonly Label activeFile = Caption(10f, FontStyle.Bold);
        // Regular weight: Windows has no bold fallback for 🔒 and would draw a box.
        private readonly Label activeStatus = Caption(9.5f, FontStyle.Regular);
        private readonly Label activeHint = Caption(8.5f, FontStyle.Regular, "", SystemColors.GrayText);
        private readonly Button edit = Action("Edit", "Edit");
        private readonly LinkLabel ask = Link("Ask for it");
        private readonly LinkLabel history = Link("History of this file");
        private readonly Card card = new Card();
        // My work: only while this student has locks, changes or new files.
        private readonly FlowLayoutPanel work = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 14, 0, 0) };
        private readonly TableLayoutPanel workHeader = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        private readonly Label workTitle = Heading("MY WORK");
        private readonly LinkLabel release = Link("Give back unchanged", 8.25f);
        private readonly FlowLayoutPanel workRows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        private readonly Label interrupted = Caption(9f, FontStyle.Bold, "An earlier Submit was interrupted. Submit checks what reached the team and finishes it safely.", Color.DarkOrange);
        private readonly LinkLabel notNow = Link("Not yet: let them know", 8.25f);
        private readonly Button submit = Action("Submit", "Submit");
        private string workShown;
        private readonly TableLayoutPanel header = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        private readonly FlowLayoutPanel layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 12, 14, 12) };
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
            closeUpdate.Click += (s, e) => actions.CloseAndUpdate();
            edit.Click += (s, e) => actions.Edit();
            submit.Click += (s, e) => actions.Submit();
            release.LinkClicked += (s, e) => actions.ReleaseUnchanged();
            history.LinkClicked += (s, e) => actions.History();
            ask.LinkClicked += (s, e) => actions.AskForFile?.Invoke();
            notNow.LinkClicked += (s, e) => actions.DismissRequests?.Invoke();
            refresh.Click += (s, e) => actions.Refresh();
            refresh.MouseEnter += (s, e) => refresh.ForeColor = SystemColors.HotTrack;
            refresh.MouseLeave += (s, e) => refresh.ForeColor = SystemColors.GrayText;
            tips.SetToolTip(refresh, "Check the server now");
            tips.SetToolTip(release, "Give back the files you locked but didn't change, so teammates can edit them");

            // 1. Where am I: robot name on the left, whether it's up to date (and ↻) on the right. Details on hover.
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            robot.Margin = new Padding(0, 0, 8, 0);
            sync.Margin = new Padding(0, 3, 0, 0);
            header.Controls.Add(robot, 0, 0);
            header.Controls.Add(sync, 1, 0);
            header.Controls.Add(refresh, 2, 0);

            // 2. This file.
            history.Margin = new Padding(0, 4, 0, 0);
            activeStatus.Margin = new Padding(0, 4, 0, 0);
            foreach (var control in new Control[] { activeFile, activeStatus, activeHint, edit, ask, history })
                card.Body.Controls.Add(control);
            card.Margin = new Padding(0, 12, 0, 0);

            // 3. My work.
            workHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            release.Anchor = AnchorStyles.Right;
            workHeader.Controls.Add(workTitle, 0, 0);
            workHeader.Controls.Add(release, 1, 0);
            foreach (var control in new Control[] { workHeader, interrupted, workRows, notNow, submit })
                work.Controls.Add(control);

            foreach (var control in new Control[] { header, details, update, install, warning, flash, working, open, closeUpdate, card, work })
                layout.Controls.Add(control);

            // 4. What do I want to work on: the robot's files fill the rest. Then a small footer.
            files = new RobotFilesPanel(actions.OpenFile, actions.RevealFile, actions.FileHistoryOf, actions.WhereUsedOf, actions.AskFor)
                { Dock = DockStyle.Fill, MinimumSize = new Size(0, 160) };
            var robotGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, BackColor = SystemColors.Window };
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
            layout.Resize += (s, e) => FitWidth();
            robotGrid.Resize += (s, e) => FitWidth();
            Show(new PaneState());
        }

        // Diagnostics on the left, the version on the right: there when needed, out of the way otherwise.
        private Control Footer(PaneActions actions)
        {
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(14, 5, 14, 6), Margin = new Padding(0) };
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

        // Stretch buttons and wrap text to the pane's width.
        private void FitWidth()
        {
            // The visible width comes from the container: the layout itself grows to fit its widest child and would never shrink back.
            int visible = layout.Parent != null ? layout.Parent.ClientSize.Width : layout.ClientSize.Width;
            int width = Math.Max(150, visible - layout.Padding.Horizontal - layout.Margin.Horizontal - SystemInformation.VerticalScrollBarWidth);
            header.MinimumSize = header.MaximumSize = new Size(width, 0);
            robot.MaximumSize = new Size(Math.Max(80, width / 2), 0);
            sync.MaximumSize = new Size(Math.Max(80, (robot.Visible ? width - robot.PreferredSize.Width - 10 : width) - refresh.PreferredSize.Width - 6), 0);
            card.MinimumSize = card.MaximumSize = new Size(width, 0);
            foreach (Control control in layout.Controls)
            {
                if (control is Button) control.Width = width;
                else if (control is Label && control != robot && control != sync) control.MaximumSize = new Size(width, 0);
            }
            int inner = width - card.Padding.Horizontal;
            foreach (Control control in card.Body.Controls)
            {
                if (control is Button) control.Width = inner;
                else if (control is Label) control.MaximumSize = new Size(inner, 0);
            }
            work.MinimumSize = work.MaximumSize = new Size(width, 0);
            workHeader.MinimumSize = workHeader.MaximumSize = new Size(width, 0);
            interrupted.MaximumSize = new Size(width, 0);
            submit.Width = width;
            foreach (Control row in workRows.Controls)
            {
                if (!(row is TableLayoutPanel)) { row.MaximumSize = new Size(width, 0); continue; } // "… and 3 more"
                row.MinimumSize = row.MaximumSize = new Size(width, 0);
                foreach (Control part in row.Controls)
                    part.MaximumSize = new Size(part is LinkLabel ? Math.Max(60, width * 3 / 5) : width, 0);
            }
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

        private static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 0, 2),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.5f, FontStyle.Bold), ForeColor = SystemColors.GrayText };
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
            robot.Text = state.Robot;
            bool named = state.Robot.Length > 0;
            robot.Visible = named;
            // No robot name yet (not signed in, checking): the status takes the line.
            header.SetColumn(sync, named ? 1 : 0);
            header.SetColumnSpan(sync, named ? 1 : 2);
            sync.Anchor = AnchorStyles.Top | (named ? AnchorStyles.Right : AnchorStyles.Left);
            sync.Text = state.Sync;
            sync.ForeColor = ColorOf(state.SyncTone);
            tips.SetToolTip(sync, state.SyncTip);
            tips.SetToolTip(robot, state.SyncTip);
            refresh.Visible = named;
            details.Text = state.Details;
            details.Visible = state.Details.Length > 0;
            open.Visible = state.ShowOpen;
            closeUpdate.Visible = state.ShowCloseAndUpdate;
            warning.Text = state.Warning ?? "";
            warning.Visible = !String.IsNullOrEmpty(state.Warning);

            activeFile.Text = state.ActiveFile;
            activeStatus.Text = state.ActiveStatus;
            activeStatus.ForeColor = ColorOf(state.ActiveTone);
            activeStatus.Visible = state.ActiveStatus.Length > 0;
            activeHint.Text = state.ActiveHint;
            activeHint.Visible = state.ActiveHint.Length > 0;
            edit.Visible = state.EditTarget != null;
            edit.Text = "  " + (String.IsNullOrEmpty(state.EditTarget) ? "Edit" : "Edit " + state.EditTarget);
            ask.Text = state.AskOwner == null ? "Ask for it" : "Ask " + state.AskOwner + " for it";
            ask.Visible = state.AskOwner != null;
            history.Visible = state.ShowHistory;
            card.Visible = state.ActiveFile.Length > 0;

            bool showSubmit = state.SubmitCount > 0 || state.InterruptedSubmit;
            submit.Visible = showSubmit;
            submit.Text = "  Submit" + (state.SubmitCount > 0 ? " " + state.SubmitCount : "");
            interrupted.Visible = state.InterruptedSubmit;
            release.Text = "Give back " + state.Unchanged + " unchanged";
            release.Visible = state.Unchanged > 0;
            notNow.Visible = state.AnyoneWaiting;
            workTitle.Text = "MY WORK" + (state.Work.Count > 0 ? " (" + state.Work.Count + ")" : "");
            ShowWork(state.Work);
            work.Visible = state.Work.Count > 0 || state.InterruptedSubmit;
            layout.ResumeLayout();
            FitWidth();
        }

        // One row per file: mark and name (click to open), its state on the right, and who's waiting for it underneath.
        private void ShowWork(List<WorkItem> items)
        {
            string shown = String.Join("|", items.Select(w => w.Path + ":" + w.State + ":" + w.WaitingFor));
            if (shown == workShown) return;
            workShown = shown;
            workRows.SuspendLayout();
            foreach (Control old in workRows.Controls.Cast<Control>().ToList()) { workRows.Controls.Remove(old); old.Dispose(); }
            foreach (var item in items.Take(MaxWorkRows))
            {
                var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 1, 0, 1) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var name = Link(item.Mark + " " + Path.GetFileNameWithoutExtension(item.Name), 9f);
                name.AutoEllipsis = true;
                name.LinkColor = item.Mark == "⚠" ? Color.DarkOrange : SystemColors.ControlText;
                string path = item.Path;
                name.LinkClicked += (s, e) => actions.OpenFile?.Invoke(path);
                tips.SetToolTip(name, item.Name + "\nClick to open");
                var stateLabel = Caption(8.5f, FontStyle.Regular, item.State, StatusPane.ColorOf(item.Tone));
                stateLabel.Anchor = AnchorStyles.Right;
                row.Controls.Add(name, 0, 0);
                row.Controls.Add(stateLabel, 1, 0);
                if (item.WaitingFor != null)
                {
                    var waiting = Caption(8.5f, FontStyle.Bold, "   ✋ " + item.WaitingFor + (item.WaitingFor.Contains(",") ? " are" : " is") + " waiting for it", Color.DarkOrange);
                    row.Controls.Add(waiting, 0, 1);
                    row.SetColumnSpan(waiting, 2);
                }
                workRows.Controls.Add(row);
            }
            if (items.Count > MaxWorkRows)
                workRows.Controls.Add(Caption(8.5f, FontStyle.Regular, "… and " + (items.Count - MaxWorkRows) + " more (Submit lists them all)", SystemColors.GrayText));
            workRows.ResumeLayout();
        }

        /// <summary>A light box for the open file.</summary>
        private sealed class Card : Panel
        {
            internal readonly FlowLayoutPanel Body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), BackColor = CardBack, Dock = DockStyle.Top };

            internal Card()
            {
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                BackColor = CardBack;
                Padding = new Padding(10, 8, 10, 10);
                ResizeRedraw = true;
                DoubleBuffered = true;
                Controls.Add(Body);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var pen = new Pen(Hairline)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}
