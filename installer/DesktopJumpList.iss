#define MyAppName "Desktop Jump List"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "Desktop Jump List"
#define MyAppURL "https://github.com/1182793425/Desktop-Jump-List"
#define MyAppExeName "DesktopJumpList.exe"

[Setup]
AppId={{F5F24237-8CCE-48CD-851C-1AA23FB650BA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\Desktop Jump List
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\installer
OutputBaseFilename=DesktopJumpList-Setup-v2.0.0
SetupIconFile=..\src\DesktopJumpList\Assets\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "登录 Windows 时自动启动 Desktop Jump List"; GroupDescription: "其他选项："; Flags: unchecked
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他选项："; Flags: unchecked

[Files]
Source: "..\artifacts\DesktopJumpList-v2.0.0-runtime\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DesktopJumpList"; ValueData: """{app}\{#MyAppExeName}"" --autostart"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function HasNet8DesktopRuntimeAt(const RuntimeRoot: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(RuntimeRoot) + '8.*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function HasDesktopRuntime(): Boolean;
begin
  Result := HasNet8DesktopRuntimeAt(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
            HasNet8DesktopRuntimeAt(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not HasDesktopRuntime() then begin
    MsgBox('此程序需要 .NET 8 Desktop Runtime。请先安装运行时，然后重新运行安装程序。' + #13#10 + #13#10 + '下载地址：https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0', mbInformation, MB_OK);
    ShellExec('open', 'https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    Result := False;
  end;
end;
