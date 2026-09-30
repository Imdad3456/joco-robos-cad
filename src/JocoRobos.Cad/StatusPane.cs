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
        internal Action OpenRobot, Edit, Submit, CloseAndUpdate, InstallUpdate, Refresh, ReleaseUnchanged;
        internal Func<NetworkCredential> Login;
        internal Action<FrcItem, Dictionary<string, string>> InsertFrc;
        internal Func<string, List<string>> SearchTeam;
        internal Action<string> InsertTeam;
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
        private readonly FlowLayoutPanel layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 10) };
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
            foreach (var control in new Control[] { version, update, install, flash, working, robot, sync, details, open, closeUpdate, Spacer(),
                activeFile, activeStatus, edit, Spacer(), pending, submit, Spacer(), locks, release, check })
                layout.Controls.Add(control);
            var robotTab = new TabPage("Robot") { BackColor = SystemColors.Window };
            robotTab.Controls.Add(layout);
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
            submit.Visible = state.SubmitCount > 0;
            submit.Text = "  Submit " + state.SubmitCount;
            locks.Text = state.Locks;
            locks.Visible = state.Locks.Length > 0;
            release.Visible = state.HasLocks;
            layout.ResumeLayout();
        }
    }
}
