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
            // Most operations finish in a moment: only show the window (which blocks SOLIDWORKS while files change) if one doesn't.
            var quick = Task.Run(action);
            try
            {
                if (quick.Wait(500)) return quick.Result;
            }
            catch (AggregateException error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.GetBaseException()).Throw();
            }
            return Show(message, quick);
        }

        private static T Show<T>(string message, Task<T> running)
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
                    try { result = await running; }
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
                Location = new Point(20, 150), Size = new Size(430, 20) });
            var firstTime = new LinkLabel { Text = "First time? Set up your account with the code from your mentor", Location = new Point(20, 178), AutoSize = true };
            firstTime.LinkClicked += (s, e) => { DialogResult = DialogResult.Yes; Close(); };
            Controls.Add(firstTime);
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

    internal sealed class PickRobotDialog : Form
    {
        private readonly ListBox list = new ListBox();
        internal int Index { get { return list.SelectedIndex; } }

        internal PickRobotDialog(string title, string prompt, IList<string> items)
        {
            Text = "JOCO ROBOS CAD — " + title;
            ClientSize = new Size(420, 280);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Controls.Add(new Label { Text = prompt, Location = new Point(20, 15), AutoSize = true });
            list.SetBounds(20, 40, 380, 170);
            foreach (string item in items) list.Items.Add(item);
            list.SelectedIndex = 0;
            list.DoubleClick += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(list);
            var ok = new Button { Text = "Open", DialogResult = DialogResult.OK, Location = new Point(200, 230), Size = new Size(95, 30) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(305, 230), Size = new Size(95, 30) };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    /// <summary>A list of files to check, one explanation, one action button.</summary>
    internal sealed class ChecklistDialog : Form
    {
        private readonly CheckedListBox files = new CheckedListBox();
        internal List<SubmitItem> Selected { get { return files.CheckedItems.Cast<SubmitItem>().ToList(); } }

        internal ChecklistDialog(string title, string explanation, string action, IEnumerable<SubmitItem> items, bool checkAll)
        {
            Text = "JOCO ROBOS CAD — " + title;
            ClientSize = new Size(600, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Controls.Add(new Label { Text = explanation, Location = new Point(20, 15), Size = new Size(560, 40) });
            files.SetBounds(20, 60, 560, 210);
            files.CheckOnClick = true;
            files.HorizontalScrollbar = true;
            foreach (var item in items) files.Items.Add(item, checkAll);
            Controls.Add(files);
            var ok = new Button { Text = action, Location = new Point(380, 285), Size = new Size(95, 30) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(485, 285), Size = new Size(95, 30) };
            ok.Click += (s, e) =>
            {
                if (files.CheckedItems.Count == 0) { MessageBox.Show(this, "Check at least one file."); return; }
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            CancelButton = cancel;
        }
    }

    /// <summary>
    /// First sign-in: the student picks a username and password and sends a request (no code needed); a mentor gives them the
    /// code shown for them, and they finish here with it. A code handed out beforehand works the same way.
    /// Also used, without the code, for Change Password.
    /// </summary>
    internal sealed class SetupAccountDialog : Form
    {
        private readonly TextBox username = new TextBox();
        private readonly TextBox code = new TextBox { CharacterCasing = CharacterCasing.Upper };
        private readonly TextBox password = new TextBox { UseSystemPasswordChar = true };
        private readonly TextBox confirm = new TextBox { UseSystemPasswordChar = true };
        private readonly Label status = new Label { ForeColor = Color.ForestGreen };
        internal string Username { get { return username.Text.Trim().ToLowerInvariant(); } }
        internal string Code { get { return code.Text.Trim(); } }
        internal string Password { get { return password.Text; } }

        internal SetupAccountDialog(string title, string intro, bool askCode, string currentUser, Action<string, string> request = null, string presetCode = null)
        {
            Text = "JOCO ROBOS CAD — " + title;
            ClientSize = new Size(480, askCode ? 400 : 250);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Controls.Add(new Label { Text = intro, Location = new Point(20, 15), Size = new Size(440, 55) });
            int y = 80;
            Action<string, TextBox> row = (label, box) =>
            {
                Controls.Add(new Label { Text = label, Location = new Point(20, y + 3), AutoSize = true });
                box.SetBounds(170, y, 285, 25);
                Controls.Add(box);
                y += 38;
            };
            if (askCode) row("Username", username);
            username.Text = currentUser ?? "";
            row(askCode ? "Password" : "New password", password);
            row(askCode ? "Password again" : "New password again", confirm);
            Controls.Add(new Label { Text = "At least 10 characters. Only you know it; it's saved in Windows Credential Manager.",
                Location = new Point(20, y), Size = new Size(440, 20), ForeColor = SystemColors.GrayText });
            y += 30;
            if (askCode)
            {
                row("Code from a mentor", code);
                code.Text = presetCode ?? "";
                status.SetBounds(20, y, 440, 36);
                Controls.Add(status);
                y += 40;
            }
            var ok = new Button { Text = askCode ? "Send request" : "Save", Location = new Point(235, y), Size = new Size(115, 30) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(360, y), Size = new Size(95, 30) };
            if (askCode)
            {
                // No code yet: ask for an account. With a code: finish.
                Action label = () => ok.Text = Code.Length == 0 ? "Send request" : "Finish";
                code.TextChanged += (s, e) => label();
                label();
            }
            ok.Click += (s, e) =>
            {
                if (askCode && !System.Text.RegularExpressions.Regex.IsMatch(Username, "^[a-z0-9][a-z0-9._-]{1,31}$"))
                { MessageBox.Show(this, "Choose a username: 2–32 lowercase letters, numbers, dot, dash, or underscore, starting with a letter or number (like sarah or j.smith)."); return; }
                if (password.Text.Length < 10) { MessageBox.Show(this, "Use at least 10 characters."); return; }
                if (password.Text != confirm.Text) { MessageBox.Show(this, "The two passwords don't match."); return; }
                if (password.Text.Equals(Username, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "Don't use your username as your password."); return; }
                if (askCode && Code.Length == 0)
                {
                    if (request == null) { MessageBox.Show(this, "Enter the code from your mentor."); return; }
                    try
                    {
                        request(Username, Password);
                        status.ForeColor = Color.ForestGreen;
                        status.Text = "✓ Request sent. Ask a mentor for your code (they see it next to " + Username + "), type it above, and click Finish.";
                        code.Focus();
                    }
                    catch (Exception exception)
                    {
                        status.ForeColor = Color.Firebrick;
                        status.Text = exception.Message;
                    }
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (s, e) => { if (askCode && username.Text.Length > 0) password.Focus(); };
        }
    }
}
