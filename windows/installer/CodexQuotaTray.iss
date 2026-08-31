; CodexQuotaTray per-user Windows installer (Inno Setup 7).
; Build with windows/scripts/package-inno.ps1 so the executable and version are supplied
; from the locked release build rather than checked into the installer source.

#ifndef MyAppVersion
  #error MyAppVersion must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef RepoRoot
  #define RepoRoot "..\.."
#endif
#ifndef WindowsRoot
  #define WindowsRoot ".."
#endif
#ifndef OutputDir
  #define OutputDir "{#RepoRoot}\dist-inno"
#endif
#ifndef PublishDir
  #define PublishDir "{#RepoRoot}\target\winui-publish"
#endif
#ifndef WindowsAppRuntimeVersion
  #error WindowsAppRuntimeVersion must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeArchitecture
  #error WindowsAppRuntimeArchitecture must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeFileName
  #error WindowsAppRuntimeFileName must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeDownloadUrl
  #error WindowsAppRuntimeDownloadUrl must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeSha256
  #error WindowsAppRuntimeSha256 must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeProbeScript
  #error WindowsAppRuntimeProbeScript must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimePublisherId
  #error WindowsAppRuntimePublisherId must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeFrameworkName
  #error WindowsAppRuntimeFrameworkName must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeFrameworkMinimumVersion
  #error WindowsAppRuntimeFrameworkMinimumVersion must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeMainName
  #error WindowsAppRuntimeMainName must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeMainMinimumVersion
  #error WindowsAppRuntimeMainMinimumVersion must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeSingletonName
  #error WindowsAppRuntimeSingletonName must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeSingletonMinimumVersion
  #error WindowsAppRuntimeSingletonMinimumVersion must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeDdlmNamePattern
  #error WindowsAppRuntimeDdlmNamePattern must be supplied by windows/scripts/package-inno.ps1
#endif
#ifndef WindowsAppRuntimeDdlmMinimumVersion
  #error WindowsAppRuntimeDdlmMinimumVersion must be supplied by windows/scripts/package-inno.ps1
#endif

