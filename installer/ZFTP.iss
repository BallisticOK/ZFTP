; ============================================================================
;  ZFTP installer (Inno Setup)
;  Installs the compiled ZFTP app + its updater, and makes sure the two things
;  it depends on are present: the .NET 8 Desktop Runtime and WinFsp. Both are
;  bundled and installed silently only if missing. Creates shortcuts and an
;  uninstaller. No source code — just the compiled program.
; ============================================================================

#define MyAppName "ZFTP"
#define MyAppVersion "2.6.1"
#define MyAppPublisher "ZFTP"
#define MyAppExeName "ZFTP.exe"
#define MyUpdaterExe "ZFTP.Updater.exe"

#define PublishDir "E:\SFTP Net Drive\ZFTP\src\ZFTP.App\bin\Release\net8.0-windows\win-x64\fd-publish"
#define IconFile   "E:\SFTP Net Drive\ZFTP\src\ZFTP.App\zftp.ico"
#define WinFspMsi  "E:\SFTP Net Drive\_setup\winfsp-2.1.25156.msi"
#define DotNetExe  "E:\SFTP Net Drive\_setup\windowsdesktop-runtime-8-x64.exe"

[Setup]
AppId={{B7E1F3A2-9C4D-4E6F-8A1B-2C3D4E5F6071}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=E:\SFTP Net Drive\ZFTP\dist
OutputBaseFilename=ZFTP-Setup-{#MyAppVersion}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "Start ZFTP automatically when Windows starts (in the tray)"; GroupDescription: "Startup:"; Flags: unchecked

[InstallDelete]
; Clear any previous install in the app folder first, so upgrading from an older
; (e.g. self-contained) build doesn't leave hundreds of orphaned files behind.
Type: filesandordirs; Name: "{app}\*"

[Files]
; The compiled app + its libraries + the updater (framework-dependent build).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#IconFile}";     DestDir: "{app}"; DestName: "zftp.ico"; Flags: ignoreversion
; Prerequisites — shipped inside the setup and removed after install.
Source: "{#DotNetExe}"; DestDir: "{tmp}"; DestName: "dotnet-desktop.exe"; Flags: deleteafterinstall
Source: "{#WinFspMsi}"; DestDir: "{tmp}"; DestName: "winfsp.msi";        Flags: deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}";                 Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\zftp.ico"
Name: "{group}\Check for ZFTP Updates";       Filename: "{app}\{#MyUpdaterExe}"; IconFilename: "{app}\zftp.ico"
Name: "{group}\Uninstall {#MyAppName}";       Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";           Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\zftp.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "ZFTP"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; \
  Flags: uninsdeletevalue; Tasks: startupicon

[Run]
; 1) .NET 8 Desktop Runtime — only if not already installed.
Filename: "{tmp}\dotnet-desktop.exe"; Parameters: "/install /quiet /norestart"; \
  StatusMsg: "Installing .NET 8 Desktop Runtime..."; Check: DotNetDesktopMissing; Flags: waituntilterminated
; 2) WinFsp driver — only if not already installed.
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; \
  StatusMsg: "Installing WinFsp driver (required by ZFTP)..."; Check: WinFspMissing; Flags: waituntilterminated
; 3) Offer to launch ZFTP at the end.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
  Flags: nowait postinstall skipifsilent

[Code]
// Before replacing files, make sure a leftover adb background server from a prior
// install isn't still running and holding tools\adb.exe open (which would block the
// update). This is targeted: `adb kill-server` only stops the background server, it
// does NOT force-kill other adb.exe processes (e.g. Android Studio's).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  AdbPath: String;
begin
  AdbPath := ExpandConstant('{app}\tools\adb.exe');
  if FileExists(AdbPath) then
    Exec(AdbPath, 'kill-server', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

function WinFspMissing: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonpf32}\WinFsp\bin\winfsp-x64.dll'));
end;

function DotNetDesktopMissing: Boolean;
var
  fr: TFindRec;
  found: Boolean;
begin
  found := False;
  if FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), fr) then
  try
    found := True;
  finally
    FindClose(fr);
  end;
  Result := not found;
end;
