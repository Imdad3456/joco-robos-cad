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
    /// <summary>
    /// CAD Hub's windows wait for the server with async/await, which comes back to SOLIDWORKS' window thread only if that thread has
    /// a Windows Forms synchronization context. SOLIDWORKS doesn't always leave one in place (1.13.7: the Submit window then tried
    /// to update itself from a background thread). Called on the window thread before awaiting.
    /// </summary>
    internal static class UiThread
    {
        internal static void Ensure()
        {
            if (!(System.Threading.SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
    }

    /// <summary>
    /// SOLIDWORKS' main window, as the owner of every CAD Hub window: owned windows stay in front of it, also after
    /// Alt+Tab. The handle comes from SOLIDWORKS itself; Windows' guess at the process's main window can be another window
    /// (or none), which left the Submit window unowned and hidden behind SOLIDWORKS.
    /// </summary>
    internal sealed class SolidWorksWindow : IWin32Window
    {
        internal static IntPtr Frame;
        public IntPtr Handle { get { return Frame != IntPtr.Zero ? Frame : Process.GetCurrentProcess().MainWindowHandle; } }
    }

    internal static class OperationDialog
    {
        internal static T Run<T>(string message, Func<T> action)
        {
            // Most operations finish in a moment: only show the window (which blocks SOLIDWORKS while files change) if one doesn't.
            var clock = Stopwatch.StartNew();
            var quick = Task.Run(action);
            try
            {
                if (quick.Wait(500)) return quick.Result;
            }
            catch (AggregateException error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.GetBaseException()).Throw();
            }
            try { return Show(message, quick); }
            finally { ErrorLog.Slow("\"" + message + "\"", clock.ElapsedMilliseconds, 5000); }
        }

        private static T Show<T>(string message, Task<T> running)
        {
            T result = default(T);
            Exception failure = null;
            bool completed = false;
            using (var form = new Form { Text = "CAD Hub", ClientSize = new Size(480, 115),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ControlBox = false, ShowInTaskbar = false })
            {
                form.Controls.Add(new Label { Text = message, AutoSize = false, Location = new Point(20, 18), Size = new Size(440, 38) });
                form.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(20, 70), Size = new Size(440, 18) });
                form.FormClosing += (s, e) => { if (!completed) e.Cancel = true; };
                form.Shown += async (s, e) =>
                {
                    UiThread.Ensure();
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

    /// <summary>
    /// Sign in: with Discord (everyone in the team's Discord server), or, for the few special accounts that aren't Discord's,
    /// with a username and password. Discord happens in the browser; this window waits for the team server to say it's done.
    /// </summary>
    internal sealed class SignInDialog : Form
    {
        private static readonly Color Blurple = Color.FromArgb(88, 101, 242);
        private readonly TextBox username = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly Label status;
        private readonly LinkLabel again = new LinkLabel { Text = "Open the Discord page again", AutoSize = true, Visible = false, Margin = new Padding(0, 4, 0, 0) };
        private readonly Button discord;
        private readonly Timer poll = new Timer { Interval = 2000 };
        private DiscordSignIn signIn;
        private DateTime started;
        private bool checking;

        internal NetworkCredential Login { get; private set; }
        internal bool ViaDiscord { get; private set; }

        internal SignInDialog(string currentUser, string reason = null)
        {
            Text = "CAD Hub — Sign In";
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = SystemColors.Window;
            int width = (int)(400 * DeviceDpi / 96f);
            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 14) };
            Func<string, Label> note = text => new Label { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 0) };

            layout.Controls.Add(new Label { Text = "Sign in to CAD Hub", AutoSize = true, Font = new Font(Font.FontFamily, 13f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) });
            if (reason != null) layout.Controls.Add(new Label { Text = reason, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 4, 0, 6) });
            var server = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            server.Controls.Add(new Label { Text = "Team server: " + WorkspaceInfo.Server.Host, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 0) });
            // Wrong team server (typo, or another team's): change it here. The caller asks for the new one.
            var change = new LinkLabel { Text = "Change…", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            change.LinkClicked += (s, e) => { DialogResult = DialogResult.Retry; Close(); };
            server.Controls.Add(change);
            layout.Controls.Add(server);

            discord = new Button
            {
                Text = "Sign in with Discord", FlatStyle = FlatStyle.Flat, BackColor = Blurple, ForeColor = Color.White, Cursor = Cursors.Hand,
                Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), Size = new Size(width, (int)(42 * DeviceDpi / 96f)), Margin = new Padding(0, 0, 0, 6),
            };
            discord.FlatAppearance.BorderSize = 0;
            discord.Click += (s, e) => StartDiscord();
            layout.Controls.Add(discord);
            status = note("Use your Discord account. You need to be in the team's Discord server; your CAD username is your Discord username.");
            layout.Controls.Add(status);
            again.LinkClicked += (s, e) => OpenBrowser();
            layout.Controls.Add(again);
            layout.Controls.Add(note("Saved in Windows, so you sign in once per computer."));

            // The rare account that isn't Discord's (a mentor's spare account): username and password, hidden until asked for.
            var other = new LinkLabel { Text = "Use a password account instead", AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
            var fields = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = width, Visible = false, Margin = new Padding(0, 6, 0, 0) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.Controls.Add(new Label { Text = "Username", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            fields.Controls.Add(username, 1, 0);
            fields.Controls.Add(new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            fields.Controls.Add(password, 1, 1);
            var passwordSignIn = new Button { Text = "Sign In", AutoSize = true, Anchor = AnchorStyles.Right };
            fields.Controls.Add(passwordSignIn, 1, 2);
            username.Text = currentUser ?? "";
            other.LinkClicked += (s, e) => { other.Visible = false; fields.Visible = true; AcceptButton = passwordSignIn; (username.Text.Length > 0 ? (Control)password : username).Focus(); };
            passwordSignIn.Click += (s, e) =>
            {
                if (String.IsNullOrWhiteSpace(username.Text) || password.Text.Length == 0) { MessageBox.Show(this, "Enter the account's username and password."); return; }
                Login = new NetworkCredential(username.Text.Trim(), password.Text);
                ViaDiscord = false;
                DialogResult = DialogResult.OK;
                Close();
            };
            layout.Controls.Add(other);
            layout.Controls.Add(fields);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, MinimumSize = new Size(width, 0), Margin = new Padding(0, 14, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            CancelButton = cancel;
            poll.Tick += (s, e) => CheckDiscord();
            FormClosed += (s, e) => { poll.Stop(); poll.Dispose(); };
        }

        private void StartDiscord()
        {
            signIn = new DiscordSignIn(WorkspaceInfo.Server);
            started = DateTime.UtcNow;
            discord.Text = "Waiting for Discord…";
            ShowStatus("Your browser opened Discord. Approve CAD Hub there; this window finishes by itself.", SystemColors.GrayText);
            again.Visible = true;
            OpenBrowser();
            poll.Start();
        }

        private void OpenBrowser()
        {
            if (signIn == null) return;
            try { Process.Start(new ProcessStartInfo(signIn.StartUrl) { UseShellExecute = true }); }
            catch (Exception)
            {
                ShowStatus("Couldn't open your browser. Copied the sign-in link: paste it into a browser's address bar.", Color.DarkOrange);
                try { Clipboard.SetText(signIn.StartUrl); } catch (Exception) { }
            }
        }

        private async void CheckDiscord()
        {
            if (checking || signIn == null) return;
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(10))
            {
                poll.Stop();
                Reset("That took too long, so it stopped waiting. Click Sign in with Discord to try again.");
                return;
            }
            checking = true;
            var current = signIn;
            UiThread.Ensure();
            DiscordSignIn.State state;
            try { state = await Task.Run(() => current.Check()); }
            finally { checking = false; }
            if (IsDisposed || current != signIn) return;
            if (state == DiscordSignIn.State.Done)
            {
                poll.Stop();
                Login = current.Login;
                ViaDiscord = true;
                DialogResult = DialogResult.OK;
                Close();
            }
            else if (state != DiscordSignIn.State.Pending)
            {
                poll.Stop();
                Reset(current.Problem);
            }
        }

        private void Reset(string problem)
        {
            signIn = null;
            discord.Text = "Sign in with Discord";
            again.Visible = false;
            ShowStatus(problem, Color.Firebrick);
        }

        private void ShowStatus(string text, Color color)
        {
            status.Text = text;
            status.ForeColor = color;
        }
    }

    /// <summary>Which team server this computer uses: asked once, before the first sign-in (see TeamServer).</summary>
    internal sealed class ServerDialog : Form
    {
        private readonly TextBox address = new TextBox();
        internal Uri Server { get; private set; }

        internal ServerDialog(Uri current)
        {
            Text = "CAD Hub — Team Server";
            ClientSize = new Size(475, 205);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Controls.Add(new Label { Text = "Welcome! Which CAD server does your team use?\n\nYour mentor gives you its address together with your setup code, " +
                "for example cad.yourteam.org.", Location = new Point(20, 18), Size = new Size(435, 62) });
            Controls.Add(new Label { Text = "Server address", Location = new Point(20, 97), AutoSize = true });
            address.SetBounds(125, 94, 325, 25);
            address.Text = current == null ? "" : current.Host + (current.IsDefaultPort ? "" : ":" + current.Port);
            Controls.Add(address);
            var ok = new Button { Text = "Continue", Location = new Point(255, 155), Size = new Size(95, 30) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(355, 155), Size = new Size(95, 30) };
            ok.Click += (s, e) =>
            {
                Uri server;
                if (!WorkspacePolicy.TryServerAddress(address.Text, out server))
                {
                    MessageBox.Show(this, "That doesn't look like a server address. Type it like cad.yourteam.org (it must use https).", Text);
                    return;
                }
                Server = server;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
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
            Text = "CAD Hub — Choose Robot";
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
            Text = "CAD Hub — " + title;
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
            Text = "CAD Hub — " + title;
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

    /// <summary>File History: recent submits of one team file, and saving an older version as a separate copy.</summary>
    internal sealed class HistoryDialog : Form
    {
        private readonly ListView list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };

        internal HistoryDialog(string file, IList<FileVersion> versions, Action<FileVersion> saveCopy)
        {
            Text = "CAD Hub — History of " + file;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(640, 400);
            MinimumSize = new Size(480, 300);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            var intro = new Label { Text = "Recent submits that changed " + file + ", newest first. To look at an older version, save it as a separate copy " +
                "(outside the robot folder) and open that; the robot itself stays as it is.", Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 10, 12, 0) };
            list.Columns.Add("When", 150);
            list.Columns.Add("Who", 100);
            list.Columns.Add("What changed", 300);
            list.Columns.Add("#", 60);
            foreach (var version in versions)
                list.Items.Add(new ListViewItem(new[] { version.Time.ToString("ddd MMM d yyyy, h:mm tt"), version.Author,
                    version.Comment.Split('\n')[0], version.Revision.ToString() }) { Tag = version });
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6) };
            list.Dock = DockStyle.Fill;
            panel.Controls.Add(list);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
            var close = new Button { Text = "Close", Size = new Size(95, 30), DialogResult = DialogResult.Cancel };
            var save = new Button { Text = "Save this version as a copy…", AutoSize = true, MinimumSize = new Size(0, 30), Enabled = false };
            list.SelectedIndexChanged += (s, e) => save.Enabled = list.SelectedItems.Count == 1;
            save.Click += (s, e) =>
            {
                if (list.SelectedItems.Count != 1) return;
                try { saveCopy((FileVersion)list.SelectedItems[0].Tag); }
                catch (Exception exception) { MessageBox.Show(this, exception.Message, "CAD Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            buttons.Controls.Add(close);
            buttons.Controls.Add(save);
            Controls.Add(panel);
            Controls.Add(buttons);
            Controls.Add(intro);
            CancelButton = close;
        }
    }
}
