#ifndef AppVersion
  #error AppVersion must be supplied by build-delivery.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be supplied by build-delivery.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by build-delivery.ps1
#endif

#define AppName "NetRelay"
#define AppPublisher "lansi"
#define AppExeName "NetRelay.exe"
#define RuntimeUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{30D70777-CECF-49AA-AD6E-706EBF03C3A8}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\NetRelay
DefaultGroupName=NetRelay
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir={#OutputDir}
OutputBaseFilename=NetRelaySetup
SetupIconFile={#SourceDir}\Assets\NetRelay.ico
UninstallDisplayIcon={app}\Assets\NetRelay.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
RuntimeRequiredPrompt=NetRelay 需要 Microsoft .NET 8 Desktop Runtime。安装程序将从微软官方下载并静默安装，是否继续？
RuntimeRequiredError=未安装 .NET 8 Desktop Runtime，NetRelay 无法继续安装。
RuntimeDownloadError=下载 .NET 8 Desktop Runtime 失败，请检查网络后重试。
RuntimeLaunchError=无法启动 .NET 8 Desktop Runtime 安装程序。
RuntimeInstallFailed=.NET 8 Desktop Runtime 安装失败，错误码：
RuntimeDetectionFailed=.NET 8 Desktop Runtime 安装完成后仍未被检测到，请重启系统后重试。
DeleteUserDataPrompt=是否同时删除 NetRelay 的配置、日志、诊断报告和更新缓存？选择“否”将保留用户数据。
AdditionalTasks=请选择安装选项：
AutoStartTask=登录 Windows 后自动启动 NetRelay
DesktopIconTask=创建桌面快捷方式

[Tasks]
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:DesktopIconTask}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NetRelay"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\NetRelay"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Parameters: "--configure-autostart true"; Flags: runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\{#AppExeName}"; Parameters: "--configure-autostart false"; Flags: runhidden waituntilterminated; Check: not WizardIsTaskSelected('autostart')
Filename: "{app}\{#AppExeName}"; Description: "启动 NetRelay"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM NetRelay.exe"; Flags: runhidden skipifdoesntexist; RunOnceId: "StopNetRelay"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""NetRelay AutoStart"" /F"; Flags: runhidden; RunOnceId: "DeleteAutoStartTask"

[Code]
var
  DeleteUserData: Boolean;

function IsDesktopRuntime8Installed: Boolean;
var
  RuntimeVersions: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if RegGetValueNames(
    HKLM32,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
    RuntimeVersions) then
  begin
    for Index := 0 to GetArrayLength(RuntimeVersions) - 1 do
    begin
      if Pos('8.', RuntimeVersions[Index]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RuntimeInstaller: String;
  ResultCode: Integer;
begin
  Result := '';
  if IsDesktopRuntime8Installed then
    Exit;

  if MsgBox(
    ExpandConstant('{cm:RuntimeRequiredPrompt}'),
    mbConfirmation,
    MB_YESNO) <> IDYES then
  begin
    Result := ExpandConstant('{cm:RuntimeRequiredError}');
    Exit;
  end;

  RuntimeInstaller := ExpandConstant('{tmp}\windowsdesktop-runtime-8-win-x64.exe');
  try
    DownloadTemporaryFile('{#RuntimeUrl}', RuntimeInstaller, '', nil);
  except
    Result := ExpandConstant('{cm:RuntimeDownloadError}');
    Exit;
  end;

  if not Exec(RuntimeInstaller, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := ExpandConstant('{cm:RuntimeLaunchError}');
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    Result := ExpandConstant('{cm:RuntimeInstallFailed}') + IntToStr(ResultCode);
    Exit;
  end;

  if ResultCode = 3010 then
    NeedsRestart := True;

  if not IsDesktopRuntime8Installed then
    Result := ExpandConstant('{cm:RuntimeDetectionFailed}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteUserData := False;
    if not UninstallSilent then
    begin
      DeleteUserData :=
        MsgBox(
          ExpandConstant('{cm:DeleteUserDataPrompt}'),
          mbConfirmation,
          MB_YESNO or MB_DEFBUTTON2) = IDYES;
    end;
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
  begin
    DelTree(ExpandConstant('{userappdata}\NetRelay'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\NetRelay'), True, True, True);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\AppUserModelId\NetRelay.App');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\netrelay');
  end;
end;
