; Inno Setup 6. Compile with /DLegacy for Windows 7 SP1 / .NET Framework 4.8.
#if Ver < EncodeVer(6, 3, 0) || Ver >= EncodeVer(7, 0, 0)
  #error Inno Setup 6.3 or later in the 6.x series is required.
#endif
#define AppName "Program Manager"
#define AppVersion "0.1.1"
#define AppExe "ProgramManager.exe"
#ifdef Legacy
  #define PublishDir "..\artifacts\publish\win7"
  #define OutputName "ProgramManager-Setup-" + AppVersion + "-win7"
#else
  #define PublishDir "..\artifacts\publish\win10-x64"
  #define OutputName "ProgramManager-Setup-" + AppVersion
#endif

[Setup]
AppId={{430AD44C-84B5-44AB-B870-0717B39671C9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=yunhyok
DefaultDirName={localappdata}\Programs\Program Manager
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\installers
OutputBaseFilename={#OutputName}
SetupIconFile=..\src\ProgramManager\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
#ifdef Legacy
MinVersion=6.1sp1
#else
MinVersion=10.0.14393
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Windows 로그인 시 트레이에서 자동 실행"; GroupDescription: "추가 옵션:"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ProgramManager"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} {#AppVersion} 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser

#ifdef Legacy
[Code]
function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(
      'Program Manager {#AppVersion}에는 .NET Framework 4.8이 필요합니다.' + #13#10 +
      'Microsoft에서 .NET Framework 4.8을 설치한 뒤 다시 실행하세요.' + #13#10 +
      '이 설치 프로그램은 런타임을 자동으로 다운로드하지 않습니다.' + #13#10 + #13#10 +
      'Program Manager requires .NET Framework 4.8. Install it from Microsoft, then run Setup again.',
      mbCriticalError, MB_OK, IDOK);
end;
#endif
