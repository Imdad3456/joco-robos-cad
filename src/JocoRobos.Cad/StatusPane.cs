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
        internal Action<string> OpenFile, RevealFile, FileHistoryOf, WhereUsedOf;
        internal Action BrowseTeam, ImportDownloaded;
        // Library tab extras: how many copies the next insert adds, and CAD Hub's modeling tools.
        internal Action<int> SetCopies;
        internal Action MakeStock, MakeGear, BeltChain;
        internal Action<StockType, double?, double?, int> InsertStock;
        // "Ask for it": ask the person editing the active file; tell teammates who asked you "not yet".
        internal Action AskForFile, DismissRequests;
    }

    /// <summary>
    /// The JOCO panel. The Robot tab has three layers: where am I (robot and whether it's up to date), what do I need to do
    /// (only the actions that apply right now, plus a card for the open file that appears only when there's something to
    /// say), and what do I want to work on (search and the robot's files). Support links sit in a small footer. The Library
    /// tab has every way of getting a part; everything else lives in Tools → CAD Hub.
    /// </summary>
    internal sealed class StatusPane : UserControl
    {
        internal static readonly Color Hairline = Color.FromArgb(226, 229, 233), CardBack = Color.FromArgb(246, 247, 249);

        private readonly Label update = Caption(9f, FontStyle.Bold, "", Color.RoyalBlue);
        private readonly Button install = Action("Install update", null);
        private readonly Label flash = Caption(9.5f, FontStyle.Bold, "", Color.ForestGreen);
        private readonly Label warning = Caption(9.5f, FontStyle.Bold, "", Color.Firebrick);
        private readonly Label working = Caption(9f, FontStyle.Italic, "", SystemColors.GrayText);
        private readonly Label robot = Caption(12f, FontStyle.Bold);
        private readonly Label sync = Caption(9.5f, FontStyle.Bold);
        private readonly Label details = Caption(8.5f, FontStyle.Regular, "", SystemColors.GrayText);
        private readonly Button open = Action("Open Robot", "Open Robot");
        private readonly Button closeUpdate = Action("Close & Update", "Update");
        // The open file's card: only there when there's something to say about it.
        private readonly Label activeFile = Caption(10f, FontStyle.Bold);
        private readonly Label activeStatus = Caption(9f, FontStyle.Regular);
        private readonly Button edit = Action("Edit", "Edit");
        private readonly LinkLabel history = Link("History of this file");
        private readonly LinkLabel ask = Link("Ask for it");
        private readonly Label requests = Caption(9f, FontStyle.Bold, "", Color.DarkOrange);
        private readonly LinkLabel notNow = Link("Not yet: let them know");
        private readonly Label pending = Caption(9.5f, FontStyle.Bold, "", Color.DarkOrange);
        private readonly Button submit = Action("Submit", "Submit");
        private readonly Label locks = Caption(8.5f, FontStyle.Regular, "", SystemColors.GrayText);
        private readonly LinkLabel release = Link("Give back the ones I didn't change");
        private readonly Card card = new Card();
        private readonly TableLayoutPanel header = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        private readonly FlowLayoutPanel layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 12, 14, 12) };
        private readonly RobotFilesPanel files;
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
            history.LinkClicked += (s, e) => actions.History();
            ask.LinkClicked += (s, e) => actions.AskForFile?.Invoke();
            notNow.LinkClicked += (s, e) => actions.DismissRequests?.Invoke();

            // 1. Where am I: robot name on the left, whether it's up to date on the right.
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            robot.Margin = new Padding(0, 0, 8, 0);
            sync.Margin = new Padding(0, 3, 0, 0);
            header.Controls.Add(robot, 0, 0);
            header.Controls.Add(sync, 1, 0);

            // 2. What do I need to do: only what applies now, the open file's part in a card.
            history.Margin = new Padding(0, 2, 0, 0);
            foreach (var control in new Control[] { activeFile, activeStatus, edit, ask, history, requests, notNow, pending, submit, locks, release })
                card.Body.Controls.Add(control);
            card.Margin = new Padding(0, 10, 0, 0);
            foreach (var control in new Control[] { header, details, update, install, warning, flash, working, open, closeUpdate, card })
                layout.Controls.Add(control);

            // 3. What do I want to work on: the robot's files fill the rest. Then a small footer for support actions.
            files = new RobotFilesPanel(actions.OpenFile, actions.RevealFile, actions.FileHistoryOf, actions.WhereUsedOf) { Dock = DockStyle.Fill, MinimumSize = new Size(0, 160) };
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
            Show(new PaneState());
        }

        // Check now · Diagnostics on the left, the version on the right: there when needed, out of the way otherwise.
        private static Control Footer(PaneActions actions)
        {
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(14, 5, 14, 6), Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var check = Link("Check now", 8.25f);
            check.LinkClicked += (s, e) => actions.Refresh();
            var diagnostics = Link("Diagnostics", 8.25f);
            diagnostics.LinkClicked += (s, e) => actions.Diagnostics();
            var tips = new ToolTip();
            tips.SetToolTip(check, "Check the server for new changes and locks now");
            tips.SetToolTip(diagnostics, "Copy diagnostics for a mentor");
            links.Controls.Add(check);
            links.Controls.Add(Caption(8.25f, FontStyle.Regular, "·", SystemColors.GrayText));
            links.Controls.Add(diagnostics);
            footer.Controls.Add(links, 0, 0);
            var version = Caption(8f, FontStyle.Regular, Updater.Current + (Updater.IsDevelopmentBuild ? " (dev)" : ""), SystemColors.GrayText);
            version.Anchor = AnchorStyles.Right;
            tips.SetToolTip(version, "CAD Hub " + Updater.Current);
            footer.Controls.Add(version, 1, 0);
            return footer;
        }

        // Stretch buttons and wrap text to the pane's width.
        private void FitWidth()
        {
            int width = Math.Max(150, layout.ClientSize.Width - layout.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            header.MinimumSize = header.MaximumSize = new Size(width, 0);
            robot.MaximumSize = new Size(Math.Max(80, width / 2), 0);
            sync.MaximumSize = new Size(Math.Max(80, robot.Visible ? width - robot.PreferredSize.Width - 10 : width), 0);
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
            bool named = state.Robot.Length > 0;
            robot.Visible = named;
            // No robot name yet (not signed in, checking): the status takes the whole line.
            header.SetColumn(sync, named ? 1 : 0);
            header.SetColumnSpan(sync, named ? 1 : 2);
            sync.Anchor = AnchorStyles.Top | (named ? AnchorStyles.Right : AnchorStyles.Left);
            sync.Text = state.Sync;
            sync.ForeColor = ColorOf(state.SyncTone);
            details.Text = state.Details;
            details.Visible = state.Details.Length > 0;
            open.Visible = state.ShowOpen;
            closeUpdate.Visible = state.ShowCloseAndUpdate;
            warning.Text = state.Warning ?? "";
            warning.Visible = !String.IsNullOrEmpty(state.Warning);

            activeFile.Text = state.ActiveFile;
            activeFile.Visible = state.ActiveFile.Length > 0;
            activeStatus.Text = state.ActiveStatus;
            activeStatus.ForeColor = ColorOf(state.ActiveTone);
            activeStatus.Visible = state.ActiveStatus.Length > 0;
            edit.Visible = state.EditTarget != null;
            edit.Text = "  " + (String.IsNullOrEmpty(state.EditTarget) ? "Edit" : "Edit " + state.EditTarget);
            history.Visible = state.ShowHistory;
            ask.Text = state.AskOwner == null ? "Ask for it" : "Ask " + state.AskOwner + " for it";
            ask.Visible = state.AskOwner != null;
            requests.Text = state.Requests;
            requests.Visible = notNow.Visible = state.Requests.Length > 0;
            pending.Text = state.Pending;
            pending.Visible = state.Pending.Length > 0;
            bool showSubmit = state.SubmitCount > 0 || state.InterruptedSubmit;
            submit.Visible = showSubmit;
            submit.Text = "  Submit" + (state.SubmitCount > 0 ? " " + state.SubmitCount : "");
            locks.Text = state.Locks;
            locks.Visible = state.Locks.Length > 0;
            release.Visible = state.HasLocks;
            // The card exists only while it has something in it; sections inside it are spaced only after another section.
            bool hasFile = state.ActiveFile.Length > 0 || state.ActiveStatus.Length > 0 || state.EditTarget != null || state.ShowHistory;
            requests.Margin = new Padding(0, hasFile ? 12 : 0, 0, 2);
            bool hasSubmit = state.Pending.Length > 0 || showSubmit;
            bool hasLocks = state.Locks.Length > 0 || state.HasLocks;
            pending.Margin = new Padding(0, hasFile ? 12 : 0, 0, 2);
            if (!pending.Visible) submit.Margin = new Padding(0, hasFile ? 12 : 0, 0, 2);
            else submit.Margin = new Padding(0, 8, 0, 2);
            locks.Margin = new Padding(0, hasFile || hasSubmit ? 12 : 0, 0, 2);
            card.Visible = hasFile || hasSubmit || hasLocks || state.Requests.Length > 0;
            layout.ResumeLayout();
            FitWidth();
        }

        /// <summary>A light box that groups what's about the open file and what's waiting to submit.</summary>
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
