; CodexQuotaTray per-user Windows installer (Inno Setup 7).
; Build with scripts/package-inno.ps1 so the executable and version are supplied
; from the locked release build rather than checked into the installer source.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.4"
#endif
#ifndef SourceDir
  #define SourceDir ".."
#endif
#ifndef OutputDir
  #define OutputDir "..\dist-inno"
#endif
#ifndef BinaryPath
  #define BinaryPath "{#SourceDir}\target\release\codex-quota-tray-gui.exe"
#endif

[Setup]
AppId={{8F5D1A9B-2B4B-4A17-9D1E-8DAB4D2C4E61}
AppName=CodexQuotaTray
AppVersion={#MyAppVersion}
AppPublisher=CodexQuotaTray
AppPublisherURL=https://github.com/DDJang/CodexQuotaTray
DefaultDirName={localappdata}\Programs\CodexQuotaTray
DefaultGroupName=CodexQuotaTray
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=CodexQuotaTray-{#MyAppVersion}-setup
SetupIconFile={#SourceDir}\assets\app-icon.ico
UninstallDisplayIcon={app}\codex-quota-tray-gui.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
AllowNoIcons=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoDescription=CodexQuotaTray installer
VersionInfoProductName=CodexQuotaTray
VersionInfoProductVersion={#MyAppVersion}
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "autostart"; Description: "登录 Windows 时自动启动 CodexQuotaTray"; GroupDescription: "启动选项："

[Files]
Source: "{#BinaryPath}"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\docs\PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\docs\DEPENDENCIES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\CodexQuotaTray"; Filename: "{app}\codex-quota-tray-gui.exe"; IconFilename: "{app}\codex-quota-tray-gui.exe"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexQuotaTray"; ValueData: """{app}\codex-quota-tray-gui.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\codex-quota-tray-gui.exe"; Description: "启动 CodexQuotaTray"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\codex-quota-tray-gui.exe"; Parameters: "--shutdown-existing"; RunOnceId: "ShutdownExisting"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  NeedsRestart := False;
  if not FileExists(ExpandConstant('{app}\codex-quota-tray-gui.exe')) then
    exit;

  if not Exec(
    ExpandConstant('{app}\codex-quota-tray-gui.exe'),
    '--shutdown-existing',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ExitCode
  ) then begin
    Result := '无法请求旧版 CodexQuotaTray 正常退出。请先从托盘退出后重试。';
    exit;
  end;
  if ExitCode <> 0 then
    Result := '旧版 CodexQuotaTray 未能在规定时间内退出。请先从托盘退出后重试。';
end;
