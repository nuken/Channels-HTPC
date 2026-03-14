[Setup]
AppName=Channels HTPC
AppVersion=0.1.0-alpha
AppPublisher=Channels HTPC Development
DefaultDirName={autopf}\ChannelsHTPC
DefaultGroupName=Channels HTPC
OutputBaseFilename=ChannelsHTPC_Alpha_Setup
UninstallDisplayIcon={app}\ChannelsHTPC.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
; NEW: Explicitly tell Inno to drop the finished .exe into an Output folder
OutputDir=Output

[Files]
; FIX: Use a relative path from the root of your GitHub repository
Source: "bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Channels HTPC"; Filename: "{app}\ChannelsHTPC.exe"
Name: "{autodesktop}\Channels HTPC"; Filename: "{app}\ChannelsHTPC.exe"

[Run]
Filename: "{app}\ChannelsHTPC.exe"; Description: "{cm:LaunchProgram,Channels HTPC}"; Flags: nowait postinstall skipifsilent
