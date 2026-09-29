using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

[assembly: ComVisible(false)]

namespace JocoRobos.Cad
{
    // Explicit IDispatch interface lets SOLIDWORKS resolve callback names through COM.
    [ComVisible(true)]
    [Guid("01CE207C-1D59-4DCB-BE56-1FF3815061F1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IAddinCallbacks
    {
        [DispId(1)] void OpenRobot();
        [DispId(2)] void TestConnection();
        [DispId(3)] int CanRun();
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
        // Bump this ID when changing the command layout in a later milestone.
        private const int GroupId = 591901;
        private const string RobotPath = @"C:\JOCO-ROBOS\2027-Robot\00_Master\Robot.SLDASM";
        private SldWorks application;
        private CommandManager commands;

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                application = (SldWorks)ThisSW;
                if (!application.SetAddinCallbackInfo2(0, this, Cookie))
                    throw new InvalidOperationException("SOLIDWORKS could not register the add-in callbacks.");
                commands = application.GetCommandManager(Cookie);
                CreateCommands();
                return true;
            }
            catch (Exception exception)
            {
                ShowError("The add-in could not load", exception);
                DisconnectFromSW();
                return false;
            }
        }

        private void CreateCommands()
        {
            int error;
            CommandGroup group = commands.CreateCommandGroup2(
                GroupId, Title, "Local prototype — SVN not connected", Title, -1, false, out error);
            if (group == null)
                throw new InvalidOperationException("Could not create the command group. API code: " + error);

            int menuAndToolbar = (int)swCommandItemType_e.swMenuItem |
                                 (int)swCommandItemType_e.swToolbarItem;
            int openIndex = group.AddCommandItem2("Open Robot", -1,
                "Open the local test robot read-only (no synchronization)", "Open Robot", -1,
                nameof(OpenRobot), nameof(CanRun), 1, menuAndToolbar);
            int testIndex = group.AddCommandItem2("Test Callback", -1,
                "Verify that the SOLIDWORKS callback reaches the add-in", "Test Callback", -1,
                nameof(TestConnection), nameof(CanRun), 2, menuAndToolbar);
            if (openIndex < 0 || testIndex < 0)
                throw new InvalidOperationException("SOLIDWORKS could not add the prototype commands.");

            group.HasMenu = true;
            group.HasToolbar = true;
            if (!group.Activate())
                throw new InvalidOperationException("SOLIDWORKS could not activate the toolbar.");

            int[] documentTypes = { (int)swDocumentTypes_e.swDocPART,
                (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING };
            foreach (int documentType in documentTypes)
            {
                // Keep existing tabs to preserve user customizations across sessions.
                if (commands.GetCommandTab(documentType, Title) != null)
                    continue;
                CommandTab tab = commands.AddCommandTab(documentType, Title);
                if (tab == null)
                    throw new InvalidOperationException("Could not create the CommandManager tab.");
                CommandTabBox box = tab.AddCommandTabBox();
                int[] ids = { group.get_CommandID(openIndex), group.get_CommandID(testIndex) };
                int text = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextHorizontal;
                if (box == null || !box.AddCommands(ids, new[] { text, text }))
                {
                    commands.RemoveCommandTab(tab);
                    throw new InvalidOperationException("Could not add CommandManager buttons.");
                }
            }
        }

        public int CanRun() { return application == null ? 0 : 1; }

        public void TestConnection()
        {
            MessageBox.Show("JOCO ROBOS CAD is loaded and the callback works.\n\n" +
                "Local prototype — SVN is not connected.", Title,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public void OpenRobot()
        {
            try
            {
                if (application == null) return;
                if (!File.Exists(RobotPath))
                {
                    MessageBox.Show("Place a copy of your test assembly and its referenced parts here:\n\n" +
                        RobotPath + "\n\nThis prototype does not download CAD files.", Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // Never reopen or change the mode of an already-open document: it may be dirty.
                if (application.GetOpenDocumentByName(RobotPath) != null)
                {
                    MessageBox.Show("The robot is already open. Select its document window in SOLIDWORKS.\n\n" +
                        "Its current edits and read-only state have been left as they are.", Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                int errors = 0;
                int warnings = 0;
                ModelDoc2 robot = application.OpenDoc6(RobotPath,
                    (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,
                    "", ref errors, ref warnings);
                if (robot == null)
                    throw new InvalidOperationException("The test assembly could not be opened.\n" +
                        "SOLIDWORKS load errors: " + errors + "; warnings: " + warnings +
                        ".\nCheck the file version and referenced component paths.");
                if (errors != 0 || warnings != 0)
                    MessageBox.Show("The assembly opened with SOLIDWORKS diagnostics.\n" +
                        "Load errors: " + errors + "; warnings: " + warnings +
                        ".\nCheck missing or unresolved references before using the assembly.", Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception exception) { ShowError("Open Robot failed", exception); }
        }

        public bool DisconnectFromSW()
        {
            try { if (commands != null) commands.RemoveCommandGroup2(GroupId, true); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
            // Do not FinalReleaseComObject on the shared SOLIDWORKS application RCW.
            commands = null;
            application = null;
            return true;
        }

        private static void ShowError(string action, Exception exception)
        {
            MessageBox.Show(action + ".\n\n" + exception.Message, Title,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
