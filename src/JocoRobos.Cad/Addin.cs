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
        [DispId(14)] void RestoreDeletedFiles();
        [DispId(15)] void InsertExternalPart();
        [DispId(16)] void ImportOutsideReferences();
        [DispId(17)] void RepairMovedReferences();
        [DispId(18)] void ChangePassword();
        [DispId(19)] void UpgradeRobotFiles();
        [DispId(20)] void ShowLibrary();
        [DispId(21)] void FileHistory();
        [DispId(22)] void CopyDiagnostics();
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
        // A new id whenever commands are added: SOLIDWORKS caches menu text per group id and can show old names otherwise.
        private const int GroupId = 591906;
        private static readonly int[] OldGroupIds = { 591901, 591902, 591903, 591904, 591905 };
        // Bump when toolbar commands change so SOLIDWORKS rebuilds its cached layout.
        private const int LayoutVersion = 591916;
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
        // The robot from the last catalog read, so the file browser still lists local files when the server check fails.
        private WorkspaceInfo robotInfo;
        private WorkspaceSnapshot robotOffline;
        private string paneUser, paneError;
        private Catalog.AddinRelease offeredUpdate;
        private string promptedVersion;
        private bool updateChecked;
        private volatile bool heartbeatSent;
        private DocumentWatcher watcher;
        private SubmitWindow submitWindow;
        private readonly SubmitDraft submitDraft = new SubmitDraft();
        // A short success line at the top of the pane instead of another OK dialog.
        private string flash;
        private Timer flashTimer, savedTimer;
        private readonly HashSet<string> warnedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Background work the panel shows as a line of text instead of a window (automatic update).
        private string working, autoUpdateProblem;
        private DateTime nextAutoUpdateRetry;
        // Set by an automatic update, cleared by the next status check: the old snapshot must not trigger it again.
        private bool awaitingFreshStatus;
        private DateTime autoUpdateEnded = DateTime.MinValue;
        private readonly List<ModelDoc2> pendingLockOffers = new List<ModelDoc2>();

        // Lock offers that arrived while JOCO was busy, handled once it isn't: only files still read-only with unsaved changes.
        private void OfferPendingLocks()
        {
            if (busy || pendingLockOffers.Count == 0) return;
            var waiting = pendingLockOffers.ToList();
            pendingLockOffers.Clear();
            foreach (var doc in waiting)
                OnUi("pending lock offer", () =>
                {
                    bool stillNeeded;
                    try { stillNeeded = doc.IsOpenedReadOnly() && doc.GetSaveFlag(); }
                    catch (Exception) { stillNeeded = false; } // Closed meanwhile.
                    if (stillNeeded) OfferLock(doc);
                });
        }

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                application = (SldWorks)ThisSW;
                // Can't stop SOLIDWORKS from closing on an escaped error, but leaves a record of why.
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ErrorLog.Write("unhandled", e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject)));
                if (!application.SetAddinCallbackInfo2(0, this, Cookie))
                    throw new InvalidOperationException("SOLIDWORKS could not register the callbacks.");
                commands = application.GetCommandManager(Cookie);
                CreateCommands();
                application.DestroyNotify += OnSolidWorksClosing;
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
                if (migrate)
                    foreach (int old in OldGroupIds)
                        try { commands.RemoveCommandGroup2(old, false); }
                        catch (Exception exception) { ErrorLog.Write("remove old toolbar " + old, exception); }
                int error = 0;
                CommandGroup group = commands.CreateCommandGroup2(GroupId, Title,
                    "Robot CAD collaboration", Title, -1, migrate, ref error);
                if (group == null) throw new InvalidOperationException("Could not create toolbar. API code: " + error);
                // One strip per size; each command's image index picks its icon (see tools/make-icons.py).
                string[] strips = IconFiles("toolbar"), mains = IconFiles("main");
                if (strips != null) group.IconList = strips;
                if (mains != null) group.MainIconList = mains;
                int both = (int)swCommandItemType_e.swMenuItem | (int)swCommandItemType_e.swToolbarItem;
                int menu = (int)swCommandItemType_e.swMenuItem;
                // Everyday work first; the rest in sections below separators. Add-in menus can't reliably nest submenus.
                int open = Add(group, "Open Robot", "Get the newest robot and open it", nameof(OpenRobot), 1, both, 0);
                int edit = Add(group, "Edit", "Lock the active part (or the selected component) so you can change it", nameof(Edit), 3, both, 2);
                int submit = Add(group, "Submit", "Send your changed and new CAD files to the team", nameof(Submit), 7, both, 3);
                int library = Add(group, "Library", "Find a part: the team Library, FRCDesignLib, or a downloaded file", nameof(ShowLibrary), 19, both, 4);
                group.AddSpacer2(-1, menu);
                Add(group, "Update", "Get teammates' changes now (normally automatic)", nameof(UpdateRobot), 2, menu, 1);
                Add(group, "Release Edit", "Give back your lock on an unchanged file (normally automatic when you close it)", nameof(ReleaseEdit), 6, menu, 8);
                Add(group, "Choose Robot", "Pick which season's robot to work on", nameof(ChooseRobot), 9, menu, 9);
                Add(group, "Open Old Robot", "Open a previous season read-only for reference", nameof(OpenOldRobot), 11, menu, 10);
                Add(group, "File History", "Who changed the active file, when, and why; save an older version as a copy", nameof(FileHistory), 20, menu);
                group.AddSpacer2(-1, menu);
                Add(group, "Sign In", "Connect your CAD account", nameof(SignIn), 4, menu, 5);
                Add(group, "Change Password", "Choose a new password for your CAD account", nameof(ChangePassword), 17, menu, 7);
                Add(group, "Test Connection", "Check your CAD account and connection", nameof(TestConnection), 5, menu, 6);
                Add(group, "Copy Diagnostics", "Copy a report (no passwords) to send a mentor when something's wrong", nameof(CopyDiagnostics), 21, menu);
                group.AddSpacer2(-1, menu);
                Add(group, "Set Aside My Changes", "Recovery: keep your version of changed files as a copy and restore the team's", nameof(SetAsideChanges), 12, menu, 11);
                Add(group, "Restore Deleted Files", "Recovery: bring back team files deleted on this computer", nameof(RestoreDeletedFiles), 13, menu, 12);
                Add(group, "Import Outside References", "Recovery: copy parts this assembly uses from outside the robot into it", nameof(ImportOutsideReferences), 15, menu, 14);
                Add(group, "Repair Moved References", "Recovery: after reorganizing folders, repoint every file's links to the same-named file", nameof(RepairMovedReferences), 16, menu, 16);
                Add(group, "Upgrade Robot Files", "Mentor: convert every robot file to this SOLIDWORKS version once", nameof(UpgradeRobotFiles), 18, menu, 15);
                Add(group, "Install Add-in Update", "Install the newest JOCO ROBOS CAD version", nameof(InstallUpdate), 10, menu, 17);
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
                    int[] ids = new[] { open, edit, submit, library }.Select(x => group.get_CommandID(x)).ToArray();
                    if (box == null || !box.AddCommands(ids, ids.Select(x => text).ToArray()))
                    {
                        commands.RemoveCommandTab(tab);
                        throw new InvalidOperationException("Could not add CommandManager buttons.");
                    }
                }
                settings.SetValue("CommandLayout", LayoutVersion, RegistryValueKind.DWord);
            }
        }

        private int Add(CommandGroup group, string name, string hint, string callback, int id, int options, int image = -1)
        {
            int index = group.AddCommandItem2(name, -1, hint, name, image, callback, nameof(CanRun), id, options);
            if (index < 0) throw new InvalidOperationException("Could not add " + name + ".");
            return index;
        }

        internal static string IconFolder
        {
            get { return Path.Combine(Path.GetDirectoryName(typeof(Addin).Assembly.Location), "Icons"); }
        }

        // SOLIDWORKS picks the size it needs from 20, 32, 40, 64, 96, and 128 px files; null if they're missing.
        private static string[] IconFiles(string prefix)
        {
            var files = new[] { 20, 32, 40, 64, 96, 128 }.Select(size => Path.Combine(IconFolder, prefix + "_" + size + ".png")).ToArray();
            return files.All(File.Exists) ? files : null;
        }

        public int CanRun() { return application != null && !busy ? 1 : 0; }

        private void Execute(Action action, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
        {
            if (CanRun() == 0) return;
            busy = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { action(); }
            catch (Exception exception)
            {
                ErrorLog.Write("command", exception);
                string text = exception.Message;
                // SVN's own wording when a school filter swaps in its HTTPS certificate.
                if (text.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("verif", StringComparison.OrdinalIgnoreCase) >= 0)
                    text = "This network is interfering with the secure connection to " + WorkspaceInfo.Server.Host + " (school web filters that inspect HTTPS do this). " +
                        "Try another network (home Wi-Fi or a phone hotspot), or ask the network's IT to allow it.\n\nDetails: " + text;
                bool login = text.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("authoriz", StringComparison.OrdinalIgnoreCase) >= 0 || text.Contains("401");
                Message(text + (login ? "\n\nIf your password changed, use Tools → JOCO ROBOS CAD → Sign In." : ""), MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                OfferPendingLocks();
                // Includes dialogs the student had open, so only very long commands are worth noting.
                ErrorLog.Slow("command " + name, clock.ElapsedMilliseconds, 60000);
                RefreshStatus();
            }
        }

        // ---------- status pane ----------

        private void CreatePane()
        {
            // The task pane tab uses the Open Robot icon; the plain "J" is only a fallback if the icon files are missing.
            string[] mains = IconFiles("main");
            if (mains != null) taskpane = application.CreateTaskpaneView3(mains, Title);
            if (taskpane == null)
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
            }
            if (taskpane == null) throw new InvalidOperationException("SOLIDWORKS did not create the task pane.");
            pane = new StatusPane(new PaneActions
            {
                OpenRobot = OpenRobot, Edit = Edit, Submit = Submit, CloseAndUpdate = CloseAndUpdate, InstallUpdate = InstallUpdate,
                Refresh = RefreshStatus, ReleaseUnchanged = ReleaseUnchangedLocks,
                Login = () =>
                {
                    try { return CredentialStore.Read(); }
                    catch (Exception) { return null; }
                },
                History = FileHistory, Diagnostics = CopyDiagnostics,
                OpenFile = OpenRobotFile, RevealFile = RevealRobotFile, FileHistoryOf = path => FileHistoryFor(path),
                InsertFrc = InsertFromFrcDesign, SearchTeam = SearchTeamLibrary, InsertTeam = path => InsertTeamPart(path),
                BrowseTeam = InsertFromLibrary, ImportDownloaded = InsertExternalPart,
            });
            pane.CreateControl();
            if (!taskpane.DisplayWindowFromHandlex64(pane.Handle.ToInt64()))
                throw new InvalidOperationException("SOLIDWORKS did not accept the task pane window.");
            application.ActiveModelDocChangeNotify += OnActiveDocumentChanged;
            watcher = new DocumentWatcher(application,
                path => path.StartsWith(WorkspaceInfo.BaseFolder + "\\", StringComparison.OrdinalIgnoreCase) && WorkspacePolicy.IsSubmittableCad(path),
                doc => OnUi("lock offer", () => OfferLock(doc)),
                path => OnUi("release on close", () => ReleaseIfUnchanged(path)),
                path => OnUi("saved", () => OnDocumentSaved(path)));
            // Saving changes what's waiting to submit: refresh shortly after, once per burst of saves (Save All).
            savedTimer = new Timer { Interval = 1000 };
            savedTimer.Tick += (s, e) => { savedTimer.Stop(); try { RefreshStatus(); } catch (Exception exception) { ErrorLog.Write("refresh after save", exception); } };
            flashTimer = new Timer { Interval = 15000 };
            flashTimer.Tick += (s, e) => { flashTimer.Stop(); flash = null; RenderStatus(); };
            // Just installed and never signed in: welcome the student and let them set up their own password.
            bool signedIn;
            try { signedIn = CredentialStore.Read() != null; }
            catch (Exception) { signedIn = true; }
            if (!signedIn) OnUi("first sign-in", () => Execute(() => SetUpAccount(null)));
            statusTimer = new Timer { Interval = 3 * 60 * 1000 };
            statusTimer.Tick += (s, e) => { try { RefreshStatus(); } catch (Exception exception) { ErrorLog.Write("status timer", exception); } };
            statusTimer.Start();
            RefreshStatus();
        }

        // Runs on SOLIDWORKS' UI thread, later. An error escaping there would close SOLIDWORKS, so it's logged instead.
        private void OnUi(string where, Action action)
        {
            try
            {
                if (pane == null || pane.IsDisposed) return;
                pane.BeginInvoke((Action)(() =>
                {
                    try { action(); }
                    catch (Exception exception) { ErrorLog.Write(where, exception); }
                }));
            }
            catch (Exception exception) { ErrorLog.Write(where, exception); }
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
                var state = PaneState.Describe(paneUser, robotSnapshot, librarySnapshot, path, doc != null && doc.IsOpenedReadOnly(), paneError, checkedAt,
                    doc != null && doc.GetSaveFlag(), robotSnapshot != null && RobotDocuments(robotSnapshot.Info).Any());
                state.Working = working;
                state.Warning = paneCatalog == null ? null : WorkspacePolicy.SolidWorksProblem(SolidWorksYear, paneCatalog.SolidWorks);
                if (state.Warning != null) state.EditTarget = null;
                if (autoUpdateProblem != null && state.CanAutoUpdate)
                    state.Details += "\nCouldn't get them automatically: " + autoUpdateProblem + "\nTry Tools → JOCO ROBOS CAD → Update.";
                var season = path == null || paneCatalog == null ? null : paneCatalog.Owning(path);
                if (season != null && !season.IsLibrary && robotSnapshot != null && season.Name != robotSnapshot.Info.Name)
                {
                    state.ActiveStatus = "Reference copy from " + season.Name + ". Read-only; your robot is " + robotSnapshot.Info.Name + ".";
                    state.ActiveTone = Tone.Muted;
                    state.EditTarget = null;
                }
                state.Flash = flash;
                if (state.CanAutoUpdate) pane.BeginInvoke((Action)(() => StartAutoUpdate()));
                state.Update = offeredUpdate == null ? null : "Add-in " + offeredUpdate.Version + " is available" + (offeredUpdate.Required ? " (required)" : "") + ".";
                pane.Show(state);
                pane.ShowRobotFiles(robotSnapshot ?? OfflineRobot());
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO status pane: " + exception); }
        }

        // Server unreachable before the first good check: the browser still lists what's on this computer (no statuses).
        private WorkspaceSnapshot OfflineRobot()
        {
            if (robotInfo == null || !Directory.Exists(Path.Combine(robotInfo.Root, ".svn"))) return null;
            if (robotOffline == null || robotOffline.Info != robotInfo) robotOffline = new WorkspaceSnapshot { Info = robotInfo, Local = -1 };
            return robotOffline;
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
            int year = SolidWorksYear; // SOLIDWORKS is only asked on its own thread.
            var started = DateTime.UtcNow;
            WorkspaceInfo knownRobot = null;
            Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var catalog = cached ?? Catalog.Fetch(login);
                knownRobot = catalog.Robot;
                if (!heartbeatSent) heartbeatSent = SendHeartbeat(login, year);
                var robot = new SvnWorkspace(login, catalog.Robot).Snapshot();
                var library = catalog.Library == null ? null : new SvnWorkspace(login, catalog.Library).Snapshot();
                ErrorLog.Slow("status check (background)", clock.ElapsedMilliseconds, 20000);
                return Tuple.Create(catalog, robot, library);
            }).ContinueWith(task =>
            {
                if (pane == null || pane.IsDisposed) return;
                OnUi("status refresh", () =>
                {
                    refreshing = false;
                    if (knownRobot != null) robotInfo = knownRobot;
                    // Only a check that started after the last automatic update ended knows what's still new.
                    if (started > autoUpdateEnded) awaitingFreshStatus = false;
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
                });
            });
        }

        // Lets mentors see which add-in version each student runs (Accounts tab). Best effort, once per session.
        private static bool SendHeartbeat(NetworkCredential login, int solidWorks)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(new Uri(WorkspaceInfo.Server, "admin/api/heartbeat"));
                request.Method = "POST";
                request.Timeout = 15000;
                request.ContentType = "application/json";
                request.UserAgent = "JOCO-ROBOS-CAD";
                request.Headers["X-Joco-Client"] = "addin";
                request.Headers[HttpRequestHeader.Authorization] = "Basic " +
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(login.UserName + ":" + login.Password));
                byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"version\": \"" + Updater.Current + "\", \"computer\": \"" +
                    System.Text.RegularExpressions.Regex.Replace(System.Environment.MachineName, "[^A-Za-z0-9._-]", "") + "\", \"solidworks\": \"" +
                    (solidWorks > 0 ? solidWorks.ToString() : "") + "\"}");
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (request.GetResponse()) { }
                return true;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine("JOCO heartbeat: " + exception.Message);
                return false;
            }
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
                (offer.Required ? "\nMentors marked it required: new edits and inserts need it (you can still Submit your current work)." : "") +
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
        // This computer's SOLIDWORKS as a year (2026), read once.
        private int solidWorksYear = -1;
        private int SolidWorksYear
        {
            get
            {
                if (solidWorksYear < 0)
                    try { solidWorksYear = WorkspacePolicy.SolidWorksYear(application.RevisionNumber()); }
                    catch (Exception) { solidWorksYear = 0; }
                return solidWorksYear;
            }
        }

        // Anything that changes team CAD (Edit, inserts, imports, Submit, Upgrade) goes through here.
        private void RequireTeamSolidWorks(Catalog catalog)
        {
            string problem = WorkspacePolicy.SolidWorksProblem(SolidWorksYear, catalog.SolidWorks);
            if (problem != null) throw new InvalidOperationException(problem);
        }

        private void RequireCurrentAddin(NetworkCredential login, Catalog catalog)
        {
            RequireTeamSolidWorks(catalog);
            var offer = Updater.Offer(catalog.Addin, Updater.Current);
            if (offer == null || !offer.Required) return;
            InstallUpdate(login, offer);
            throw new InvalidOperationException("JOCO ROBOS CAD " + offer.Version + " is required before you start new edits or inserts. " +
                "Your current work is safe: Submit, Set Aside, and Release Edit still work. Close SOLIDWORKS to finish installing the update.");
        }

        // ---------- sign-in and seasons ----------

        private NetworkCredential GetLogin(bool force)
        {
            NetworkCredential saved = CredentialStore.Read();
            if (saved != null && !force) return saved;
            using (var dialog = new SignInDialog(saved?.UserName))
            {
                var answer = dialog.ShowDialog(new SolidWorksWindow());
                if (answer == DialogResult.Yes) return SetUpAccount(saved);
                if (answer != DialogResult.OK) return null;
                NetworkCredential login = dialog.Login;
                // Lock tokens and unsubmitted edits belong to the account that made them.
                if (saved != null && !String.Equals(saved.UserName, login.UserName, StringComparison.OrdinalIgnoreCase))
                {
                    var work = new List<string>();
                    OperationDialog.Run("Checking this computer's unsubmitted work…", () =>
                    {
                        var catalog = Catalog.Fetch(saved);
                        foreach (var workspace in catalog.All) work.AddRange(new SvnWorkspace(saved, workspace).LocalWork());
                        return true;
                    });
                    if (work.Count > 0)
                        throw new InvalidOperationException("This Windows account has unsubmitted work from the CAD account '" + saved.UserName + "':\n\n" +
                            String.Join("\n", work.Take(8)) + "\n\nSubmit or Set Aside it while signed in as " + saved.UserName + " before switching accounts. " +
                            "Each student should use their own Windows account; on a shared PC each gets a separate robot folder.");
                }
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

        // First sign-in with a mentor's one-time setup code: the student chooses their own password.
        private NetworkCredential SetUpAccount(NetworkCredential saved)
        {
            string name = saved?.UserName ?? PendingUsername, presetCode = null;
            while (true)
            {
                SetupAccountDialog dialog;
                using (dialog = new SetupAccountDialog("Set Up Your Account",
                    "Welcome! Choose a username (lowercase, like sarah or j.smith) and a password, then click Send request. A mentor gives you a code: " +
                    "type it in and click Finish. (Got a code already, or a mentor reset your password? Fill everything in and click Finish.)", true, name,
                    (user, password) =>
                    {
                        OperationDialog.Run("Sending your request…", () => { Accounts.Request(user, password); return true; });
                        PendingUsername = user;
                    }, presetCode))
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return null;
                    name = dialog.Username;
                    presetCode = dialog.Code;
                    try { OperationDialog.Run("Setting up your account…", () => { Accounts.Setup(dialog.Username, dialog.Code, dialog.Password); return true; }); }
                    catch (InvalidOperationException problem)
                    {
                        // Wrong code or password: show why, then the same form again with what they typed (except the passwords).
                        Message(problem.Message, MessageBoxIcon.Warning);
                        continue;
                    }
                    PendingUsername = null;
                    return FinishSetup(new NetworkCredential(dialog.Username, dialog.Password));
                }
            }
        }

        // Remembered between SOLIDWORKS sessions so the form comes back with the name the student asked for.
        private static string PendingUsername
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\JOCO ROBOS\CAD"))
                    return key?.GetValue("PendingUsername") as string;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\JOCO ROBOS\CAD"))
                {
                    if (value == null) key.DeleteValue("PendingUsername", false);
                    else key.SetValue("PendingUsername", value, RegistryValueKind.String);
                }
            }
        }

        private NetworkCredential FinishSetup(NetworkCredential login)
        {
            OperationDialog.Run("Checking your CAD account…", () =>
            {
                var catalog = Catalog.Fetch(login);
                new SvnWorkspace(login, catalog.Robot).TestConnection();
                return true;
            });
            CredentialStore.Write(login);
            Message("You're set up as " + login.UserName + ". Your password is saved in Windows, so you won't need to type it again.\n\nClick Open Robot to get started.");
            return login;
        }

        public void ChangePassword()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                using (var dialog = new SetupAccountDialog("Change Password", "Choose a new password for " + login.UserName + ".", false, login.UserName))
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    OperationDialog.Run("Changing your password…", () => { Accounts.ChangePassword(login, dialog.Password); return true; });
                    CredentialStore.Write(new NetworkCredential(login.UserName, dialog.Password));
                }
                ShowFlash("✓ Password changed");
            });
        }

        private Catalog LoadCatalog(NetworkCredential login)
        {
            // The robot list changes rarely (a new season, a new add-in): reuse the one the panel fetched in the last two
            // minutes instead of making every click wait for the server first.
            var catalog = paneCatalog != null && paneUser == login.UserName && DateTime.UtcNow - paneCatalogAt < TimeSpan.FromMinutes(2)
                ? paneCatalog
                : OperationDialog.Run("Contacting the CAD server…", () => Catalog.Fetch(login));
            KeepUnfinishedSeason(login, catalog);
            return catalog;
        }

        // Mentors made a new season active, but this student still has unsubmitted work in the old one:
        // stay on the old season until it's submitted or set aside, so nothing is stranded.
        private void KeepUnfinishedSeason(NetworkCredential login, Catalog catalog)
        {
            if (Catalog.ChosenRobot != "") return;
            string previous;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\JOCO ROBOS\CAD"))
                previous = key?.GetValue("LastRobot") as string;
            var old = catalog.Robots.FirstOrDefault(r => r.Name == previous);
            if (old == null || old.Name == catalog.Robot.Name) return;
            var work = new SvnWorkspace(login, old).LocalWork();
            if (work.Count == 0) return;
            if (old.Archived)
            {
                // Can't be submitted any more; the files stay on disk untouched.
                Message(old.Name + " is archived, but this computer still has unsubmitted work in it:\n\n" + String.Join("\n", work.Take(8)) +
                    "\n\nThose files stay in " + old.Root + ". Ask a mentor to unarchive " + old.Name + " briefly so you can Submit them, " +
                    "or copy what you need into " + catalog.Active + ".", MessageBoxIcon.Warning);
                return;
            }
            Catalog.ChosenRobot = old.Name;
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\JOCO ROBOS\CAD"))
                key.SetValue("AutoPinned", 1, RegistryValueKind.DWord);
            Message("Mentors started " + catalog.Active + ", but you still have unfinished work in " + old.Name + ":\n\n" +
                String.Join("\n", work.Take(8)) + (work.Count > 8 ? "\n…" : "") + "\n\nYou'll stay on " + old.Name + " until you Submit or Set Aside it. " +
                "After that, Open Robot switches you to " + catalog.Active + " automatically.", MessageBoxIcon.Warning);
        }

        // After an automatic hold on an old season: once its work is done, follow the active season again.
        private void ReleaseSeasonHold(NetworkCredential login, Catalog catalog)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\JOCO ROBOS\CAD"))
                if (key == null || Convert.ToInt32(key.GetValue("AutoPinned", 0)) == 0) return;
            if (catalog.Robot.Name == catalog.Active || new SvnWorkspace(login, catalog.Robot).LocalWork().Count > 0) return;
            string finished = catalog.Robot.Name;
            Catalog.ChosenRobot = "";
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\JOCO ROBOS\CAD"))
                key.DeleteValue("AutoPinned", false);
            Message("Everything in " + finished + " is done. Open Robot will now switch you to " + catalog.Active + ".");
        }

        public void SignIn()
        {
            Execute(() => { if (GetLogin(true) != null) ShowFlash("✓ Signed in"); });
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
            string summary = robot.Name + " is up to date." + (robot.Archived ? " It is archived and read-only." : "");
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
                if (summary.StartsWith("Switched", StringComparison.Ordinal) || summary.Contains("not updated") || FindMaster(catalog.Robot) == null)
                    Message(summary + (FindMaster(catalog.Robot) != null ? "" : "\n\nNo master assembly has been uploaded to this robot yet."));
                else ShowFlash("✓ Up to date");
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
                    var component = components[0];
                    if (component.IsSuppressed())
                        throw new InvalidOperationException(Path.GetFileName(component.GetPathName()) + " is suppressed. Unsuppress it (or open the file), then click Edit.");
                    var part = component.GetModelDoc2() as ModelDoc2;
                    if (part == null)
                    {
                        // Lightweight: load it fully so it can be locked. This only changes what's loaded in memory.
                        component.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
                        part = component.GetModelDoc2() as ModelDoc2;
                    }
                    if (part == null)
                        throw new InvalidOperationException("SOLIDWORKS couldn't load " + Path.GetFileName(component.GetPathName()) + ". Open the file itself, then click Edit.");
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
            Execute(() => EditDocument(null, true));
        }

        // target null: the active document or selected component (Edit button). Otherwise a document the watcher saw change,
        // or one the Submit window locks. backup: save unsaved changes as a copy first. quiet: no message unless something needs attention.
        private void EditDocument(ModelDoc2 target, bool backup, Catalog known = null, bool quiet = false)
        {
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = known ?? LoadCatalog(login);
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
                string safety = unsaved && backup ? SaveSafetyCopy(doc, workspace) : null;
                var svn = new SvnWorkspace(login, workspace);
                try
                {
                    if (!unsaved && !doc.SetReadOnlyState(true)) throw new InvalidOperationException("SOLIDWORKS could not put the document in read-only mode.");
                    string owner = OperationDialog.Run("Checking it's the newest version and locking it for you…",
                        () => SvnWorkspace.Exclusive(() => svn.Edit(path)));
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    if (!doc.SetReadOnlyState(false) || doc.IsOpenedReadOnly())
                        throw new InvalidOperationException("The file is locked for you, but SOLIDWORKS couldn't make it editable. Close and reopen it, then click Edit again.");
                    bool component = !ReferenceEquals(doc, application.ActiveDoc);
                    string kept = !unsaved ? "" : doc.GetSaveFlag()
                        ? "\n\nYour earlier changes are still here. Save to keep them." + (safety != null ? " (A backup copy is in " + safety + ")" : "")
                        : safety != null ? "\n\nSOLIDWORKS reloaded the file, so your earlier changes aren't in this window. They are safe in:\n" + safety
                        : "\n\nSOLIDWORKS reloaded the file; redo your last change.";
                    // The usual case is quiet: the panel says it. A dialog only when changes were reloaded away or it's a component.
                    bool changesLost = unsaved && !doc.GetSaveFlag();
                    if (!changesLost && (quiet || !component))
                    {
                        ShowFlash("✎ You're editing " + Path.GetFileName(path) + (workspace.IsLibrary ? " (in the Library)" : ""));
                        return;
                    }
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

        private void ShowFlash(string text)
        {
            flash = text;
            if (flashTimer != null) { flashTimer.Stop(); flashTimer.Start(); }
            RenderStatus();
        }

        private void OnDocumentSaved(string path)
        {
            if (application == null) return;
            // Show the save in the panel right away; the server check a moment later confirms it.
            try
            {
                path = Path.GetFullPath(path);
                var snapshot = new[] { robotSnapshot, librarySnapshot }.FirstOrDefault(x => x != null && x.Info.Contains(path));
                if (snapshot != null && WorkspacePolicy.IsSubmittableCad(path))
                {
                    if (snapshot.Mine.Contains(path)) snapshot.Changed.Add(path);
                    else if (!snapshot.Locks.ContainsKey(path) && !snapshot.Changed.Contains(path) && CredentialStore.Read() is NetworkCredential login &&
                             new SvnWorkspace(login, snapshot.Info).IsNewFile(path))
                        snapshot.New.Add(path);
                    RenderStatus();
                }
            }
            catch (Exception exception) { ErrorLog.Write("saved: quick panel update", exception); }
            if (savedTimer != null) { savedTimer.Stop(); savedTimer.Start(); }
            try { WarnDuplicateName(path); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO duplicate check: " + exception); }
        }

        // Right after a new file is saved with the same name as a team file (Ctrl+S on a read-only file opens Save As,
        // which is the usual way this happens), rather than twenty minutes later at Submit. Never renames or deletes anything.
        private void WarnDuplicateName(string path)
        {
            path = Path.GetFullPath(path);
            var catalog = paneCatalog;
            if (catalog == null || !WorkspacePolicy.IsSubmittableCad(path) || warnedNames.Contains(path)) return;
            var workspace = new[] { catalog.Robot, catalog.Library }.FirstOrDefault(w => w != null && !w.Archived && w.Contains(path));
            if (workspace == null) return;
            var others = SubmitCheck.CadByName(workspace.Root)[Path.GetFileName(path)]
                .Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (others.Count == 0) return;
            // Two team files sharing a name is an older problem for a mentor; only newly created files get the warning.
            var login = CredentialStore.Read();
            if (login == null || !new SvnWorkspace(login, workspace).IsNewFile(path)) return;
            warnedNames.Add(path);
            string name = Path.GetFileName(path);
            Message("A team file named " + name + " already exists:\n   " + others[0].Substring(workspace.Root.Length + 1) +
                "\n\nYou just saved a new file with the same name:\n   " + path.Substring(workspace.Root.Length + 1) +
                "\n\nSOLIDWORKS can confuse files with identical names. Choose a unique name: File → Save As with a new name (for example " +
                Path.GetFileNameWithoutExtension(name) + "_2" + Path.GetExtension(name) + "), then delete this copy." +
                "\n\nIf you meant to change the team's file instead: close this one without saving, open the original, and click Edit.", MessageBoxIcon.Warning);
        }

        // ---------- automatic update ----------

        private IEnumerable<string> RobotDocuments(WorkspaceInfo robot)
        {
            return OpenDocuments().Select(d => d.GetPathName()).Where(p => !String.IsNullOrEmpty(p) && robot.Contains(p));
        }

        // Teammates' changes come in by themselves when nothing is in the way: no robot or Library documents open,
        // no unsubmitted work (Update never merges into it), and nothing else running. Otherwise the panel says what's needed.
        private void StartAutoUpdate()
        {
            try
            {
                if (busy || application == null || robotSnapshot == null || paneCatalog == null) return;
                // After an update (worked or not), wait for a fresh status check before deciding again: the old one still
                // lists what just came down. A failure is also retried at most once a minute.
                if (awaitingFreshStatus || DateTime.UtcNow < nextAutoUpdateRetry) return;
                var robot = robotSnapshot.Info;
                var library = paneCatalog.Library;
                if (RobotDocuments(robot).Any() || (library != null && RobotDocuments(library).Any())) return;
                var login = CredentialStore.Read();
                if (login == null) return;
                // Each one only when it has news and no unsubmitted work of its own (Update never merges into work).
                bool robotToo = robotSnapshot.Incoming.Count > 0 && robotSnapshot.Changed.Count + robotSnapshot.New.Count == 0;
                bool libraryToo = library != null && librarySnapshot != null && librarySnapshot.Local > 0 && librarySnapshot.Incoming.Count > 0 &&
                    librarySnapshot.Changed.Count + librarySnapshot.New.Count == 0;
                if (!robotToo && !libraryToo) return;
                int count = (robotToo ? robotSnapshot.Incoming.Count : 0) + (libraryToo ? librarySnapshot.Incoming.Count : 0);
                awaitingFreshStatus = true;
                busy = true;
                working = "Getting " + count + (count == 1 ? " new change…" : " new changes…");
                RenderStatus();
                var workspaces = new[] { robotToo ? robot : null, libraryToo ? library : null }.Where(w => w != null && !w.Archived)
                    .Select(w => new SvnWorkspace(login, w)).Where(w => w.IsCheckedOut).ToList();
                Task.Run(() => SvnWorkspace.Exclusive(() => { foreach (var svn in workspaces) svn.Update(); return true; })).ContinueWith(task => OnUi("auto update", () =>
                {
                    busy = false;
                    working = null;
                    autoUpdateEnded = DateTime.UtcNow;
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        autoUpdateProblem = null;
                        ShowFlash("✓ Got " + count + (count == 1 ? " new change" : " new changes"));
                    }
                    else
                    {
                        nextAutoUpdateRetry = DateTime.UtcNow.AddSeconds(60);
                        autoUpdateProblem = task.Exception?.GetBaseException().Message ?? "unknown error";
                        ErrorLog.Write("auto update", task.Exception?.GetBaseException() ?? new Exception(autoUpdateProblem));
                    }
                    OfferPendingLocks();
                    RefreshStatus();
                }));
            }
            catch (Exception exception)
            {
                busy = false;
                working = null;
                awaitingFreshStatus = false;
                ErrorLog.Write("auto update start", exception);
            }
        }

        // The panel's "Close & Update": closes the robot's documents (never throwing away saved-able work), gets
        // teammates' changes, and reopens what was on screen.
        private void CloseAndUpdate()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                var mine = new[] { catalog.Robot, catalog.Library }.Where(w => w != null).ToList();
                var docs = OpenDocuments().Where(d => !String.IsNullOrEmpty(d.GetPathName()) && mine.Any(w => w.Contains(d.GetPathName()))).ToList();
                var unsaved = docs.Where(d => d.GetSaveFlag() && !d.IsOpenedReadOnly()).Select(d => Path.GetFileName(d.GetPathName())).ToList();
                if (unsaved.Count > 0)
                    throw new InvalidOperationException("Save these first (they have changes you can keep):\n\n" + String.Join("\n", unsaved.Take(10)));
                // Read-only documents (assemblies too: an assembly edit can be real, not just a rebuild) can't be saved into the robot.
                // Closing discards those changes, so warn, and keep a copy of each in Set Aside first.
                var dirty = docs.Where(d => d.GetSaveFlag() && d.IsOpenedReadOnly()).ToList();
                if (dirty.Count > 0)
                {
                    if (MessageBox.Show(new SolidWorksWindow(), "These read-only files have unsaved changes (for an assembly this can also be just a rebuild):\n\n" +
                        String.Join("\n", dirty.Take(10).Select(d => Path.GetFileName(d.GetPathName()))) + (dirty.Count > 10 ? "\n…" : "") +
                        "\n\nThey can't be saved into the robot, so a copy of each goes to " + Path.Combine(WorkspaceInfo.BaseFolder, "Set Aside") +
                        " before closing. Close them and get teammates' changes?", Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                        return;
                    foreach (var doc in dirty)
                        SaveSafetyCopy(doc, mine.First(w => w.Contains(doc.GetPathName())));
                }
                // Reopen afterwards what had its own window (not the parts loaded inside an assembly), in the same order,
                // and end on the one that was active.
                var active = application.ActiveDoc as ModelDoc2;
                string activePath = active == null ? null : active.GetPathName();
                var reopen = docs.Where(d => d.Visible).Select(d => d.GetPathName())
                    .OrderBy(p => String.Equals(p, activePath, StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
                foreach (var doc in docs) application.CloseDoc(doc.GetTitle());
                var left = OpenDocuments().Select(d => d.GetPathName()).Where(p => !String.IsNullOrEmpty(p) && mine.Any(w => w.Contains(p))).ToList();
                if (left.Count > 0)
                    throw new InvalidOperationException("These are still open (probably used by another open document). Close that too, then try again:\n\n" +
                        String.Join("\n", left.Take(10).Select(Path.GetFileName)));
                string summary = UpdateAll(login, catalog);
                if (summary.StartsWith("Switched", StringComparison.Ordinal) || summary.Contains("not updated")) Message(summary);
                else ShowFlash("✓ Up to date");
                var missing = new List<string>();
                foreach (string path in reopen)
                {
                    // A teammate may have removed or renamed it; say so instead of failing.
                    if (!File.Exists(path)) { missing.Add(Path.GetFileName(path)); continue; }
                    int errors = 0, warnings = 0;
                    int type = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY
                        : path.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocDRAWING : (int)swDocumentTypes_e.swDocPART;
                    if (application.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) == null)
                        missing.Add(Path.GetFileName(path));
                }
                if (missing.Count > 0)
                    Message("Updated, but these couldn't be reopened:\n\n" + String.Join("\n", missing.Take(10)) + "\n\nUse File → Open if you still need them.", MessageBoxIcon.Warning);
            });
        }

        // The Library tab's team results: local files, quick enough to search as the student types.
        private List<string> SearchTeamLibrary(string query)
        {
            var library = paneCatalog?.Library;
            if (library == null || !Directory.Exists(library.Root)) return new List<string>();
            string frc = Path.Combine(library.Root, "FRCDesignLib") + "\\"; // FRCDesignLib parts come from its own search.
            var words = query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return SubmitCheck.CadByName(library.Root).SelectMany(g => g)
                .Where(p => !p.StartsWith(frc, StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
                .Where(p => { string relative = p.Substring(library.Root.Length + 1).ToLowerInvariant(); return words.All(relative.Contains); })
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        }

        // ---------- file history and diagnostics ----------

        public void FileHistory()
        {
            FileHistoryFor(null);
        }

        // file: a robot file from the Robot tab's browser; null: the active document (or selected component).
        private void FileHistoryFor(string file)
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                WorkspaceInfo workspace;
                string path;
                if (file == null) path = Path.GetFullPath(ActiveCad(catalog, out workspace).GetPathName());
                else
                {
                    path = Path.GetFullPath(file);
                    workspace = catalog.Owning(path);
                    if (workspace == null) throw new InvalidOperationException(Path.GetFileName(path) + " isn't in a robot folder.");
                }
                var svn = new SvnWorkspace(login, workspace);
                var versions = OperationDialog.Run("Reading the history of " + Path.GetFileName(path) + "…", () => svn.History(path, 30));
                if (versions.Count == 0) { Message(Path.GetFileName(path) + " is new: it has no team history yet."); return; }
                using (var dialog = new HistoryDialog(Path.GetFileName(path), versions, version =>
                {
                    string name = Path.GetFileNameWithoutExtension(path) + " (version " + version.Revision + ")" + Path.GetExtension(path);
                    using (var save = new SaveFileDialog { Title = "Save an older version as a copy", FileName = name,
                        InitialDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory),
                        Filter = "SOLIDWORKS file|*" + Path.GetExtension(path) })
                    {
                        if (save.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                        string target = Path.GetFullPath(save.FileName);
                        // A copy inside the robot would become a second file with a clashing name at the next Submit.
                        if (target.StartsWith(WorkspaceInfo.BaseFolder + "\\", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Save the copy outside " + WorkspaceInfo.BaseFolder + " (for example on the Desktop), so it doesn't become part of the robot.");
                        OperationDialog.Run("Saving version " + version.Revision + "…", () => { svn.SaveVersion(path, version.Revision, target); return true; });
                        ShowFlash("✓ Saved version " + version.Revision + " of " + Path.GetFileName(path) + " as a copy");
                        System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + target + "\"");
                    }
                }))
                    dialog.ShowDialog(new SolidWorksWindow());
            });
        }

        // Everything a mentor needs to help, in one report the student can paste or send. No passwords, codes, or tokens:
        // the report never reads Credential Manager beyond the username, and Diagnostics.Sanitize scrubs the rest.
        public void CopyDiagnostics()
        {
            Execute(() =>
            {
                var text = new System.Text.StringBuilder();
                Action<string, object> line = (label, value) => text.Append(label).Append(": ").Append(value).Append("\r\n");
                text.Append("JOCO ROBOS CAD diagnostics — ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")).Append("\r\n\r\n");
                line("Add-in", Updater.Current + (Updater.IsDevelopmentBuild ? " (dev build)" : ""));
                string revision;
                try { revision = application.RevisionNumber(); } catch (Exception exception) { revision = "unknown (" + exception.Message + ")"; }
                line("SOLIDWORKS", revision + (SolidWorksYear > 0 ? " (" + SolidWorksYear + ")" : ""));
                line("Windows", System.Environment.OSVersion.VersionString + (System.Environment.Is64BitOperatingSystem ? " 64-bit" : ""));
                line(".NET", System.Environment.Version);
                line("Computer", System.Environment.MachineName);
                line("Windows user", System.Environment.UserName);
                NetworkCredential login = null;
                try { login = CredentialStore.Read(); } catch (Exception exception) { line("Saved sign-in", "unreadable: " + exception.Message); }
                line("CAD username", login?.UserName ?? "(not signed in)");
                line("Robot folder", WorkspaceInfo.BaseFolder);
                line("Server", WorkspaceInfo.Server);
                if (login != null)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var catalog = OperationDialog.Run("Checking the server…", () => Catalog.Fetch(login));
                        line("Server reachable", "yes, " + clock.ElapsedMilliseconds + " ms");
                        line("Active season", catalog.Active);
                        line("This computer uses", catalog.Robot.Name + (Catalog.ChosenRobot != "" ? " (chosen with Choose Robot)" : ""));
                        line("Team SOLIDWORKS", catalog.SolidWorks ?? "(not set)");
                        line("Published add-in", catalog.Addin == null ? "(none)" : catalog.Addin.Version + (catalog.Addin.Required ? " (required)" : ""));
                    }
                    catch (Exception exception) { line("Server reachable", "NO after " + clock.ElapsedMilliseconds + " ms: " + exception.Message); }
                }
                line("Panel status error", paneError ?? "(none)");
                line("Automatic update", working ?? (autoUpdateProblem != null ? "failed: " + autoUpdateProblem : "ok"));
                line("Last status check", checkedAt == default(DateTime) ? "(none)" : checkedAt.ToString("HH:mm:ss"));
                foreach (var snapshot in new[] { robotSnapshot, librarySnapshot }.Where(x => x != null))
                {
                    text.Append("\r\n[").Append(snapshot.Info.Label).Append("] ").Append(snapshot.Info.Root).Append("\r\n");
                    line("  On this computer / on the server", snapshot.Local + " / " + snapshot.Head);
                    line("  New changes on the server", snapshot.Incoming.Count + (snapshot.Incoming.Count > 0 ? ": " + String.Join(" | ", snapshot.Incoming.Take(5)) : ""));
                    line("  Changed here, not submitted", snapshot.Changed.Count + ": " + String.Join(", ", snapshot.Changed.Take(15).Select(Path.GetFileName)));
                    line("  New here, not submitted", snapshot.New.Count + ": " + String.Join(", ", snapshot.New.Take(15).Select(Path.GetFileName)));
                    line("  Editing (locked by this computer)", snapshot.Mine.Count + ": " + String.Join(", ", snapshot.Mine.Take(15).Select(Path.GetFileName)));
                    line("  Locked by anyone", snapshot.Locks.Count + ": " + String.Join(", ", snapshot.Locks.Take(15).Select(p => Path.GetFileName(p.Key) + " (" + p.Value + ")")));
                    line("  Interrupted Submit waiting", snapshot.PendingSubmit ? "yes" : "no");
                }
                try
                {
                    var docs = OpenDocuments().Where(d => d.Visible).Select(d => Path.GetFileName(d.GetPathName()) + (d.IsOpenedReadOnly() ? " (read-only)" : "") + (d.GetSaveFlag() ? " (unsaved)" : "")).ToList();
                    text.Append("\r\nOpen windows (").Append(docs.Count).Append("): ").Append(String.Join(", ", docs.Take(15))).Append("\r\n");
                }
                catch (Exception exception) { line("Open windows", "unreadable: " + exception.Message); }
                foreach (var log in new[] { ErrorLog.FilePath, Path.Combine(Path.GetDirectoryName(ErrorLog.FilePath), "upgrade.log") })
                {
                    text.Append("\r\n--- ").Append(Path.GetFileName(log)).Append(" (latest) ---\r\n");
                    try
                    {
                        var lines = File.Exists(log) ? File.ReadAllLines(log) : new string[0];
                        text.Append(lines.Length == 0 ? "(empty)" : String.Join("\r\n", lines.Skip(Math.Max(0, lines.Length - 80)))).Append("\r\n");
                    }
                    catch (Exception exception) { text.Append("unreadable: ").Append(exception.Message).Append("\r\n"); }
                }
                string report = Diagnostics.Sanitize(text.ToString());
                string folder = Path.Combine(Path.GetDirectoryName(ErrorLog.FilePath), "diagnostics");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "JOCO-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(file, report);
                try { Clipboard.SetText(report); } catch (Exception exception) { ErrorLog.Write("diagnostics clipboard", exception); }
                ShowFlash("✓ Diagnostics copied. Paste them to a mentor (also saved as a file).");
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\"");
            });
        }

        // ---------- Robot tab file browser ----------

        // Opens a robot file's local copy, exactly like File → Open: no lock, no Edit, no update. Only files of the robot
        // the panel shows, and only SOLIDWORKS files that exist.
        private void OpenRobotFile(string path)
        {
            try
            {
                var robot = robotSnapshot?.Info;
                if (robot == null || !WorkspacePolicy.IsRobotFile(robot.Root, path))
                    throw new InvalidOperationException(Path.GetFileName(path) + " isn't a SOLIDWORKS file in " + (robot?.Name ?? "the robot") + ".");
                path = Path.GetFullPath(path);
                if (!File.Exists(path)) throw new InvalidOperationException(Path.GetFileName(path) + " isn't there any more (a teammate may have moved it). Click Refresh.");
                if (busy) { ShowFlash("JOCO is busy for a moment (getting changes or checking): try again in a few seconds."); return; }
                var open = FindOpen(path);
                int errors = 0, warnings = 0;
                if (open != null)
                {
                    application.ActivateDoc3(open.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                    return;
                }
                int type = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY
                    : path.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocDRAWING : (int)swDocumentTypes_e.swDocPART;
                if (application.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) == null)
                    throw new InvalidOperationException("SOLIDWORKS couldn't open " + Path.GetFileName(path) + " (error " + errors + ").");
            }
            catch (Exception exception)
            {
                ErrorLog.Write("open robot file", exception);
                Message(exception.Message, MessageBoxIcon.Warning);
            }
        }

        private void RevealRobotFile(string path)
        {
            try
            {
                var robot = robotSnapshot?.Info;
                if (robot != null && WorkspacePolicy.IsRobotFile(robot.Root, path) && File.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Path.GetFullPath(path) + "\"");
            }
            catch (Exception exception) { ErrorLog.Write("show robot file", exception); }
        }

        public void ShowLibrary()
        {
            try
            {
                if (taskpane == null || pane == null) { InsertFromLibrary(); return; }
                taskpane.ShowView();
                pane.ShowLibrary();
            }
            catch (Exception exception) { ErrorLog.Write("show library", exception); }
        }

        // ---------- lock on first change, release on close ----------

        // A read-only team file was just changed (even if it was opened with File → Open): offer to lock it now,
        // before the student spends time on changes they could not save.
        private void OfferLock(ModelDoc2 doc)
        {
            if (application == null) return;
            // Busy (a command, the Submit window's checks, an automatic update): ask as soon as it's done, not never.
            if (busy)
            {
                if (!pendingLockOffers.Any(d => ReferenceEquals(d, doc))) pendingLockOffers.Add(doc);
                return;
            }
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
            RenderStatus();
            if (owner != null && owner == paneUser)
            {
                Message("You locked " + name + " from another computer, so changes here can't be saved into the robot.\n\n" +
                    "Submit it from that computer, or ask a mentor to release the lock.", MessageBoxIcon.Warning);
                return;
            }
            if (owner != null)
            {
                Message(owner + " is editing " + name + ", so your changes can't be saved into the robot.\n\n" +
                    "Undo them (Ctrl+Z), or use File → Save As to keep a copy outside the robot folder and show it to " + owner + ".", MessageBoxIcon.Warning);
                return;
            }
            // Parts and drawings: free and (Edit checks) current, so lock it now, the same as clicking Edit. Closing it unchanged
            // gives the lock back by itself; if the lock can't be taken, Edit says why and the change stays unsaved.
            // Assemblies still ask: rebuilding can mark them changed by itself, and quietly locking a big assembly blocks everyone.
            if (doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY &&
                MessageBox.Show(new SolidWorksWindow(), "You're changing the assembly " + name + ", which is read-only until you lock it.\n\nLock it for editing now? " +
                "Your change is kept, and nobody else can edit it until you Submit.", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                RenderStatus(); // The pane keeps showing the unsaved changes until they lock or undo.
                return;
            }
            Execute(() => EditDocument(doc, false, null, true));
        }

        // Closing a locked file without saving changes gives it back, so forgotten locks don't block teammates.
        private void ReleaseIfUnchanged(string path)
        {
            // While a command runs (Upgrade Robot Files opens and closes hundreds of locked files), locks stay put.
            if (application == null || busy) return;
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
                OnUi("release on close", () =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) RefreshStatus();
                });
            });
        }

        // Saves the in-memory document as a copy under C:\JOCO-ROBOS\Set Aside without changing what SOLIDWORKS has open.
        private string SaveSafetyCopy(ModelDoc2 doc, WorkspaceInfo workspace)
        {
            string path = doc.GetPathName();
            string copy = Path.Combine(WorkspacePolicy.UniqueFolder(Path.Combine(WorkspaceInfo.BaseFolder, "Set Aside"), DateTime.Now), workspace.Name,
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
                using (var dialog = new ChecklistDialog("Set Aside My Changes",
                    "Checked files are copied to " + Path.Combine(WorkspaceInfo.BaseFolder, "Set Aside") + ", then replaced with the team's version.\n" +
                    "Use this when someone else is editing a file you changed, or to undo changes while keeping a copy.", "Set aside", candidates, false))
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
                ReleaseSeasonHold(login, catalog);
            });
        }

        // One click from the panel: give back every lock on a file you didn't change, without opening anything.
        public void ReleaseUnchangedLocks()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                var workspaces = new[] { catalog.Robot, catalog.Library }.Where(w => w != null && !w.Archived)
                    .Select(w => new SvnWorkspace(login, w)).Where(w => w.IsCheckedOut).ToList();
                var released = OperationDialog.Run("Releasing your unchanged files…", () =>
                    SvnWorkspace.Exclusive(() => workspaces.SelectMany(w => w.ReleaseUnchangedLocks()).ToList()));
                var done = new HashSet<string>(released, StringComparer.OrdinalIgnoreCase);
                foreach (var doc in OpenDocuments().Where(d => done.Contains(d.GetPathName() ?? "") && !d.GetSaveFlag()))
                    doc.SetReadOnlyState(true);
                ShowFlash(released.Count == 0 ? "Nothing to give back: files you changed stay yours until you Submit."
                    : "✓ Gave back " + released.Count + (released.Count == 1 ? " file" : " files") + ". Files you changed stay yours until you Submit.");
            });
        }

        public void RestoreDeletedFiles()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                var workspaces = new[] { catalog.Robot, catalog.Library }
                    .Where(w => w != null && !w.Archived).Select(w => new SvnWorkspace(login, w)).Where(w => w.IsCheckedOut).ToList();
                var missing = new List<SubmitItem>();
                OperationDialog.Run("Looking for deleted files…", () => SvnWorkspace.Exclusive(() =>
                {
                    foreach (var plan in workspaces.Select(w => w.PrepareSubmit())) missing.AddRange(plan.Restore);
                    return true;
                }));
                if (missing.Count == 0) { Message("No team files are missing on this computer."); return; }
                List<SubmitItem> chosen;
                using (var dialog = new ChecklistDialog("Restore Deleted Files",
                    "These team files are missing or deleted on this computer. Checked files are restored from the server.\n" +
                    "Renaming or removing team CAD on purpose is a mentor task.", "Restore", missing, true))
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    chosen = dialog.Selected;
                }
                int restored = 0;
                foreach (var svn in workspaces.Where(w => chosen.Any(c => c.Workspace.Name == w.Info.Name)))
                    restored += OperationDialog.Run("Restoring…", () => SvnWorkspace.Exclusive(() => svn.RestoreDeleted(chosen)));
                ShowFlash("✓ Restored " + restored + (restored == 1 ? " file" : " files") + " from the team");
            });
        }

        // One reminder when SOLIDWORKS closes with unsubmitted work. Never submits by itself.
        private int OnSolidWorksClosing()
        {
            try
            {
                var login = CredentialStore.Read();
                var catalog = paneCatalog;
                if (busy || login == null || catalog == null) return 0;
                var work = new[] { catalog.Robot, catalog.Library }.Where(w => w != null && !w.Archived)
                    .SelectMany(w => new SvnWorkspace(login, w).LocalWork().Where(x => !x.EndsWith("(missing)")).Select(x => w.Label + ": " + x)).ToList();
                if (work.Count == 0) return 0;
                if (MessageBox.Show(new SolidWorksWindow(), "You still have unsubmitted team CAD:\n\n" + String.Join("\n", work.Take(10)) +
                    (work.Count > 10 ? "\n…" : "") + "\n\nYour locks stay until you Submit, which can block teammates.\n\nSubmit now before exiting?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    Execute(() => OpenSubmit(true));
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO closing check: " + exception); }
            return 0;
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
                ShowFlash("✓ Gave back " + Path.GetFileName(path) + "; it's read-only again");
            });
        }

        // ---------- submit ----------

        public void Submit()
        {
            Execute(() => OpenSubmit(false));
        }

        // One Submit window at a time. Modeless, so the student can save or rename in SOLIDWORKS and come back;
        // modal only while SOLIDWORKS is closing.
        private void OpenSubmit(bool modal)
        {
            if (submitWindow != null && !submitWindow.IsDisposed)
            {
                if (!modal) { submitWindow.Activate(); return; }
                submitWindow.Close();
            }
            var login = GetLogin(false);
            if (login == null) return;
            var catalog = LoadCatalog(login);
            // No required-update check here: an outdated add-in can always finish (Submit) the work it already has.
            // But never from a SOLIDWORKS version the rest of the team can't open.
            RequireTeamSolidWorks(catalog);
            var workspaces = new[] { catalog.Robot, catalog.Library }
                .Where(w => w != null && !w.Archived).Select(w => new SvnWorkspace(login, w)).Where(w => w.IsCheckedOut).ToList();
            if (workspaces.Count == 0) throw new InvalidOperationException("Click Open Robot first to download the robot.");
            var infos = workspaces.Select(w => w.Info).ToList();
            bool wasBusy = false;
            var host = new SubmitHost
            {
                Target = catalog.Robot.Name,
                Scan = () => Task.Run(() => SvnWorkspace.Exclusive(() =>
                {
                    var plan = new SubmitPlan();
                    foreach (var svn in workspaces) plan.Add(svn.PrepareSubmit());
                    return plan;
                })),
                Check = (plan, selected, acknowledged) =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var issues = SubmitCheck.Run(new SubmitCheckInput
                    {
                        Plan = plan, Selected = selected, Workspaces = infos, Documents = DocumentStates(), References = CachedDependencies,
                        Owning = catalog.Owning, LockedBy = KnownLocks(), User = login.UserName, Acknowledged = acknowledged,
                    });
                    if (clock.ElapsedMilliseconds > 3000)
                        ErrorLog.Write("slow Submit check (" + clock.ElapsedMilliseconds + " ms)", new TimeoutException(selected.Count + " files checked, " + plan.Items.Count + " listed"));
                    return issues;
                },
                Prepare = async (paths, progress) =>
                {
                    var todo = paths.Where(p => !DependenciesCached(p)).ToList();
                    for (int i = 0; i < todo.Count; i++)
                    {
                        if (!progress(i + 1, todo.Count)) return;
                        CachedDependencies(todo[i]);
                        await Task.Yield(); // Let SOLIDWORKS repaint and handle clicks between files.
                    }
                },
                Fix = (issue, action) => FixSubmitIssue(issue, action, catalog, workspaces),
                Commit = (selected, comment) => CommitSubmit(selected, comment, workspaces),
                Busy = value =>
                {
                    if (value) { wasBusy = busy; busy = true; }
                    else { busy = wasBusy; RenderStatus(); OfferPendingLocks(); }
                },
            };
            var window = new SubmitWindow(host, submitDraft);
            window.FormClosed += (s, e) =>
            {
                if (submitWindow == window) submitWindow = null;
                try { SubmitFinished(window.Outcome, login, catalog); }
                catch (Exception exception) { Message(exception.Message, MessageBoxIcon.Error); }
            };
            submitWindow = window;
            if (modal) using (window) window.ShowDialog(new SolidWorksWindow());
            else window.Show(new SolidWorksWindow());
        }

        private void SubmitFinished(SubmitOutcome outcome, NetworkCredential login, Catalog catalog)
        {
            if (outcome == null) return;
            // Normal success is quiet: the pane says so. Only warnings need a dialog.
            ShowFlash(outcome.Summary);
            if (outcome.Warnings.Count > 0) Message(outcome.Summary + "\n\n" + String.Join("\n\n", outcome.Warnings), MessageBoxIcon.Warning);
            ReleaseSeasonHold(login, catalog);
            RefreshStatus();
        }

        private List<OpenDocument> DocumentStates()
        {
            var states = new List<OpenDocument>();
            foreach (var doc in OpenDocuments())
            {
                string path = doc.GetPathName();
                // Virtual components ("Part1^Shooter") are saved inside their assembly.
                if (!String.IsNullOrEmpty(path) && Path.GetFileName(path).Contains("^")) continue;
                states.Add(new OpenDocument { Path = String.IsNullOrEmpty(path) ? null : Path.GetFullPath(path), Title = doc.GetTitle(),
                    Dirty = doc.GetSaveFlag(), ReadOnly = doc.IsOpenedReadOnly() });
            }
            return states;
        }

        // Reading references is the slow part of the checks, and it runs on SOLIDWORKS' own thread (SOLIDWORKS is frozen
        // meanwhile). Only each file's direct references are read: deeper ones belong to files that are either in this Submit
        // (checked themselves) or unchanged team files. Cached until the file is saved again. Slow reads are logged.
        private readonly Dictionary<string, Tuple<DateTime, List<string>>> dependencyCache =
            new Dictionary<string, Tuple<DateTime, List<string>>>(StringComparer.OrdinalIgnoreCase);

        private bool DependenciesCached(string path)
        {
            Tuple<DateTime, List<string>> cached;
            return dependencyCache.TryGetValue(path, out cached) && cached.Item1 == (File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue);
        }

        private IEnumerable<string> CachedDependencies(string path)
        {
            DateTime written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            Tuple<DateTime, List<string>> cached;
            if (dependencyCache.TryGetValue(path, out cached) && cached.Item1 == written) return cached.Item2;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var list = new List<string>();
            var raw = application.GetDocumentDependencies2(path, false, false, false) as object[];
            for (int i = 1; raw != null && i < raw.Length; i += 2)
            {
                string reference = raw[i] as string;
                if (String.IsNullOrEmpty(reference)) continue;
                try { list.Add(Path.GetFullPath(reference)); } catch (ArgumentException) { list.Add(reference); }
            }
            if (clock.ElapsedMilliseconds > 1500)
                ErrorLog.Write("slow references (" + clock.ElapsedMilliseconds + " ms)", new TimeoutException(path + " has " + list.Count + " direct references"));
            dependencyCache[path] = Tuple.Create(written, list);
            return list;
        }

        // From the last status check; SvnWorkspace.Submit asks the server again when it commits.
        private Dictionary<string, string> KnownLocks()
        {
            var locks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in new[] { robotSnapshot, librarySnapshot }.Where(x => x != null))
                foreach (var pair in snapshot.Locks) locks[pair.Key] = pair.Value;
            return locks;
        }

        private ModelDoc2 FindOpen(string path)
        {
            return OpenDocuments().FirstOrDefault(d => !String.IsNullOrEmpty(d.GetPathName()) &&
                String.Equals(Path.GetFullPath(d.GetPathName()), path, StringComparison.OrdinalIgnoreCase));
        }

        // The Submit window's fix buttons, reusing the same code as the commands.
        private string FixSubmitIssue(SubmitIssue issue, IssueAction action, Catalog catalog, List<SvnWorkspace> workspaces)
        {
            switch (action)
            {
                case IssueAction.SaveDocuments:
                    SaveTeamDocuments(issue.Files, workspaces.Select(w => w.Info).ToList());
                    return null;
                case IssueAction.LockFile:
                {
                    var doc = FindOpen(issue.Files[0]);
                    if (doc == null) throw new InvalidOperationException(Path.GetFileName(issue.Files[0]) + " isn't open any more.");
                    EditDocument(doc, true, catalog, true);
                    return null;
                }
                case IssueAction.ImportIntoRobot:
                    return ImportIntoRobot(issue.Files[0], catalog, workspaces.Select(w => w.Info).ToList());
                case IssueAction.RestoreFiles:
                {
                    int restored = 0;
                    foreach (var svn in workspaces.Where(w => issue.Items.Any(x => x.Workspace.Name == w.Info.Name)))
                        restored += SvnWorkspace.Exclusive(() => svn.RestoreDeleted(issue.Items));
                    return "Restored " + restored + (restored == 1 ? " file" : " files") + " from the server.";
                }
                default:
                    return null;
            }
        }

        // Saves only what the student asked for, and only writable robot or Library documents that still have
        // unsaved changes. Never Save All: rebuilds dirty unrelated files, and read-only team files must be locked first.
        private void SaveTeamDocuments(IEnumerable<string> paths, IList<WorkspaceInfo> workspaces)
        {
            var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var failed = new List<string>();
            foreach (var doc in OpenDocuments().ToList())
            {
                string path = doc.GetPathName();
                if (String.IsNullOrEmpty(path)) continue;
                path = Path.GetFullPath(path);
                if (!wanted.Contains(path) || !doc.GetSaveFlag() || doc.IsOpenedReadOnly() || !workspaces.Any(w => w.Contains(path))) continue;
                int errors = 0, warnings = 0;
                if (!doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                    failed.Add(Path.GetFileName(path) + " (SOLIDWORKS error " + errors + ")");
            }
            if (failed.Count > 0)
                throw new InvalidOperationException("SOLIDWORKS couldn't save:\n" + String.Join("\n", failed) +
                    "\n\nSave it yourself with File → Save. This window checks again when you come back to it.");
        }

        // Submit would lock and save this assembly anyway, so the Import button does both before importing.
        private string ImportIntoRobot(string assembly, Catalog catalog, IList<WorkspaceInfo> workspaces)
        {
            if (!catalog.Robot.Contains(assembly)) throw new InvalidOperationException("Only " + catalog.Robot.Name + " assemblies can import outside files.");
            var doc = FindOpen(assembly);
            if (doc == null)
            {
                int errors = 0, warnings = 0;
                doc = application.OpenDoc6(assembly, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                if (doc == null) throw new InvalidOperationException("SOLIDWORKS couldn't open " + Path.GetFileName(assembly) + " (error " + errors + ").");
            }
            if (doc.IsOpenedReadOnly()) EditDocument(doc, true, catalog, true);
            if (doc.IsOpenedReadOnly()) throw new InvalidOperationException(Path.GetFileName(assembly) + " is still read-only. Click Edit on it, then try again.");
            if (doc.GetSaveFlag()) SaveTeamDocuments(new[] { assembly }, workspaces);
            return ImportOutsideReferencesOf(doc, catalog);
        }

        private async Task<SubmitOutcome> CommitSubmit(List<SubmitItem> selected, string comment, List<SvnWorkspace> workspaces)
        {
            var outcome = await Task.Run(() =>
            {
                var result = new SubmitOutcome();
                // One revision per repository. The library goes last so a robot failure stops before it.
                foreach (var svn in workspaces.Where(w => selected.Any(x => x.Workspace.Name == w.Info.Name)).OrderBy(w => w.Info.IsLibrary))
                {
                    var mine = selected.Where(x => x.Workspace.Name == svn.Info.Name).ToList();
                    try { result.Record(svn.Info, mine, SvnWorkspace.Exclusive(() => svn.Submit(mine, comment))); }
                    catch (Exception exception)
                    {
                        result.Error = (result.Done.Count > 0 ? result.Summary + ".\n\n" : "") + svn.Info.Label + " was not submitted:\n" + exception.Message;
                        break;
                    }
                }
                return result;
            });
            // Submitted files are no longer locked; stop SOLIDWORKS from saving over them.
            var done = new HashSet<string>(outcome.Done, StringComparer.OrdinalIgnoreCase);
            foreach (var doc in OpenDocuments().Where(d => done.Contains(d.GetPathName() ?? "")))
                doc.SetReadOnlyState(true);
            return outcome;
        }

        private static string Resolve(string reference, string referencing, ILookup<string, string> index)
        {
            return SubmitCheck.Resolve(reference, referencing, index, Path.GetTempPath());
        }

        private static ILookup<string, string> CadByName(string root)
        {
            return SubmitCheck.CadByName(root);
        }

        // ---------- library ----------

        public void InsertFromLibrary()
        {
            InsertTeamPart(null);
        }

        // chosen: a Library file picked in the Library tab; null asks with a file dialog.
        private void InsertTeamPart(string chosen)
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireCurrentAddin(login, catalog);
                var library = catalog.Library;
                if (library == null) throw new InvalidOperationException("The server has no parts library yet. Ask a mentor.");
                var robot = catalog.Robot;
                if (robot.Archived) throw new InvalidOperationException(robot.Name + " is archived and read-only.");
                if (!new SvnWorkspace(login, robot).IsCheckedOut) throw new InvalidOperationException("Click Open Robot first, so the part has a robot to go into.");

                // Keep the library fresh; a local copy is fine if the update cannot run right now.
                var librarySvn = new SvnWorkspace(login, library);
                try { UpdateWorkspace(login, library); }
                catch (Exception) when (librarySvn.IsCheckedOut) { }

                string source = chosen == null ? null : Path.GetFullPath(chosen);
                if (source == null)
                    using (var dialog = new OpenFileDialog { Title = "Insert from Library", InitialDirectory = library.Root,
                        Filter = "SOLIDWORKS parts and assemblies (*.sldprt;*.sldasm)|*.sldprt;*.sldasm", RestoreDirectory = true })
                    {
                        if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                        source = Path.GetFullPath(dialog.FileName);
                    }
                if (!File.Exists(source)) throw new InvalidOperationException(Path.GetFileName(source) + " isn't in the team Library any more. Search again.");
                if (!library.Contains(source)) throw new InvalidOperationException("Choose a part from the Library folder:\n" + library.Root);
                bool cancelled;
                var target = InsertTarget(catalog, Path.GetFileNameWithoutExtension(source), out cancelled);
                if (cancelled) return;
                DeliverPart(target, CopyFromLibrary(library, robot, source), Path.GetFileNameWithoutExtension(source));
            });
        }

        // ---------- FRCDesignLib ----------

        // One click for the student. Behind it: reuse the team's Library copy if this exact part and configuration
        // was imported before; otherwise download it once, import it natively in SOLIDWORKS, add it to the Library,
        // then copy it into the robot and insert it like any Library part.
        private void InsertFromFrcDesign(FrcItem item, Dictionary<string, string> configuration)
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireCurrentAddin(login, catalog);
                var library = catalog.Library;
                if (library == null) throw new InvalidOperationException("The server has no team Library yet. Ask a mentor.");
                if (catalog.Robot.Archived) throw new InvalidOperationException(catalog.Robot.Name + " is archived and read-only.");
                if (!new SvnWorkspace(login, catalog.Robot).IsCheckedOut) throw new InvalidOperationException("Click Open Robot first, so the part has a robot to go into.");
                bool cancelled;
                var assemblyDoc = InsertTarget(catalog, item.Name, out cancelled);
                if (cancelled) return;
                var client = new FrcClient(login);
                var claim = OperationDialog.Run("Checking the team Library for " + item.Name + "…", () => client.Claim(item.Id, configuration));
                if (claim.Status == "busy")
                {
                    Message(claim.Name + " is being prepared by " + claim.By + " right now. Try again in a minute; it will then insert straight from the team Library.");
                    return;
                }
                string libraryFile = Path.Combine(library.Root, claim.LibraryPath.Replace('/', '\\'));
                WorkspacePolicy.RequireInside(library.Root, libraryFile);
                var librarySvn = new SvnWorkspace(login, library);
                if (claim.Status == "ready")
                {
                    if (!File.Exists(libraryFile)) UpdateWorkspace(login, library);
                    if (!File.Exists(libraryFile)) throw new InvalidOperationException("The team Library should have " + claim.LibraryPath + " but it didn't download. Click Update and try again.");
                }
                else
                {
                    if (!librarySvn.IsCheckedOut) UpdateWorkspace(login, library);
                    ImportIntoLibrary(client, claim, item, library, libraryFile, login);
                }
                // The download may have taken a while: make sure the assembly is still open and ours to change.
                if (assemblyDoc != null && (!OpenDocuments().Any(d => ReferenceEquals(d, assemblyDoc)) || assemblyDoc.IsOpenedReadOnly()))
                    throw new InvalidOperationException(claim.Name + " is in the team Library now, but your assembly was closed or is no longer locked. " +
                        "Open it, click Edit, and use Insert again (it will be instant).");
                DeliverPart(assemblyDoc, CopyFromLibrary(library, catalog.Robot, libraryFile), claim.Name);
            });
        }

        // Self-healing: the server-assigned Library path is ours for this reservation, so anything left there
        // by an earlier failed attempt is our own import artifact and safe to clear. A copy already committed
        // to the Library is simply used.
        private void ImportIntoLibrary(FrcClient client, FrcClaim claim, FrcItem item, WorkspaceInfo library, string libraryFile, NetworkCredential login)
        {
            string staging = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "JocoRobos.Cad", "imports", claim.Fingerprint, "export.x_t");
            var svn = new SvnWorkspace(login, library);
            var state = OperationDialog.Run("Checking the team Library…", () => SvnWorkspace.Exclusive(() => svn.PrepareImportPath(libraryFile)));
            if (state == ImportPathState.Committed)
            {
                FinishImport(client, claim);
                return;
            }
            try
            {
                OperationDialog.Run("Preparing " + claim.Name + " for the team (first time only)…", () => { client.Download(claim, staging); return true; });
                int errors = 0;
                // Without 3D Interconnect: a linked import keeps pointing at export.x_t, a file only this computer has.
                // Teammates' SOLIDWORKS would go looking for it (and can hang doing so); a plain import is self-contained.
                int interconnect = (int)swUserPreferenceToggle_e.swMultiCAD_Enable3DInterconnect;
                bool wasLinking = application.GetUserPreferenceToggle(interconnect);
                ModelDoc2 imported;
                application.SetUserPreferenceToggle(interconnect, false);
                try { imported = application.LoadFile4(staging, "r", null, ref errors) as ModelDoc2; }
                finally { application.SetUserPreferenceToggle(interconnect, wasLinking); }
                if (imported == null)
                    throw new InvalidOperationException("SOLIDWORKS could not open the downloaded " + claim.Name + " (error " + errors + "). Nothing was inserted.");
                try
                {
                    if (imported.GetType() != (int)swDocumentTypes_e.swDocPART)
                        throw new InvalidOperationException(claim.Name + " came in as an assembly, which isn't supported yet. Nothing was inserted.");
                    Directory.CreateDirectory(Path.GetDirectoryName(libraryFile));
                    int saveErrors = 0, warnings = 0;
                    bool saved = imported.Extension.SaveAs3(libraryFile, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref saveErrors, ref warnings) && File.Exists(libraryFile);
                    if (!saved) throw new InvalidOperationException("Could not save " + claim.Name + " as a SOLIDWORKS part (error " + saveErrors + "). Nothing was inserted.");
                }
                finally
                {
                    application.CloseDoc(imported.GetTitle());
                }
                // The download isn't needed once the part is saved natively.
                try { Directory.Delete(Path.GetDirectoryName(staging), true); }
                catch (Exception exception) { ErrorLog.Write("import cleanup", exception); }
                var newItem = new SubmitItem { Kind = SubmitKind.New, Path = libraryFile, Workspace = library };
                OperationDialog.Run("Adding " + claim.Name + " to the team Library…", () => SvnWorkspace.Exclusive(() =>
                    svn.Submit(new List<SubmitItem> { newItem }, "Import " + claim.Name + " from FRCDesignLib (" + item.Vendor + ")")));
            }
            catch (Exception failure)
            {
                // Did the Library commit land anyway (for example the connection dropped during the response)?
                ImportPathState after;
                try { after = OperationDialog.Run("Checking what reached the team Library…", () => SvnWorkspace.Exclusive(() => svn.PrepareImportPath(libraryFile))); }
                catch (Exception) { after = ImportPathState.Unknown; }
                if (after == ImportPathState.Committed)
                {
                    FinishImport(client, claim);
                    return;
                }
                // Not in the Library: PrepareImportPath cleared any local leftovers, so the Library and robot are unchanged.
                client.Abandon(claim);
                if (after == ImportPathState.Unknown)
                    throw new InvalidOperationException(failure.Message + "\n\nThe server couldn't be checked, so a partial import may remain in the Library folder. " +
                        "Click Insert again when you're connected; it cleans up after itself.", failure);
                throw;
            }
            FinishImport(client, claim);
        }

        // Marks the import done. If this fails, the server notices the committed file on the next request by itself.
        private static void FinishImport(FrcClient client, FrcClaim claim)
        {
            try { OperationDialog.Run("Finishing…", () => { client.Complete(claim); return true; }); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO FRC complete (server self-heals): " + exception.Message); }
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
            var map = CopyAndRepoint(robot, files, f => WorkspacePolicy.LibraryCopyPath(library.Root, robot.Root, f), index, reuseAny: true);
            return map[source];
        }

        // Where an outside file goes: 90_COTS\Imported\<name of what was imported>\..., keeping its folder layout when it's under the source's folder.
        private static Func<string, string> ImportTarget(WorkspaceInfo robot, string sourceRoot, string label)
        {
            string folder = Path.Combine(robot.Root, "90_COTS", "Imported", System.Text.RegularExpressions.Regex.Replace(label, @"[^A-Za-z0-9 _().&+,-]", "_").Trim());
            string root = Path.GetFullPath(sourceRoot).TrimEnd('\\') + "\\";
            return file => file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(folder, file.Substring(root.Length))
                : Path.Combine(folder, Path.GetFileName(file));
        }

        /// <summary>
        /// Copies each file to targetFor(file) and points copied assemblies and drawings at the copies.
        /// reuseAny: an existing target is kept as is (Library rule). Otherwise an existing target is reused only if identical.
        /// Refuses names that already exist elsewhere in the robot. On failure, removes everything this call created.
        /// </summary>
        private Dictionary<string, string> CopyAndRepoint(WorkspaceInfo robot, IList<string> files, Func<string, string> targetFor,
            ILookup<string, string> index, bool reuseAny)
        {
            var map = files.ToDictionary(f => f, targetFor, StringComparer.OrdinalIgnoreCase);
            var tooLong = map.Values.Where(WorkspacePolicy.TooLong).ToList();
            if (tooLong.Count > 0)
                throw new InvalidOperationException("The copy would have a path longer than " + WorkspacePolicy.MaxPath + " characters, which Windows and SOLIDWORKS don't handle reliably:\n\n" +
                    String.Join("\n", tooLong.Take(4)) + "\n\nRename the file with a shorter name first. Nothing was copied.");
            var robotIndex = CadByName(robot.Root);
            var clashes = map.Values.SelectMany(t => robotIndex[Path.GetFileName(t)].Where(p => !p.Equals(t, StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetFileName(t) + " already exists at " + p)).ToList();
            clashes.AddRange(map.Values.GroupBy(t => Path.GetFileName(t), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                .Select(g => "two different files are named " + g.Key));
            if (clashes.Count > 0)
                throw new InvalidOperationException("SOLIDWORKS can't tell apart different files with the same name, so nothing was copied:\n\n" +
                    String.Join("\n", clashes.Distinct().Take(8)) + "\n\nRename the file you're importing (File → Save As with a more specific name), then try again.");
            var created = new List<string>();
            try
            {
                foreach (var pair in map)
                {
                    if (File.Exists(pair.Value))
                    {
                        if (reuseAny || File.ReadAllBytes(pair.Value).SequenceEqual(File.ReadAllBytes(pair.Key))) continue;
                        throw new InvalidOperationException("A different " + Path.GetFileName(pair.Value) + " was already imported at\n" + pair.Value +
                            "\n\nRename one of them, then try again.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(pair.Value));
                    File.Copy(pair.Key, pair.Value);
                    File.SetAttributes(pair.Value, File.GetAttributes(pair.Value) & ~FileAttributes.ReadOnly);
                    created.Add(pair.Value);
                }
                foreach (var pair in map.Where(p => created.Contains(p.Value) && !p.Value.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (string reference in Dependencies(pair.Key))
                    {
                        string resolved = Resolve(reference, pair.Key, index);
                        string copy;
                        if (resolved != null && map.TryGetValue(resolved, out copy))
                            application.ReplaceReferencedDocument(pair.Value, reference, copy);
                    }
                    var stillLinked = Dependencies(pair.Value).Where(d => map.ContainsKey(d)).ToList();
                    if (stillLinked.Count > 0)
                        throw new InvalidOperationException("Could not point " + Path.GetFileName(pair.Value) + " at the robot copies of:\n" + String.Join("\n", stillLinked));
                }
            }
            catch
            {
                // Leave the robot exactly as it was: remove only the files this attempt created.
                foreach (string target in created) { try { File.Delete(target); } catch (IOException) { } }
                throw;
            }
            return map;
        }

        // Everything a file needs that SOLIDWORKS can find on this computer (including 3D Interconnect temp copies while they exist).
        private List<string> WithDependencies(string source)
        {
            var index = CadByName(Path.GetDirectoryName(source));
            return new[] { source }.Concat(Dependencies(source).Select(r => File.Exists(r) ? Path.GetFullPath(r) : Resolve(r, source, index)).Where(r => r != null))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public void InsertExternalPart()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireCurrentAddin(login, catalog);
                if (catalog.Robot.Archived) throw new InvalidOperationException(catalog.Robot.Name + " is archived and read-only.");
                if (!new SvnWorkspace(login, catalog.Robot).IsCheckedOut) throw new InvalidOperationException("Click Open Robot first, so the part has a robot to go into.");
                string source;
                using (var dialog = new OpenFileDialog { Title = "Import a downloaded CAD file (vendor download, Desktop, USB…)",
                    InitialDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) + "\\Downloads",
                    Filter = "SOLIDWORKS parts and assemblies (*.sldprt;*.sldasm)|*.sldprt;*.sldasm", RestoreDirectory = true })
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    source = Path.GetFullPath(dialog.FileName);
                }
                var owner = catalog.Owning(source);
                if (owner != null && owner.IsLibrary) throw new InvalidOperationException("That's a team Library part: search for it in the Library tab instead.");
                if (owner != null) throw new InvalidOperationException("That file is already in " + owner.Name + ". Drag it into the assembly instead.");
                bool cancelled;
                var target = InsertTarget(catalog, Path.GetFileNameWithoutExtension(source), out cancelled);
                if (cancelled) return;
                var files = WithDependencies(source);
                var map = CopyAndRepoint(catalog.Robot, files, ImportTarget(catalog.Robot, Path.GetDirectoryName(source), Path.GetFileNameWithoutExtension(source)),
                    CadByName(Path.GetDirectoryName(source)), reuseAny: false);
                DeliverPart(target, map[source], Path.GetFileNameWithoutExtension(source));
            });
        }

        // Fixes "uses a file outside the robot": copies every outside file the active assembly uses into the robot and repoints it.
        public void ImportOutsideReferences()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireCurrentAddin(login, catalog);
                var assemblyDoc = WritableRobotAssembly(catalog);
                if (assemblyDoc.GetSaveFlag()) throw new InvalidOperationException("Save the assembly first, then try again.");
                Message(ImportOutsideReferencesOf(assemblyDoc, catalog));
            });
        }

        // Copies everything a saved, writable robot assembly uses from outside the robot into it, then repoints the links.
        private string ImportOutsideReferencesOf(ModelDoc2 assemblyDoc, Catalog catalog)
        {
            var robot = catalog.Robot;
            string assembly = Path.GetFullPath(assemblyDoc.GetPathName());
            var robotIndex = CadByName(robot.Root);
            var outside = Dependencies(assembly).Select(r => File.Exists(r) ? Path.GetFullPath(r) : Resolve(r, assembly, robotIndex))
                .Where(r => r != null && !robot.Contains(r) && File.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (outside.Count == 0) return "Everything " + Path.GetFileName(assembly) + " uses is already inside " + robot.Name + ".";
            var files = outside.SelectMany(WithDependencies).Where(f => !robot.Contains(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var library = catalog.Library;
            var importTarget = ImportTarget(robot, Path.GetDirectoryName(outside[0]), Path.GetFileNameWithoutExtension(assembly) + " imports");
            Func<string, string> target = f => library != null && library.Contains(f) ? WorkspacePolicy.LibraryCopyPath(library.Root, robot.Root, f) : importTarget(Path.Combine(Path.GetDirectoryName(outside[0]), Path.GetFileName(f)));
            var map = CopyAndRepoint(robot, files, target, CadByName(Path.GetDirectoryName(outside[0])), reuseAny: false);
            // SOLIDWORKS can only repoint a closed file: close, repoint this assembly and its writable sub-assemblies, reopen.
            var writable = new[] { assembly }.Concat(Dependencies(assembly).Where(d => robot.Contains(d) && File.Exists(d) && d.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                && (File.GetAttributes(d) & FileAttributes.ReadOnly) == 0)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            application.CloseDoc(assemblyDoc.GetTitle());
            var notFixed = new List<string>();
            try
            {
                foreach (string file in writable)
                    foreach (string reference in Dependencies(file))
                    {
                        string resolved = File.Exists(reference) ? Path.GetFullPath(reference) : Resolve(reference, file, robotIndex);
                        string copy;
                        if (resolved != null && map.TryGetValue(resolved, out copy))
                            application.ReplaceReferencedDocument(file, reference, copy);
                    }
                notFixed = Dependencies(assembly).Where(d => File.Exists(d) && !robot.Contains(Path.GetFullPath(d)) && !WorkspacePolicy.IsTemporary(d, Path.GetTempPath())).ToList();
            }
            finally
            {
                int errors = 0, warnings = 0;
                application.OpenDoc6(assembly, (int)swDocumentTypes_e.swDocASSEMBLY, 0, "", ref errors, ref warnings);
            }
            return "Copied " + map.Count + " outside file(s) into " + robot.Name + "\\90_COTS and pointed " + Path.GetFileName(assembly) + " at them." +
                (notFixed.Count > 0 ? "\n\nStill outside (inside a sub-assembly you haven't locked — Edit it and run this again):\n" + String.Join("\n", notFixed.Take(6)) : "") +
                "\n\nCheck the assembly looks right, save, then Submit.";
        }

        // Files saved in an older SOLIDWORKS are converted when opened, which marks them changed: every save then asks
        // to save read-only team parts somewhere else. Converting the whole robot once, as one submit, ends that for everyone.
        public void UpgradeRobotFiles()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                var catalog = LoadCatalog(login);
                RequireCurrentAddin(login, catalog);
                var robot = catalog.Robot;
                if (robot.Archived) throw new InvalidOperationException(robot.Name + " is archived and read-only.");
                if (OpenDocuments().Any()) throw new InvalidOperationException("Close all SOLIDWORKS documents first.");
                var svn = new SvnWorkspace(login, robot);
                if (svn.IsCheckedOut && !FinishInterruptedUpgrade(svn)) return;
                var files = SubmitCheck.CadByName(robot.Root).SelectMany(g => g)
                    // Parts first, then assemblies, then drawings: each file finds what it uses already converted.
                    .OrderBy(f => f.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ? 0 : f.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 0) { Message(robot.Name + " has no CAD files yet."); return; }
                UpdateWorkspace(login, robot);
                // Resumes an interrupted upgrade: files an earlier conversion submit already covered are skipped.
                var done = OperationDialog.Run("Checking which files are already converted…", () => svn.ConvertedFiles());
                int total = files.Count;
                files = files.Where(f => !done.Contains(f)).ToList();
                if (files.Count == 0) { Message("All " + total + " files in " + robot.Name + " are already converted."); return; }
                if (MessageBox.Show(new SolidWorksWindow(), "Convert " + (files.Count == total ? "all " + total : files.Count + " remaining (of " + total + ")") +
                    " files in " + robot.Name + " to this SOLIDWORKS version?\n\n" +
                    "This locks the files (it stops if anyone is editing one), opens and saves each one without showing it, and submits them " + UpgradeBatch +
                    " at a time. It can take a while, and SOLIDWORKS is busy until it finishes. Everyone downloads the converted files at their next Update.\n\n" +
                    "Do it when nobody else is working on the robot. If SOLIDWORKS stops, run it again: it continues where it stopped.",
                    Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                OperationDialog.Run("Locking " + files.Count + " robot files…", () => SvnWorkspace.Exclusive(() => { svn.LockAll(files); return true; }));
                int submitted = 0;
                var revisions = new List<long>();
                var failed = new List<string>();
                try
                {
                    Action<List<string>> commit = batch =>
                    {
                        var result = SubmitConverted(svn, batch);
                        submitted += result.Item1;
                        if (result.Item2 > 0) revisions.Add(result.Item2);
                    };
                    ConvertFiles(files, failed, commit);
                }
                finally
                {
                    // Files that failed (or weren't reached) are unchanged: give their locks back.
                    try { OperationDialog.Run("Releasing unchanged files…", () => SvnWorkspace.Exclusive(svn.ReleaseUnchangedLocks)); }
                    catch (Exception exception) { ErrorLog.Write("upgrade: release", exception); }
                }
                Message("Converted and submitted " + submitted + " file(s)" + (revisions.Count > 0 ? " (team changes #" + String.Join(", #", revisions) + ")" : "") + "." +
                    (failed.Count > 0 ? "\n\nThese couldn't be converted and were left as they were:\n" + String.Join("\n", failed.Take(12)) + (failed.Count > 12 ? "\n…" : "") : "") +
                    "\n\nTeammates get them with Update (close documents first).", failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            });
        }

        private const int UpgradeBatch = 40;
        private const string ConversionComment = "Convert files to the current SOLIDWORKS format (Upgrade Robot Files)";

        private static string UpgradeMarker
        {
            get { return Path.Combine(Path.GetDirectoryName(ErrorLog.FilePath), "upgrade-current.txt"); }
        }

        // Submits the converted (changed, locked) files among these paths. Returns how many, and the revision.
        private static Tuple<int, long> SubmitConverted(SvnWorkspace svn, IEnumerable<string> paths)
        {
            var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var plan = OperationDialog.Run("Checking the converted files…", () => SvnWorkspace.Exclusive(svn.PrepareSubmit));
            var converted = plan.Items.Where(x => x.Kind == SubmitKind.Modified && !x.NeedsLock && wanted.Contains(x.Path)).ToList();
            if (converted.Count == 0) return Tuple.Create(0, 0L);
            var result = OperationDialog.Run("Submitting " + converted.Count + " converted files…",
                () => SvnWorkspace.Exclusive(() => svn.Submit(converted, ConversionComment)));
            return Tuple.Create(converted.Count, result.Revision);
        }

        // After SOLIDWORKS crashed during an upgrade: the files it finished are changed and locked here. The one it was
        // working on may be half-written, so it's set aside (copy kept, team version back); the rest are submitted.
        // Returns false if the student chose to stop.
        private bool FinishInterruptedUpgrade(SvnWorkspace svn)
        {
            var plan = OperationDialog.Run("Checking for an interrupted upgrade…", () => SvnWorkspace.Exclusive(svn.PrepareSubmit));
            var changed = plan.Items.Where(x => x.Kind == SubmitKind.Modified && !x.NeedsLock && WorkspacePolicy.IsCad(x.Path)).ToList();
            if (changed.Count == 0) { TryDelete(UpgradeMarker); return true; }
            string suspect = null;
            try { if (File.Exists(UpgradeMarker)) suspect = Path.GetFullPath(File.ReadAllText(UpgradeMarker).Trim()); }
            catch (Exception exception) { ErrorLog.Write("upgrade marker", exception); }
            var halfDone = changed.Where(x => String.Equals(x.Path, suspect, StringComparison.OrdinalIgnoreCase)).ToList();
            var finished = changed.Except(halfDone).ToList();
            if (MessageBox.Show(new SolidWorksWindow(), changed.Count + " files are changed and locked by you, probably from an upgrade that stopped partway.\n\n" +
                "Submit them now as converted files, then continue the upgrade?" +
                (halfDone.Count > 0 ? "\n\n" + halfDone[0].Name + " was being saved when it stopped, so it's set aside (a copy is kept) and converted again." : "") +
                "\n\nChoose No if some of these are your own edits: Submit those yourself first.",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            if (halfDone.Count > 0) OperationDialog.Run("Setting aside " + halfDone[0].Name + "…", () => SvnWorkspace.Exclusive(() => svn.SetAside(halfDone)));
            if (finished.Count > 0) SubmitConverted(svn, finished.Select(x => x.Path));
            TryDelete(UpgradeMarker);
            return true;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception exception) { ErrorLog.Write("delete " + path, exception); }
        }

        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enable);

        // Opens and saves each file without showing it (much lighter on graphics memory, which a long run in a VM runs out of),
        // submitting every UpgradeBatch files so a crash loses little. SOLIDWORKS' own window is disabled meanwhile; the progress
        // window keeps processing messages. The file being worked on is written to a marker file, and each finished one to upgrade.log.
        private void ConvertFiles(List<string> files, List<string> failed, Action<List<string>> commit)
        {
            string log = Path.Combine(Path.GetDirectoryName(ErrorLog.FilePath), "upgrade.log");
            var types = new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING };
            IntPtr main = new SolidWorksWindow().Handle;
            EnableWindow(main, false);
            foreach (int type in types) application.DocumentVisible(false, type);
            try
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(log)); File.AppendAllText(log, DateTime.Now + "  starting " + files.Count + " files\r\n"); }
                catch (Exception) { }
                using (var progress = new Form { Text = Title, ClientSize = new System.Drawing.Size(520, 90), FormBorderStyle = FormBorderStyle.FixedDialog,
                    ControlBox = false, StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = false, TopMost = true })
                {
                    var label = new Label { AutoSize = false, Location = new System.Drawing.Point(16, 14), Size = new System.Drawing.Size(488, 36) };
                    var bar = new ProgressBar { Location = new System.Drawing.Point(16, 56), Size = new System.Drawing.Size(488, 18), Maximum = files.Count };
                    progress.Controls.Add(label);
                    progress.Controls.Add(bar);
                    progress.Show(new SolidWorksWindow());
                    var batch = new List<string>();
                    for (int i = 0; i < files.Count; i++)
                    {
                        string file = files[i];
                        label.Text = "Converting " + (i + 1) + " of " + files.Count + ":\n" + Path.GetFileName(file);
                        bar.Value = i;
                        progress.Refresh();
                        Application.DoEvents();
                        try
                        {
                            File.WriteAllText(UpgradeMarker, file);
                            int type = file.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ? types[0] : file.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? types[1] : types[2];
                            int errors = 0, warnings = 0;
                            var doc = application.OpenDoc6(file, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                            if (doc == null) failed.Add(Path.GetFileName(file) + " (couldn't open, error " + errors + ")");
                            else if (doc.IsOpenedReadOnly()) failed.Add(Path.GetFileName(file) + " (opened read-only)");
                            else if (!doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                                failed.Add(Path.GetFileName(file) + " (couldn't save, error " + errors + ")");
                            else batch.Add(file);
                        }
                        catch (Exception exception)
                        {
                            ErrorLog.Write("upgrade " + file, exception);
                            failed.Add(Path.GetFileName(file) + " (" + exception.Message + ")");
                        }
                        finally
                        {
                            application.CloseAllDocuments(true);
                            TryDelete(UpgradeMarker);
                            try { File.AppendAllText(log, DateTime.Now + "  " + (i + 1) + "/" + files.Count + "  " + file + "\r\n"); } catch (Exception) { }
                        }
                        if (batch.Count >= UpgradeBatch || (i == files.Count - 1 && batch.Count > 0))
                        {
                            label.Text = "Submitting " + batch.Count + " converted files…";
                            progress.Refresh();
                            commit(batch);
                            batch = new List<string>();
                        }
                    }
                }
            }
            finally
            {
                foreach (int type in types) application.DocumentVisible(true, type);
                EnableWindow(main, true);
            }
        }

        // For reorganizing an old robot before import: file names are unique, so every link that points outside the
        // chosen folder (or to a file that moved) is repointed to the one file with that name inside it. Works on closed files.
        public void RepairMovedReferences()
        {
            Execute(() =>
            {
                string folder;
                using (var dialog = new FolderBrowserDialog { Description = "Choose the reorganized robot folder (for example C:\\JOCO-ROBOS\\2026-Robot)", ShowNewFolderButton = false,
                    SelectedPath = WorkspaceInfo.BaseFolder })
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    folder = Path.GetFullPath(dialog.SelectedPath).TrimEnd('\\');
                }
                if (OpenDocuments().Any())
                    throw new InvalidOperationException("Close all SOLIDWORKS documents first. Links can only be repaired in closed files.");
                var index = CadByName(folder);
                var duplicates = index.Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (duplicates.Count > 0)
                    throw new InvalidOperationException("These names appear more than once in the folder, so links to them would be ambiguous:\n\n" +
                        String.Join("\n", duplicates.Take(10)) + "\n\nRename one of each first. Nothing was changed.");
                string inside = folder + "\\";
                int repaired = 0, files = 0;
                var readOnly = new List<string>();
                var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var all = index.SelectMany(g => g).ToList();
                foreach (string file in all)
                {
                    var raw = application.GetDocumentDependencies2(file, false, false, false) as object[];
                    if (raw == null) continue;
                    bool changed = false;
                    for (int i = 1; i < raw.Length; i += 2)
                    {
                        string reference = raw[i] as string;
                        if (String.IsNullOrEmpty(reference)) continue;
                        bool stillGood = reference.StartsWith(inside, StringComparison.OrdinalIgnoreCase) && File.Exists(reference);
                        if (stillGood) continue;
                        var match = index[Path.GetFileName(reference)].FirstOrDefault();
                        if (match == null)
                        {
                            if (!WorkspacePolicy.IsTemporary(reference, Path.GetTempPath())) missing.Add(Path.GetFileName(reference));
                            continue;
                        }
                        if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0) { readOnly.Add(file); break; }
                        if (application.ReplaceReferencedDocument(file, reference, match)) { repaired++; changed = true; }
                    }
                    if (changed) files++;
                }
                Message("Repaired " + repaired + " link(s) in " + files + " file(s) under\n" + folder + "." +
                    (readOnly.Count > 0 ? "\n\nSkipped read-only files (use Edit, or work on an unzipped copy):\n" + String.Join("\n", readOnly.Distinct().Take(8).Select(Path.GetFileName)) : "") +
                    (missing.Count > 0 ? "\n\nThese referenced files aren't in the folder at all, so SOLIDWORKS will ask for them:\n" + String.Join("\n", missing.Take(12)) + (missing.Count > 12 ? "\n…" : "") : "") +
                    "\n\nNow open the top assembly and check it.");
            });
        }

        // Where an inserted part goes: the active robot assembly if you're editing it (after offering to lock a read-only one),
        // or a new assembly that isn't saved yet. Null otherwise: the part is then copied into the robot and opened by itself,
        // to use anywhere. The Library copy never changes either way; unused copies can be unchecked at Submit.
        private ModelDoc2 InsertTarget(Catalog catalog, string partName, out bool cancelled)
        {
            cancelled = false;
            var doc = application.ActiveDoc as ModelDoc2;
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return null;
            string path = doc.GetPathName();
            if (String.IsNullOrEmpty(path)) return doc; // New assembly: saved into the robot folder later.
            var owner = catalog.Owning(path);
            if (owner == null || owner.IsLibrary || owner.Name != catalog.Robot.Name || owner.Archived) return null;
            if (!doc.IsOpenedReadOnly()) return doc;
            string name = Path.GetFileName(path);
            var answer = MessageBox.Show(new SolidWorksWindow(), name + " is read-only.\n\n" +
                "Yes: lock " + name + " (like Edit) and insert " + partName + " into it.\n" +
                "No: just add " + partName + " to the robot and open it on its own, to use anywhere.", Title,
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) { cancelled = true; return null; }
            if (answer == DialogResult.No) return null;
            EditDocument(doc, true, catalog, true);
            if (doc.IsOpenedReadOnly()) { cancelled = true; return null; } // Edit explained why it couldn't lock.
            return doc;
        }

        private void DeliverPart(ModelDoc2 assemblyDoc, string copy, string name)
        {
            if (assemblyDoc != null)
            {
                AddToAssembly(assemblyDoc, copy);
                ShowFlash("✓ Inserted " + name + ". Mate it, save, and Submit.");
                return;
            }
            int errors = 0, warnings = 0;
            int type = copy.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
            if (application.OpenDoc6(copy, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) == null)
                throw new InvalidOperationException(name + " is in your robot at\n" + copy + "\nbut SOLIDWORKS couldn't open it (error " + errors + "). Open it from there.");
            ShowFlash("✓ " + name + " is open and in your robot (90_COTS). Drag it into any assembly; it goes to the team with your next Submit (uncheck it there if you don't use it).");
        }

        private ModelDoc2 WritableRobotAssembly(Catalog catalog)
        {
            var doc = application.ActiveDoc as ModelDoc2;
            var owner = doc == null || String.IsNullOrEmpty(doc.GetPathName()) ? null : catalog.Owning(doc.GetPathName());
            if (owner == null || owner.IsLibrary || owner.Name != catalog.Robot.Name || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Open the " + catalog.Robot.Name + " assembly you're working on, and click Edit on it first.");
            if (doc.IsOpenedReadOnly())
                throw new InvalidOperationException("Click Edit on " + Path.GetFileName(doc.GetPathName()) + " first, so it can be changed.");
            return doc;
        }

        private void AddToAssembly(ModelDoc2 assemblyDoc, string path)
        {
            bool wasOpen = OpenDocuments().Any(d => String.Equals(d.GetPathName(), path, StringComparison.OrdinalIgnoreCase));
            int errors = 0, warnings = 0;
            int type = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
            // SOLIDWORKS requires the component to be loaded before AddComponent5. Loaded without a window: opening and closing
            // one would leave SOLIDWORKS showing whatever window was behind it (often the whole robot) instead of this assembly.
            if (!wasOpen) application.DocumentVisible(false, type);
            ModelDoc2 component;
            try { component = application.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings); }
            finally { if (!wasOpen) application.DocumentVisible(true, type); }
            if (component == null) throw new InvalidOperationException("SOLIDWORKS could not open " + Path.GetFileName(path) + " (error " + errors + "). It is copied into the robot; insert it manually.");
            int activateErrors = 0;
            application.ActivateDoc3(assemblyDoc.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
            var added = ((AssemblyDoc)assemblyDoc).AddComponent5(path, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                "", false, "", 0, 0, 0);
            if (!wasOpen) application.CloseDoc(component.GetTitle());
            // Whatever happened above, end on the assembly the part went into.
            application.ActivateDoc3(assemblyDoc.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
            if (added == null) throw new InvalidOperationException(Path.GetFileName(path) + " is copied into the robot, but SOLIDWORKS could not insert it. Drag it in from:\n" + path);
        }

        // ---------- helpers ----------

        // Robot.SLDASM when present; otherwise the only 00_Master assembly that no other assembly there references.
        private string FindMaster(WorkspaceInfo robot)
        {
            if (robot.Master != null && File.Exists(robot.Master)) return robot.Master;
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
                if (submitWindow != null && !submitWindow.IsDisposed) submitWindow.Close();
                submitWindow = null;
                watcher?.Dispose();
                watcher = null;
                savedTimer?.Dispose();
                flashTimer?.Dispose();
                if (application != null) application.DestroyNotify -= OnSolidWorksClosing;
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
