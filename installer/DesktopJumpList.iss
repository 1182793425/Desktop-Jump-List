; Jump — Desktop Jump List installer (Inno Setup 6)
; Build with ISCC.exe against the published single-file exe.

#define MyAppName "Jump"
#define MyAppVersion "3.1.0"
#define MyAppPublisher "ShiPengbo"
#define MyAppExeName "Jump.exe"

[Setup]
AppId={{6F2E8B3A-1C4D-4E7F-9A5B-3D2C7E9B4A1F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=屏幕边缘的快捷启动器
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
CloseApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: checkedonce
Name: "startmenu"; Description: "创建开始菜单快捷方式（可固定到任务栏）"; GroupDescription: "附加快捷方式："; Flags: checkedonce

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenu
Name: "{autoprograms}\{#MyAppName}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"; Tasks: startmenu
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
