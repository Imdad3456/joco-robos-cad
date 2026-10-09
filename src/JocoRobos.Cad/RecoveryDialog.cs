using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    /// <summary>
    /// Recovered work: one row per file whose recovery copy holds work the robot file doesn't. Preview opens the copy read-only
    /// under a different name outside the robot; Restore puts it back only after the add-in's safety checks and an explicit
    /// confirmation; Save a copy keeps it somewhere else. Nothing here changes a file by itself: the add-in does each action.
    /// </summary>
    internal sealed class RecoveryDialog : Form
    {
        private readonly FlowLayoutPanel rows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 4) };
        private readonly Func<RecoveryCopy, string> status;
        private readonly Action<RecoveryCopy> preview, saveCopy;
        private readonly Func<RecoveryCopy, bool> restore;

        /// <param name="status">What the row says about the robot file right now, e.g. "You're still editing it".</param>
        /// <param name="restore">Runs every check and the confirmation; true once the file was restored.</param>
        internal RecoveryDialog(string explanation, IList<RecoveryCopy> copies, Func<RecoveryCopy, string> status,
            Action<RecoveryCopy> preview, Func<RecoveryCopy, bool> restore, Action<RecoveryCopy> saveCopy, Action openFolder)
        {
            this.status = status;
            this.preview = preview;
            this.restore = restore;
            this.saveCopy = saveCopy;
            Text = "CAD Hub — Recovered work";
            float scale = DeviceDpi / 96f; // these sizes are in pixels; the text scales with the display by itself
            ClientSize = new Size((int)(600 * scale), (int)(520 * scale));
            MinimumSize = new Size((int)(480 * scale), (int)(360 * scale));
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f);
            BackColor = SystemColors.Window;

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16, 14, 16, 12) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = "Recovered work", AutoSize = true, Font = new Font(Font.FontFamily, 12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
            var intro = new Label { Text = explanation + "\nNothing is replaced unless you confirm it.", AutoSize = true,
                ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6) };
            grid.Controls.Add(intro, 0, 1);
            grid.Controls.Add(rows, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var folder = new LinkLabel { Text = "Open the recovery folder", AutoSize = true, Anchor = AnchorStyles.Left, LinkBehavior = LinkBehavior.HoverUnderline };
            folder.LinkClicked += (s, e) => openFolder();
            var done = new Button { Text = "Done", DialogResult = DialogResult.OK, Size = new Size(90, 30), Anchor = AnchorStyles.Right };
            footer.Controls.Add(folder, 0, 0);
            footer.Controls.Add(done, 1, 0);
            grid.Controls.Add(footer, 0, 3);
            Controls.Add(grid);
            AcceptButton = done;
            CancelButton = done;

            foreach (var copy in copies) rows.Controls.Add(Row(copy));
            // Every row as wide as the list, so the buttons line up on the right.
            EventHandler fit = (s, e) =>
            {
                int width = Math.Max(200, rows.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                intro.MaximumSize = new Size(Math.Max(200, grid.ClientSize.Width - grid.Padding.Horizontal), 0);
                foreach (TableLayoutPanel row in rows.Controls)
                {
                    row.MinimumSize = row.MaximumSize = new Size(width, 0);
                    foreach (Control part in row.Controls) if (part is Label && row.GetColumnSpan(part) > 1) part.MaximumSize = new Size(width, 0);
                }
            };
            rows.Resize += fit;
            Load += fit;
        }

        // Name ............ [Preview] [Restore] [Save a copy]
        // Copy 2:45 PM · robot file saved 1:10 PM
        // You're still editing it  /  Not locked by you: restore isn't possible
        private Control Row(RecoveryCopy copy)
        {
            var row = new TableLayoutPanel { ColumnCount = 4, RowCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 2), Padding = new Padding(0, 8, 0, 8) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Paint += (s, e) => { using (var pen = new Pen(StatusPane.Hairline)) e.Graphics.DrawLine(pen, 0, 0, row.Width, 0); };
            var name = new Label { Text = Path.GetFileNameWithoutExtension(copy.Original), AutoSize = true, Anchor = AnchorStyles.Left,
                Font = new Font(Font.FontFamily, 10f, FontStyle.Bold), Margin = new Padding(0, 4, 8, 0) };
            new ToolTip().SetToolTip(name, copy.Original);
            row.Controls.Add(name, 0, 0);
            bool assembly = copy.Original.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
            var previewButton = Small("Preview");
            previewButton.Click += (s, e) => preview(copy);
            new ToolTip().SetToolTip(previewButton, "Opens the copy read-only as \"" + RecoveryRestore.RecoveredName(copy.Original, copy.TakenAt) + "\", outside the robot. " +
                "Your robot file isn't touched." + (assembly ? "\nAn assembly opens with the robot's current parts, which may be newer than when the copy was made." : ""));
            var restoreButton = Small("Restore");
            var saveButton = Small("Save a copy");
            saveButton.Click += (s, e) => saveCopy(copy);
            var state = new Label { AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, 1, 0, 0) };
            Action refresh = () =>
            {
                string now = status(copy);
                bool fine = now.StartsWith("✓");
                state.Text = now;
                state.ForeColor = fine ? Color.ForestGreen : Color.DarkOrange;
            };
            restoreButton.Click += (s, e) =>
            {
                if (restore(copy))
                {
                    restoreButton.Enabled = false;
                    restoreButton.Text = "Restored";
                }
                refresh();
            };
            row.Controls.Add(previewButton, 1, 0);
            row.Controls.Add(restoreButton, 2, 0);
            row.Controls.Add(saveButton, 3, 0);
            string saved = File.Exists(copy.Original) ? "robot file saved " + When(File.GetLastWriteTime(copy.Original)) : "robot file not on this computer";
            var times = new Label { Text = "Copy from " + When(copy.TakenAt) + " · " + saved, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 0) };
            row.Controls.Add(times, 0, 1);
            row.SetColumnSpan(times, 4);
            row.Controls.Add(state, 0, 2);
            row.SetColumnSpan(state, 4);
            if (assembly)
            {
                var note = new Label { Text = "ⓘ Assembly preview uses the robot's current parts, which may be newer than when the copy was made.", AutoSize = true,
                    MaximumSize = new Size(500, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 1, 0, 0) };
                row.RowCount = 4;
                row.Controls.Add(note, 0, 3);
                row.SetColumnSpan(note, 4);
            }
            refresh();
            return row;
        }

        private static string When(DateTime time)
        {
            return time.Date == DateTime.Now.Date ? time.ToString("h:mm tt") : time.ToString("ddd MMM d, h:mm tt");
        }

        private static Button Small(string text)
        {
            return new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 26, Margin = new Padding(4, 0, 0, 0),
                Padding = new Padding(4, 0, 4, 0), Anchor = AnchorStyles.Right };
        }
    }
}