#define WindowsAppRuntimeProbeFileName "CodexQuotaTray-WindowsAppRuntimeProbe.ps1"

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
SetupIconFile={#WindowsRoot}\assets\app-icon.ico
UninstallDisplayIcon={app}\codex-quota-tray-gui.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; CodexQuotaTray has no unsaved document state. If graceful shutdown misses its
; bounded deadline, force-close the process instead of leaving updates blocked.
CloseApplications=force
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
Source: "{#WindowsAppRuntimeProbeScript}"; DestDir: "{tmp}"; DestName: "{#WindowsAppRuntimeProbeFileName}"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion restartreplace recursesubdirs createallsubdirs
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\docs\PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\docs\DEPENDENCIES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\CodexQuotaTray"; Filename: "{app}\codex-quota-tray-gui.exe"; IconFilename: "{app}\Assets\AppIcon.ico"; IconIndex: 0; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexQuotaTray"; ValueData: """{app}\codex-quota-tray-gui.exe"" --startup"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\codex-quota-tray-gui.exe"; Description: "启动 CodexQuotaTray"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\codex-quota-tray-gui.exe"; Parameters: "--shutdown-existing"; RunOnceId: "ShutdownExisting"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  KeepUserData: Boolean;

function HasKeepUserDataParam(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/KEEPUSERDATA') = 0 then begin
      Result := True;
      exit;
    end;
end;

function InitializeUninstall(): Boolean;
begin
  KeepUserData := HasKeepUserDataParam();
  if (not KeepUserData) and (not UninstallSilent) then
    KeepUserData := SuppressibleMsgBox(
      '是否保留 CodexQuotaTray 的设置、额度缓存和提醒防重复状态？' + #13#10 +
      '选择“否”将执行默认卸载并删除全部用户数据。',
      mbConfirmation,
      MB_YESNO,
      IDNO
    ) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not KeepUserData) then
    DelTree(ExpandConstant('{localappdata}\CodexQuotaTray'), True, True, True);
end;

function IsWindowsAppRuntimeReady(): Boolean;
var
  PowerShellPath: String;
  ProbePath: String;
  ProbeParams: String;
  ProbeExitCode: Integer;
  ProbeOutput: TExecOutput;
begin
  Result := False;
  try
    ProbePath := ExpandConstant('{tmp}\{#WindowsAppRuntimeProbeFileName}');
    ExtractTemporaryFile('{#WindowsAppRuntimeProbeFileName}');
    if not FileExists(ProbePath) then begin
      Log('Windows App Runtime probe script was not extracted.');
      exit;
    end;

    PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
    ProbeParams := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ProbePath + '"' +
      ' -Architecture "{#WindowsAppRuntimeArchitecture}"' +
      ' -PublisherId "{#WindowsAppRuntimePublisherId}"' +
      ' -FrameworkName "{#WindowsAppRuntimeFrameworkName}"' +
      ' -FrameworkMinimumVersion "{#WindowsAppRuntimeFrameworkMinimumVersion}"' +
      ' -MainName "{#WindowsAppRuntimeMainName}"' +
      ' -MainMinimumVersion "{#WindowsAppRuntimeMainMinimumVersion}"' +
      ' -SingletonName "{#WindowsAppRuntimeSingletonName}"' +
      ' -SingletonMinimumVersion "{#WindowsAppRuntimeSingletonMinimumVersion}"' +
      ' -DdlmNamePattern "{#WindowsAppRuntimeDdlmNamePattern}"' +
      ' -DdlmMinimumVersion "{#WindowsAppRuntimeDdlmMinimumVersion}"';

    if not ExecAndCaptureOutputWithNativeSysDir(
      PowerShellPath,
      ProbeParams,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ProbeExitCode,
      ProbeOutput
    ) then begin
      Log('Windows App Runtime probe could not start.');
      exit;
    end;

    if ProbeOutput.Error or (ProbeExitCode <> 0) then begin
      Log('Windows App Runtime probe did not confirm a ready runtime.');
      exit;
    end;
    if GetArrayLength(ProbeOutput.StdErr) <> 0 then begin
      Log('Windows App Runtime probe wrote unexpected error output.');
      exit;
    end;
    if GetArrayLength(ProbeOutput.StdOut) <> 1 then begin
      Log('Windows App Runtime probe returned unexpected output.');
      exit;
    end;

    Result := CompareText(Trim(ProbeOutput.StdOut[0]), 'READY') = 0;
  except
    Log('Windows App Runtime probe failed: ' + GetExceptionMessage);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ShutdownProcessId: Integer;
  RuntimeExitCode: Integer;
  RuntimeInstallerPath: String;
begin
  Result := '';
  NeedsRestart := False;
  if FileExists(ExpandConstant('{app}\codex-quota-tray-gui.exe')) then begin
    // Older builds can block while redirecting the shutdown request to the
    // current single-instance owner. Do not make the installer wait on that
    // helper; CloseApplications below will handle any remaining file locks.
    if not Exec(
      ExpandConstant('{app}\codex-quota-tray-gui.exe'),
      '--shutdown-existing',
      '',
      SW_HIDE,
      ewNoWait,
      ShutdownProcessId
    ) then begin
      Result := '无法请求旧版 CodexQuotaTray 正常退出。请先从托盘退出后重试。';
      exit;
    end;
  end;

  if IsWindowsAppRuntimeReady() then begin
    Log('Windows App Runtime is already ready; skipping runtime download.');
    exit;
  end;

  Log('Windows App Runtime is missing or incomplete; downloading the pinned Microsoft installer.');
  try
    DownloadTemporaryFile(
      '{#WindowsAppRuntimeDownloadUrl}',
      '{#WindowsAppRuntimeFileName}',
      '{#WindowsAppRuntimeSha256}',
      nil
    );
  except
    Result := '无法下载 Windows App Runtime，CodexQuotaTray 未完成安装。' + #13#10 +
      GetExceptionMessage;
    exit;
  end;

  RuntimeInstallerPath := ExpandConstant('{tmp}\{#WindowsAppRuntimeFileName}');
  if not FileExists(RuntimeInstallerPath) then begin
    Result := 'Windows App Runtime 下载未生成安装文件，CodexQuotaTray 未完成安装。';
    exit;
  end;
  if CompareText(GetSHA256OfFile(RuntimeInstallerPath), '{#WindowsAppRuntimeSha256}') <> 0 then begin
    Result := 'Windows App Runtime SHA-256 校验失败，CodexQuotaTray 未完成安装。';
    exit;
  end;
  if not Exec(
    RuntimeInstallerPath,
    '--quiet',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    RuntimeExitCode
  ) then begin
    Result := 'Windows App Runtime 安装程序启动失败（' + SysErrorMessage(RuntimeExitCode) +
      '），CodexQuotaTray 未完成安装。';
    exit;
  end;
  if RuntimeExitCode <> 0 then begin
    Result := 'Windows App Runtime 安装失败（退出码 ' + IntToStr(RuntimeExitCode) +
      '），CodexQuotaTray 未完成安装。';
    exit;
  end;
  if not IsWindowsAppRuntimeReady() then begin
    Result := 'Windows App Runtime 安装后复检未通过，CodexQuotaTray 未完成安装。';
  end;
end;
