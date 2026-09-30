using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

[assembly: ComVisible(false)]

namespace JocoRobos.Cad
{
    [ComVisible(true)]
    [Guid("01CE207C-1D59-4DCB-BE56-1FF3815061F1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IAddinCallbacks
    {
        [DispId(1)] void OpenRobot();
        [DispId(2)] void TestConnection();
        [DispId(3)] int CanRun();
        [DispId(4)] void UpdateRobot();
        [DispId(5)] void Edit();
        [DispId(6)] void SignIn();
        [DispId(7)] void ReleaseEdit();
        [DispId(8)] void Submit();
        [DispId(9)] void InsertFromLibrary();
        [DispId(10)] void ChooseRobot();
        [DispId(11)] void InstallUpdate();
        [DispId(12)] void OpenOldRobot();
        [DispId(13)] void SetAsideChanges();
    }

    [ComVisible(true)]
    [Guid(Addin.ClassId)]
    [ProgId("JocoRobos.Cad.Addin")]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IAddinCallbacks))]
    public sealed class Addin : ISwAddin, IAddinCallbacks
    {
        public const string ClassId = "E219FE9C-5919-4BE5-98B7-A518C11AD901";
        private const string Title = "JOCO ROBOS CAD";
        private const int GroupId = 591902;
        // Bump when toolbar commands change so SOLIDWORKS rebuilds its cached layout.
        private const int LayoutVersion = 591907;
        private SldWorks application;
        private CommandManager commands;
        private bool busy;
        // Status pane: refreshed in the background, rendered on the SOLIDWORKS UI thread.
        private TaskpaneView taskpane;
        private StatusPane pane;
        private Timer statusTimer;
        private bool refreshing;
        private Catalog paneCatalog;
        private DateTime paneCatalogAt, checkedAt;
        private WorkspaceSnapshot robotSnapshot, librarySnapshot;
        private string paneUser, paneError;
        private Catalog.AddinRelease offeredUpdate;
        private string promptedVersion;
        private bool updateChecked;
        private DocumentWatcher watcher;

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                application = (SldWorks)ThisSW;
                if (!application.SetAddinCallbackInfo2(0, this, Cookie))
                    throw new InvalidOperationException("SOLIDWORKS could not register the callbacks.");
                commands = application.GetCommandManager(Cookie);
                CreateCommands();
                // The pane is a convenience; the toolbar must still work if it cannot be created.
                try { CreatePane(); }
                catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO status pane: " + exception); }
                return true;
            }
            catch (Exception exception)
            {
                Message("The add-in could not load.\n\n" + exception.Message, MessageBoxIcon.Error);
                DisconnectFromSW();
                return false;
            }
        }

        private void CreateCommands()
        {
            using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(@"Software\JOCO ROBOS\CAD"))
            {
                bool migrate = Convert.ToInt32(settings.GetValue("CommandLayout", 0)) != LayoutVersion;
                if (migrate) commands.RemoveCommandGroup2(591901, false);
                int error = 0;
                CommandGroup group = commands.CreateCommandGroup2(GroupId, Title,
                    "Robot CAD collaboration", Title, -1, migrate, ref error);
                if (group == null) throw new InvalidOperationException("Could not create toolbar. API code: " + error);
                int both = (int)swCommandItemType_e.swMenuItem | (int)swCommandItemType_e.swToolbarItem;
                int menu = (int)swCommandItemType_e.swMenuItem;
                int open = Add(group, "Open Robot", "Update the workspace and open the robot", nameof(OpenRobot), 1, both);
                int update = Add(group, "Update", "Download the latest robot and library files", nameof(UpdateRobot), 2, both);
                int edit = Add(group, "Edit", "Lock the active CAD document for editing", nameof(Edit), 3, both);
                int submit = Add(group, "Submit", "Upload your changed and new CAD files", nameof(Submit), 7, both);
                int insert = Add(group, "Insert from Library", "Copy a reusable part into the robot and insert it", nameof(InsertFromLibrary), 8, both);
                Add(group, "Sign In", "Connect your CAD account", nameof(SignIn), 4, menu);
                Add(group, "Test Connection", "Verify your CAD account and repository", nameof(TestConnection), 5, menu);
                Add(group, "Release Edit", "Release your lock on an unchanged file", nameof(ReleaseEdit), 6, menu);
                Add(group, "Choose Robot", "Pick which season's robot to work on", nameof(ChooseRobot), 9, menu);
                Add(group, "Open Old Robot", "Open a previous season read-only for reference", nameof(OpenOldRobot), 11, menu);
                Add(group, "Set Aside My Changes", "Save your version of changed files separately and restore the team's", nameof(SetAsideChanges), 12, menu);
                Add(group, "Install Add-in Update", "Install the newest JOCO ROBOS CAD version", nameof(InstallUpdate), 10, menu);
                group.HasMenu = true;
                group.HasToolbar = true;
                if (!group.Activate()) throw new InvalidOperationException("Could not activate toolbar.");
                foreach (int type in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING })
                {
                    CommandTab existing = commands.GetCommandTab(type, Title);
                    if (migrate && existing != null) { commands.RemoveCommandTab(existing); existing = null; }
                    if (existing != null) continue;
                    CommandTab tab = commands.AddCommandTab(type, Title);
                    if (tab == null) throw new InvalidOperationException("Could not create CommandManager tab.");
                    CommandTabBox box = tab.AddCommandTabBox();
                    int text = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextHorizontal;
                    int[] ids = new[] { open, update, edit, submit, insert }.Select(x => group.get_CommandID(x)).ToArray();
                    if (box == null || !box.AddCommands(ids, ids.Select(x => text).ToArray()))
                    {
                        commands.RemoveCommandTab(tab);
                        throw new InvalidOperationException("Could not add CommandManager buttons.");
                    }
                }
                settings.SetValue("CommandLayout", LayoutVersion, RegistryValueKind.DWord);
            }
        }

        private int Add(CommandGroup group, string name, string hint, string callback, int id, int options)
        {
            int index = group.AddCommandItem2(name, -1, hint, name, -1, callback, nameof(CanRun), id, options);
            if (index < 0) throw new InvalidOperationException("Could not add " + name + ".");
            return index;
        }

        public int CanRun() { return application != null && !busy ? 1 : 0; }

        private void Execute(Action action)
        {
            if (CanRun() == 0) return;
            busy = true;
            try { action(); }
            catch (Exception exception)
            {
                string text = exception.Message;
                bool login = text.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("authoriz", StringComparison.OrdinalIgnoreCase) >= 0 || text.Contains("401");
                Message(text + (login ? "\n\nIf your password changed, use Tools → JOCO ROBOS CAD → Sign In." : ""), MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                RefreshStatus();
            }
        }

        // ---------- status pane ----------

        private void CreatePane()
        {
            string icon = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "JocoRobos.Cad", "pane.bmp");
            Directory.CreateDirectory(Path.GetDirectoryName(icon));
            using (var bitmap = new System.Drawing.Bitmap(16, 18))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var font = new System.Drawing.Font("Segoe UI", 8f, System.Drawing.FontStyle.Bold))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(31, 95, 191));
                graphics.DrawString("J", font, System.Drawing.Brushes.White, 2, 1);
                bitmap.Save(icon, System.Drawing.Imaging.ImageFormat.Bmp);
            }
            taskpane = application.CreateTaskpaneView2(icon, Title);
            if (taskpane == null) throw new InvalidOperationException("SOLIDWORKS did not create the task pane.");
            pane = new StatusPane(new[]
            {
                new KeyValuePair<string, Action>("Open Robot", OpenRobot),
                new KeyValuePair<string, Action>("Update", UpdateRobot),
                new KeyValuePair<string, Action>("Edit", Edit),
                new KeyValuePair<string, Action>("Submit", Submit),
                new KeyValuePair<string, Action>("Insert from Library", InsertFromLibrary),
            }, RefreshStatus, InstallUpdate);
            pane.CreateControl();
            if (!taskpane.DisplayWindowFromHandlex64(pane.Handle.ToInt64()))
                throw new InvalidOperationException("SOLIDWORKS did not accept the task pane window.");
            application.ActiveModelDocChangeNotify += OnActiveDocumentChanged;
            watcher = new DocumentWatcher(application,
                path => path.StartsWith(WorkspaceInfo.BaseFolder + "\\", StringComparison.OrdinalIgnoreCase) && WorkspacePolicy.IsSubmittableCad(path),
                doc => pane.BeginInvoke((Action)(() => OfferLock(doc))),
                path => pane.BeginInvoke((Action)(() => ReleaseIfUnchanged(path))));
            statusTimer = new Timer { Interval = 3 * 60 * 1000 };
            statusTimer.Tick += (s, e) => RefreshStatus();
            statusTimer.Start();
            RefreshStatus();
        }

        private int OnActiveDocumentChanged()
        {
            RenderStatus();
            return 0;
        }

        private void RenderStatus()
        {
            if (pane == null || pane.IsDisposed) return;
            try
            {
                var doc = application.ActiveDoc as ModelDoc2;
                string path = doc == null || String.IsNullOrEmpty(doc.GetPathName()) ? null : Path.GetFullPath(doc.GetPathName());
                var state = StatusPane.Describe(paneUser, robotSnapshot, librarySnapshot, path, doc != null && doc.IsOpenedReadOnly(), paneError, checkedAt);
                var season = path == null || paneCatalog == null ? null : paneCatalog.Owning(path);
                if (season != null && !season.IsLibrary && robotSnapshot != null && season.Name != robotSnapshot.Info.Name)
                {
                    state.ActiveStatus = "Reference copy from " + season.Name + ". Read-only; your robot is " + robotSnapshot.Info.Name + ".";
                    state.ActiveColor = System.Drawing.SystemColors.GrayText;
                }
                state.Update = offeredUpdate == null ? null : "Add-in " + offeredUpdate.Version + " is available" + (offeredUpdate.Required ? " (required)" : "") + ".";
                pane.Show(state);
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO status pane: " + exception); }
        }

        // Read-only server check: never prompts, never changes files, skipped while a command runs.
        private void RefreshStatus()
        {
            if (pane == null || pane.IsDisposed) return;
            if (busy || refreshing) { RenderStatus(); return; }
            NetworkCredential login;
            try { login = CredentialStore.Read(); }
            catch (Exception) { login = null; }
            paneUser = login?.UserName;
            if (login == null) { RenderStatus(); return; }
            refreshing = true;
            var cached = DateTime.UtcNow - paneCatalogAt < TimeSpan.FromMinutes(10) ? paneCatalog : null;
            Task.Run(() =>
            {
                var catalog = cached ?? Catalog.Fetch(login);
                var robot = new SvnWorkspace(login, catalog.Robot).Snapshot();
                var library = catalog.Library == null ? null : new SvnWorkspace(login, catalog.Library).Snapshot();
                return Tuple.Create(catalog, robot, library);
            }).ContinueWith(task =>
            {
                if (pane == null || pane.IsDisposed) return;
                pane.BeginInvoke((Action)(() =>
                {
                    refreshing = false;
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        if (task.Result.Item1 != paneCatalog) { paneCatalog = task.Result.Item1; paneCatalogAt = DateTime.UtcNow; }
                        robotSnapshot = task.Result.Item2;
                        librarySnapshot = task.Result.Item3;
                        paneError = null;
                        checkedAt = DateTime.Now;
                        offeredUpdate = Updater.Offer(task.Result.Item1.Addin, Updater.Current);
                    }
                    else
                    {
                        paneCatalog = null;
                        paneError = task.Exception?.GetBaseException().Message ?? "unknown error";
                        if (paneError.Length > 120) paneError = paneError.Substring(0, 120) + "…";
                    }
                    RenderStatus();
                    if (!updateChecked && !busy)
                    {
                        updateChecked = true;
                        string failed = null;
                        try { failed = Updater.TakeFailedUpdate(); }
                        catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
                        if (failed != null) { Message(failed, MessageBoxIcon.Warning); promptedVersion = offeredUpdate?.Version; }
                    }
                    // Ask once per version per session; the pane keeps offering it afterwards.
                    if (offeredUpdate != null && promptedVersion != offeredUpdate.Version && !busy)
                    {
                        promptedVersion = offeredUpdate.Version;
                        InstallUpdate();
                    }
                }));
            });
        }

        // ---------- add-in updates ----------

        public void InstallUpdate()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var offer = Updater.Offer(LoadCatalog(login).Addin, Updater.Current);
                if (offer == null) { Message("JOCO ROBOS CAD " + Updater.Current + " is the newest version."); return; }
                InstallUpdate(login, offer);
            });
        }

        private void InstallUpdate(NetworkCredential login, Catalog.AddinRelease offer)
        {
            string question = "JOCO ROBOS CAD " + offer.Version + " is available (you have " + Updater.Current + ")." +
                (offer.Required ? "\nMentors marked it required: Edit and Submit need it." : "") +
                "\n\nInstall it now? It downloads first; Windows then asks for permission. " +
                "When you close SOLIDWORKS it installs and SOLIDWORKS reopens. Your files and locks are not touched.";
            if (MessageBox.Show(new SolidWorksWindow(), question, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string installer = OperationDialog.Run("Downloading JOCO ROBOS CAD " + offer.Version + "…", () => Updater.Download(login, offer));
            try { Updater.Launch(installer); }
            catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                Message("Update cancelled. It stays available in the JOCO ROBOS CAD pane.", MessageBoxIcon.Warning);
                return;
            }
            Message("Update " + offer.Version + " is ready.\n\nSave your work and close SOLIDWORKS. The update installs by itself and SOLIDWORKS reopens.");
        }

        // A required update means the server changed in a way older add-ins must not write to.
        private void RequireCurrentAddin(NetworkCredential login, Catalog catalog)
        {
            var offer = Updater.Offer(catalog.Addin, Updater.Current);
            if (offer == null || !offer.Required) return;
            InstallUpdate(login, offer);
            throw new InvalidOperationException("JOCO ROBOS CAD " + offer.Version + " is required before you can edit or submit. " +
                "Close SOLIDWORKS to finish installing it, or use Tools → JOCO ROBOS CAD → Install Add-in Update.");
        }

        // ---------- sign-in and seasons ----------

        private NetworkCredential GetLogin(bool force)
        {
            NetworkCredential saved = CredentialStore.Read();
            if (saved != null && !force) return saved;
            using (var dialog = new SignInDialog(saved?.UserName))
            {
                if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return null;
                NetworkCredential login = dialog.Login;
                OperationDialog.Run("Checking your CAD account…", () =>
                {
                    var catalog = Catalog.Fetch(login);
                    new SvnWorkspace(login, catalog.Robot).TestConnection();
                    return true;
                });
                CredentialStore.Write(login);
                return login;
            }
        }

        private static Catalog LoadCatalog(NetworkCredential login)
        {
            return OperationDialog.Run("Contacting the CAD server…", () => Catalog.Fetch(login));
        }

        public void SignIn()
        {
            Execute(() => { if (GetLogin(true) != null) Message("Signed in. Your login is saved in Windows Credential Manager."); });
        }

        public void TestConnection()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                string robot = OperationDialog.Run("Checking your CAD account…", () =>
                {
                    var catalog = Catalog.Fetch(login);
                    foreach (var workspace in new[] { catalog.Robot, catalog.Library }.Where(w => w != null))
                        new SvnWorkspace(login, workspace).TestConnection();
                    return catalog.Robot.Name;
                });
                Message("Connected as " + login.UserName + ". Robot: " + robot + ".");
            });
        }

        public void ChooseRobot()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                using (var dialog = new ChooseRobotDialog(catalog, Catalog.ChosenRobot))
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    Catalog.ChosenRobot = dialog.Choice;
                }
                Message("Open Robot and Update will now use " + catalog.Robot.Name + ".");
            });
        }

        public void OpenOldRobot()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                var older = catalog.Robots.Where(r => r.Name != catalog.Robot.Name).Reverse().ToList();
                if (older.Count == 0) { Message("There are no other seasons yet."); return; }
                WorkspaceInfo season;
                using (var dialog = new PickRobotDialog("Open Old Robot", "Open a previous robot read-only, for reference:",
                    older.Select(r => r.Name + (r.Archived ? "  (archived)" : "") + (new SvnWorkspace(login, r).IsCheckedOut ? "" : "  (downloads once)")).ToList()))
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    season = older[dialog.Index];
                }
                RequireNoOtherSeasonOpen(catalog, season);
                // Downloaded once, then left alone: reference copies never update unless the student switches to that season.
                if (!new SvnWorkspace(login, season).IsCheckedOut) UpdateWorkspace(login, season);
                string master = FindMaster(season);
                if (master == null) { Message("No master assembly was found in " + season.MasterFolder + ". Use File → Open in that folder."); return; }
                int errors = 0, warnings = 0;
                if (application.OpenDoc6(master, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly, "", ref errors, ref warnings) == null)
                    throw new InvalidOperationException(season.Name + " could not open. SOLIDWORKS errors: " + errors + "; warnings: " + warnings);
            });
        }

        // SOLIDWORKS can't hold two different files with the same name. Each season reuses names like
        // Shooter.SLDASM, so opening a second season would silently show the first one's file.
        private void RequireNoOtherSeasonOpen(Catalog catalog, WorkspaceInfo target)
        {
            var clash = OpenDocuments().Select(d => d.GetPathName()).Where(p => !String.IsNullOrEmpty(p))
                .Select(p => new { Path = p, Season = catalog.Owning(p) })
                .Where(x => x.Season != null && !x.Season.IsLibrary && x.Season.Name != target.Name).ToList();
            if (clash.Count > 0)
                throw new InvalidOperationException("Close the " + clash[0].Season.Name + " documents first. SOLIDWORKS can't open two seasons at once, " +
                    "because files with the same name (like Shooter.SLDASM) would be mixed up.\n\nOpen: " +
                    String.Join(", ", clash.Take(5).Select(x => Path.GetFileName(x.Path))) + (clash.Count > 5 ? ", …" : ""));
        }

        // ---------- update and open ----------

        // Never change files beneath a loaded document, including hidden assembly components.
        private void RequireDocumentsClosed(WorkspaceInfo workspace)
        {
            var open = OpenDocuments().Select(d => d.GetPathName()).Where(p => !String.IsNullOrEmpty(p) && workspace.Contains(p)).ToList();
            if (open.Count > 0)
                throw new InvalidOperationException("Save and close these SOLIDWORKS documents before updating " + workspace.Label + ":\n\n" +
                    String.Join("\n", open.Take(10).Select(Path.GetFileName)) + (open.Count > 10 ? "\n…" : "") + "\n\nYour files have not been changed.");
        }

        private long UpdateWorkspace(NetworkCredential login, WorkspaceInfo workspace)
        {
            RequireDocumentsClosed(workspace);
            var svn = new SvnWorkspace(login, workspace);
            return OperationDialog.Run("Downloading " + workspace.Label + " updates…", () => SvnWorkspace.Exclusive(svn.Update));
        }

        // Robot and library together; students never manage the library separately.
        private string UpdateAll(NetworkCredential login, Catalog catalog)
        {
            var robot = catalog.Robot;
            string previous;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\JOCO ROBOS\CAD"))
                previous = key?.GetValue("LastRobot") as string;
            long revision = UpdateWorkspace(login, robot);
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\JOCO ROBOS\CAD"))
                key.SetValue("LastRobot", robot.Name, RegistryValueKind.String);
            string summary = robot.Name + " is at revision " + revision + "." + (robot.Archived ? " It is archived and read-only." : "");
            if (previous != null && previous != robot.Name)
                summary = "Switched to " + robot.Name + ". " + summary + "\nYour previous robot stays in " + Path.Combine(WorkspaceInfo.BaseFolder, previous) + ".";
            if (catalog.Library != null)
            {
                try { UpdateWorkspace(login, catalog.Library); }
                catch (Exception exception) { summary += "\n\nThe parts library was not updated: " + exception.Message; }
            }
            return summary;
        }

        public void UpdateRobot()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                string summary = UpdateAll(login, catalog);
                Message(summary + (FindMaster(catalog.Robot) != null ? "" : "\n\nNo master assembly has been uploaded to this robot yet."));
            });
        }

        public void OpenRobot()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireNoOtherSeasonOpen(catalog, catalog.Robot);
                string summary = UpdateAll(login, catalog);
                if (summary.StartsWith("Switched", StringComparison.Ordinal) || summary.Contains("not updated")) Message(summary);
                string master = FindMaster(catalog.Robot);
                if (master == null)
                {
                    Message("Workspace updated, but no single master assembly was found in:\n" + catalog.Robot.MasterFolder +
                        "\n\nAsk a mentor to name the top-level assembly Robot.SLDASM. You can still use File → Open.");
                    return;
                }
                int errors = 0, warnings = 0;
                ModelDoc2 robot = application.OpenDoc6(master,
                    (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,
                    "", ref errors, ref warnings);
                if (robot == null) throw new InvalidOperationException("Robot could not open. SOLIDWORKS errors: " + errors + "; warnings: " + warnings);
                if (errors != 0 || warnings != 0)
                    Message("Robot opened with SOLIDWORKS diagnostics. Errors: " + errors + "; warnings: " + warnings, MessageBoxIcon.Warning);
            });
        }

        // ---------- edit ----------

        // The active document, or the one component selected in the active assembly's tree or graphics.
        private ModelDoc2 ActiveCad(Catalog catalog, out WorkspaceInfo workspace)
        {
            var doc = application.ActiveDoc as ModelDoc2;
            if (doc == null || String.IsNullOrEmpty(doc.GetPathName()))
                throw new InvalidOperationException("Open a saved CAD document first.");
            if (doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var selection = (SelectionMgr)doc.SelectionManager;
                var components = new List<Component2>();
                for (int i = 1; i <= selection.GetSelectedObjectCount2(-1); i++)
                {
                    var component = selection.GetSelectedObjectsComponent4(i, -1) as Component2;
                    if (component != null && !components.Any(c => String.Equals(c.GetPathName(), component.GetPathName(), StringComparison.OrdinalIgnoreCase)))
                        components.Add(component);
                }
                if (components.Count > 1)
                    throw new InvalidOperationException("Select just one component, or clear the selection to use the whole assembly.");
                if (components.Count == 1)
                {
                    var part = components[0].GetModelDoc2() as ModelDoc2;
                    if (part == null)
                        throw new InvalidOperationException(Path.GetFileName(components[0].GetPathName()) +
                            " is lightweight or suppressed. Right-click it → Set to Resolved, then try again.");
                    doc = part;
                }
            }
            if (!WorkspacePolicy.IsCad(doc.GetPathName())) throw new InvalidOperationException("Select a SOLIDWORKS CAD document first.");
            workspace = catalog.Owning(doc.GetPathName());
            if (workspace == null)
                throw new InvalidOperationException("This file is not in a robot or library folder under " + WorkspaceInfo.BaseFolder + ".");
            return doc;
        }

        public void Edit()
        {
            Execute(() => EditDocument(null));
        }

        // target null: the active document or selected component (Edit button). Otherwise a document the watcher saw change.
        private void EditDocument(ModelDoc2 target)
        {
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireCurrentAddin(login, catalog);
                WorkspaceInfo workspace;
                ModelDoc2 doc = target ?? ActiveCad(catalog, out workspace);
                workspace = catalog.Owning(doc.GetPathName());
                if (workspace == null) throw new InvalidOperationException("This file is not in a robot or library folder under " + WorkspaceInfo.BaseFolder + ".");
                // Submit only covers the current season and the library, so edits elsewhere could never be submitted.
                if (!workspace.IsLibrary && workspace.Name != catalog.Robot.Name)
                    throw new InvalidOperationException(Path.GetFileName(doc.GetPathName()) + " is from " + workspace.Name + ", a reference copy.\n\n" +
                        "To reuse it in " + catalog.Robot.Name + ", ask a mentor to add it to the Library, then use Insert from Library. " +
                        "To edit " + workspace.Name + " itself, switch with Tools → JOCO ROBOS CAD → Choose Robot.");
                string path = doc.GetPathName();
                // Changes made before clicking Edit: keep a copy first, whatever happens next.
                bool unsaved = doc.GetSaveFlag();
                if (unsaved && !doc.IsOpenedReadOnly())
                    throw new InvalidOperationException("Save this document first, then click Edit.");
                // A change noticed seconds ago doesn't need a backup file; one from the Edit button might be long work.
                string safety = unsaved && target == null ? SaveSafetyCopy(doc, workspace) : null;
                var svn = new SvnWorkspace(login, workspace);
                try
                {
                    if (!unsaved && !doc.SetReadOnlyState(true)) throw new InvalidOperationException("SOLIDWORKS could not put the document in read-only mode.");
                    string owner = OperationDialog.Run("Checking the revision and acquiring your edit lock…",
                        () => SvnWorkspace.Exclusive(() => svn.Edit(path)));
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    if (!doc.SetReadOnlyState(false) || doc.IsOpenedReadOnly())
                        throw new InvalidOperationException("Your SVN lock is held, but SOLIDWORKS could not make the document writable. Close and reopen it, then retry Edit.");
                    bool component = !ReferenceEquals(doc, application.ActiveDoc);
                    string kept = !unsaved ? "" : doc.GetSaveFlag()
                        ? "\n\nYour earlier changes are still here. Save to keep them." + (safety != null ? " (A backup copy is in " + safety + ")" : "")
                        : safety != null ? "\n\nSOLIDWORKS reloaded the file, so your earlier changes aren't in this window. They are safe in:\n" + safety
                        : "\n\nSOLIDWORKS reloaded the file; redo your last change.";
                    Message("Locked by " + owner + ". You can now edit " + Path.GetFileName(path) +
                        (workspace.IsLibrary ? " in the Library. Robots that already have a copy keep their own; only future first-time inserts get your version." : ".") +
                        (component ? "\n\nEdit it in place (Edit Part) or open it. Save it with File → Save All; the assembly itself stays read-only." : "") +
                        kept + "\n\nSave normally, then click Submit when you are done.");
                }
                catch (Exception exception)
                {
                    if (!unsaved) doc.SetReadOnlyState(true);
                    if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                    if (safety == null) throw;
                    throw new InvalidOperationException(exception.Message + "\n\nYour unsaved changes were saved as a copy in:\n" + safety +
                        "\n\nYou can close this document without saving; your work is in that copy. Show it to whoever is editing the file.", exception);
                }
            }
        }

        // ---------- lock on first change, release on close ----------

        // A read-only team file was just changed (even if it was opened with File → Open): offer to lock it now,
        // before the student spends time on changes they could not save.
        private void OfferLock(ModelDoc2 doc)
        {
            if (busy || application == null) return;
            string path;
            try { path = Path.GetFullPath(doc.GetPathName()); if (!doc.IsOpenedReadOnly()) return; }
            catch (Exception) { return; } // Closed in the meantime.
            var season = paneCatalog?.Owning(path);
            if (season != null && (season.Archived || (!season.IsLibrary && robotSnapshot != null && season.Name != robotSnapshot.Info.Name))) return;
            string name = Path.GetFileName(path);
            // From the last status check; Edit re-checks with the server either way.
            string owner = new[] { robotSnapshot, librarySnapshot }
                .Where(x => x != null && !x.Mine.Contains(path) && x.Locks.ContainsKey(path))
                .Select(x => x.Locks[path]).FirstOrDefault();
            if (owner != null)
            {
                Message(owner + " is editing " + name + ", so your changes can't be saved into the robot.\n\n" +
                    "Undo them (Ctrl+Z), or use File → Save As to keep a copy outside the robot folder and show it to " + owner + ".", MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(new SolidWorksWindow(), "You're changing " + name + ", which is read-only until you lock it.\n\nLock it for editing now? " +
                "Your change is kept, and nobody else can edit it until you Submit.", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Execute(() => EditDocument(doc));
        }

        // Closing a locked file without saving changes gives it back, so forgotten locks don't block teammates.
        private void ReleaseIfUnchanged(string path)
        {
            if (application == null) return;
            var season = paneCatalog?.Owning(path);
            bool mine = new[] { robotSnapshot, librarySnapshot }.Any(x => x != null && x.Mine.Contains(path));
            var login = paneUser == null ? null : CredentialStore.Read();
            if (season == null || !mine || login == null) return;
            if (OpenDocuments().Any(d => String.Equals(d.GetPathName(), path, StringComparison.OrdinalIgnoreCase))) return;
            var svn = new SvnWorkspace(login, season);
            Task.Run(() =>
            {
                // ReleaseEdit refuses unless this computer owns the lock and the file on disk is unchanged.
                svn.ReleaseEdit(path);
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            }).ContinueWith(task =>
            {
                if (pane == null || pane.IsDisposed) return;
                pane.BeginInvoke((Action)(() =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) RefreshStatus();
                }));
            });
        }

        // Saves the in-memory document as a copy under C:\JOCO-ROBOS\Set Aside without changing what SOLIDWORKS has open.
        private string SaveSafetyCopy(ModelDoc2 doc, WorkspaceInfo workspace)
        {
            string path = doc.GetPathName();
            string copy = Path.Combine(WorkspaceInfo.BaseFolder, "Set Aside", DateTime.Now.ToString("yyyy-MM-dd HHmm"), workspace.Name,
                path.Substring(workspace.Root.Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(copy));
            int errors = 0, warnings = 0;
            bool saved = doc.Extension.SaveAs3(copy, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, null, ref errors, ref warnings);
            if (!saved || !File.Exists(copy))
                throw new InvalidOperationException("Could not save a backup of your unsaved changes (error " + errors + "). Nothing was changed.\n\n" +
                    "Use File → Save As to save your work somewhere else first.");
            return copy;
        }

        public void SetAsideChanges()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                var workspaces = new[] { catalog.Robot, catalog.Library }
                    .Where(w => w != null && !w.Archived).Select(w => new SvnWorkspace(login, w)).Where(w => w.IsCheckedOut).ToList();
                var candidates = new List<SubmitItem>();
                OperationDialog.Run("Checking your changes…", () => SvnWorkspace.Exclusive(() =>
                {
                    foreach (var plan in workspaces.Select(w => w.PrepareSubmit()))
                        candidates.AddRange(plan.SetAside.Concat(plan.Items.Where(x => x.Kind == SubmitKind.Modified && WorkspacePolicy.IsCad(x.Path))));
                    return true;
                }));
                if (candidates.Count == 0) { Message("You have no changed files to set aside."); return; }
                List<SubmitItem> chosen;
                using (var dialog = new SetAsideDialog(candidates))
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    chosen = dialog.Selected;
                }
                var open = OpenDocuments().Select(d => d.GetPathName()).Where(p => chosen.Any(c => String.Equals(c.Path, p, StringComparison.OrdinalIgnoreCase))).ToList();
                if (open.Count > 0)
                    throw new InvalidOperationException("Close these documents first (don't save):\n\n" + String.Join("\n", open.Select(Path.GetFileName)));
                var folders = new List<string>();
                foreach (var svn in workspaces.Where(w => chosen.Any(c => c.Workspace.Name == w.Info.Name)))
                    folders.Add(OperationDialog.Run("Saving your versions and restoring the team's…", () => SvnWorkspace.Exclusive(() => svn.SetAside(chosen))));
                Message("Your versions are saved in:\n" + String.Join("\n", folders) + "\n\nThe team's versions are back in the robot. Click Update to get the newest, " +
                    "then Edit when the file is free. Open your saved copy side by side to redo or copy your changes.");
            });
        }

        public void ReleaseEdit()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                WorkspaceInfo workspace;
                ModelDoc2 doc = ActiveCad(catalog, out workspace);
                if (doc.GetSaveFlag()) throw new InvalidOperationException("Cannot release a file with unsaved changes.");
                string path = doc.GetPathName();
                if (!doc.SetReadOnlyState(true)) throw new InvalidOperationException("Could not make the document read-only.");
                var svn = new SvnWorkspace(login, workspace);
                OperationDialog.Run("Releasing your unchanged file…", () => SvnWorkspace.Exclusive(() => { svn.ReleaseEdit(path); return true; }));
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                Message("Edit lock released. The file is read-only.");
            });
        }

        // ---------- submit ----------

        public void Submit()
        {
            Execute(() =>
            {
                // SOLIDWORKS keeps edits in memory; only saved bytes can be submitted.
                // Read-only documents can look modified after a rebuild, but their changes can never be saved or submitted.
                var unsaved = OpenDocuments().Where(d => d.GetSaveFlag() && !d.IsOpenedReadOnly()).Select(d =>
                    String.IsNullOrEmpty(d.GetPathName()) ? d.GetTitle() + " (never saved)" : d.GetPathName()).ToList();
                if (unsaved.Count > 0)
                    throw new InvalidOperationException("Save these documents first (Save As into your robot folder for new files):\n\n" +
                        String.Join("\n", unsaved) + "\n\nNothing was submitted.");
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireCurrentAddin(login, catalog);
                var workspaces = new[] { catalog.Robot, catalog.Library }
                    .Where(w => w != null && !w.Archived).Select(w => new SvnWorkspace(login, w)).Where(w => w.IsCheckedOut).ToList();
                if (workspaces.Count == 0) throw new InvalidOperationException("Click Update first to download the robot.");
                var plan = new SubmitPlan();
                OperationDialog.Run("Checking your changes…", () => SvnWorkspace.Exclusive(() =>
                {
                    foreach (var part in workspaces.Select(w => w.PrepareSubmit()))
                    {
                        plan.Items.AddRange(part.Items);
                        plan.Blocked.AddRange(part.Blocked);
                        plan.SetAside.AddRange(part.SetAside);
                        if (part.Notice != null) plan.Notice = (plan.Notice == null ? "" : plan.Notice + "\n\n") + part.Notice;
                    }
                    return true;
                }));
                if (plan.Notice != null) Message(plan.Notice);
                if (plan.Items.Count == 0)
                {
                    Message(plan.Blocked.Count == 0 ? "Nothing to submit. Your workspace matches the server." :
                        "Nothing can be submitted:\n\n" + String.Join("\n", plan.Blocked), plan.Blocked.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    return;
                }
                List<SubmitItem> selected;
                string comment;
                using (var dialog = new SubmitDialog(plan))
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    selected = dialog.Selected;
                    comment = dialog.Comment;
                }
                if (!RequireReferencesIncluded(selected, plan, catalog)) return;
                // One revision per repository. The library goes last so a robot failure stops before it.
                var lines = new List<string>();
                bool warned = false;
                foreach (var svn in workspaces.Where(w => selected.Any(x => x.Workspace.Name == w.Info.Name)).OrderBy(w => w.Info.IsLibrary))
                {
                    var mine = selected.Where(x => x.Workspace.Name == svn.Info.Name).ToList();
                    SubmitResult result;
                    try
                    {
                        result = OperationDialog.Run("Submitting " + mine.Count + " file(s) to " + svn.Info.Label + "…",
                            () => SvnWorkspace.Exclusive(() => svn.Submit(mine, comment)));
                    }
                    catch (Exception exception) when (lines.Count > 0)
                    {
                        throw new InvalidOperationException(String.Join("\n", lines) + "\n\n" + svn.Info.Label + " was not submitted:\n" + exception.Message, exception);
                    }
                    // Submitted files are no longer locked; stop SOLIDWORKS from saving over them.
                    var done = new HashSet<string>(mine.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
                    foreach (var doc in OpenDocuments().Where(d => done.Contains(d.GetPathName() ?? "")))
                        doc.SetReadOnlyState(true);
                    lines.Add(svn.Info.Label + ": " + (result.Revision > 0 ? "submitted as revision " + result.Revision + "." : "locks released."));
                    lines.AddRange(result.Warnings);
                    warned |= result.Warnings.Count > 0;
                }
                Message(String.Join("\n\n", lines), warned ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            });
        }

        // Teammates must be able to open what is submitted: every reference inside the same robot
        // (library parts are copied in, never linked), and no new files left behind on this computer.
        // Returns false if the student chose not to continue.
        private bool RequireReferencesIncluded(List<SubmitItem> selected, SubmitPlan plan, Catalog catalog)
        {
            var chosen = new HashSet<string>(selected.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            var newFiles = new HashSet<string>(plan.Items.Where(x => x.Kind == SubmitKind.New).Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();
            var temporary = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexes = new Dictionary<string, ILookup<string, string>>();
            foreach (var item in selected.Where(x => x.Kind != SubmitKind.ReleaseOnly && WorkspacePolicy.IsCad(x.Path)))
            {
                ILookup<string, string> index;
                if (!indexes.TryGetValue(item.Workspace.Name, out index)) indexes[item.Workspace.Name] = index = CadByName(item.Workspace.Root);
                var mine = new List<string>();
                foreach (string reference in Dependencies(item.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string resolved = Resolve(reference, item.Path, index);
                    if (resolved == null)
                    {
                        // Imported (3D Interconnect) or virtual data that only lives in SOLIDWORKS' temp folder.
                        if (WorkspacePolicy.IsTemporary(reference, Path.GetTempPath())) temporary.Add(Path.GetFileName(reference));
                        continue; // Otherwise missing here too; Submit doesn't make that worse.
                    }
                    if (!item.Workspace.Contains(resolved))
                    {
                        var other = catalog.Owning(resolved);
                        mine.Add(other != null && other.IsLibrary
                            ? "links directly to the Library part " + Path.GetFileName(resolved) + " (use Insert from Library)"
                            : "uses " + resolved);
                    }
                    else if (newFiles.Contains(resolved) && !chosen.Contains(resolved))
                        mine.Add("uses the new file " + Path.GetFileName(resolved) + ", which is unchecked");
                }
                if (mine.Count > 0)
                    problems.Add(Path.GetFileName(item.Path) + ":\n" + String.Join("\n", mine.Take(6).Select(x => "   • " + x)) +
                        (mine.Count > 6 ? "\n   • …and " + (mine.Count - 6) + " more" : ""));
            }
            if (problems.Count > 0)
                throw new InvalidOperationException("Teammates would not be able to open this submission:\n\n" +
                    String.Join("\n\n", problems.Take(5)) + (problems.Count > 5 ? "\n\n…and " + (problems.Count - 5) + " more files" : "") +
                    "\n\nSave a copy of each outside part into the robot folder, replace the component, save, and Submit again. Nothing was submitted.");
            if (temporary.Count == 0) return true;
            string examples = String.Join(", ", temporary.Take(4)) + (temporary.Count > 4 ? ", …" : "");
            return MessageBox.Show(new SolidWorksWindow(),
                temporary.Count + " imported part(s) exist only in SOLIDWORKS' temporary folder (" + examples + ").\n\n" +
                "This happens with parts inserted from STEP or other CAD formats. They open on this computer, but teammates may see them as missing.\n" +
                "To make them permanent: right-click each imported part → Break Link, or open it and Save As into the robot folder.\n\n" +
                "Submit anyway?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        // Where SOLIDWORKS will actually find a reference: the stored path if it exists (outside the temp folder),
        // otherwise a file with the same name, preferring the referencing document's own folder.
        private static string Resolve(string reference, string referencing, ILookup<string, string> index)
        {
            if (File.Exists(reference) && !WorkspacePolicy.IsTemporary(reference, Path.GetTempPath())) return Path.GetFullPath(reference);
            var matches = index[Path.GetFileName(reference)].ToList();
            string folder = Path.GetDirectoryName(referencing);
            return matches.FirstOrDefault(m => String.Equals(Path.GetDirectoryName(m), folder, StringComparison.OrdinalIgnoreCase)) ?? matches.FirstOrDefault();
        }

        private static ILookup<string, string> CadByName(string root)
        {
            if (!Directory.Exists(root)) return new string[0].ToLookup(x => x);
            return Directory.EnumerateFiles(root, "*.sld*", SearchOption.AllDirectories)
                .Where(f => WorkspacePolicy.IsSubmittableCad(f) && f.IndexOf(@"\.svn\", StringComparison.OrdinalIgnoreCase) < 0)
                .ToLookup(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        }

        // ---------- library ----------

        public void InsertFromLibrary()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireCurrentAddin(login, catalog);
                var library = catalog.Library;
                if (library == null) throw new InvalidOperationException("The server has no parts library yet. Ask a mentor.");
                var assemblyDoc = application.ActiveDoc as ModelDoc2;
                var robot = assemblyDoc == null || String.IsNullOrEmpty(assemblyDoc.GetPathName()) ? null : catalog.Owning(assemblyDoc.GetPathName());
                if (robot == null || robot.IsLibrary || assemblyDoc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                    throw new InvalidOperationException("Open the robot assembly you want to add the part to, and click Edit on it first.");
                if (robot.Archived) throw new InvalidOperationException(robot.Name + " is archived and read-only.");
                if (assemblyDoc.IsOpenedReadOnly())
                    throw new InvalidOperationException("Click Edit on " + Path.GetFileName(assemblyDoc.GetPathName()) + " first, so you can add parts to it.");

                // Keep the library fresh; a local copy is fine if the update cannot run right now.
                var librarySvn = new SvnWorkspace(login, library);
                try { UpdateWorkspace(login, library); }
                catch (Exception) when (librarySvn.IsCheckedOut) { }

                string source;
                using (var dialog = new OpenFileDialog { Title = "Insert from Library", InitialDirectory = library.Root,
                    Filter = "SOLIDWORKS parts and assemblies (*.sldprt;*.sldasm)|*.sldprt;*.sldasm", RestoreDirectory = true })
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    source = Path.GetFullPath(dialog.FileName);
                }
                if (!library.Contains(source)) throw new InvalidOperationException("Choose a part from the Library folder:\n" + library.Root);

                string copy = CopyFromLibrary(library, robot, source);
                AddToAssembly(assemblyDoc, copy);
                Message("Inserted " + Path.GetFileName(copy) + ".\n\nIt was copied into your robot at:\n" + copy +
                    "\n\nMate it, save the assembly, and it will be included in your next Submit.");
            });
        }

        // Copies a library part (and an assembly's library parts) into the robot and points copied
        // assemblies at the copies. Files already copied into this robot are reused, never overwritten.
        private string CopyFromLibrary(WorkspaceInfo library, WorkspaceInfo robot, string source)
        {
            var index = CadByName(library.Root);
            var files = new[] { source }.Concat(Dependencies(source).Select(r => Resolve(r, source, index)).Where(r => r != null))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var outside = files.Where(f => !library.Contains(f)).ToList();
            if (outside.Count > 0)
                throw new InvalidOperationException(Path.GetFileName(source) + " uses files outside the Library, so it cannot be copied safely:\n" +
                    String.Join("\n", outside) + "\n\nAsk a mentor to fix this library part.");
            var missing = files.Where(f => !File.Exists(f)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("The Library is missing files this part needs:\n" + String.Join("\n", missing) + "\n\nClick Update and try again.");

            var created = new List<string>();
            try
            {
                foreach (string file in files)
                {
                    string target = WorkspacePolicy.LibraryCopyPath(library.Root, robot.Root, file);
                    if (File.Exists(target)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(file, target);
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    created.Add(target);
                }
                foreach (string target in created.Where(t => !t.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)))
                {
                    string original = files.First(f => WorkspacePolicy.LibraryCopyPath(library.Root, robot.Root, f).Equals(target, StringComparison.OrdinalIgnoreCase));
                    foreach (string reference in Dependencies(original))
                    {
                        string resolved = Resolve(reference, original, index);
                        if (resolved != null && library.Contains(resolved))
                            application.ReplaceReferencedDocument(target, reference, WorkspacePolicy.LibraryCopyPath(library.Root, robot.Root, resolved));
                    }
                    var stillLinked = Dependencies(target).Where(library.Contains).ToList();
                    if (stillLinked.Count > 0)
                        throw new InvalidOperationException("Could not point " + Path.GetFileName(target) + " at the robot copies of:\n" + String.Join("\n", stillLinked));
                }
            }
            catch
            {
                // Leave the robot exactly as it was: remove only the files this attempt created.
                foreach (string target in created) { try { File.Delete(target); } catch (IOException) { } }
                throw;
            }
            return WorkspacePolicy.LibraryCopyPath(library.Root, robot.Root, source);
        }

        private void AddToAssembly(ModelDoc2 assemblyDoc, string path)
        {
            bool wasOpen = OpenDocuments().Any(d => String.Equals(d.GetPathName(), path, StringComparison.OrdinalIgnoreCase));
            int errors = 0, warnings = 0;
            int type = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
            // SOLIDWORKS requires the component to be loaded before AddComponent5.
            var component = application.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
            if (component == null) throw new InvalidOperationException("SOLIDWORKS could not open " + Path.GetFileName(path) + " (error " + errors + "). It is copied into the robot; insert it manually.");
            int activateErrors = 0;
            application.ActivateDoc3(assemblyDoc.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
            var added = ((AssemblyDoc)assemblyDoc).AddComponent5(path, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                "", false, "", 0, 0, 0);
            if (!wasOpen) application.CloseDoc(component.GetTitle());
            if (added == null) throw new InvalidOperationException(Path.GetFileName(path) + " is copied into the robot, but SOLIDWORKS could not insert it. Drag it in from:\n" + path);
        }

        // ---------- helpers ----------

        // Robot.SLDASM when present; otherwise the only 00_Master assembly that no other assembly there references.
        private string FindMaster(WorkspaceInfo robot)
        {
            if (File.Exists(robot.RobotPath)) return robot.RobotPath;
            if (!Directory.Exists(robot.MasterFolder)) return null;
            string[] assemblies = Directory.GetFiles(robot.MasterFolder, "*.sldasm")
                .Where(WorkspacePolicy.IsSubmittableCad).ToArray();
            // Compare names: stored reference paths can be stale (for example after a STEP import).
            var referenced = new HashSet<string>(assemblies.SelectMany(Dependencies).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            string[] top = assemblies.Where(x => !referenced.Contains(Path.GetFileName(x))).ToArray();
            return top.Length == 1 ? top[0] : null;
        }

        // Stored reference paths of a saved document, all levels deep, read without opening it.
        private IEnumerable<string> Dependencies(string path)
        {
            var raw = application.GetDocumentDependencies2(path, true, false, false) as object[];
            if (raw == null) return Enumerable.Empty<string>();
            var result = new List<string>();
            for (int i = 1; i < raw.Length; i += 2)
            {
                string reference = raw[i] as string;
                if (String.IsNullOrEmpty(reference)) continue;
                try { result.Add(Path.GetFullPath(reference)); } catch (ArgumentException) { result.Add(reference); }
            }
            return result;
        }

        private IEnumerable<ModelDoc2> OpenDocuments()
        {
            for (var doc = application.GetFirstDocument() as ModelDoc2; doc != null; doc = doc.GetNext() as ModelDoc2)
                yield return doc;
        }

        public bool DisconnectFromSW()
        {
            if (busy) return false;
            try
            {
                watcher?.Dispose();
                watcher = null;
                statusTimer?.Stop();
                statusTimer?.Dispose();
                if (application != null && pane != null) application.ActiveModelDocChangeNotify -= OnActiveDocumentChanged;
                taskpane?.DeleteView();
                pane?.Dispose();
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
            try { if (commands != null) commands.RemoveCommandGroup2(GroupId, true); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
            // Unreleased COM references can keep SLDWORKS.exe running after its window closes,
            // which also stops a waiting update from installing.
            Release(taskpane);
            Release(commands);
            Release(application);
            statusTimer = null;
            taskpane = null;
            pane = null;
            commands = null;
            application = null;
            paneCatalog = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        private static void Release(object comObject)
        {
            try { if (comObject != null && Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
        }

        private static void Message(string text, MessageBoxIcon icon = MessageBoxIcon.Information)
        {
            MessageBox.Show(new SolidWorksWindow(), text, Title, MessageBoxButtons.OK, icon);
        }
    }
}
