#ifndef ProductVersion
  #error ProductVersion must be provided by build.ps1
#endif
[Setup]
AppId={{{#InstallerAppId}}
AppName=UsbWebLauncher
AppVersion={#ProductVersion}
AppVerName=UsbWebLauncher {#ProductVersion}
AppPublisher=UsbWebLauncher
AppPublisherURL=https://github.com/yxsimony-dot/CCB-USB-User
DefaultDirName={localappdata}\Programs\UsbWebLauncher
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir={#ReleaseDirectory}
OutputBaseFilename=UsbWebLauncher-{#ProductVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\UsbWebLauncher.App.exe
SetupLogging=yes
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#FileVersion}
UsePreviousAppDir=yes
UsePreviousGroup=no
DisableReadyMemo=no

[Files]
; Keep the small preflight helpers before the solid-compressed application payload.
Source: "{#PayloadDirectory}\installer\Validate-InstallDirectory.ps1"; Flags: dontcopy
Source: "{#PayloadDirectory}\installer\InstallDirectory.psm1"; Flags: dontcopy
Source: "{#PayloadDirectory}\installer\Transaction.psm1"; Flags: dontcopy
Source: "{#PayloadDirectory}\*"; DestDir: "{tmp}\payload"; Flags: dontcopy recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\UsbWebLauncher"; Filename: "{app}\UsbWebLauncher.App.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\UsbWebLauncher.App.exe"; Parameters: "--installer-first-run"; Description: "打开应用并检查本机使用环境"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: IsCommitted

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=0

[Messages]
SetupAppTitle=安装程序
SetupWindowTitle=%1 安装程序
UninstallAppTitle=卸载程序
UninstallAppFullTitle=%1 卸载程序
InformationTitle=提示
ConfirmTitle=确认
ErrorTitle=错误
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonFinish=完成(&F)
ClickNext=点击“下一步”继续，或点击“取消”退出。
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=即将在此电脑上安装 [name/ver]。%n%n请先正常关闭本应用。仅支持同一 Windows 用户在本机使用。
WizardSelectDir=选择安装位置
SelectDirDesc=您希望将 [name] 安装在哪里？
SelectDirLabel3=请选择本机可写入的专用文件夹。点击“下一步”检查位置；点击“浏览”选择其他文件夹。
SelectDirBrowseLabel=安装目录：
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&B)...
DiskSpaceMBLabel=至少需要 [mb] MB 可用磁盘空间。
BrowseDialogTitle=选择安装文件夹
BrowseDialogLabel=请选择文件夹，然后点击“确定”。
WizardReady=准备安装
ReadyLabel1=现在可以开始安装 [name]。
ReadyLabel2a=点击“安装”继续，或点击“上一步”查看设置。
ReadyLabel2b=点击“安装”继续。
ReadyMemoDir=安装位置：
ReadyMemoGroup=开始菜单：
WizardPreparing=正在准备安装
PreparingDesc=正在检查并准备安装。
WizardInstalling=正在安装
InstallingLabel=正在安装 [name]，请稍候。
FinishedHeadingLabel=[name] 安装向导已完成
FinishedLabelNoIcons=[name] 已安装。
FinishedLabel=[name] 已安装。请通过开始菜单打开应用。
ClickFinish=点击“完成”退出安装向导。
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。是否退出？%n%n如果存在未完成事务，请再次运行本安装程序恢复。
ConfirmUninstall=是否卸载 %1？%n%nPIN、个人设置、PDF 和第三方银行组件将保留。
UninstallStatusLabel=正在卸载 %1，请稍候。
UninstalledAll=%1 已卸载。个人数据已保留。

[Code]
var
  PayloadExtracted, TransactionStarted, TransactionCommitted: Boolean;
  ExistingDirectory: String;

function Quote(Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function RunTransaction(Action: String; Uninstalling: Boolean): Boolean;
var
  ScriptPath, Params: String;
  ExitCode: Integer;
begin
  if Uninstalling then
    ScriptPath := ExpandConstant('{app}\installer\Invoke-Install.ps1')
  else
    ScriptPath := ExpandConstant('{tmp}\payload\installer\Invoke-Install.ps1');
  Params := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + Quote(ScriptPath) +
    ' -Action ' + Action + ' -AppDirectory ' + Quote(ExpandConstant('{app}'));
  if not Uninstalling then
    Params := Params + ' -PayloadDirectory ' + Quote(ExpandConstant('{tmp}\payload')) + ' -Version {#ProductVersion}';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Result := Result and (ExitCode = 0);
end;

function FailureDetail: String;
var
  Text: AnsiString;
begin
  Result := '安装未完成。未启动银行业务。请根据下方技术信息处理后重试：';
  if LoadStringFromFile(GetEnv('TEMP') + '\UsbWebLauncher-installer-error.txt', Text) then
    Result := Result + #13#10 + String(Text);
end;

function ValidateDirectory: Boolean;
var
  Params: String;
  ExitCode: Integer;
begin
  Result := False;
  if Pos('"', WizardDirValue) > 0 then begin
    MsgBox('安装路径不能包含双引号。', mbError, MB_OK);
    exit;
  end;
  ExtractTemporaryFile('Validate-InstallDirectory.ps1');
  ExtractTemporaryFile('InstallDirectory.psm1');
  ExtractTemporaryFile('Transaction.psm1');
  Params := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    Quote(ExpandConstant('{tmp}\Validate-InstallDirectory.ps1')) +
    ' -AppDirectory ' + Quote(WizardDirValue) + ' -InstallerAppId {#InstallerAppId}';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Result := Result and (ExitCode = 0);
  if not Result then MsgBox('此安装位置无法使用，请选择其他专用文件夹。' + #13#10 + FailureDetail, mbError, MB_OK);
end;

procedure InitializeWizard;
var
  LegacyDirectory: String;
begin
  ExistingDirectory := '';
  RegQueryStringValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#InstallerAppId}' + '}_is1', 'InstallLocation', ExistingDirectory);
  if ExistingDirectory = '' then begin
    RegQueryStringValue(HKCU64, 'Software\UsbWebLauncher\Installer', 'PendingInstallLocation', ExistingDirectory);
    if (ExistingDirectory <> '') and not (DirExists(ExistingDirectory + '.transaction') or
      FileExists(ExistingDirectory + '\payload-manifest.json') or FileExists(ExistingDirectory + '\installed-version.json')) then
      ExistingDirectory := '';
  end;
  LegacyDirectory := ExpandConstant('{localappdata}\Programs\UsbWebLauncher');
  if (ExistingDirectory = '') and (FileExists(LegacyDirectory + '\installed-version.json') or
    FileExists(LegacyDirectory + '\payload-manifest.json') or DirExists(LegacyDirectory + '.transaction')) then
    ExistingDirectory := LegacyDirectory;
  if ExistingDirectory <> '' then begin
    WizardForm.DirEdit.Text := ExistingDirectory;
    WizardForm.DirEdit.ReadOnly := True;
    WizardForm.DirBrowseButton.Enabled := False;
    WizardForm.SelectDirLabel.Caption := '检测到现有或未完成安装，本次将在以下位置升级或恢复。若要更换位置，请先卸载，再重新安装；PIN、个人设置和已下载 PDF 会保留。';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then Result := ValidateDirectory;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not ValidateDirectory then begin
    Result := '请返回选择可用的安装位置。';
    exit;
  end;
  if not PayloadExtracted then begin
    ExtractTemporaryFiles('{tmp}\payload\*');
    PayloadExtracted := True;
  end;
  if DirExists(ExpandConstant('{app}.transaction')) then begin
    if MsgBox('检测到上次安装未完成。是否先恢复事务，再继续安装？已开始提交的版本将继续完成提交；其他失败阶段将恢复旧版本。', mbConfirmation, MB_YESNO) <> IDYES then begin
      Result := '请先恢复未完成事务。备份已保留。';
      exit;
    end;
    if not RunTransaction('Recover', False) then begin
      Result := FailureDetail;
      exit;
    end;
  end;
  if not RunTransaction('Install', False) then begin
    Result := FailureDetail;
    exit;
  end;
  TransactionStarted := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    if RunTransaction('Commit', False) then
      TransactionCommitted := True
    else
      RaiseException(FailureDetail + #13#10 + '恢复文件已保留，请再次运行此安装程序恢复。');
  end;
end;

procedure DeinitializeSetup;
begin
  if TransactionStarted and not TransactionCommitted then begin
    if not RunTransaction('Recover', False) then
      MsgBox('安装未提交成功。备份已保留，请再次运行此安装程序恢复。', mbError, MB_OK);
  end;
end;

function IsCommitted: Boolean;
begin
  Result := TransactionCommitted;
end;

function InitializeSetup: Boolean;
begin
  Result := not WizardSilent;
  if not Result then MsgBox('此版本只支持交互式安装，请正常双击安装程序。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then begin
    if not RunTransaction('Uninstall', True) then RaiseException(FailureDetail);
  end;
end;
