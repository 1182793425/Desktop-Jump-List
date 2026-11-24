; Jump — Desktop Jump List installer (Inno Setup 6)
; Build with ISCC.exe against the published self-contained output.

#define MyAppName "Jump"
#define MyAppVersion "3.10.1"
#define MyAppPublisher "ShiPengbo"
#define MyAppExeName "Jump.exe"

[Setup]
AppId={{6F2E8B3A-1C4D-4E7F-9A5B-3D2C7E9B4A1F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=常驻系统托盘的快捷启动器，不占用任务栏
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\installer
OutputBaseFilename=Jump-{#MyAppVersion}-setup
SetupIconFile=..\src\DesktopJumpList\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
AppMutex=DesktopJumpList.Jump.v3
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: checkedonce
Name: "startmenu"; Description: "创建开始菜单快捷方式"; GroupDescription: "附加快捷方式："; Flags: checkedonce
Name: "autostart"; Description: "登录 Windows 时自动启动（系统托盘）"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; A standalone copy of the app icon lets shortcuts point at a fresh icon path,
; bypassing any stale per-executable Windows icon cache from older versions.
Source: "..\src\DesktopJumpList\Assets\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; IconIndex: 0; Comment: "Jump（系统托盘）"; Tasks: startmenu
Name: "{autoprograms}\{#MyAppName}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"; Tasks: startmenu
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; IconIndex: 0; Comment: "Jump（系统托盘）"; Tasks: desktopicon

[Registry]
; Per-user autostart, removed again on uninstall. The app's own tray menu writes the same value.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Jump"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure SHChangeNotify(EventId: LongWord; Flags: LongWord; Item1: LongWord; Item2: LongWord);
  external 'SHChangeNotify@shell32.dll stdcall';

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SHChangeNotify($08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED: refresh shell/icon caches
end;
