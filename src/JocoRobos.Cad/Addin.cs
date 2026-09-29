using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
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
        private const int LayoutVersion = 591903;
        private SldWorks application;
        private CommandManager commands;
        private bool busy;

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                application = (SldWorks)ThisSW;
                if (!application.SetAddinCallbackInfo2(0, this, Cookie))
                    throw new InvalidOperationException("SOLIDWORKS could not register the callbacks.");
                commands = application.GetCommandManager(Cookie);
                CreateCommands();
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
                    "2027 Robot collaboration", Title, -1, migrate, ref error);
                if (group == null) throw new InvalidOperationException("Could not create toolbar. API code: " + error);
                int both = (int)swCommandItemType_e.swMenuItem | (int)swCommandItemType_e.swToolbarItem;
                int menu = (int)swCommandItemType_e.swMenuItem;
                int open = Add(group, "Open Robot", "Update the workspace and open the robot", nameof(OpenRobot), 1, both);
                int update = Add(group, "Update", "Download the latest robot files", nameof(UpdateRobot), 2, both);
                int edit = Add(group, "Edit", "Lock the active CAD document for editing", nameof(Edit), 3, both);
                int submit = Add(group, "Submit", "Upload your changed and new CAD files", nameof(Submit), 7, both);
                Add(group, "Sign In", "Connect your CAD account", nameof(SignIn), 4, menu);
                Add(group, "Test Connection", "Verify your CAD account and repository", nameof(TestConnection), 5, menu);
                Add(group, "Release Edit", "Release your lock on an unchanged file", nameof(ReleaseEdit), 6, menu);
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
                    int[] ids = { group.get_CommandID(open), group.get_CommandID(update), group.get_CommandID(edit), group.get_CommandID(submit) };
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
                Message(exception.Message + "\n\nIf your password changed, use Tools → JOCO ROBOS CAD → Sign In.", MessageBoxIcon.Error);
            }
            finally { busy = false; }
        }

        private NetworkCredential GetLogin(bool force)
        {
            NetworkCredential saved = CredentialStore.Read();
            if (saved != null && !force) return saved;
            using (var dialog = new SignInDialog(saved?.UserName))
            {
                if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return null;
                NetworkCredential login = dialog.Login;
                var workspace = new SvnWorkspace(login);
                OperationDialog.Run("Checking your CAD account…", () => { workspace.TestConnection(); return true; });
                CredentialStore.Write(login);
                return login;
            }
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
                OperationDialog.Run("Checking your CAD account…", () => { new SvnWorkspace(login).TestConnection(); return true; });
                Message("Connected to 2027-Robot as " + login.UserName + ".");
            });
        }

        // Conservative first release: no file updates while CAD documents are loaded.
        // Includes hidden components to avoid changing files beneath an assembly.
        private void RequireDocumentsClosed()
        {
            if (application.GetFirstDocument() != null)
                throw new InvalidOperationException("Save and close all SOLIDWORKS documents before updating. Your files have not been changed.");
        }

        private long UpdateWorkspace(NetworkCredential login)
        {
            RequireDocumentsClosed();
            var workspace = new SvnWorkspace(login);
            return OperationDialog.Run("Downloading robot updates…", () => workspace.Exclusive(workspace.Update));
        }

        public void UpdateRobot()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                long revision = UpdateWorkspace(login);
                Message("Workspace updated to revision " + revision + "." +
                    (FindMaster() != null ? "" : "\n\nThe server folder structure is ready. A mentor still needs to upload the initial robot CAD."));
            });
        }

        public void OpenRobot()
        {
            Execute(() =>
            {
                var login = GetLogin(false);
                if (login == null) return;
                UpdateWorkspace(login);
                string master = FindMaster();
                if (master == null)
                {
                    Message("Workspace updated, but no single master assembly was found in:\n" + SvnWorkspace.MasterFolder +
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

        private ModelDoc2 ActiveCad()
        {
            var doc = application.ActiveDoc as ModelDoc2;
            if (doc == null || String.IsNullOrEmpty(doc.GetPathName()))
                throw new InvalidOperationException("Open a saved CAD document in its own window first.");
            WorkspacePolicy.RequireInside(SvnWorkspace.Root, doc.GetPathName());
            if (!WorkspacePolicy.IsCad(doc.GetPathName())) throw new InvalidOperationException("Select a SOLIDWORKS CAD document first.");
            return doc;
        }

        public void Edit()
        {
            Execute(() =>
            {
                ModelDoc2 doc = ActiveCad();
                // Never reload a dirty document or change it behind an open assembly.
                if (doc.GetSaveFlag())
                    throw new InvalidOperationException("This document has unsaved changes. Preserve them before acquiring a new edit lock.");
                var login = GetLogin(false);
                if (login == null) return;
                string path = doc.GetPathName();
                var workspace = new SvnWorkspace(login);
                try
                {
                    if (!doc.SetReadOnlyState(true)) throw new InvalidOperationException("SOLIDWORKS could not put the document in read-only mode.");
                    string owner = OperationDialog.Run("Checking the revision and acquiring your edit lock…",
                        () => workspace.Exclusive(() => workspace.Edit(path)));
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    if (!doc.SetReadOnlyState(false) || doc.IsOpenedReadOnly())
                        throw new InvalidOperationException("Your SVN lock is held, but SOLIDWORKS could not make the document writable. Close and reopen it, then retry Edit.");
                    Message("Locked by " + owner + ". You can now edit " + Path.GetFileName(path) +
                        ".\n\nSave normally, then click Submit when you are done.");
                }
                catch
                {
                    doc.SetReadOnlyState(true);
                    if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                    throw;
                }
            });
        }

        public void ReleaseEdit()
        {
            Execute(() =>
            {
                ModelDoc2 doc = ActiveCad();
                if (doc.GetSaveFlag()) throw new InvalidOperationException("Cannot release a file with unsaved changes.");
                var login = GetLogin(false);
                if (login == null) return;
                string path = doc.GetPathName();
                if (!doc.SetReadOnlyState(true)) throw new InvalidOperationException("Could not make the document read-only.");
                var workspace = new SvnWorkspace(login);
                OperationDialog.Run("Releasing your unchanged file…", () => workspace.Exclusive(() => { workspace.ReleaseEdit(path); return true; }));
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                Message("Edit lock released. The file is read-only.");
            });
        }

        // Robot.SLDASM when present; otherwise the only 00_Master assembly that no other assembly there references.
        private string FindMaster()
        {
            if (File.Exists(SvnWorkspace.RobotPath)) return SvnWorkspace.RobotPath;
            if (!Directory.Exists(SvnWorkspace.MasterFolder)) return null;
            string[] assemblies = Directory.GetFiles(SvnWorkspace.MasterFolder, "*.sldasm")
                .Where(WorkspacePolicy.IsSubmittableCad).ToArray();
            var referenced = new HashSet<string>(assemblies.SelectMany(Dependencies), StringComparer.OrdinalIgnoreCase);
            string[] top = assemblies.Where(x => !referenced.Contains(Path.GetFullPath(x))).ToArray();
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

        public void Submit()
        {
            Execute(() =>
            {
                // SOLIDWORKS keeps edits in memory; only saved bytes can be submitted.
                var unsaved = OpenDocuments().Where(d => d.GetSaveFlag()).Select(d =>
                    String.IsNullOrEmpty(d.GetPathName()) ? d.GetTitle() + " (never saved)" : d.GetPathName()).ToList();
                if (unsaved.Count > 0)
                    throw new InvalidOperationException("Save these documents first (Save As into " + SvnWorkspace.Root + " for new files):\n\n" +
                        String.Join("\n", unsaved) + "\n\nNothing was submitted.");
                var login = GetLogin(false);
                if (login == null) return;
                var workspace = new SvnWorkspace(login);
                SubmitPlan plan = OperationDialog.Run("Checking your changes…", () => workspace.Exclusive(workspace.PrepareSubmit));
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
                RequireReferencesIncluded(selected, plan);
                SubmitResult result = OperationDialog.Run("Submitting " + selected.Count + " file(s)…",
                    () => workspace.Exclusive(() => workspace.Submit(selected, comment)));
                // Submitted files are no longer locked; stop SOLIDWORKS from saving over them.
                var done = new HashSet<string>(selected.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var doc in OpenDocuments().Where(d => done.Contains(d.GetPathName() ?? "")))
                    doc.SetReadOnlyState(true);
                string summary = result.Revision > 0 ? "Submitted as revision " + result.Revision + "." : "Locks released.";
                Message(summary + (result.Warnings.Count > 0 ? "\n\n" + String.Join("\n\n", result.Warnings) : ""),
                    result.Warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            });
        }

        // Teammates must be able to open what is submitted: no references outside the workspace
        // and no new parts left behind on this computer.
        private void RequireReferencesIncluded(List<SubmitItem> selected, SubmitPlan plan)
        {
            var chosen = new HashSet<string>(selected.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            var newFiles = new HashSet<string>(plan.Items.Where(x => x.Kind == SubmitKind.New).Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();
            foreach (var item in selected.Where(x => x.Kind != SubmitKind.ReleaseOnly && WorkspacePolicy.IsCad(x.Path)))
                foreach (string reference in Dependencies(item.Path))
                {
                    bool inside;
                    try { WorkspacePolicy.RequireInside(SvnWorkspace.Root, reference); inside = true; }
                    catch (Exception) { inside = false; }
                    if (!inside)
                        problems.Add(Path.GetFileName(item.Path) + " uses a file outside the robot folder:\n    " + reference);
                    else if (newFiles.Contains(reference) && !chosen.Contains(reference))
                        problems.Add(Path.GetFileName(item.Path) + " uses the new file " + Path.GetFileName(reference) + ", which is unchecked");
                }
            if (problems.Count > 0)
                throw new InvalidOperationException("Teammates would not be able to open this submission:\n\n" +
                    String.Join("\n", problems.Distinct()) + "\n\nCopy outside parts into the robot folder (Pack and Go or Save As), " +
                    "replace the component, save, and Submit again. Nothing was submitted.");
        }

        public bool DisconnectFromSW()
        {
            if (busy) return false;
            try { if (commands != null) commands.RemoveCommandGroup2(GroupId, true); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
            commands = null;
            application = null;
            return true;
        }

        private static void Message(string text, MessageBoxIcon icon = MessageBoxIcon.Information)
        {
            MessageBox.Show(new SolidWorksWindow(), text, Title, MessageBoxButtons.OK, icon);
        }
    }
}
