using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
        private readonly Action<string> open, reveal, history;
        private readonly Label empty = new Label { Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(2, 6, 2, 0),
            Text = "Open Robot to download the robot files." };
        private readonly TextBox search = new TextBox { Dock = DockStyle.Fill };
        private readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, BorderStyle = BorderStyle.FixedSingle,
            FullRowSelect = true, ShowLines = false };
        private readonly Timer debounce = new Timer { Interval = 220 };
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private RobotFileIndex index;
        private WorkspaceSnapshot snapshot;
        private string root;
        private int generation;
        // Which folders are open, kept across searches and refreshes.
        private readonly HashSet<string> expandedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool filling;
        private const string Placeholder = "\u0001";

        internal RobotFilesPanel(Action<string> open, Action<string> reveal, Action<string> history)
        {
            this.open = open;
            this.reveal = reveal;
            this.history = history;
            BackColor = SystemColors.Window;
            var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            header.Controls.Add(new Label { Text = "ROBOT FILES", AutoSize = true, Margin = new Padding(0, 4, 8, 4),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f, FontStyle.Bold), ForeColor = SystemColors.GrayText });
            var refresh = new LinkLabel { Text = "Refresh", AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            refresh.LinkClicked += (s, e) => Reindex();
            header.Controls.Add(refresh);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 4, 12, 8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(header, 0, 0);
            grid.Controls.Add(search, 0, 1);
            // The tree, or "Open Robot first" in its place.
            var body = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
            body.Controls.Add(tree);
            body.Controls.Add(empty);
            grid.Controls.Add(body, 0, 2);
            Controls.Add(grid);
            SetCue(search, "Search robot files…");

            search.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
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
            menu.Items.Add("Show in Explorer", null, (s, e) => WithSelected(reveal));
            menu.Items.Add("File History", null, (s, e) => WithSelected(history));
            menu.Opening += (s, e) => e.Cancel = tree.SelectedNode == null || tree.SelectedNode.Name != "file";
            tree.ContextMenuStrip = menu;
            ShowEmpty(true);
        }

        private void WithSelected(Action<string> action)
        {
            if (tree.SelectedNode != null && tree.SelectedNode.Name == "file") action(index.FullPath((string)tree.SelectedNode.Tag));
        }

        /// <summary>Called whenever the panel's status is drawn. Rebuilds only for a new robot or a new status check.</summary>
        internal void Show(WorkspaceSnapshot robot)
        {
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
                string query = search.Text.Trim();
                if (query.Length > 0)
                {
                    var found = index.Search(query);
                    foreach (string relative in found)
                    {
                        var node = FileNode(relative);
                        node.Text += "   " + (Path.GetDirectoryName(relative) is string dir && dir.Length > 0 ? dir : "(robot folder)");
                        tree.Nodes.Add(node);
                    }
                    if (found.Count == 0) tree.Nodes.Add(new TreeNode("No robot files match \"" + query + "\"") { ForeColor = SystemColors.GrayText });
                }
                else
                {
                    AddChildren(tree.Nodes, "");
                    foreach (string folder in expanded.Where(f => !String.IsNullOrEmpty(f)).OrderBy(f => f.Length))
                    {
                        var node = Find(tree.Nodes, folder, "folder");
                        if (node != null) node.Expand();
                    }
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

        private void AddChildren(TreeNodeCollection nodes, string relative)
        {
            foreach (string name in index.SubfoldersOf(relative))
            {
                string path = relative.Length == 0 ? name : relative + Path.DirectorySeparatorChar + name;
                var node = new TreeNode(name) { Name = "folder", Tag = path };
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

        // Who has it, from the last status check (no extra server request): ✎ yours, 🔒 someone else's, ● new.
        private TreeNode FileNode(string relative)
        {
            var node = new TreeNode(Path.GetFileName(relative)) { Name = "file", Tag = relative };
            if (snapshot == null) return node;
            string full = index.FullPath(relative), owner;
            DateTime since;
            if (snapshot.Mine.Contains(full))
            {
                node.ForeColor = Color.ForestGreen;
                node.Text = "✎ " + node.Text;
                node.ToolTipText = "You're editing this" + (snapshot.Changed.Contains(full) ? " (changes not submitted yet)" : "");
            }
            else if (snapshot.Locks.TryGetValue(full, out owner))
            {
                node.ForeColor = Color.Firebrick;
                node.Text = "🔒 " + node.Text;
                node.ToolTipText = owner + " is editing this" + (snapshot.LockedSince.TryGetValue(full, out since)
                    ? " since " + (since.Date == DateTime.Now.Date ? since.ToString("h:mm tt") : since.ToString("ddd MMM d, h:mm tt")) : "");
            }
            else if (snapshot.New.Contains(full))
            {
                node.ForeColor = Color.RoyalBlue;
                node.Text = "● " + node.Text;
                node.ToolTipText = "New file: it goes to the team with your next Submit";
            }
            else if (snapshot.Changed.Contains(full))
                node.ToolTipText = "Changed here, not submitted yet";
            return node;
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

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        // Grey hint text in an empty search box.
        private static void SetCue(TextBox box, string text)
        {
            box.HandleCreated += (s, e) => SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, text);
        }
    }
}
