; Inno Setup script for Claude Usage Tray. Per-user install: no administrator rights needed.
; Build: dotnet publish src\ClaudeUsageTray -p:PublishProfile=win-x64
;        iscc /DAppVersion=0.2.0 installer\ClaudeUsageTray.iss
; Output: artifacts\ClaudeUsageTraySetup-<version>.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6A1C8F2E-3B4D-4E8A-9C71-5D2F0B9E4A13}
AppName=Claude Usage Tray
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=charitty-ccrp
AppCopyright=Copyright (c) 2026 Charittiporn Chamcharoenporn. MIT License.
AppPublisherURL=https://github.com/charitty-ccrp/ClaudeUsageTray
AppSupportURL=https://github.com/charitty-ccrp/ClaudeUsageTray/issues
DefaultDirName={localappdata}\Programs\ClaudeUsageTray
DisableDirPage=yes
DefaultGroupName=Claude Usage Tray
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=ClaudeUsageTraySetup-{#AppVersion}
SetupIconFile=..\assets\app.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\ClaudeUsageTray.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; the app holds this mutex while running, so Setup can ask the user to close it before replacing the .exe
AppMutex=Local\ClaudeUsageTray
CloseApplications=yes

[Tasks]
Name: "startup"; Description: "Start Claude Usage Tray when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\ClaudeUsageTray.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Claude Usage Tray"; Filename: "{app}\ClaudeUsageTray.exe"
; same name the app's own "Shortcuts on the Start menu and desktop" setting uses (AppShortcuts.ShortcutName)
Name: "{userdesktop}\Claude Usage Tray"; Filename: "{app}\ClaudeUsageTray.exe"; Tasks: desktopicon
; same file name the app's own "Start with Windows" setting uses, so the two always agree
Name: "{userstartup}\ClaudeUsageTray"; Filename: "{app}\ClaudeUsageTray.exe"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\ClaudeUsageTray.exe"; Description: "Open Claude Usage Tray now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; a startup shortcut the app created itself after install
Type: files; Name: "{userstartup}\ClaudeUsageTray.lnk"
; a desktop shortcut the app created itself from Settings
Type: files; Name: "{userdesktop}\Claude Usage Tray.lnk"

[Code]
// Settings and logs in %LOCALAPPDATA%\ClaudeUsageTray are kept unless the user asks to remove them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and
     (MsgBox('Also delete your Claude Usage Tray settings, saved cookie and logs?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(ExpandConstant('{localappdata}\ClaudeUsageTray'), True, True, True);
end;
