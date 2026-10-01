; CAD Hub student installer. Build with scripts\Build-Installer.ps1 on Windows.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; "{{" is Inno Setup's escape for a literal "{" in registry paths.
#define AddinGuid "{{E219FE9C-5919-4BE5-98B7-A518C11AD901}"
#define BuildDir "..\src\JocoRobos.Cad\bin\Release\net48"

[Setup]
AppId={{3EDEFDD9-9838-42F5-8A06-75A920BE3640}
AppName=CAD Hub
AppVersion={#AppVersion}
AppPublisher=FRC Team 5919
DefaultDirName={autopf}\JOCO ROBOS CAD
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=JOCO-ROBOS-CAD-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
UninstallDisplayName=CAD Hub
; SignTool=joco $f  ; enable once the team has a code-signing certificate

[Files]
Source: "{#BuildDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\*.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#BuildDir}\Icons\*.png"; DestDir: "{app}\Icons"; Flags: ignoreversion
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Registry]
; SOLIDWORKS add-in entry. Default value 1 = load at startup for users who have not chosen otherwise.
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{#AddinGuid}"; ValueType: dword; ValueName: ""; ValueData: 1; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{#AddinGuid}"; ValueType: string; ValueName: "Title"; ValueData: "CAD Hub"
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{#AddinGuid}"; ValueType: string; ValueName: "Description"; ValueData: "Open Robot, Edit, Submit: team CAD with exclusive locks."
; Start with SOLIDWORKS for the student who ran setup.
Root: HKCU; Subkey: "Software\SolidWorks\AddInsStartup\{#AddinGuid}"; ValueType: dword; ValueName: ""; ValueData: 1; Flags: uninsdeletekey

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft Visual C++ runtime…"; Check: NeedsVcRedist; Flags: waituntilterminated

[UninstallRun]
Filename: "{dotnet4064}\RegAsm.exe"; Parameters: """{app}\JocoRobos.Cad.dll"" /unregister"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterAddin"

[Messages]
FinishedLabel=CAD Hub is installed.%n%nStart SOLIDWORKS: it asks you to choose a username and password once, then a mentor gives you a code to finish. After that, click Open Robot on the CAD Hub tab.%n%nYour robot files will be in C:\JOCO-ROBOS. Updating or uninstalling never deletes them or your sign-in.

[Code]
function SolidWorksRunning(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq SLDWORKS.exe" | find /I "SLDWORKS.exe" >NUL',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function WaitForSolidWorks(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/WAITFORSW') = 0 then
      Result := True;
end;

function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  if not IsDotNetInstalled(net48, 0) then
  begin
    MsgBox('CAD Hub needs Microsoft .NET Framework 4.8, which SOLIDWORKS normally installs. Install SOLIDWORKS first.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if WaitForSolidWorks() then
  begin
    // Automatic update from the add-in: wait quietly (up to 12 hours) for the student to close SOLIDWORKS.
    Waited := 0;
    while SolidWorksRunning() do
    begin
      Sleep(3000);
      Waited := Waited + 3;
      if Waited > 12 * 3600 then
      begin
        Result := False;
        Exit;
      end;
    end;
  end
  else
    while SolidWorksRunning() do
      if MsgBox('Please save your work and close SOLIDWORKS, then click OK.', mbInformation, MB_OKCANCEL) = IDCANCEL then
      begin
        Result := False;
        Exit;
      end;
end;

function NeedsVcRedist(): Boolean;
var
  Installed: Cardinal;
begin
  Result := not (RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Relaunch: String;
begin
  if CurStep = ssPostInstall then
  begin
    // COM registration is what lets SOLIDWORKS create the add-in; fail loudly if it does not work.
    if not Exec(ExpandConstant('{dotnet4064}\RegAsm.exe'), '"' + ExpandConstant('{app}\JocoRobos.Cad.dll') + '" /codebase /silent',
      '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException('Registering the SOLIDWORKS add-in failed (RegAsm code ' + IntToStr(Code) + '). Run setup again as an administrator.');
    // Reopen SOLIDWORKS for the student (not elevated) after an automatic update.
    Relaunch := ExpandConstant('{param:RELAUNCH}');
    if (Relaunch <> '') and FileExists(Relaunch) and (CompareText(ExtractFileName(Relaunch), 'SLDWORKS.exe') = 0) then
      ExecAsOriginalUser(Relaunch, '', '', SW_SHOWNORMAL, ewNoWait, Code);
  end;
end;
