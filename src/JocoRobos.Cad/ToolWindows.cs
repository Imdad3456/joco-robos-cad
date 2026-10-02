using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    /// <summary>"Where used": the assemblies above a file (nearest first) and the files it uses. Double-click opens one.</summary>
    internal sealed class WhereUsedDialog : Form
    {
        internal WhereUsedDialog(string file, List<Tuple<string, int>> usedBy, List<string> uses, Action<string> open)
        {
            Text = "CAD Hub — Where Used: " + Path.GetFileName(file);
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(560, 520);
            MinimumSize = new Size(420, 380);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 12) };
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var above = List(usedBy.Select(x => Tuple.Create(new string(' ', (x.Item2 - 1) * 4) + (x.Item2 > 1 ? "↳ " : "") + Path.GetFileName(x.Item1), x.Item1)),
                "Nothing in the robot uses it (or the robot isn't downloaded).", open);
            var below = List(uses.Select(x => Tuple.Create(Path.GetFileName(x), x)), file.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                ? "It doesn't use any other files." : "A part: it doesn't use other files.", open);
            grid.Controls.Add(Heading("Used by (" + usedBy.Count(x => x.Item2 == 1) + " directly, " + usedBy.Count + " in all): changing it changes these"), 0, 0);
            grid.Controls.Add(above, 0, 1);
            grid.Controls.Add(Heading("Uses (" + uses.Count + ")"), 0, 2);
            grid.Controls.Add(below, 0, 3);
            grid.Controls.Add(new Label { Text = "Double-click to open one (read-only until you Edit it).", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0) }, 0, 4);
            Controls.Add(grid);
        }

        private static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(0, 6, 0, 4) };
        }

        private static ListBox List(IEnumerable<Tuple<string, string>> items, string empty, Action<string> open)
        {
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            var paths = new List<string>();
            foreach (var item in items) { list.Items.Add(item.Item1); paths.Add(item.Item2); }
            if (paths.Count == 0) { list.Items.Add(empty); list.Enabled = false; }
            list.DoubleClick += (s, e) => { if (list.SelectedIndex >= 0 && list.SelectedIndex < paths.Count) open(paths[list.SelectedIndex]); };
            return list;
        }
    }

    /// <summary>"Check This Computer": problems first, then what's fine.</summary>
    internal sealed class HealthDialog : Form
    {
        internal HealthDialog(List<HealthFinding> findings)
        {
            int problems = findings.Count(f => f.Problem);
            Text = "CAD Hub — Check This Computer";
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(560, 440);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            var heading = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(14, 12, 14, 0), Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold),
                ForeColor = problems == 0 ? Color.ForestGreen : Color.DarkOrange,
                Text = problems == 0 ? "✓ Healthy" : "⚠ " + problems + (problems == 1 ? " thing needs" : " things need") + " attention" };
            var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window, Text = String.Join("\r\n\r\n", findings.Select(f => (f.Problem ? "⚠ " : "✓ ") + f.Text.Replace("\n", "\r\n"))) };
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 8), BackColor = SystemColors.Window };
            body.Controls.Add(text);
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 90 };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(14, 8, 14, 8) };
            bottom.Controls.Add(close);
            Controls.Add(body);
            Controls.Add(bottom);
            Controls.Add(heading);
            AcceptButton = close;
            CancelButton = close;
            BackColor = SystemColors.Window;
            Shown += (s, e) => { text.SelectionLength = 0; close.Focus(); };
        }
    }

    /// <summary>The Parts List inside SOLIDWORKS: double-click a line to select every copy of it in the assembly.</summary>
    internal sealed class PartsWindow : Form
    {
        internal PartsWindow(string assembly, List<PartsRow> rows, List<List<PartsRow>> duplicates, Action<PartsRow> select, string spreadsheet, bool sent)
        {
            Text = "CAD Hub — Parts List: " + assembly;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(820, 600);
            MinimumSize = new Size(560, 400);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            int buy = rows.Where(r => r.Buy).Sum(r => r.Quantity), make = rows.Where(r => !r.Buy).Sum(r => r.Quantity);
            var summary = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(14, 12, 14, 0),
                Text = buy + " to buy from " + rows.Where(r => r.Buy).Select(r => r.Vendor).Distinct().Count() + " vendors, " + make + " team-made." +
                    (sent ? " Also on the mentor page's Parts tab." : "") + " Double-click a line to select it in the assembly." };
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, ShowGroups = true, BorderStyle = BorderStyle.None };
            list.Columns.Add("Part number", 120);
            list.Columns.Add("Name", 300);
            list.Columns.Add("Qty", 50, HorizontalAlignment.Right);
            list.Columns.Add("Folder", 300);
            var groups = new Dictionary<string, ListViewGroup>();
            Func<string, ListViewGroup> group = name =>
            {
                ListViewGroup g;
                if (!groups.TryGetValue(name, out g)) { g = new ListViewGroup(name); groups[name] = g; list.Groups.Add(g); }
                return g;
            };
            var flagged = new HashSet<PartsRow>(duplicates.SelectMany(d => d));
            if (duplicates.Count > 0) group("⚠ Possible duplicates: the same item in two files (" + duplicates.Count + ")");
            foreach (var row in rows)
            {
                var item = new ListViewItem(new[] { row.PartNumber, row.Name + (row.Configuration.Length > 0 ? " (" + row.Configuration + ")" : ""), row.Quantity.ToString(), row.Folder })
                    { Tag = row, Group = group(row.Buy ? "Buy: " + row.Vendor : "Make (team-made)") };
                if (flagged.Contains(row)) item.ForeColor = Color.DarkOrange;
                list.Items.Add(item);
            }
            foreach (var set in duplicates)
                foreach (var row in set)
                    list.Items.Add(new ListViewItem(new[] { row.PartNumber, row.Name, row.Quantity.ToString(), row.Folder })
                        { Tag = row, Group = groups.First().Value, ForeColor = Color.DarkOrange });
            list.DoubleClick += (s, e) =>
            {
                try { if (list.SelectedItems.Count > 0) select((PartsRow)list.SelectedItems[0].Tag); }
                catch (Exception exception) { ErrorLog.Write("parts list select", exception); }
            };
            var open = new Button { Text = "Open spreadsheet", AutoSize = true, Dock = DockStyle.Right };
            open.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(spreadsheet); }
                catch (Exception) { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + spreadsheet + "\""); }
            };
            var close = new Button { Text = "Close", Width = 90, Dock = DockStyle.Right };
            close.Click += (s, e) => Close();
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(14, 8, 14, 8) };
            bottom.Controls.Add(open);
            bottom.Controls.Add(new Panel { Width = 8, Dock = DockStyle.Right });
            bottom.Controls.Add(close);
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 0) };
            body.Controls.Add(list);
            Controls.Add(body);
            Controls.Add(bottom);
            Controls.Add(summary);
            CancelButton = close;
        }
    }
}
