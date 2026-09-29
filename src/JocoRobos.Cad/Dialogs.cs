using System;
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
}
