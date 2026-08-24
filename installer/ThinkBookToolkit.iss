#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\v1.0.0\ThinkBookToolkit-1.0.0-win-x64-framework-dependent"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist\v1.0.0"
#endif
#ifndef ChineseMessagesFile
  #define ChineseMessagesFile "compiler:Languages\ChineseSimplified.isl"
#endif
#ifndef FanBackendFileVersion
  #define FanBackendFileVersion "1.1.0.0"
#endif

#define AppName "ThinkBook Toolkit"
#define AppPublisher "luhongzhen"
#define RuntimeVersion "9.0.18"
#define RuntimeInstaller "windowsdesktop-runtime-9.0.18-win-x64.exe"
#define RuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/9.0.18/windowsdesktop-runtime-9.0.18-win-x64.exe"
#define RuntimeSha256 "12CD00688FC9F8F5187D25911BF656DB61998C264F03EEF4022FF2D9321D6982"
#define GuardianServiceName "ThinkBookToolkitGuardian"

[Setup]
AppId={{A4967548-B6D5-4A77-94B6-C84B6E5685AC}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\ThinkBook Toolkit
DefaultGroupName=ThinkBook Toolkit
AllowNoIcons=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=ThinkBookToolkit-{#AppVersion}-Setup
SetupIconFile=..\src\ThinkBookToolkit\Assets\app-icon-tb.ico
UninstallDisplayIcon={app}\ThinkBookToolkit.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
DisableWelcomePage=no
DisableDirPage=no
UsePreviousAppDir=yes
AlwaysShowDirOnReadyPage=yes
UninstallLogMode=new

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#ChineseMessagesFile}"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.RuntimeDownloadError=无法下载 Microsoft .NET Desktop Runtime {#RuntimeVersion}：%1
english.RuntimeDownloadError=Could not download Microsoft .NET Desktop Runtime {#RuntimeVersion}: %1
chinesesimplified.RuntimeLaunchError=无法启动 Microsoft .NET Desktop Runtime 安装程序。
english.RuntimeLaunchError=Could not start the Microsoft .NET Desktop Runtime installer.
chinesesimplified.RuntimeInstallError=Microsoft .NET Desktop Runtime 安装失败，退出代码：%1。
english.RuntimeInstallError=Microsoft .NET Desktop Runtime setup failed with exit code %1.
chinesesimplified.LenovoDllOptionTitle=可选的联想 DLL 目录
english.LenovoDllOptionTitle=Optional Lenovo DLL directory
chinesesimplified.LenovoDllOptionSubtitle=选择 Toolkit 查找联想组件的位置
english.LenovoDllOptionSubtitle=Choose where Toolkit looks for Lenovo components
chinesesimplified.LenovoDllOptionDescription=默认不启用。启用后，Toolkit 会优先从指定目录加载联想 DLL；文件不会复制到安装目录。
english.LenovoDllOptionDescription=Disabled by default. When enabled, Toolkit loads Lenovo DLLs from this directory first; the files are not copied into the installation.
chinesesimplified.LenovoDllOptionCheck=使用自定义联想 DLL 目录
english.LenovoDllOptionCheck=Use a custom Lenovo DLL directory
chinesesimplified.LenovoDllDirectoryTitle=自定义联想 DLL 目录
english.LenovoDllDirectoryTitle=Custom Lenovo DLL directory
chinesesimplified.LenovoDllDirectorySubtitle=请选择目录
english.LenovoDllDirectorySubtitle=Choose a directory
chinesesimplified.LenovoDllDirectoryDescription=目录中应包含 VantageAddins 和/或 LenovoPcManager 子目录。
english.LenovoDllDirectoryDescription=The directory should contain a VantageAddins and/or LenovoPcManager subdirectory.
chinesesimplified.LenovoDllDirectoryMissing=请选择一个存在的目录。
english.LenovoDllDirectoryMissing=Choose an existing directory.
chinesesimplified.LenovoDllLayoutInvalid=所选目录中没有找到 VantageAddins 或 LenovoPcManager 子目录。
english.LenovoDllLayoutInvalid=The selected directory does not contain a VantageAddins or LenovoPcManager subdirectory.
chinesesimplified.InstallDirectoryNotEmpty=安装文件夹已包含文件：%n%n%1%n%n继续安装会删除该文件夹中的全部文件和子文件夹。是否继续？
english.InstallDirectoryNotEmpty=The installation folder already contains files:%n%n%1%n%nContinuing will delete every file and subfolder in this directory. Continue?
chinesesimplified.InstallDirectoryUnsafe=不能清空所选目录，因为它是系统目录或范围过大的目录：%n%n%1%n%n请新建并选择一个专用于 ThinkBook Toolkit 的子文件夹。
english.InstallDirectoryUnsafe=Setup cannot clear the selected directory because it is a system directory or is too broad:%n%n%1%n%nCreate and select a dedicated subfolder for ThinkBook Toolkit.
chinesesimplified.PreserveFanBackend=检测到版本兼容（%1）的现有风扇后端。是否在覆盖安装时保留该 DLL？%n%n默认选择保留；选择“否”将使用安装包中的后端。
english.PreserveFanBackend=A compatible existing fan backend (version %1) was found. Keep this DLL during the overwrite installation?%n%nThe default is to keep it; choose No to use the backend included with Setup.
chinesesimplified.IncompatibleFanBackend=检测到安装目录中的风扇后端 DLL 版本不符合当前要求。%n%n检测到的版本：%1%n要求的版本：%2%n%n该自定义风扇后端 DLL 将被安装包内置后端替换。
english.IncompatibleFanBackend=The fan-backend DLL in the installation directory does not match the required version.%n%nDetected version: %1%nRequired version: %2%n%nThis custom fan-backend DLL will be replaced by the backend included with Setup.
chinesesimplified.UnknownFanBackendVersion=无法读取
english.UnknownFanBackendVersion=unavailable
chinesesimplified.PreserveFanBackendFailed=无法在覆盖安装前暂存现有风扇后端：%1
english.PreserveFanBackendFailed=Could not preserve the existing fan backend before overwriting the installation: %1
chinesesimplified.RestoreFanBackendFailed=安装已完成，但无法恢复选择保留的风扇后端：%1
english.RestoreFanBackendFailed=Setup completed, but the preserved fan backend could not be restored: %1
chinesesimplified.ToolkitStillRunning=ThinkBook Toolkit 仍在运行，安装程序无法安全地自动退出这个版本。%n%n请在系统托盘中右键单击 ThinkBook Toolkit 图标并选择“退出”。程序完全退出后，点击“重试”。%n%n请勿使用任务管理器强制结束进程，以免风扇保持在最后写入的转速。
english.ToolkitStillRunning=ThinkBook Toolkit is still running and this version cannot be closed safely by Setup.%n%nRight-click the ThinkBook Toolkit tray icon and choose Exit. After the application has completely exited, click Retry.%n%nDo not force-end the process in Task Manager, because the fans could remain at the last written speed.
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"; Check: ShouldCleanInstallDirectory

[Icons]
Name: "{group}\ThinkBook Toolkit"; Filename: "{app}\ThinkBookToolkit.exe"
Name: "{autodesktop}\ThinkBook Toolkit"; Filename: "{app}\ThinkBookToolkit.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create {#GuardianServiceName} binPath= ""\""{app}\ThinkBookToolkit.exe\"" --fan-watchdog-service"" start= demand DisplayName= ""ThinkBook Toolkit Guardian"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#GuardianServiceName} ""Restores firmware automatic fan control if ThinkBook Toolkit exits unexpectedly."""; Flags: runhidden waituntilterminated
Filename: "{app}\ThinkBookToolkit.exe"; Parameters: "--configure-lenovo-dll-directory ""{code:CustomLenovoDllDirectory}"""; Flags: runhidden waituntilterminated; Check: UseCustomLenovoDllDirectory
Filename: "{app}\ThinkBookToolkit.exe"; Parameters: "--configure-lenovo-dll-directory"; Flags: runhidden waituntilterminated; Check: not UseCustomLenovoDllDirectory
Filename: "{app}\ThinkBookToolkit.exe"; Description: "{cm:LaunchProgram,ThinkBook Toolkit}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#GuardianServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopThinkBookToolkitGuardian"
Filename: "{sys}\sc.exe"; Parameters: "delete {#GuardianServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteThinkBookToolkitGuardian"

[Code]
var
  LenovoDllOptionPage: TInputOptionWizardPage;
  LenovoDllDirectoryPage: TInputDirWizardPage;
  ConfirmedDeleteDirectory: String;
  PreserveExistingFanBackend: Boolean;
  FanBackendDecisionDirectory: String;
  PreservedFanBackendPath: String;

const
  FanBackendFileName = 'ThinkBookToolkit.FanBackend.dll';

function NormalizedDirectory(Path: String): String;
begin
  Result := RemoveBackslashUnlessRoot(Path);
end;

function DirectoryHasContents(Path: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(Path) then
    Exit;

  if FindFirst(AddBackslash(Path) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsUnsafeCleanupDirectory(Path: String): Boolean;
begin
  Path := NormalizedDirectory(Path);
  Result :=
    (Length(Path) <= 3) or
    SameText(Path, NormalizedDirectory(ExpandConstant('{win}'))) or
    SameText(Path, NormalizedDirectory(ExpandConstant('{sys}'))) or
    SameText(Path, NormalizedDirectory(ExpandConstant('{commonpf64}'))) or
    SameText(Path, NormalizedDirectory(ExpandConstant('{commonappdata}'))) or
    SameText(Path, NormalizedDirectory(GetEnv('USERPROFILE'))) or
    SameText(Path, NormalizedDirectory(ExpandConstant('{localappdata}'))) or
    SameText(Path, NormalizedDirectory(ExpandConstant('{userappdata}'))) or
    SameText(Path, NormalizedDirectory(ExpandConstant('{userdocs}')));
end;

function HasDotNet9DesktopRuntime: Boolean;
var
  RuntimeRoot: String;
  FindRec: TFindRec;
begin
  Result := False;
  RuntimeRoot := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(AddBackslash(RuntimeRoot) + '9.*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function TryGetExistingFanBackendVersion(
  Path: String;
  var ExistingVersion: String): Boolean;
var
  BackendPath: String;
begin
  Result := False;
  ExistingVersion := '';
  BackendPath := AddBackslash(Path) + FanBackendFileName;
  if not FileExists(BackendPath) then
    Exit;

  Result := True;
  if not GetVersionNumbersString(BackendPath, ExistingVersion) then
    ExistingVersion := CustomMessage('UnknownFanBackendVersion');
end;

function PreserveFanBackendBeforeCleanup: String;
var
  ExistingBackendPath: String;
begin
  Result := '';
  if not PreserveExistingFanBackend then
    Exit;

  ExistingBackendPath := AddBackslash(ConfirmedDeleteDirectory) +
    FanBackendFileName;
  PreservedFanBackendPath := ExpandConstant(
    '{tmp}\ThinkBookToolkit.FanBackend.preserved.dll');
  if not CopyFile(
           ExistingBackendPath,
           PreservedFanBackendPath,
           False) then
  begin
    Result := FmtMessage(
      CustomMessage('PreserveFanBackendFailed'), [ExistingBackendPath]);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RuntimePath: String;
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\sc.exe'),
    'stop {#GuardianServiceName}',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Exec(
    ExpandConstant('{sys}\sc.exe'),
    'delete {#GuardianServiceName}',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Result := PreserveFanBackendBeforeCleanup;
  if Result <> '' then
    Exit;
  if HasDotNet9DesktopRuntime then
    Exit;

  try
    DownloadTemporaryFile(
      '{#RuntimeUrl}',
      '{#RuntimeInstaller}',
      '{#RuntimeSha256}',
      nil);
    RuntimePath := ExpandConstant('{tmp}\{#RuntimeInstaller}');
  except
    Result := FmtMessage(
      CustomMessage('RuntimeDownloadError'), [GetExceptionMessage]);
    Exit;
  end;

  if not Exec(
    RuntimePath,
    '/install /quiet /norestart',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := CustomMessage('RuntimeLaunchError');
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 1641) and (ResultCode <> 3010) then
  begin
    Result := FmtMessage(
      CustomMessage('RuntimeInstallError'), [IntToStr(ResultCode)]);
    Exit;
  end;
  if (ResultCode = 1641) or (ResultCode = 3010) then
    NeedsRestart := True;
end;

function ToolkitIsRunning: Boolean;
begin
  Result := CheckForMutexes('Local\ThinkBookToolkit.Application.v1');
end;

procedure StopGuardianServiceBeforeInstall;
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\net.exe'),
    'stop {#GuardianServiceName} /y',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Exec(
    ExpandConstant('{sys}\sc.exe'),
    'delete {#GuardianServiceName}',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function EnsureToolkitStoppedBeforeInstall: Boolean;
var
  ApplicationPath: String;
  ResultCode: Integer;
  WaitIndex: Integer;
begin
  Result := True;
  while ToolkitIsRunning do
  begin
    ApplicationPath := ExpandConstant('{app}\ThinkBookToolkit.exe');
    if FileExists(ApplicationPath) then
    begin
      Exec(
        ApplicationPath,
        '--exit-for-update',
        ExpandConstant('{app}'),
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode);
    end;

    for WaitIndex := 1 to 20 do
    begin
      if not ToolkitIsRunning then
        Break;
      Sleep(250);
    end;
    if not ToolkitIsRunning then
      Break;

    if SuppressibleMsgBox(
         CustomMessage('ToolkitStillRunning'),
         mbError,
         MB_RETRYCANCEL,
         IDRETRY) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;

  StopGuardianServiceBeforeInstall;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Destination: String;
begin
  if (CurStep <> ssPostInstall) or
     not PreserveExistingFanBackend or
     (PreservedFanBackendPath = '') then
  begin
    Exit;
  end;

  Destination := ExpandConstant('{app}\') + FanBackendFileName;
  if not CopyFile(PreservedFanBackendPath, Destination, False) then
  begin
    MsgBox(
      FmtMessage(
        CustomMessage('RestoreFanBackendFailed'), [Destination]),
      mbError,
      MB_OK);
  end;
end;

function JsonValueStart(
  Content: String;
  PropertyName: String;
  var ValueStart: Integer): Boolean;
var
  Marker: String;
  PropertyPosition: Integer;
  ColonPosition: Integer;
  Remaining: String;
begin
  Result := False;
  Marker := '"' + PropertyName + '"';
  PropertyPosition := Pos(Marker, Content);
  if PropertyPosition = 0 then
    Exit;

  Remaining := Copy(
    Content,
    PropertyPosition + Length(Marker),
    Length(Content));
  ColonPosition := Pos(':', Remaining);
  if ColonPosition = 0 then
    Exit;

  ValueStart := PropertyPosition + Length(Marker) + ColonPosition;
  while (ValueStart <= Length(Content)) and
        (Content[ValueStart] <= ' ') do
  begin
    ValueStart := ValueStart + 1;
  end;
  Result := ValueStart <= Length(Content);
end;

function TryReadJsonString(
  Content: String;
  PropertyName: String;
  var Value: String): Boolean;
var
  Position: Integer;
  EscapeCode: Char;
  HexValue: Integer;
begin
  Result := False;
  Value := '';
  if not JsonValueStart(Content, PropertyName, Position) or
     (Content[Position] <> '"') then
  begin
    Exit;
  end;

  Position := Position + 1;
  while Position <= Length(Content) do
  begin
    if Content[Position] = '"' then
    begin
      Result := True;
      Exit;
    end;

    if Content[Position] <> '\' then
    begin
      Value := Value + Content[Position];
      Position := Position + 1;
      Continue;
    end;

    Position := Position + 1;
    if Position > Length(Content) then
      Exit;
    EscapeCode := Content[Position];
    case EscapeCode of
      '"', '\', '/': Value := Value + EscapeCode;
      'b': Value := Value + Chr(8);
      'f': Value := Value + Chr(12);
      'n': Value := Value + #10;
      'r': Value := Value + #13;
      't': Value := Value + #9;
      'u':
        begin
          if Position + 4 > Length(Content) then
            Exit;
          HexValue := StrToIntDef(
            '$' + Copy(Content, Position + 1, 4), -1);
          if HexValue < 0 then
            Exit;
          Value := Value + Chr(HexValue);
          Position := Position + 4;
        end;
    else
      Exit;
    end;
    Position := Position + 1;
  end;
end;

function TryLoadExistingLenovoDllDirectory(
  var Directory: String): Boolean;
var
  ConfigPath: String;
  Content: AnsiString;
  UnicodeContent: String;
begin
  Result := False;
  Directory := '';
  ConfigPath := AddBackslash(GetEnv('USERPROFILE')) +
    '.thinkbook_toolkit\app_settings.csharp.json';
  if not LoadStringFromFile(ConfigPath, Content) then
    Exit;

  UnicodeContent := String(Content);
  if not TryReadJsonString(
           UnicodeContent,
           'CustomLenovoDllDirectory',
           Directory) then
  begin
    Directory := '';
    Exit;
  end;

  Directory := NormalizedDirectory(Trim(Directory));
  Result := Directory <> '';
end;

procedure InitializeWizard;
var
  ExistingLenovoDllDirectory: String;
  UseExistingLenovoDllDirectory: Boolean;
begin
  UseExistingLenovoDllDirectory :=
    TryLoadExistingLenovoDllDirectory(ExistingLenovoDllDirectory);

  LenovoDllOptionPage := CreateInputOptionPage(
    wpSelectDir,
    CustomMessage('LenovoDllOptionTitle'),
    CustomMessage('LenovoDllOptionSubtitle'),
    CustomMessage('LenovoDllOptionDescription'),
    False,
    False);
  LenovoDllOptionPage.Add(CustomMessage('LenovoDllOptionCheck'));
  LenovoDllOptionPage.Values[0] := UseExistingLenovoDllDirectory;

  LenovoDllDirectoryPage := CreateInputDirPage(
    LenovoDllOptionPage.ID,
    CustomMessage('LenovoDllDirectoryTitle'),
    CustomMessage('LenovoDllDirectorySubtitle'),
    CustomMessage('LenovoDllDirectoryDescription'),
    False,
    '');
  LenovoDllDirectoryPage.Add('');
  LenovoDllDirectoryPage.Values[0] := ExistingLenovoDllDirectory;
end;

function UseCustomLenovoDllDirectory: Boolean;
begin
  Result := Assigned(LenovoDllOptionPage) and
            LenovoDllOptionPage.Values[0];
end;

function CustomLenovoDllDirectory(Param: String): String;
begin
  if UseCustomLenovoDllDirectory then
    Result := LenovoDllDirectoryPage.Values[0]
  else
    Result := '';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := Assigned(LenovoDllDirectoryPage) and
            (PageID = LenovoDllDirectoryPage.ID) and
            not UseCustomLenovoDllDirectory;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SelectedDirectory: String;
  ExistingFanBackendVersion: String;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    Result := EnsureToolkitStoppedBeforeInstall;
    Exit;
  end;
  if CurPageID = wpSelectDir then
  begin
    SelectedDirectory := NormalizedDirectory(WizardDirValue);
    ConfirmedDeleteDirectory := '';
    if not SameText(FanBackendDecisionDirectory, SelectedDirectory) then
    begin
      PreserveExistingFanBackend := False;
      FanBackendDecisionDirectory := '';
    end;
    if DirectoryHasContents(SelectedDirectory) then
    begin
      if IsUnsafeCleanupDirectory(SelectedDirectory) then
      begin
        MsgBox(
          FmtMessage(CustomMessage('InstallDirectoryUnsafe'), [SelectedDirectory]),
          mbError,
          MB_OK);
        Result := False;
        Exit;
      end;
      if SuppressibleMsgBox(
           FmtMessage(CustomMessage('InstallDirectoryNotEmpty'), [SelectedDirectory]),
           mbConfirmation,
           MB_YESNO,
           IDNO) <> IDYES then
      begin
        Result := False;
        Exit;
      end;
      ConfirmedDeleteDirectory := SelectedDirectory;
      if FanBackendDecisionDirectory = '' then
      begin
        FanBackendDecisionDirectory := SelectedDirectory;
        if TryGetExistingFanBackendVersion(
             SelectedDirectory,
             ExistingFanBackendVersion) then
        begin
          if SameText(
               ExistingFanBackendVersion,
               '{#FanBackendFileVersion}') then
          begin
            PreserveExistingFanBackend :=
              SuppressibleMsgBox(
                FmtMessage(CustomMessage('PreserveFanBackend'), ['{#FanBackendFileVersion}']),
                mbConfirmation,
                MB_YESNO,
                IDYES) = IDYES;
          end
          else
          begin
            PreserveExistingFanBackend := False;
            MsgBox(
              FmtMessage(CustomMessage('IncompatibleFanBackend'), [ExistingFanBackendVersion, '{#FanBackendFileVersion}']),
              mbInformation,
              MB_OK);
          end;
        end;
      end;
    end;
  end;

  if Assigned(LenovoDllDirectoryPage) and
     (CurPageID = LenovoDllDirectoryPage.ID) then
  begin
    SelectedDirectory := LenovoDllDirectoryPage.Values[0];
    if not DirExists(SelectedDirectory) then
    begin
      MsgBox(CustomMessage('LenovoDllDirectoryMissing'), mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if not DirExists(AddBackslash(SelectedDirectory) + 'VantageAddins') and
       not DirExists(AddBackslash(SelectedDirectory) + 'LenovoPcManager') then
    begin
      MsgBox(
        CustomMessage('LenovoDllLayoutInvalid'),
        mbError,
        MB_OK);
      Result := False;
    end;
  end;
end;

function ShouldCleanInstallDirectory: Boolean;
var
  InstallDirectory: String;
begin
  InstallDirectory := NormalizedDirectory(ExpandConstant('{app}'));
  Result :=
    (ConfirmedDeleteDirectory <> '') and
    SameText(InstallDirectory, ConfirmedDeleteDirectory) and
    not IsUnsafeCleanupDirectory(InstallDirectory);
end;
