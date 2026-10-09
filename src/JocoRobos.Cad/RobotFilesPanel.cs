using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    /// <summary>
    /// The Robot tab's file browser: find robot CAD and open it. Nothing else: no rename, move, delete, or SVN from here.
    /// The file list is built in the background; folders fill in only when expanded; statuses come from the last status check.
    /// </summary>
    internal sealed class RobotFilesPanel : UserControl
    {
        private readonly Action<string> open, reveal, history, whereUsed, askFor;
        private readonly Label empty = new Label { Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(2, 8, 2, 0),
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f), Text = "Open Robot to download the robot files." };
        private readonly SearchBox search = new SearchBox("Search robot files…") { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        private readonly TreeView tree = new FileTree { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, BorderStyle = BorderStyle.None,
            FullRowSelect = true, ShowLines = false, DrawMode = TreeViewDrawMode.OwnerDrawText, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f) };
        private readonly Timer debounce = new Timer { Interval = 220 };
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private RobotFileIndex index;
        private WorkspaceSnapshot snapshot;
        private string user;
        private string root;
        // The open document, selected in the tree once the list is ready (and again whenever another one becomes active).
        private string follow, followed;
        private readonly ToolStripMenuItem askItem = new ToolStripMenuItem("Ask for it");
        private int generation;
        // Which folders are open, kept across searches and refreshes.
        private readonly HashSet<string> expandedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool filling;
        private const string Placeholder = "\u0001";

        internal RobotFilesPanel(Action<string> open, Action<string> reveal, Action<string> history, Action<string> whereUsed, Action<string> askFor)
        {
            this.askFor = askFor;
            this.whereUsed = whereUsed;
            this.open = open;
            this.reveal = reveal;
            this.history = history;
            BackColor = SystemColors.Window;

            // ROBOT FILES (the panel's ↻ checks the server, and every new check lists the files again)
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new Label { Text = "ROBOT FILES", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 0, 2),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.5f, FontStyle.Bold), ForeColor = SystemColors.GrayText }, 0, 0);


            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 10, 14, 4) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(header, 0, 0);
            grid.Controls.Add(search, 0, 1);
            // The tree, or "Open Robot first" in its place.
            var body = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            body.Controls.Add(tree);
            body.Controls.Add(empty);
            grid.Controls.Add(body, 0, 2);
            Controls.Add(grid);

            // Roomy rows, a little more indent, and an icon for folders, assemblies, parts and drawings.
            tree.ItemHeight = Math.Max(22, tree.Font.Height + 9);
            tree.Indent = Math.Max(19, tree.Font.Height + 4);
            tree.ImageList = FileIcons.Build();
            tree.HandleCreated += (s, e) => { try { SetWindowTheme(tree.Handle, "explorer", null); } catch (Exception) { } }; // Modern arrows and hover.
            // Rows are drawn to the pane's width (DrawNode): a narrower or wider pane redraws them all.
            tree.Resize += (s, e) => tree.Invalidate();
            tree.DrawNode += DrawNode;

            search.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            search.Escape += () => search.Text = "";
            search.Down += () => { if (tree.Nodes.Count > 0) { tree.Focus(); if (tree.SelectedNode == null) tree.SelectedNode = tree.Nodes[0]; } };
            debounce.Tick += (s, e) => { debounce.Stop(); Fill(); };
            tree.BeforeExpand += (s, e) => LoadFolder(e.Node);
            tree.AfterExpand += (s, e) => { if (!filling && e.Node.Name == "folder") expandedFolders.Add((string)e.Node.Tag); };
            tree.AfterCollapse += (s, e) => { if (!filling && e.Node.Name == "folder") expandedFolders.Remove((string)e.Node.Tag); };
            tree.NodeMouseDoubleClick += (s, e) => { if (e.Node.Tag is string && e.Node.Name == "file") open(index.FullPath((string)e.Node.Tag)); };
            tree.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && tree.SelectedNode != null && tree.SelectedNode.Name == "file") { open(index.FullPath((string)tree.SelectedNode.Tag)); e.Handled = true; }
            };
            tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };
            menu.Items.Add("Open", null, (s, e) => WithSelected(open));
            askItem.Click += (s, e) => WithSelected(p => askFor?.Invoke(p));
            menu.Items.Add(askItem);
            menu.Items.Add("File History", null, (s, e) => WithSelected(history));
            menu.Items.Add("Where Used…", null, (s, e) => WithSelected(whereUsed));
            menu.Items.Add("Show in Explorer", null, (s, e) => WithSelected(reveal));
            menu.Opening += (s, e) =>
            {
                e.Cancel = tree.SelectedNode == null || tree.SelectedNode.Name != "file";
                if (e.Cancel) return;
                // Someone else is editing it: ask them right from here, without opening it first.
                var status = RobotFileStatus.Of(snapshot, index.FullPath((string)tree.SelectedNode.Tag), user, DateTime.Now);
                askItem.Visible = status.Mark == FileMark.Theirs && askFor != null;
                askItem.Text = "Ask " + status.Owner + " for it";
            };
            tree.ContextMenuStrip = menu;
            ShowEmpty(true);
        }

        private void WithSelected(Action<string> action)
        {
            if (tree.SelectedNode != null && tree.SelectedNode.Name == "file") action(index.FullPath((string)tree.SelectedNode.Tag));
        }

        /// <summary>Called whenever the panel's status is drawn. Rebuilds only for a new robot or a new status check.</summary>
        internal void Show(WorkspaceSnapshot robot, string user)
        {
            this.user = user;
            string newRoot = robot == null || robot.Local == 0 || !Directory.Exists(robot.Info.Root) ? null : robot.Info.Root;
            bool changed = !ReferenceEquals(robot, snapshot) || !String.Equals(newRoot, root, StringComparison.OrdinalIgnoreCase);
            snapshot = robot;
            if (!String.Equals(newRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                // Another robot (Choose Robot): never keep showing the previous one's files.
                root = newRoot;
                index = null;
                tree.Nodes.Clear();
            }
            if (root == null) { ShowEmpty(true); return; }
            if (changed) Reindex();
        }

        /// <summary>Selects the open document in the tree (expanding its folders) when it changes; never while searching.</summary>
        internal void Follow(string activePath)
        {
            follow = activePath;
            if (index == null || activePath == null || String.Equals(activePath, followed, StringComparison.OrdinalIgnoreCase)) return;
            if (root == null || !activePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            if (search.Text.Trim().Length > 0) return;
            followed = activePath;
            var node = Find(tree.Nodes, activePath.Substring(root.Length + 1), "file");
            if (node != null) { tree.SelectedNode = node; node.EnsureVisible(); }
        }

        private void ShowEmpty(bool on)
        {
            empty.Visible = on;
            tree.Visible = search.Enabled = !on;
        }

        // The file listing runs off SOLIDWORKS' thread; only the result is handed back.
        private void Reindex()
        {
            if (root == null) return;
            string folder = root;
            int mine = ++generation;
            Task.Run(() => RobotFileIndex.Build(folder)).ContinueWith(task =>
            {
                if (IsDisposed || task.Status != TaskStatus.RanToCompletion) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (mine != generation || !String.Equals(folder, root, StringComparison.OrdinalIgnoreCase)) return;
                        index = task.Result;
                        ShowEmpty(false);
                        Fill();
                        followed = null;
                        Follow(follow);
                    }));
                }
                catch (InvalidOperationException) { } // Pane closed.
            });
        }

        // Search results as a flat list, or the folder tree (keeping which folders were open and what was selected).
        private void Fill()
        {
            if (index == null) return;
            var expanded = expandedFolders.ToList();
            string selected = tree.SelectedNode?.Tag as string;
            filling = true;
            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear();
                details.Clear();
                string query = search.Text.Trim();
                if (query.Length > 0)
                {
                    var found = index.Search(query);
                    tree.Nodes.Add(Note(found.Count == 0 ? "No robot files match \"" + query + "\"" : "Search results for \"" + query + "\""));
                    foreach (string relative in found)
                    {
                        // The folder shows after the name, greyed (see DrawNode).
                        string folder = Path.GetDirectoryName(relative);
                        tree.Nodes.Add(FileNode(relative, String.IsNullOrEmpty(folder) ? "robot folder" : folder));
                    }
                }
                else
                {
                    AddChildren(tree.Nodes, "");
                    foreach (string folder in expanded.Where(f => !String.IsNullOrEmpty(f)).OrderBy(f => f.Length))
                    {
                        var node = Find(tree.Nodes, folder, "folder");
                        if (node != null) node.Expand();
                    }
                    if (tree.Nodes.Count == 0) tree.Nodes.Add(Note("No SOLIDWORKS files in the robot yet."));
                }
                var keep = selected == null ? null : Find(tree.Nodes, selected, "file") ?? Find(tree.Nodes, selected, "folder");
                if (keep != null) tree.SelectedNode = keep;
            }
            finally
            {
                tree.EndUpdate();
                filling = false;
            }
        }

        // What shows in grey after each name (who's editing, folder summaries, a search result's folder). Kept out of the
        // node's text so the tree never scrolls sideways and the name can shorten to keep it visible.
        private readonly Dictionary<TreeNode, string> details = new Dictionary<TreeNode, string>();

        private static TreeNode Note(string text)
        {
            return new TreeNode(text) { Name = "note", ForeColor = SystemColors.GrayText, ImageKey = FileIcons.None, SelectedImageKey = FileIcons.None };
        }

        private void AddChildren(TreeNodeCollection nodes, string relative)
        {
            foreach (string name in index.SubfoldersOf(relative))
            {
                string path = relative.Length == 0 ? name : relative + Path.DirectorySeparatorChar + name;
                var node = new TreeNode(name) { Name = "folder", Tag = path, ImageKey = FileIcons.Folder, SelectedImageKey = FileIcons.Folder };
                // Who's working in here, without opening it: ✎ mine, 🔒 teammates', ● new, ⬇ newer versions waiting.
                var summary = RobotFileStatus.Summarize(snapshot, index.FullPath(path), user);
                if (summary.Text.Length > 0)
                {
                    details[node] = summary.Text;
                    node.ToolTipText = Describe(summary);
                }
                node.Nodes.Add(new TreeNode(Placeholder)); // Filled in when expanded.
                nodes.Add(node);
            }
            foreach (string name in index.FilesIn(relative))
                nodes.Add(FileNode(relative.Length == 0 ? name : relative + Path.DirectorySeparatorChar + name));
        }

        private void LoadFolder(TreeNode node)
        {
            if (index == null || node.Name != "folder" || node.Nodes.Count != 1 || node.Nodes[0].Text != Placeholder) return;
            node.Nodes.Clear();
            AddChildren(node.Nodes, (string)node.Tag);
        }

        // Who has it, from the last status check (no extra server request), shown after the name: ✎ you, 🔒 sarah, ● new, ⬇ newer.
        private TreeNode FileNode(string relative, string folder = null)
        {
            string icon = FileIcons.For(relative);
            // The icon says part, assembly or drawing, so the name goes without its extension.
            var node = new TreeNode(Path.GetFileNameWithoutExtension(relative)) { Name = "file", Tag = relative, ImageKey = icon, SelectedImageKey = icon };
            var status = RobotFileStatus.Of(snapshot, index.FullPath(relative), user, DateTime.Now);
            string glyph;
            switch (status.Mark)
            {
                case FileMark.Mine: glyph = "✎ "; node.ForeColor = Color.ForestGreen; break;
                case FileMark.MineElsewhere: glyph = "🔒 "; node.ForeColor = Color.DarkOrange; break;
                case FileMark.Theirs: glyph = "🔒 "; node.ForeColor = Color.Firebrick; break;
                case FileMark.New: glyph = "● "; node.ForeColor = Color.RoyalBlue; break;
                case FileMark.Incoming: glyph = "⬇ "; node.ForeColor = Color.RoyalBlue; break;
                case FileMark.Changed: glyph = "⚠ "; node.ForeColor = Color.DarkOrange; break;
                default: glyph = ""; break;
            }
            node.Text = glyph + node.Text;
            var detail = new[] { status.Detail, folder }.Where(x => !String.IsNullOrEmpty(x)).ToList();
            if (detail.Count > 0) details[node] = String.Join(" · ", detail);
            node.ToolTipText = Path.GetFileName(relative) + (status.Tip.Length > 0 ? "\n" + status.Tip : "");
            return node;
        }

        private static string Describe(FolderSummary summary)
        {
            var lines = new List<string>();
            if (summary.Mine > 0) lines.Add("✎ " + summary.Mine + " you're editing");
            if (summary.Theirs > 0) lines.Add("🔒 " + summary.Theirs + " teammates are editing");
            if (summary.New > 0) lines.Add("● " + summary.New + " new, not submitted");
            if (summary.Incoming > 0) lines.Add("⬇ " + summary.Incoming + " with newer versions on the server");
            return String.Join("\n", lines);
        }

        // Text only (the theme draws the row, icon and selection): the name, then its detail in grey. When space runs out the
        // name shortens first, so who's editing a file stays readable.
        private void DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null || e.Bounds.IsEmpty) return;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            var color = e.Node.ForeColor.IsEmpty ? tree.ForeColor : e.Node.ForeColor;
            var bounds = new Rectangle(e.Bounds.X + 2, e.Bounds.Y, Math.Max(0, tree.ClientSize.Width - e.Bounds.X - 2), e.Bounds.Height);
            // The detail is drawn past the node's own text, where the tree doesn't clear: clear it here (the theme paints selected and hovered rows itself).
            if ((e.State & (TreeNodeStates.Selected | TreeNodeStates.Hot)) == 0)
                using (var back = new SolidBrush(tree.BackColor)) e.Graphics.FillRectangle(back, bounds);
            string detail;
            if (!details.TryGetValue(e.Node, out detail))
            {
                TextRenderer.DrawText(e.Graphics, e.Node.Text, tree.Font, bounds, color, flags | TextFormatFlags.EndEllipsis);
                return;
            }
            using (var small = new Font(tree.Font.FontFamily, tree.Font.Size - 0.5f))
            {
                int detailWidth = TextRenderer.MeasureText(e.Graphics, detail, small, bounds.Size, flags).Width;
                int nameWidth = TextRenderer.MeasureText(e.Graphics, e.Node.Text, tree.Font, bounds.Size, flags).Width;
                int room = Math.Max(bounds.Width * 2 / 5, bounds.Width - detailWidth - 10);
                int shown = Math.Min(nameWidth, room);
                TextRenderer.DrawText(e.Graphics, e.Node.Text, tree.Font, new Rectangle(bounds.X, bounds.Y, shown, bounds.Height), color, flags | TextFormatFlags.EndEllipsis);
                if (shown + 10 < bounds.Width)
                    TextRenderer.DrawText(e.Graphics, detail, small, new Rectangle(bounds.X + shown + 10, bounds.Y, bounds.Width - shown - 10, bounds.Height),
                        SystemColors.GrayText, flags | TextFormatFlags.EndEllipsis);
            }
        }

        // Finds a node by relative path, expanding (and so loading) the folders on the way.
        private TreeNode Find(TreeNodeCollection nodes, string relative, string kind)
        {
            foreach (TreeNode node in nodes)
            {
                string path = node.Tag as string;
                if (path == null) continue;
                if (node.Name == kind && String.Equals(path, relative, StringComparison.OrdinalIgnoreCase)) return node;
                if (node.Name == "folder" && relative.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    LoadFolder(node);
                    node.Expand();
                    return Find(node.Nodes, relative, kind);
                }
            }
            return null;
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string application, string idList);

        /// <summary>The file tree, without sideways scrolling: long names shorten with "…" (DrawNode) instead of pushing the folders off the left.</summary>
        private sealed class FileTree : TreeView
        {
            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.Style |= 0x8000; // TVS_NOHSCROLL
                    return parameters;
                }
            }
        }

        /// <summary>A search field that looks like one: taller, a magnifier, a light border that turns blue while typing.</summary>
        private sealed class SearchBox : Panel
        {
            private readonly TextBox box = new TextBox { BorderStyle = BorderStyle.None, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.5f) };
            private readonly Label glass = new Label { Text = "\uE721", AutoSize = true, Font = new Font("Segoe MDL2 Assets", 9f), ForeColor = SystemColors.GrayText };
            internal event Action Escape, Down;
            internal new event EventHandler TextChanged { add { box.TextChanged += value; } remove { box.TextChanged -= value; } }

            internal SearchBox(string cue)
            {
                BackColor = SystemColors.Window;
                Height = Math.Max(28, box.PreferredHeight + 12);
                ResizeRedraw = true;
                DoubleBuffered = true;
                Cursor = Cursors.IBeam;
                Controls.Add(glass);
                Controls.Add(box);
                glass.Click += (s, e) => box.Focus();
                Click += (s, e) => box.Focus();
                box.GotFocus += (s, e) => Invalidate();
                box.LostFocus += (s, e) => Invalidate();
                box.HandleCreated += (s, e) => SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, cue);
                box.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Escape && box.TextLength > 0) { Escape?.Invoke(); e.SuppressKeyPress = true; }
                    else if (e.KeyCode == Keys.Down) { Down?.Invoke(); e.SuppressKeyPress = true; }
                };
            }

            public override string Text { get { return box.Text; } set { box.Text = value; } }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                Invalidate();
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                glass.Location = new Point(8, (Height - glass.Height) / 2);
                int left = glass.Right + 6;
                box.SetBounds(left, (Height - box.Height) / 2, Math.Max(10, Width - left - 8), box.Height);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var color = box.Focused ? SystemColors.Highlight : Color.FromArgb(205, 209, 214);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(color))
                using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 4))
                    e.Graphics.DrawPath(pen, path);
            }

            private static GraphicsPath Rounded(Rectangle r, int radius)
            {
                var path = new GraphicsPath();
                int d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);
        }
    }

    /// <summary>Small icons for the robot file tree: Windows' own for folders and SOLIDWORKS files, simple drawn ones if those can't be had.</summary>
    internal static class FileIcons
    {
        internal const string None = "none", Folder = "folder", Assembly = "sldasm", Part = "sldprt", Drawing = "slddrw";

        internal static string For(string path)
        {
            string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return extension == Assembly || extension == Drawing ? extension : Part;
        }

        internal static ImageList Build()
        {
            var size = SystemInformation.SmallIconSize;
            var list = new ImageList { ImageSize = size, ColorDepth = ColorDepth.Depth32Bit };
            list.Images.Add(None, new Bitmap(size.Width, size.Height)); // First, so notes get no icon.
            list.Images.Add(Folder, Shell("folder", 0x10) ?? Drawn(size, Color.FromArgb(232, 184, 64), ""));
            list.Images.Add(Assembly, Shell("x.sldasm", 0x80) ?? Drawn(size, Color.FromArgb(54, 120, 200), "A"));
            list.Images.Add(Part, Shell("x.sldprt", 0x80) ?? Drawn(size, Color.FromArgb(110, 120, 132), "P"));
            list.Images.Add(Drawing, Shell("x.slddrw", 0x80) ?? Drawn(size, Color.FromArgb(70, 150, 110), "D"));
            return list;
        }

        // The icon Windows shows for this kind of item (by name only: no file is touched).
        private static Bitmap Shell(string name, uint attributes)
        {
            try
            {
                var info = new FileInfoResult();
                if (SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf(info), 0x100 | 0x1 | 0x10 /* ICON | SMALLICON | USEFILEATTRIBUTES */) == IntPtr.Zero ||
                    info.Icon == IntPtr.Zero) return null;
                try { using (var icon = Icon.FromHandle(info.Icon)) return icon.ToBitmap(); }
                finally { DestroyIcon(info.Icon); }
            }
            catch (Exception) { return null; }
        }

        private static Bitmap Drawn(Size size, Color color, string letter)
        {
            var bitmap = new Bitmap(size.Width, size.Height);
            using (var g = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(color))
            using (var font = new Font(SystemFonts.MessageBoxFont.FontFamily, 6.5f, FontStyle.Bold))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillRectangle(brush, 1, 2, size.Width - 2, size.Height - 4);
                if (letter.Length > 0)
                    TextRenderer.DrawText(g, letter, font, new Rectangle(Point.Empty, size), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            return bitmap;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfoResult
        {
            public IntPtr Icon;
            public int Index;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref FileInfoResult info, uint size, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);
    }
}
