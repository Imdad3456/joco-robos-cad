using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    /// <summary>
    /// Library tab: every way of getting a part. One search covers the team Library and FRCDesignLib; downloaded files are
    /// imported from the bottom of the tab. Insert with one click. All network work runs in the
    /// background and is marshalled back to this control; SOLIDWORKS calls happen only in the insert callback.
    /// </summary>
    internal sealed class FrcLibraryPanel : UserControl
    {
        private readonly Func<NetworkCredential> login;
        private readonly Action<FrcItem, Dictionary<string, string>> insert;
        private readonly PaneActions actions;
        private string selectedTeam;
        // Everything stretches with the task pane: results take the top half, details the bottom half.
        private const int Thumb = 72;
        private readonly TextBox search = new TextBox { Dock = DockStyle.Fill };
        private readonly Label status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Dock = DockStyle.Fill };
        private readonly ListView results = new ListView { View = View.Tile, Dock = DockStyle.Fill, MultiSelect = false, HideSelection = false, FullRowSelect = true };
        private readonly ImageList pictures = new ImageList { ImageSize = new Size(Thumb, Thumb), ColorDepth = ColorDepth.Depth32Bit };
        private readonly FlowLayoutPanel details = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        private readonly PictureBox picture = new PictureBox { Height = 200, SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Window };
        private readonly Label title = new Label { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11f, FontStyle.Bold) };
        private readonly Label subtitle = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly FlowLayoutPanel choices = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        private readonly Button insertButton = new Button { Text = "Insert", Dock = DockStyle.Fill, Height = 36, Enabled = false, FlatStyle = FlatStyle.System,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10f, FontStyle.Bold) };
        private readonly Timer debounce = new Timer { Interval = 300 };
        private readonly NumericUpDown copies = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 1, Width = 52 };
        private string budgetNote = "";
        private readonly Dictionary<string, Control> inputs = new Dictionary<string, Control>();
        // Tracked separately: Control.Visible reads false whenever the tab itself isn't on screen.
        private readonly HashSet<string> hidden = new HashSet<string>();
        private FrcItem selected;
        private int generation;

        internal FrcLibraryPanel(PaneActions actions)
        {
            this.actions = actions;
            login = actions.Login;
            insert = actions.InsertFrc;
            BackColor = SystemColors.Window;
            var heading = new Label { Text = "Find a part", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10f, FontStyle.Bold), Margin = new Padding(0, 4, 0, 2) };
            var hint = new Label { Text = "Team Library parts come first, then FRCDesignLib (motors, bearings, gears, tube…). The first time anyone uses an FRCDesignLib part and size, it's prepared for the team, which takes a little longer.",
                AutoSize = true, ForeColor = SystemColors.GrayText, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
            var browse = new LinkLabel { Text = "Browse the team Library folder…", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            browse.LinkClicked += (s, e) => actions.BrowseTeam();
            var import = new LinkLabel { Text = "Import a downloaded CAD file (McMaster, vendor site…)…", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            import.LinkClicked += (s, e) => actions.ImportDownloaded();
            // Insert and how many copies, side by side.
            var insertRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
            insertRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            insertRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            insertRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            insertRow.Controls.Add(insertButton, 0, 0);
            insertRow.Controls.Add(new Label { Text = "×", AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(8, 0, 2, 0) }, 1, 0);
            copies.Anchor = AnchorStyles.None;
            insertRow.Controls.Add(copies, 2, 0);
            new ToolTip().SetToolTip(copies, "How many to insert");
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var row in new[] { (Control)heading, search, status }) { grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.Controls.Add(row); }
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.Controls.Add(results);
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.Controls.Add(details);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(insertRow);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(hint);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(browse);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(import);

            details.Controls.Add(picture);
            details.Controls.Add(title);
            details.Controls.Add(subtitle);
            details.Controls.Add(choices);
            results.LargeImageList = pictures;
            Controls.Add(grid);
            Resize += (s, e) => FitWidths();
            details.Resize += (s, e) => FitWidths();
            pictures.Images.Add("placeholder", Placeholder());
            search.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            debounce.Tick += (s, e) => { debounce.Stop(); RunSearch(); };
            results.SelectedIndexChanged += (s, e) => ShowSelected();
            insertButton.Click += (s, e) =>
            {
                actions.SetCopies?.Invoke((int)copies.Value);
                if (selectedTeam != null) { actions.InsertTeam(selectedTeam); return; }
                if (selected == null) return;
                // A mistyped length is caught here, next to the box, instead of after the server round trip.
                foreach (Control holder in choices.Controls)
                {
                    var choice = (FrcChoice)holder.Tag;
                    var box = inputs.ContainsKey(choice.Id) ? inputs[choice.Id] as TextBox : null;
                    string value, problem = box == null || hidden.Contains(choice.Id) ? null : choice.CheckNumber(box.Text, out value);
                    if (problem == null) continue;
                    MessageBox.Show(this, problem, "CAD Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    box.Focus();
                    box.SelectAll();
                    return;
                }
                insert(selected, CurrentChoices(true));
            };
            status.Text = "Search the team Library and FRCDesignLib…";
            // Before anything is typed: the team's most-used parts, ready to insert.
            VisibleChanged += (s, e) => { if (Visible && search.Text.Trim().Length == 0 && results.Items.Count == 0) ShowPopular(); };
        }

        private void ShowPopular()
        {
            int mine = ++generation;
            Background(client => client.SearchWithBudget(""), found =>
            {
                if (mine != generation || search.Text.Trim().Length > 0) return;
                NoteBudget(found.Budget);
                results.BeginUpdate();
                results.Items.Clear();
                foreach (var item in found.Results) results.Items.Add(Row(item));
                results.EndUpdate();
                status.Text = (found.Results.Count == 0 ? "Search the team Library and FRCDesignLib…" : "⚡ The team's most-used parts (instant). Or search…") + budgetNote;
                LoadSmallPictures(found.Results.Where(i => !pictures.Images.ContainsKey(i.Id)).ToList(), mine);
            });
        }

        private ListViewItem Row(FrcItem item)
        {
            return new ListViewItem(new[] { (item.InLibrary ? "⚡ " : "") + item.Name, item.Vendor + " · " + item.Group + (item.InLibrary ? " · in the team Library" : "") })
                { Tag = item, ImageKey = pictures.Images.ContainsKey(item.Id) ? item.Id : "placeholder" };
        }

        // Onshape allows 2,500 API calls a year on free and education plans: say so when the team gets near the end.
        private void NoteBudget(FrcBudget budget)
        {
            budgetNote = budget == null || budget.Limit <= 0 || budget.Used < budget.Limit * 0.8 ? ""
                : "\n⚠ The team has used " + budget.Used + " of " + budget.Limit + " Onshape calls this year. Prefer ⚡ parts (already in the team Library).";
        }

        internal void FocusSearch()
        {
            search.Focus();
            search.SelectAll();
        }

        // FlowLayoutPanels don't stretch their children, so size them to the pane's width by hand.
        private void FitWidths()
        {
            int width = Math.Max(120, details.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
            picture.Width = width;
            picture.Height = Math.Min(260, Math.Max(120, width * 2 / 3));
            title.MaximumSize = subtitle.MaximumSize = new Size(width, 0);
            choices.Width = width;
            foreach (Control holder in choices.Controls)
                foreach (Control input in holder.Controls)
                {
                    if (input is ComboBox || input is TextBox) input.Width = width - 4;
                    else if (input is Label || input is CheckBox) input.MaximumSize = new Size(width - 4, 0);
                }
            results.TileSize = new Size(Math.Max(120, results.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4), Thumb + 8);
        }

        private static Bitmap Placeholder()
        {
            var bitmap = new Bitmap(Thumb, Thumb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Gainsboro);
            return bitmap;
        }

        private void Background<T>(Func<FrcClient, T> work, Action<T> done, Action<Exception> failed = null)
        {
            var credential = login();
            if (credential == null) { status.Text = "Sign in first: click Open Robot."; return; }
            var client = new FrcClient(credential);
            Task.Run(() => work(client)).ContinueWith(task =>
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (task.Status == TaskStatus.RanToCompletion) done(task.Result);
                        else if (failed != null) failed(task.Exception?.GetBaseException());
                    }));
                }
                catch (InvalidOperationException) { } // Pane closed.
            });
        }

        private void RunSearch()
        {
            string query = search.Text.Trim();
            int mine = ++generation;
            if (query.Length == 0) { results.Items.Clear(); ShowPopular(); return; }
            if (query.Length < 2) { results.Items.Clear(); status.Text = "Type at least 2 letters."; return; }
            status.Text = "Searching…";
            // The team Library is local files: quick, and shown even when FRCDesignLib can't be reached.
            List<string> team;
            try { team = actions.SearchTeam(query); }
            catch (Exception) { team = new List<string>(); }
            results.BeginUpdate();
            results.Items.Clear();
            foreach (string path in team)
                results.Items.Add(new ListViewItem(new[] { Path.GetFileNameWithoutExtension(path), "Team Library · " + Path.GetFileName(Path.GetDirectoryName(path)) })
                    { Tag = path, ImageKey = "placeholder" });
            results.EndUpdate();
            Background(client => client.SearchWithBudget(query), answer =>
            {
                if (mine != generation) return; // A newer search is on its way.
                NoteBudget(answer.Budget);
                var found = answer.Results;
                results.BeginUpdate();
                foreach (var row in results.Items.Cast<ListViewItem>().Where(r => r.Tag is FrcItem).ToList()) results.Items.Remove(row);
                foreach (var item in found) results.Items.Add(Row(item));
                results.EndUpdate();
                int total = found.Count + team.Count;
                status.Text = total == 0 ? "Nothing found." : (team.Count > 0 ? team.Count + " in the team Library, " : "") + found.Count + " in FRCDesignLib" +
                    (found.Count >= 40 ? " (showing 40; be more specific)" : "") + (found.Any(i => i.InLibrary) ? ". ⚡ = already in the team Library" : "") + budgetNote;
                LoadSmallPictures(found.Where(i => !pictures.Images.ContainsKey(i.Id)).ToList(), mine);
            }, error => { if (mine == generation) status.Text = (team.Count > 0 ? team.Count + " in the team Library. " : "") + "FRCDesignLib: " + (error?.Message ?? "search failed."); });
        }

        // One after another, so 40 results don't open 40 connections; stops if the student searches again.
        private void LoadSmallPictures(List<FrcItem> items, int mine)
        {
            if (items.Count == 0 || mine != generation) return;
            var item = items[0];
            Background(client => client.Thumbnail(item.Id, true), data =>
            {
                try
                {
                    using (var stream = new MemoryStream(data))
                    using (var image = Image.FromStream(stream))
                        pictures.Images.Add(item.Id, new Bitmap(image, Thumb, Thumb));
                    foreach (ListViewItem row in results.Items)
                        if (row.Tag is FrcItem && ((FrcItem)row.Tag).Id == item.Id) row.ImageKey = item.Id;
                }
                catch (ArgumentException) { }
                LoadSmallPictures(items.Skip(1).ToList(), mine);
            }, error => LoadSmallPictures(items.Skip(1).ToList(), mine));
        }

        private void ShowSelected()
        {
            if (results.SelectedItems.Count == 0) return;
            selectedTeam = results.SelectedItems[0].Tag as string;
            if (selectedTeam != null)
            {
                // A team Library part: nothing to configure.
                selected = null;
                title.Text = Path.GetFileNameWithoutExtension(selectedTeam);
                subtitle.Text = "Team Library · " + Path.GetFileName(Path.GetDirectoryName(selectedTeam));
                choices.Controls.Clear();
                inputs.Clear();
                hidden.Clear();
                picture.Image = null;
                insertButton.Enabled = true;
                return;
            }
            var item = (FrcItem)results.SelectedItems[0].Tag;
            selected = null;
            insertButton.Enabled = false;
            title.Text = item.Name;
            subtitle.Text = item.Vendor + " · " + item.Group;
            choices.Controls.Clear();
            inputs.Clear();
            hidden.Clear();
            picture.Image = null;
            Background(client => client.Thumbnail(item.Id, true), data =>
            {
                try { using (var stream = new MemoryStream(data)) picture.Image = new Bitmap(Image.FromStream(stream)); }
                catch (ArgumentException) { }
            });
            Background(client => client.Details(item.Id), details =>
            {
                var current = results.SelectedItems.Count == 0 ? null : results.SelectedItems[0].Tag as FrcItem;
                if (current == null || current.Id != details.Id) return;
                selected = details;
                subtitle.Text = details.Vendor + (String.IsNullOrEmpty(details.PartNumber) ? "" : " · " + details.PartNumber) + " · " + details.Group;
                BuildChoices(details);
                insertButton.Enabled = true;
            }, error => subtitle.Text = error?.Message ?? "Could not load this part.");
        }

        private void BuildChoices(FrcItem item)
        {
            foreach (var choice in item.Choices ?? new List<FrcChoice>())
            {
                Control input;
                if (choice.Kind == "enum")
                {
                    var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
                    foreach (var option in choice.Options ?? new List<FrcOption>()) box.Items.Add(option);
                    box.SelectedItem = box.Items.Cast<FrcOption>().FirstOrDefault(o => o.Id == choice.Default) ?? (box.Items.Count > 0 ? box.Items[0] : null);
                    box.SelectedIndexChanged += (s, e) => UpdateVisibility(item);
                    input = box;
                }
                else if (choice.Kind == "boolean")
                {
                    var box = new CheckBox { Text = choice.Name, AutoSize = true, Checked = choice.Default == "true" };
                    box.CheckedChanged += (s, e) => UpdateVisibility(item);
                    input = box;
                }
                else if (choice.Kind == "number")
                {
                    // Custom lengths and counts, like FRCDesignApp's number boxes.
                    input = new TextBox { Text = choice.Default, Width = 200 };
                }
                else
                {
                    input = new Label { Text = choice.Default + " (fixed for now)", AutoSize = true, ForeColor = SystemColors.GrayText };
                }
                var holder = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0), Tag = choice };
                string range = choice.Kind != "number" ? "" : "  (" + (String.IsNullOrEmpty(choice.Unit) ? "" : choice.Unit + ", ") +
                    (String.IsNullOrEmpty(choice.Min) || String.IsNullOrEmpty(choice.Max) ? "number" : choice.Min + " to " + choice.Max) + ")";
                if (choice.Kind != "boolean") holder.Controls.Add(new Label { Text = choice.Name + range, AutoSize = true });
                holder.Controls.Add(input);
                choices.Controls.Add(holder);
                inputs[choice.Id] = input;
            }
            UpdateVisibility(item);
            FitWidths();
        }

        // FRCDesignLib hides settings and individual options that don't apply to the current choices
        // (for example a case only for one cap type, or bore sizes that only exist for some bearing sizes).
        private bool updating;
        private void UpdateVisibility(FrcItem item)
        {
            if (updating) return;
            updating = true;
            try
            {
                var all = item.Choices ?? new List<FrcChoice>();
                for (int pass = 0; pass < 3; pass++) // A changed option can change what else is visible.
                {
                    var current = CurrentChoices(false);
                    foreach (Control holder in choices.Controls)
                    {
                        var choice = (FrcChoice)holder.Tag;
                        var combo = inputs.ContainsKey(choice.Id) ? inputs[choice.Id] as ComboBox : null;
                        if (combo != null)
                        {
                            var visible = choice.VisibleOptions(current, all);
                            var selected = combo.SelectedItem as FrcOption;
                            if (!visible.Select(o => o.Id).SequenceEqual(combo.Items.Cast<FrcOption>().Select(o => o.Id)))
                            {
                                combo.Items.Clear();
                                foreach (var option in visible) combo.Items.Add(option);
                                // Keep the choice if still offered, else the default, else the first (FRCDesignApp's rule).
                                combo.SelectedItem = visible.FirstOrDefault(o => selected != null && o.Id == selected.Id)
                                    ?? visible.FirstOrDefault(o => o.Id == choice.Default) ?? visible.FirstOrDefault();
                            }
                        }
                        bool show = (choice.VisibleWhen == null || choice.VisibleWhen.Holds(current, all)) && (combo == null || combo.Items.Count > 0);
                        holder.Visible = show;
                        if (show) hidden.Remove(choice.Id); else hidden.Add(choice.Id);
                    }
                }
            }
            finally { updating = false; }
        }

        private Dictionary<string, string> CurrentChoices(bool visibleOnly)
        {
            var result = new Dictionary<string, string>();
            foreach (Control holder in choices.Controls)
            {
                var choice = (FrcChoice)holder.Tag;
                if (visibleOnly && hidden.Contains(choice.Id)) continue;
                Control input;
                if (!inputs.TryGetValue(choice.Id, out input)) continue;
                var combo = input as ComboBox;
                var check = input as CheckBox;
                var box = input as TextBox;
                string number;
                if (combo != null && combo.SelectedItem != null) result[choice.Id] = ((FrcOption)combo.SelectedItem).Id;
                else if (check != null) result[choice.Id] = check.Checked ? "true" : "false";
                else if (box != null) result[choice.Id] = choice.CheckNumber(box.Text, out number) == null ? number : box.Text.Trim();
            }
            return result;
        }
    }
}
