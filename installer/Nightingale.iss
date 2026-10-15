#ifndef AppVersion
  #define AppVersion "0.1.2"
#endif
#ifndef PublishDir
  #define PublishDir "..\dist\Nightingale-" + AppVersion + "-win-x64"
#endif
#define AppName "夜莺播放器"
#define AppExe "Nightingale.exe"
#define AppProgId "Nightingale.Media"
#define MediaExtensions ".mp4|.mkv|.mov|.webm|.avi|.m4v|.ts|.mp3|.wav|.flac|.m4a|.aac|.ogg|.opus"

[Setup]
AppId={{690B1894-830B-48C6-BFB6-2C6EFA2A73E3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Nightingale
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription={#AppName} Windows 安装程序
DefaultDirName={localappdata}\Programs\Nightingale
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=Nightingale-{#AppVersion}-setup
SetupIconFile=..\assets\nightingale.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.DesktopShortcut=创建桌面快捷方式
chinesesimplified.AdditionalShortcuts=附加快捷方式：
chinesesimplified.LaunchPlayer=启动夜莺播放器
english.DesktopShortcut=Create a desktop shortcut
english.AdditionalShortcuts=Additional shortcuts:
english.LaunchPlayer=Launch Nightingale

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalShortcuts}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Nightingale"; Flags: uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Nightingale\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Nightingale\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "本地音视频播放器，支持字幕、播放恢复与睡眠定时。"
Root: HKCU; Subkey: "Software\Nightingale\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Nightingale"; ValueData: "Software\Nightingale\Capabilities"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\{#AppProgId}"; ValueType: string; ValueData: "Nightingale media file"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#AppProgId}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\{#AppProgId}\shell\open"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"
Root: HKCU; Subkey: "Software\Classes\{#AppProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
#define ExtensionsRemaining MediaExtensions
#sub RegisterExtension
  #define SeparatorPosition Pos("|", ExtensionsRemaining + "|")
  #define Extension Copy(ExtensionsRemaining, 1, SeparatorPosition - 1)
Root: HKCU; Subkey: "Software\Nightingale\Capabilities\FileAssociations"; ValueType: string; ValueName: "{#Extension}"; ValueData: "{#AppProgId}"
Root: HKCU; Subkey: "Software\Classes\{#Extension}\OpenWithProgids"; ValueType: none; ValueName: "{#AppProgId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: "{#Extension}"; ValueData: ""
  #expr ExtensionsRemaining = Copy(ExtensionsRemaining, SeparatorPosition + 1, Len(ExtensionsRemaining))
#endsub
#for {0; Len(ExtensionsRemaining) > 0; 0} RegisterExtension

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchPlayer}"; Flags: nowait postinstall skipifsilent
