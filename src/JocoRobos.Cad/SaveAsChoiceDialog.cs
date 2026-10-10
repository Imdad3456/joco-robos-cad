using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    internal enum SaveAsChoice { Original, Experiment, NewTeamFile }

    /// <summary>
    /// File → Save As on a team file: save the team's file itself, an experimental copy outside the robot, or a new team file with its
    /// own name. Shown instead of SOLIDWORKS' Save As, which would move the window (and any open assembly) to the new file. Checks
    /// every choice before anything is written; the add-in does the saving.
    /// </summary>
    internal sealed class SaveAsChoiceDialog : Form
    {
        internal SaveAsChoice Choice { get; private set; }
        internal string Target { get; private set; }
        internal bool UseInAssembly { get { return useInAssembly.Checked; } }

        private readonly RadioButton original = Option("Save the team's file");
        private readonly RadioButton experiment = Option("Save an experimental copy");
        private readonly RadioButton newFile = Option("Create a new team part");
        private readonly TextBox experimentPath = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly TextBox newName = new TextBox { Dock = DockStyle.Fill };
        private readonly Label newFolderLabel = Note("");
        private readonly CheckBox useInAssembly = new CheckBox { AutoSize = true, Margin = new Padding(20, 4, 0, 0) };
        private readonly Label problem = new Label { AutoSize = true, ForeColor = Color.DarkOrange, Margin = new Padding(0, 8, 0, 0) };
        private string newFolder;

        internal SaveAsChoiceDialog(string path, string seasonRoot, string baseFolder, string originalProblem, string assemblyName,
            Func<string, string> checkExperiment, Func<string, string, string> checkNewFile)
        {
            string name = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
            string kind = extension.Equals(".sldasm", StringComparison.OrdinalIgnoreCase) ? "assembly" : extension.Equals(".slddrw", StringComparison.OrdinalIgnoreCase) ? "drawing" : "part";
            Text = "CAD Hub — Save As";
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = SystemColors.Window;
            newFile.Text = "Create a new team " + kind;
            newFolder = Path.GetDirectoryName(path);

            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(16, 14, 16, 12) };
            int width = (int)(460 * DeviceDpi / 96f);
            Func<string, Label> note = text => { var l = Note(text); l.MaximumSize = new Size(width - 20, 0); return l; };
            layout.Controls.Add(new Label { Text = "Save " + name, AutoSize = true, Font = new Font(Font.FontFamily, 12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) });

            layout.Controls.Add(original);
            layout.Controls.Add(note("Saves your changes into the robot's " + name + ". CAD Hub locks it for you first if it needs to."));
            if (originalProblem != null)
            {
                original.Enabled = false;
                var why = note("Not possible now: " + originalProblem);
                why.ForeColor = Color.DarkOrange;
                layout.Controls.Add(why);
            }

            layout.Controls.Add(experiment);
            layout.Controls.Add(note("A separate copy outside the robot, for trying ideas. The robot and its assemblies keep using the team's " + name + "."));
            var experimentRow = Row(width);
            experimentPath.Text = SaveRules.ExperimentPath(baseFolder, path, File.Exists);
            var browse = new Button { Text = "Change…", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            browse.Click += (s, e) =>
            {
                using (var picker = new SaveFileDialog { Title = "Experimental copy of " + name, FileName = Path.GetFileName(experimentPath.Text),
                    InitialDirectory = Path.GetDirectoryName(experimentPath.Text), Filter = "SOLIDWORKS " + kind + "|*" + extension, OverwritePrompt = false })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(experimentPath.Text));
                    if (picker.ShowDialog(this) == DialogResult.OK) { experimentPath.Text = picker.FileName; experiment.Checked = true; }
                }
            };
            experimentRow.Controls.Add(experimentPath, 0, 0);
            experimentRow.Controls.Add(browse, 1, 0);
            layout.Controls.Add(experimentRow);

            layout.Controls.Add(newFile);
            layout.Controls.Add(note("A new " + kind + " in the robot with its own name. It goes to the team with your next Submit; " + name + " stays as it is."));
            var nameRow = Row(width);
            newName.Text = name + " 2";
            newName.TextChanged += (s, e) => newFile.Checked = true;
            var folderButton = new Button { Text = "Folder…", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            folderButton.Click += (s, e) =>
            {
                using (var picker = new FolderBrowserDialog { Description = "A folder in the robot for the new " + kind, SelectedPath = newFolder, ShowNewFolderButton = true })
                    if (picker.ShowDialog(this) == DialogResult.OK) { newFolder = picker.SelectedPath; ShowFolder(seasonRoot); newFile.Checked = true; }
            };
            nameRow.Controls.Add(newName, 0, 0);
            nameRow.Controls.Add(folderButton, 1, 0);
            layout.Controls.Add(nameRow);
            ShowFolder(seasonRoot);
            layout.Controls.Add(newFolderLabel);
            if (assemblyName != null)
            {
                useInAssembly.Text = "Use it in " + assemblyName + " instead of " + name;
                useInAssembly.CheckedChanged += (s, e) => { if (useInAssembly.Checked) newFile.Checked = true; };
                layout.Controls.Add(useInAssembly);
                layout.Controls.Add(note("Off: " + assemblyName + " keeps using " + name + ". On: CAD Hub locks " + assemblyName + " and swaps the component (you save it)."));
            }

            problem.MaximumSize = new Size(width, 0);
            layout.Controls.Add(problem);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, MinimumSize = new Size(width, 0), Margin = new Padding(0, 12, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = "Save", AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = save;
            CancelButton = cancel;
            (originalProblem == null ? original : experiment).Checked = true;

            save.Click += (s, e) =>
            {
                string why = null;
                if (original.Checked) { Choice = SaveAsChoice.Original; Target = path; }
                else if (experiment.Checked) { Choice = SaveAsChoice.Experiment; Target = experimentPath.Text; why = checkExperiment(Target); }
                else
                {
                    Choice = SaveAsChoice.NewTeamFile;
                    why = checkNewFile(newName.Text, newFolder);
                    Target = why == null ? Path.Combine(newFolder, newName.Text.Trim() + extension) : null;
                }
                if (why != null) { problem.Text = "⚠ " + why; return; }
                DialogResult = DialogResult.OK;
                Close();
            };
        }

        private void ShowFolder(string seasonRoot)
        {
            string relative = newFolder.Length > seasonRoot.Length ? newFolder.Substring(seasonRoot.TrimEnd(Path.DirectorySeparatorChar).Length + 1) : "the robot folder";
            newFolderLabel.Text = "In " + relative;
        }

        private static RadioButton Option(string text)
        {
            return new RadioButton { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 0), Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.5f, FontStyle.Bold) };
        }

        private static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(20, 2, 0, 0) };
        }

        private static TableLayoutPanel Row(int width)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = width - 20, Margin = new Padding(20, 4, 0, 0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.MinimumSize = new Size(width - 20, 0);
            return row;
        }
    }
}
