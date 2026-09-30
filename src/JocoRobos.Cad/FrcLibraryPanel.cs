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
    /// Library tab: search FRCDesignLib and insert parts with one click. All network work runs in the
    /// background and is marshalled back to this control; SOLIDWORKS calls happen only in the insert callback.
    /// </summary>
    internal sealed class FrcLibraryPanel : UserControl
    {
        private readonly Func<NetworkCredential> login;
        private readonly Action<FrcItem, Dictionary<string, string>> insert;
        private readonly TextBox search = new TextBox { Width = 220 };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(230, 0), ForeColor = SystemColors.GrayText };
        private readonly ListView results = new ListView { View = View.Tile, Width = 226, Height = 250, MultiSelect = false, HideSelection = false,
            TileSize = new Size(205, 46), FullRowSelect = true };
        private readonly ImageList pictures = new ImageList { ImageSize = new Size(64, 40), ColorDepth = ColorDepth.Depth32Bit };
        private readonly PictureBox picture = new PictureBox { Width = 220, Height = 150, SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Window };
        private readonly Label title = new Label { AutoSize = true, MaximumSize = new Size(230, 0), Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
        private readonly Label subtitle = new Label { AutoSize = true, MaximumSize = new Size(230, 0), ForeColor = SystemColors.GrayText };
        private readonly FlowLayoutPanel choices = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = 226 };
        private readonly Button insertButton = new Button { Text = "Insert", Width = 220, Height = 32, Enabled = false, FlatStyle = FlatStyle.System };
        private readonly Timer debounce = new Timer { Interval = 300 };
        private readonly Dictionary<string, Control> inputs = new Dictionary<string, Control>();
        // Tracked separately: Control.Visible reads false whenever the tab itself isn't on screen.
        private readonly HashSet<string> hidden = new HashSet<string>();
        private FrcItem selected;
        private int generation;

        internal FrcLibraryPanel(Func<NetworkCredential> login, Action<FrcItem, Dictionary<string, string>> insert, Action jocoLibrary)
        {
            this.login = login;
            this.insert = insert;
            BackColor = SystemColors.Window;
            AutoScroll = true;
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
            var team = new Button { Text = "Team Library…", Width = 220, Height = 28, FlatStyle = FlatStyle.System };
            team.Click += (s, e) => jocoLibrary();
            layout.Controls.Add(team);
            layout.Controls.Add(new Label { Text = "FRCDesignLib", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) });
            layout.Controls.Add(search);
            layout.Controls.Add(status);
            results.LargeImageList = pictures;
            layout.Controls.Add(results);
            layout.Controls.Add(picture);
            layout.Controls.Add(title);
            layout.Controls.Add(subtitle);
            layout.Controls.Add(choices);
            layout.Controls.Add(insertButton);
            layout.Controls.Add(new Label { Text = "The first time anyone on the team uses a part (and configuration), it's prepared for the team Library. That takes a little longer.",
                AutoSize = true, MaximumSize = new Size(225, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0) });
            Controls.Add(layout);
            pictures.Images.Add("placeholder", Placeholder());
            search.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            debounce.Tick += (s, e) => { debounce.Stop(); RunSearch(); };
            results.SelectedIndexChanged += (s, e) => ShowSelected();
            insertButton.Click += (s, e) => { if (selected != null) insert(selected, CurrentChoices(true)); };
            status.Text = "Search motors, bearings, gears, gearboxes…";
        }

        private static Bitmap Placeholder()
        {
            var bitmap = new Bitmap(64, 40);
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
            if (query.Length < 2) { results.Items.Clear(); status.Text = "Type at least 2 letters."; return; }
            status.Text = "Searching…";
            Background(client => client.Search(query), found =>
            {
                if (mine != generation) return; // A newer search is on its way.
                results.BeginUpdate();
                results.Items.Clear();
                foreach (var item in found)
                    results.Items.Add(new ListViewItem(new[] { item.Name, item.Vendor + " · " + item.Group }) { Tag = item, ImageKey = pictures.Images.ContainsKey(item.Id) ? item.Id : "placeholder" });
                results.EndUpdate();
                status.Text = found.Count == 0 ? "Nothing found." : found.Count + " found" + (found.Count >= 40 ? " (showing 40; be more specific)" : "");
                LoadSmallPictures(found.Where(i => !pictures.Images.ContainsKey(i.Id)).ToList(), mine);
            }, error => { if (mine == generation) status.Text = error?.Message ?? "Search failed."; });
        }

        // One after another, so 40 results don't open 40 connections; stops if the student searches again.
        private void LoadSmallPictures(List<FrcItem> items, int mine)
        {
            if (items.Count == 0 || mine != generation) return;
            var item = items[0];
            Background(client => client.Thumbnail(item.Id, false), data =>
            {
                try
                {
                    using (var stream = new MemoryStream(data))
                    using (var image = Image.FromStream(stream))
                        pictures.Images.Add(item.Id, new Bitmap(image, 64, 40));
                    foreach (ListViewItem row in results.Items)
                        if (((FrcItem)row.Tag).Id == item.Id) row.ImageKey = item.Id;
                }
                catch (ArgumentException) { }
                LoadSmallPictures(items.Skip(1).ToList(), mine);
            }, error => LoadSmallPictures(items.Skip(1).ToList(), mine));
        }

        private void ShowSelected()
        {
            if (results.SelectedItems.Count == 0) return;
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
                if (results.SelectedItems.Count == 0 || ((FrcItem)results.SelectedItems[0].Tag).Id != details.Id) return;
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
                    var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 215 };
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
                else
                {
                    input = new Label { Text = choice.Default + " (fixed for now)", AutoSize = true, ForeColor = SystemColors.GrayText };
                }
                var holder = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0), Tag = choice };
                if (choice.Kind != "boolean") holder.Controls.Add(new Label { Text = choice.Name, AutoSize = true });
                holder.Controls.Add(input);
                choices.Controls.Add(holder);
                inputs[choice.Id] = input;
            }
            UpdateVisibility(item);
        }

        // FRCDesignLib hides options that don't apply to the current choices (for example a case only for one cap type).
        private void UpdateVisibility(FrcItem item)
        {
            var current = CurrentChoices(false);
            foreach (Control holder in choices.Controls)
            {
                var choice = (FrcChoice)holder.Tag;
                bool show = choice.VisibleWhen == null || choice.VisibleWhen.Holds(current);
                holder.Visible = show;
                if (show) hidden.Remove(choice.Id); else hidden.Add(choice.Id);
            }
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
                if (combo != null && combo.SelectedItem != null) result[choice.Id] = ((FrcOption)combo.SelectedItem).Id;
                else if (check != null) result[choice.Id] = check.Checked ? "true" : "false";
            }
            return result;
        }
    }
}
