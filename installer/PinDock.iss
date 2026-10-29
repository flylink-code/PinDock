#define AppName "PinDock"
#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif

[Setup]
AppId={{8F96D13B-4C7E-4B18-9F53-0D726EA18C90}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=PinDock-Setup-{#AppVersion}
SetupIconFile=..\src\PinDock\Assets\PinDock.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\PinDock.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\PinDock.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PinDock.exe"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent
