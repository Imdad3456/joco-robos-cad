using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    internal sealed class SolidWorksWindow : IWin32Window
    {
        public IntPtr Handle { get { return Process.GetCurrentProcess().MainWindowHandle; } }
    }

    internal static class OperationDialog
    {
        internal static T Run<T>(string message, Func<T> action)
        {
            T result = default(T);
            Exception failure = null;
            bool completed = false;
            using (var form = new Form { Text = "JOCO ROBOS CAD", ClientSize = new Size(480, 115),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ControlBox = false, ShowInTaskbar = false })
            {
                form.Controls.Add(new Label { Text = message, AutoSize = false, Location = new Point(20, 18), Size = new Size(440, 38) });
                form.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(20, 70), Size = new Size(440, 18) });
                form.FormClosing += (s, e) => { if (!completed) e.Cancel = true; };
                form.Shown += async (s, e) =>
                {
                    try { result = await Task.Run(action); }
                    catch (Exception exception) { failure = exception; }
                    finally { completed = true; form.Close(); }
                };
                form.ShowDialog(new SolidWorksWindow());
            }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }
    }

    internal sealed class SignInDialog : Form
    {
        private readonly TextBox username = new TextBox();
        private readonly TextBox password = new TextBox();
        internal NetworkCredential Login { get { return new NetworkCredential(username.Text.Trim(), password.Text); } }

        internal SignInDialog(string currentUser)
        {
            Text = "JOCO ROBOS CAD — Sign In";
            ClientSize = new Size(475, 265);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Controls.Add(new Label { Text = "Server: cad.imdad.stream\nRobot: 2027-Robot", Location = new Point(20, 18), Size = new Size(435, 40) });
            Controls.Add(new Label { Text = "Username", Location = new Point(20, 78), AutoSize = true });
            username.SetBounds(125, 75, 325, 25);
            username.Text = currentUser ?? "";
            Controls.Add(username);
            Controls.Add(new Label { Text = "Password", Location = new Point(20, 117), AutoSize = true });
            password.SetBounds(125, 114, 325, 25);
            password.UseSystemPasswordChar = true;
            Controls.Add(password);
            Controls.Add(new Label { Text = "Saved in Windows Credential Manager after the server accepts your login.",
                Location = new Point(20, 155), Size = new Size(430, 40) });
            var submit = new Button { Text = "Sign In", Location = new Point(255, 215), Size = new Size(95, 30) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(355, 215), Size = new Size(95, 30) };
            submit.Click += (s, e) =>
            {
                if (String.IsNullOrWhiteSpace(username.Text) || password.Text.Length == 0)
                { MessageBox.Show(this, "Enter your CAD username and password."); return; }
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(submit);
            Controls.Add(cancel);
            AcceptButton = submit;
            CancelButton = cancel;
        }
    }

    internal sealed class SubmitDialog : Form
    {
        private readonly CheckedListBox files = new CheckedListBox();
        private readonly TextBox comment = new TextBox();
        internal List<SubmitItem> Selected { get { return files.CheckedItems.Cast<SubmitItem>().ToList(); } }
        internal string Comment { get { return comment.Text.Trim(); } }

        internal SubmitDialog(SubmitPlan plan)
        {
            Text = "JOCO ROBOS CAD — Submit CAD Changes";
            ClientSize = new Size(640, plan.Blocked.Count > 0 ? 560 : 440);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            int y = 15;
            Controls.Add(new Label { Text = "Files to submit (unchecked files stay on your computer and stay locked):", Location = new Point(20, y), AutoSize = true });
            files.SetBounds(20, y += 25, 600, 190);
            files.CheckOnClick = true;
            files.HorizontalScrollbar = true;
            foreach (var item in plan.Items.OrderBy(x => x.Kind).ThenBy(x => x.Relative, StringComparer.OrdinalIgnoreCase))
                files.Items.Add(item, true);
            Controls.Add(files);
            y += 200;
            if (plan.Blocked.Count > 0)
            {
                Controls.Add(new Label { Text = "Cannot be submitted:", Location = new Point(20, y), AutoSize = true });
                Controls.Add(new TextBox { Text = String.Join("\r\n", plan.Blocked), ReadOnly = true, Multiline = true,
                    ScrollBars = ScrollBars.Both, WordWrap = false, Location = new Point(20, y + 22), Size = new Size(600, 90) });
                y += 120;
            }
            Controls.Add(new Label { Text = "What did you change?", Location = new Point(20, y), AutoSize = true });
            comment.SetBounds(20, y += 22, 600, 60);
            comment.Multiline = true;
            comment.ScrollBars = ScrollBars.Vertical;
            Controls.Add(comment);
            var submit = new Button { Text = "Submit", Location = new Point(420, y += 75), Size = new Size(95, 30) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(525, y), Size = new Size(95, 30) };
            submit.Click += (s, e) =>
            {
                if (files.CheckedItems.Count == 0) { MessageBox.Show(this, "Select at least one file."); return; }
                if (Comment.Length < 3) { MessageBox.Show(this, "Describe what you changed."); comment.Focus(); return; }
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(submit);
            Controls.Add(cancel);
            CancelButton = cancel;
            Shown += (s, e) => comment.Focus();
        }
    }

    internal sealed class ChooseRobotDialog : Form
    {
        private readonly ListBox robots = new ListBox();
        private readonly List<string> names = new List<string>();
        /// <summary>Empty string: follow the season mentors make active.</summary>
        internal string Choice { get { return names[robots.SelectedIndex]; } }

        internal ChooseRobotDialog(Catalog catalog, string current)
        {
            Text = "JOCO ROBOS CAD — Choose Robot";
            ClientSize = new Size(420, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Controls.Add(new Label { Text = "Which robot should Open Robot and Update use?", Location = new Point(20, 15), AutoSize = true });
            robots.SetBounds(20, 40, 380, 190);
            names.Add("");
            robots.Items.Add("Current season (recommended) — " + (catalog.Active ?? "none yet"));
            foreach (var robot in catalog.Robots.AsEnumerable().Reverse())
            {
                names.Add(robot.Name);
                robots.Items.Add(robot.Name + (robot.Name == catalog.Active ? "  (current)" : "") + (robot.Archived ? "  (archived, read-only)" : ""));
            }
            robots.SelectedIndex = Math.Max(0, names.IndexOf(current ?? ""));
            Controls.Add(robots);
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(200, 250), Size = new Size(95, 30) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(305, 250), Size = new Size(95, 30) };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
