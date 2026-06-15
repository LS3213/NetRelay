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
DeleteUserDataPrompt=是否同时删除 NetRelay 的配置、日志、诊断报告和更新缓存？选择“否”将保留用户数据。
AdditionalTasks=请选择安装选项：
AutoStartTask=登录 Windows 后自动启动 NetRelay
DesktopIconTask=创建桌面快捷方式

[Tasks]
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:DesktopIconTask}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\*.deps.json"
Type: files; Name: "{app}\*.runtimeconfig.json"
Type: files; Name: "{app}\createdump.exe"
Type: filesandordirs; Name: "{app}\cs"
Type: filesandordirs; Name: "{app}\de"
Type: filesandordirs; Name: "{app}\es"
Type: filesandordirs; Name: "{app}\fr"
Type: filesandordirs; Name: "{app}\it"
Type: filesandordirs; Name: "{app}\ja"
Type: filesandordirs; Name: "{app}\ko"
Type: filesandordirs; Name: "{app}\pl"
Type: filesandordirs; Name: "{app}\pt-BR"
Type: filesandordirs; Name: "{app}\ru"
Type: filesandordirs; Name: "{app}\tr"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"

[Icons]
Name: "{autoprograms}\NetRelay"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\NetRelay"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Parameters: "--configure-autostart true"; Flags: runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\{#AppExeName}"; Parameters: "--configure-autostart false"; Flags: runhidden waituntilterminated; Check: not WizardIsTaskSelected('autostart')
Filename: "{app}\{#AppExeName}"; Description: "启动 NetRelay"; Flags: shellexec nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM NetRelay.exe"; Flags: runhidden skipifdoesntexist; RunOnceId: "StopNetRelay"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""NetRelay AutoStart"" /F"; Flags: runhidden; RunOnceId: "DeleteAutoStartTask"

[Code]
var
  DeleteUserData: Boolean;

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
