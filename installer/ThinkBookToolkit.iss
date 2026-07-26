#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\ThinkBookToolkit-0.1.1-win-x64-framework-dependent"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef ChineseMessagesFile
  #define ChineseMessagesFile "compiler:Languages\ChineseSimplified.isl"
#endif

#define AppName "ThinkBook Toolkit"
#define AppPublisher "luhongzhen"
#define RuntimeVersion "9.0.18"
#define RuntimeInstaller "windowsdesktop-runtime-9.0.18-win-x64.exe"
#define RuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/9.0.18/windowsdesktop-runtime-9.0.18-win-x64.exe"
#define RuntimeSha256 "12CD00688FC9F8F5187D25911BF656DB61998C264F03EEF4022FF2D9321D6982"

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
Filename: "{app}\ThinkBookToolkit.exe"; Parameters: "--configure-lenovo-dll-directory ""{code:CustomLenovoDllDirectory}"""; Flags: runhidden waituntilterminated; Check: UseCustomLenovoDllDirectory
Filename: "{app}\ThinkBookToolkit.exe"; Parameters: "--configure-lenovo-dll-directory"; Flags: runhidden waituntilterminated; Check: not UseCustomLenovoDllDirectory

[Code]
var
  LenovoDllOptionPage: TInputOptionWizardPage;
  LenovoDllDirectoryPage: TInputDirWizardPage;
  ConfirmedDeleteDirectory: String;

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

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RuntimePath: String;
  ResultCode: Integer;
begin
  Result := '';
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

procedure InitializeWizard;
begin
  LenovoDllOptionPage := CreateInputOptionPage(
    wpSelectDir,
    CustomMessage('LenovoDllOptionTitle'),
    CustomMessage('LenovoDllOptionSubtitle'),
    CustomMessage('LenovoDllOptionDescription'),
    False,
    False);
  LenovoDllOptionPage.Add(CustomMessage('LenovoDllOptionCheck'));
  LenovoDllOptionPage.Values[0] := False;

  LenovoDllDirectoryPage := CreateInputDirPage(
    LenovoDllOptionPage.ID,
    CustomMessage('LenovoDllDirectoryTitle'),
    CustomMessage('LenovoDllDirectorySubtitle'),
    CustomMessage('LenovoDllDirectoryDescription'),
    False,
    '');
  LenovoDllDirectoryPage.Add('');
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
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    SelectedDirectory := NormalizedDirectory(WizardDirValue);
    ConfirmedDeleteDirectory := '';
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
