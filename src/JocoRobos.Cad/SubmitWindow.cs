using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    /// <summary>What the Submit window needs from the add-in. The window itself never touches SVN or SOLIDWORKS.</summary>
    internal sealed class SubmitHost
    {
        internal string Target;
        // Background: the SVN scan of the robot and Library.
        internal Func<Task<SubmitPlan>> Scan;
        // UI thread: the preflight for the current plan, checked files, and acknowledged warnings.
        internal Func<SubmitPlan, ISet<string>, ISet<string>, List<SubmitIssue>> Check;
        // UI thread: runs one fix button (save, lock, import, restore); returns a note to show or null, throws on failure.
        internal Func<SubmitIssue, IssueAction, string> Fix;
        // The real Submit (SvnWorkspace.Submit), which rechecks everything itself.
        internal Func<List<SubmitItem>, string, Task<SubmitOutcome>> Commit;
        internal Action<bool> Busy;
    }

    internal sealed class SubmitOutcome
    {
        internal int Files, Released;
        internal readonly List<string> Revisions = new List<string>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> Done = new List<string>();
        internal string Error;

        internal void Record(WorkspaceInfo workspace, IList<SubmitItem> items, SubmitResult result)
        {
            Files += items.Count(x => x.Kind != SubmitKind.ReleaseOnly);
            Released += items.Count(x => x.Kind == SubmitKind.ReleaseOnly);
            if (result.Revision > 0) Revisions.Add((workspace.IsLibrary ? "Library " : "") + "r" + result.Revision);
            Warnings.AddRange(result.Warnings);
            Done.AddRange(items.Select(x => x.Path));
        }

        internal string Summary
        {
            get
            {
                if (Files > 0)
                    return "✓ Submitted " + Files + (Files == 1 ? " file" : " files") + (Revisions.Count > 0 ? " as " + String.Join(" and ", Revisions) : "");
                return "✓ Released " + Released + (Released == 1 ? " lock" : " locks");
            }
        }
    }

    /// <summary>Kept while SOLIDWORKS runs, so closing the window to fix something never loses the comment.</summary>
    internal sealed class SubmitDraft
    {
        internal string Comment = "";
        internal readonly HashSet<string> Unchecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal void Clear() { Comment = ""; Unchecked.Clear(); }
    }

    /// <summary>
    /// One window for the whole Submit: every check runs first, problems show at the top with buttons that fix them,
    /// and the window rechecks by itself. Nothing is committed until the student clicks Submit.
    /// </summary>
    internal sealed class SubmitWindow : Form
    {
        private readonly SubmitHost host;
        private readonly SubmitDraft draft;
        private readonly HashSet<string> acknowledged = new HashSet<string>();
        private SubmitPlan plan;
        private List<SubmitIssue> issues = new List<SubmitIssue>();
        private string notice;
        private string note;
        private IssueLevel noteLevel;
        private bool working, populating;
        private string workingText;
        private DateTime? leftAt;

        private readonly Label status = new Label { AutoSize = true, Margin = new Padding(0, 4, 0, 8) };
        private readonly LinkLabel checkAgain = new LinkLabel { Text = "Check again", AutoSize = true, Margin = new Padding(12, 6, 0, 8) };
        private readonly FlowLayoutPanel issuePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        private readonly ListView files = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, ShowGroups = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill, MultiSelect = false };
        private readonly ListViewGroup changedGroup = new ListViewGroup("Changed");
        private readonly ListViewGroup newGroup = new ListViewGroup("New");
        private readonly ListViewGroup releaseGroup = new ListViewGroup("Release lock (unchanged)");
        private readonly TextBox comment = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly Label hint = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(0, 9, 12, 0) };
        private readonly Button submit = new Button { Text = "Submit", Size = new Size(110, 32) };
        private readonly Button cancel = new Button { Text = "Cancel", Size = new Size(95, 32), DialogResult = DialogResult.Cancel };
        private readonly TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16, 12, 16, 12) };

        /// <summary>Set when the Submit went through; null when the window was closed without submitting.</summary>
        internal SubmitOutcome Outcome { get; private set; }

        internal SubmitWindow(SubmitHost host, SubmitDraft draft)
        {
            this.host = host;
            this.draft = draft;
            Text = "JOCO ROBOS CAD — Submit";
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(700, 640);
            MinimumSize = new Size(560, 480);
            // Modeless windows don't center on a SOLIDWORKS owner by themselves.
            StartPosition = FormStartPosition.Manual;
            var screen = Screen.FromHandle(new SolidWorksWindow().Handle).WorkingArea;
            Location = new Point(screen.Left + Math.Max(0, (screen.Width - Width) / 2), screen.Top + Math.Max(0, (screen.Height - Height) / 2));
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = SystemColors.Window;

            var heading = new Label { Text = "Submit to " + host.Target, AutoSize = true, Margin = new Padding(0),
                Font = new Font(Font.FontFamily, 13f, FontStyle.Bold) };
            var statusRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            status.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
            statusRow.Controls.Add(status);
            statusRow.Controls.Add(checkAgain);
            checkAgain.LinkClicked += async (s, e) => { note = null; await Rescan(); };

            files.Columns.Add("File", 250);
            files.Columns.Add("Folder", 230);
            files.Columns.Add("", 160);
            files.Groups.AddRange(new[] { changedGroup, newGroup, releaseGroup });
            files.ItemChecked += (s, e) =>
            {
                if (populating) return;
                var item = (SubmitItem)e.Item.Tag;
                if (e.Item.Checked) draft.Unchecked.Remove(item.Path); else draft.Unchecked.Add(item.Path);
                Recheck();
            };
            files.Resize += (s, e) => FitColumns();

            comment.Text = draft.Comment;
            comment.TextChanged += (s, e) => { draft.Comment = comment.Text; if (hint.Text.Length > 0 && comment.Text.Trim().Length >= 3) hint.Text = ""; };

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(submit);
            buttons.Controls.Add(hint);
            submit.Click += async (s, e) => await OnSubmit();
            cancel.Click += (s, e) => Close();
            CancelButton = cancel;

            root.RowCount = 8;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // heading
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // status
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));  // issues (sized to fit)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // "Files"
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // file list
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // "What did you change?"
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); // comment
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // buttons
            root.Controls.Add(heading);
            root.Controls.Add(statusRow);
            root.Controls.Add(issuePanel);
            root.Controls.Add(new Label { Text = "Files (unchecked files stay on your computer, and stay locked)", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4) });
            root.Controls.Add(files);
            root.Controls.Add(new Label { Text = "What did you change?", AutoSize = true, Margin = new Padding(0, 10, 0, 4) });
            root.Controls.Add(comment);
            root.Controls.Add(buttons);
            Controls.Add(root);

            Resize += (s, e) => FitIssues();
            Shown += async (s, e) => { comment.Focus(); comment.SelectionStart = comment.TextLength; await Rescan(); };
            // Back from SOLIDWORKS (saved, renamed, closed something): check again by itself.
            Deactivate += (s, e) => leftAt = DateTime.UtcNow;
            Activated += async (s, e) =>
            {
                bool away = leftAt != null && DateTime.UtcNow - leftAt.Value > TimeSpan.FromSeconds(1.5);
                leftAt = null;
                if (away && !working && plan != null) await Rescan();
            };
            FormClosing += (s, e) =>
            {
                if (working && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
                if (Outcome == null) draft.Comment = comment.Text;
            };
            Render();
        }

        private List<SubmitItem> Selected
        {
            get { return files.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (SubmitItem)i.Tag).ToList(); }
        }

        private HashSet<string> SelectedPaths
        {
            get { return new HashSet<string>(Selected.Select(x => x.Path), StringComparer.OrdinalIgnoreCase); }
        }

        private void SetNote(string text, IssueLevel level)
        {
            note = text;
            noteLevel = level;
        }

        private async Task Rescan()
        {
            if (working) return;
            Begin("Checking your changes…");
            try
            {
                var fresh = await host.Scan();
                if (fresh.Notice != null) notice = fresh.Notice;
                plan = fresh;
                Populate();
            }
            catch (Exception exception) { SetNote("Couldn't check your changes: " + exception.Message, IssueLevel.Blocking); }
            finally { End(); }
            Recheck();
        }

        private void Recheck()
        {
            if (plan != null)
            {
                try { issues = host.Check(plan, SelectedPaths, acknowledged); }
                catch (Exception exception)
                {
                    issues = new List<SubmitIssue>();
                    SetNote("Couldn't check your files: " + exception.Message, IssueLevel.Blocking);
                }
            }
            Render();
        }

        private void Begin(string text)
        {
            working = true;
            workingText = text;
            host.Busy(true);
            Render();
            Update();
        }

        private void End()
        {
            working = false;
            host.Busy(false);
        }

        private void Populate()
        {
            populating = true;
            files.BeginUpdate();
            try
            {
                files.Items.Clear();
                foreach (var item in plan.Items.OrderBy(x => x.Kind).ThenBy(x => x.Workspace.IsLibrary).ThenBy(x => x.Relative, StringComparer.OrdinalIgnoreCase))
                {
                    string extra = item.NeedsLock ? "Not locked yet; Submit locks it" : item.Kind == SubmitKind.ReleaseOnly ? "Gives your lock back" : "";
                    var row = new ListViewItem(new[] { item.Name, item.Folder, extra })
                    {
                        Tag = item,
                        Checked = !draft.Unchecked.Contains(item.Path),
                        Group = item.Kind == SubmitKind.Modified ? changedGroup : item.Kind == SubmitKind.New ? newGroup : releaseGroup,
                        ToolTipText = item.Path,
                    };
                    if (extra.Length > 0) row.UseItemStyleForSubItems = false;
                    files.Items.Add(row);
                    if (extra.Length > 0) row.SubItems[2].ForeColor = SystemColors.GrayText;
                }
                files.ShowItemToolTips = true;
                foreach (var pair in new[] { Tuple.Create(changedGroup, "Changed"), Tuple.Create(newGroup, "New"), Tuple.Create(releaseGroup, "Release lock (unchanged)") })
                    pair.Item1.Header = pair.Item2 + " (" + pair.Item1.Items.Count + ")";
            }
            finally
            {
                files.EndUpdate();
                populating = false;
            }
            FitColumns();
        }

        private void Render()
        {
            var selected = Selected;
            int blocking = issues.Count(x => x.Blocking);
            if (working)
            {
                status.Text = workingText;
                status.ForeColor = SystemColors.GrayText;
            }
            else if (plan == null)
            {
                status.Text = note != null ? "Something went wrong" : "Checking your changes…";
                status.ForeColor = note != null ? Color.Firebrick : SystemColors.GrayText;
            }
            else if (blocking > 0)
            {
                status.Text = "⚠ Fix " + blocking + (blocking == 1 ? " issue" : " issues") + " before submitting";
                status.ForeColor = Color.DarkOrange;
            }
            else if (plan.Items.Count == 0)
            {
                status.Text = "Nothing to submit. Your files match the server.";
                status.ForeColor = SystemColors.GrayText;
            }
            else if (selected.Count == 0)
            {
                status.Text = "Check at least one file to submit.";
                status.ForeColor = SystemColors.GrayText;
            }
            else
            {
                status.Text = "✓ Ready to submit";
                status.ForeColor = Color.ForestGreen;
            }
            checkAgain.Visible = !working && plan != null;
            submit.Text = SubmitCheck.SubmitLabel(selected);
            submit.Enabled = !working && plan != null && blocking == 0 && selected.Count > 0;
            AcceptButton = null; // Enter makes new lines in the comment.
            cancel.Text = plan != null && plan.Items.Count == 0 && !working ? "Close" : "Cancel";
            cancel.Enabled = !working;
            files.Enabled = comment.Enabled = !working;
            RenderIssues();
        }

        private void RenderIssues()
        {
            issuePanel.SuspendLayout();
            try
            {
                foreach (Control old in issuePanel.Controls.Cast<Control>().ToList()) old.Dispose();
                issuePanel.Controls.Clear();
                if (note != null) issuePanel.Controls.Add(Card(new SubmitIssue { Level = noteLevel, Title = noteLevel == IssueLevel.Info ? "Done" : "Not finished", Description = note }));
                if (notice != null) issuePanel.Controls.Add(Card(new SubmitIssue { Level = IssueLevel.Info, Title = "Your earlier Submit reached the server", Description = notice }));
                foreach (var issue in issues) issuePanel.Controls.Add(Card(issue));
            }
            finally { issuePanel.ResumeLayout(); }
            FitIssues();
        }

        private Control Card(SubmitIssue issue)
        {
            Color back = issue.Level == IssueLevel.Blocking ? Color.FromArgb(255, 241, 224) : issue.Level == IssueLevel.Warning ? Color.FromArgb(255, 250, 225) : Color.FromArgb(232, 242, 255);
            string glyph = issue.Level == IssueLevel.Blocking ? "⚠ " : issue.Level == IssueLevel.Warning ? "• " : "✓ ";
            var card = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = back, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 0, 0, 6), Tag = issue };
            card.Controls.Add(new Label { Text = glyph + issue.Title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2),
                ForeColor = issue.Level == IssueLevel.Blocking ? Color.FromArgb(150, 60, 0) : SystemColors.ControlText });
            if (!String.IsNullOrEmpty(issue.Description))
                card.Controls.Add(new Label { Text = issue.Description, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
            if (issue.Actions.Count > 0 && issue.Title != null)
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
                foreach (var action in issue.Actions)
                {
                    var button = new Button { Text = ActionText(issue, action), AutoSize = true, MinimumSize = new Size(0, 28), Margin = new Padding(0, 0, 6, 0),
                        BackColor = SystemColors.Control, UseVisualStyleBackColor = true, Enabled = !working };
                    var chosen = action;
                    button.Click += async (s, e) => await OnAction(issue, chosen);
                    row.Controls.Add(button);
                }
                card.Controls.Add(row);
            }
            return card;
        }

        private static string ActionText(SubmitIssue issue, IssueAction action)
        {
            switch (action)
            {
                case IssueAction.SaveDocuments: return issue.Files.Count == 1 ? "Save it and continue" : "Save these and continue";
                case IssueAction.LockFile: return "Lock this file";
                case IssueAction.ImportIntoRobot: return "Import into robot";
                case IssueAction.IncludeFile: return "Include " + Path.GetFileNameWithoutExtension(issue.Files[0]);
                case IssueAction.RestoreFiles: return issue.Items.Count == 1 ? "Restore file" : "Restore files";
                case IssueAction.ShowFile: return issue.Key != null && issue.Key.StartsWith("duplicate:", StringComparison.Ordinal) ? "Show mine" : "Show file";
                case IssueAction.ShowOther: return issue.Key != null && issue.Key.StartsWith("duplicate:", StringComparison.Ordinal) ? "Show existing" : "Show outside file";
                case IssueAction.SubmitAnyway: return "Submit anyway";
                default: return action.ToString();
            }
        }

        // Cards and their text follow the window's width; the issue area grows to fit, up to about half the window.
        private void FitIssues()
        {
            int width = Math.Max(200, root.ClientSize.Width - root.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
            foreach (Control card in issuePanel.Controls)
            {
                card.MinimumSize = new Size(width, 0);
                card.MaximumSize = new Size(width, 0);
                foreach (Control child in card.Controls)
                    child.MaximumSize = new Size(width - card.Padding.Horizontal, 0);
            }
            int wanted = issuePanel.Controls.Cast<Control>().Sum(c => c.GetPreferredSize(new Size(width, 0)).Height + c.Margin.Vertical);
            int limit = Math.Max(120, root.ClientSize.Height / 2 - 40);
            root.RowStyles[2].Height = issuePanel.Controls.Count == 0 ? 0 : Math.Min(wanted + 4, limit);
        }

        private void FitColumns()
        {
            int width = files.ClientSize.Width - 4;
            if (width < 300) return;
            files.Columns[0].Width = (int)(width * 0.38);
            files.Columns[1].Width = (int)(width * 0.36);
            files.Columns[2].Width = width - files.Columns[0].Width - files.Columns[1].Width;
        }

        private async Task OnAction(SubmitIssue issue, IssueAction action)
        {
            if (working) return;
            note = null;
            switch (action)
            {
                case IssueAction.IncludeFile:
                    populating = true;
                    foreach (ListViewItem row in files.Items)
                        if (issue.Files.Contains(((SubmitItem)row.Tag).Path, StringComparer.OrdinalIgnoreCase))
                        {
                            row.Checked = true;
                            draft.Unchecked.Remove(((SubmitItem)row.Tag).Path);
                        }
                    populating = false;
                    Recheck();
                    return;
                case IssueAction.SubmitAnyway:
                    acknowledged.Add(issue.Key);
                    Recheck();
                    return;
                case IssueAction.ShowFile:
                    Reveal(issue.Files.FirstOrDefault());
                    return;
                case IssueAction.ShowOther:
                    Reveal(issue.Other);
                    return;
            }
            Begin(action == IssueAction.SaveDocuments ? "Saving…" : action == IssueAction.ImportIntoRobot ? "Importing into the robot…" :
                action == IssueAction.LockFile ? "Locking…" : "Working…");
            try
            {
                string result = host.Fix(issue, action);
                if (result != null) SetNote(result, IssueLevel.Info);
            }
            catch (Exception exception) { SetNote(exception.Message, IssueLevel.Blocking); }
            finally { End(); }
            await Rescan();
        }

        private static void Reveal(string path)
        {
            if (String.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"");
            }
            catch (Exception exception) { Trace.WriteLine("JOCO reveal: " + exception.Message); }
        }

        private async Task OnSubmit()
        {
            if (working) return;
            if (comment.Text.Trim().Length < 3)
            {
                hint.Text = "Describe what you changed.";
                comment.Focus();
                return;
            }
            hint.Text = "";
            note = null;
            var before = new HashSet<string>(Selected.Select(x => x.Kind + "|" + x.Path), StringComparer.OrdinalIgnoreCase);
            // The last preflight runs right before committing; SvnWorkspace.Submit then rechecks locks and status itself.
            await Rescan();
            if (plan == null || issues.Any(x => x.Blocking)) return;
            var chosen = Selected;
            if (!before.SetEquals(chosen.Select(x => x.Kind + "|" + x.Path)))
            {
                SetNote("Your files changed while this window was open. Check the list, then click Submit again.", IssueLevel.Warning);
                Render();
                return;
            }
            Begin("Submitting " + chosen.Count + (chosen.Count == 1 ? " file" : " files") + "…");
            SubmitOutcome outcome;
            try { outcome = await host.Commit(chosen, comment.Text.Trim()); }
            catch (Exception exception) { outcome = new SubmitOutcome { Error = exception.Message }; }
            finally { End(); }
            if (outcome.Error == null)
            {
                Outcome = outcome;
                draft.Clear();
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            SetNote(outcome.Error, IssueLevel.Blocking);
            await Rescan();
        }
    }
}
