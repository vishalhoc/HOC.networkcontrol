[Setup]
AppName=WinNetControl
AppVersion=2.1.0
AppPublisher=Vishal
AppPublisherURL=https://github.com/vishalhoc/HOC.networkcontrol
DefaultDirName={autopf}\WinNetControl
DefaultGroupName=WinNetControl
OutputBaseFilename=WinNetControl_Setup_v2.1.0
Compression=lzma2/ultra64
SolidCompression=yes
SetupIconFile=Assets\AppIcon.ico
UninstallDisplayIcon={app}\WinNetControl.exe
DisableProgramGroupPage=yes
; If Portable is selected, don't create an uninstaller
Uninstallable=not IsTaskSelected('portable')
; Default installation type dialog
PrivilegesRequired=lowest

[Tasks]
Name: "full"; Description: "Full Installation (Includes uninstaller and Start Menu shortcuts)"; GroupDescription: "Installation Mode:"; Flags: exclusive
Name: "portable"; Description: "Portable Installation (Extract files only)"; GroupDescription: "Installation Mode:"; Flags: exclusive unchecked
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Check: not IsTaskSelected('portable')

[Files]
Source: "bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\WinNetControl"; Filename: "{app}\WinNetControl.exe"; Check: not IsTaskSelected('portable')
Name: "{autodesktop}\WinNetControl"; Filename: "{app}\WinNetControl.exe"; Tasks: desktopicon; Check: not IsTaskSelected('portable')

[Run]
Filename: "{app}\WinNetControl.exe"; Description: "Launch WinNetControl"; Flags: nowait postinstall skipifsilent
