; Inno Setup 6. Compile with /DLegacy for Windows 7 SP1 / .NET Framework 4.8.
#if Ver < EncodeVer(6, 3, 0) || Ver >= EncodeVer(7, 0, 0)
  #error Inno Setup 6.3 or later in the 6.x series is required.
#endif
#define AppName "Program Manager"
#define AppVersion "0.5.0"
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
Filename: "{app}\{#AppExe}"; Description: "{#AppName} {#AppVersion} 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: NormalInstall

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{430AD44C-84B5-44AB-B870-0717B39671C9}_is1';
  QueryAndSynchronize = $00101000;
  WaitSignaled = 0;
  ErrorInvalidParameter = 87;

var
  UpdateMode, UpdateValid, UpdateSucceeded, WaitAttempted, OriginExited: Boolean;
  BackupReady, UpdateFilesStarted: Boolean;
  UpdateInstall, UpdateData, UpdateLog, UpdateError, UpdateBackup: String;
  OriginProcess: THandle;

function OpenProcess(Access: Cardinal; InheritHandle: Boolean; ProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageName(Process: THandle; Flags: Cardinal; ImageName: String; var Size: Cardinal): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetLastError: Cardinal;
  external 'GetLastError@kernel32.dll stdcall';
function GetFileAttributes(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function NormalInstall: Boolean;
begin
  Result := not UpdateMode;
end;

function ParameterValue(Name: String; var Value: String): Boolean;
var
  I, Matches: Integer;
  Prefix, Argument: String;
begin
  Prefix := '/' + Name + '=';
  Matches := 0;
  for I := 1 to ParamCount do begin
    Argument := ParamStr(I);
    if CompareText(Copy(Argument, 1, Length(Prefix)), Prefix) = 0 then begin
      Matches := Matches + 1;
      Value := Copy(Argument, Length(Prefix) + 1, Length(Argument));
    end;
  end;
  Result := (Matches = 1) and (Value <> '');
end;

function AbsoluteDirectory(Value: String): Boolean;
var I: Integer;
begin
  Result := False;
  if Length(Value) < 3 then exit;
  if not (((Value[2] = ':') and ((Value[3] = '\') or (Value[3] = '/'))) or
      (Copy(Value, 1, 2) = '\\')) then exit;
  if (Copy(Value, 1, 4) = '\\.\') or (Copy(Value, 1, 4) = '\\?\') then exit;
  for I := 1 to Length(Value) do
    if (Ord(Value[I]) < 32) or (Ord(Value[I]) = 127) or (Value[I] = '"') or
        (Value[I] = '*') or (Value[I] = '?') then exit;
  Result := DirExists(Value);
end;

function SameDirectory(Left, Right: String): Boolean;
begin
  Result := CompareText(AddBackslash(ExpandFileName(Left)), AddBackslash(ExpandFileName(Right))) = 0;
end;

function RegisteredDirectory(Directory: String): Boolean;
var Registered: String;
begin
  Result := RegQueryStringValue(HKCU32, UninstallKey, 'InstallLocation', Registered) and
    SameDirectory(Directory, Registered);
  if (not Result) and IsWin64 then
    Result := RegQueryStringValue(HKCU64, UninstallKey, 'InstallLocation', Registered) and
      SameDirectory(Directory, Registered);
end;

function WriteUpdateResult(Status, MessageText: String): Boolean;
var Lines: TArrayOfString;
begin
  SetArrayLength(Lines, 5);
  Lines[0] := '[Update]';
  Lines[1] := 'Status=' + Status;
  Lines[2] := 'Version={#AppVersion}';
  Lines[3] := 'LogPath=' + UpdateLog;
  Lines[4] := 'Message=' + MessageText;
  Result := SaveStringsToUTF8File(AddBackslash(UpdateData) + 'update-result.ini', Lines, False);
  if not Result then Log('Could not write the update result.');
end;

function InitializeUpdate: Boolean;
var
  PidText, ImageName: String;
  Pid: Integer;
  ImageSize: Cardinal;
begin
  Result := False;
  if not (ParameterValue('PMPID', PidText) and ParameterValue('PMDATA', UpdateData) and
      ParameterValue('DIR', UpdateInstall) and ParameterValue('LOG', UpdateLog)) then exit;
  Pid := StrToIntDef(PidText, 0);
  if (Pid <= 0) or not AbsoluteDirectory(UpdateData) or not AbsoluteDirectory(UpdateInstall) then exit;
  UpdateData := ExpandFileName(UpdateData);
  UpdateInstall := ExpandFileName(UpdateInstall);
  if SameDirectory(UpdateData, UpdateInstall) then exit;
  if not RegisteredDirectory(UpdateInstall) or not FileExists(AddBackslash(UpdateInstall) + '{#AppExe}') then exit;
  if CompareText(UpdateLog, AddBackslash(UpdateData) + 'update-install.log') <> 0 then exit;
  OriginProcess := OpenProcess(QueryAndSynchronize, False, Pid);
  if OriginProcess = 0 then begin
    // The app can already have exited while Setup's loader was starting.
    if GetLastError <> ErrorInvalidParameter then exit;
    OriginExited := True;
  end else begin
    // A terminated process can retain its PID while another process still holds a handle.
    OriginExited := WaitForSingleObject(OriginProcess, 0) = WaitSignaled;
    if not OriginExited then begin
      ImageSize := 32768;
      SetLength(ImageName, ImageSize);
      if not QueryFullProcessImageName(OriginProcess, 0, ImageName, ImageSize) then begin
        // Also cover exit between the zero-time wait and the image query.
        OriginExited := WaitForSingleObject(OriginProcess, 0) = WaitSignaled;
        if not OriginExited then exit;
      end else begin
        SetLength(ImageName, ImageSize);
        if CompareText(ExpandFileName(ImageName), AddBackslash(UpdateInstall) + '{#AppExe}') <> 0 then exit;
      end;
    end;
  end;
  UpdateValid := True;
  UpdateError := '업데이트 설치가 완료되지 않았습니다. 설치 로그를 확인하세요.';
  Result := WriteUpdateResult('pending', UpdateError);
end;

function InitializeSetup: Boolean;
var I, Count: Integer;
begin
  Count := 0;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/PMUPDATE') = 0 then Count := Count + 1;
  UpdateMode := Count > 0;
  Result := True;
  if UpdateMode then begin
    if Count <> 1 then Result := False
    else Result := InitializeUpdate;
    if not Result then begin
      Log('Rejected invalid or unavailable Program Manager update handoff.');
      SuppressibleMsgBox('자동 업데이트 정보를 확인할 수 없습니다. Program Manager를 다시 실행해 주세요.', mbCriticalError, MB_OK, IDOK);
      exit;
    end;
  end;
#ifdef Legacy
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(
      'Program Manager {#AppVersion}에는 .NET Framework 4.8이 필요합니다.' + #13#10 +
      'Microsoft에서 .NET Framework 4.8을 설치한 뒤 다시 실행하세요.' + #13#10 +
      '이 설치 프로그램은 런타임을 자동으로 다운로드하지 않습니다.' + #13#10 + #13#10 +
      'Program Manager requires .NET Framework 4.8. Install it from Microsoft, then run Setup again.',
      mbCriticalError, MB_OK, IDOK);
#endif
end;

function WaitForOrigin: Boolean;
begin
  if not WaitAttempted then begin
    WaitAttempted := True;
    if not OriginExited then
      OriginExited := WaitForSingleObject(OriginProcess, 30000) = WaitSignaled;
    if not OriginExited then
      UpdateError := 'Program Manager가 30초 이내에 종료되지 않아 업데이트를 중단했습니다. 앱을 종료한 뒤 다시 시도하세요.';
  end;
  Result := OriginExited;
end;

function CopyInstallDirectory(Source, Destination: String; IsBackup: Boolean): Boolean;
var
  Entry: TFindRec;
  FromPath, ToPath: String;
  DestinationAttributes: Cardinal;
begin
  Result := False;
  // A custom data directory can be inside the install directory. Never copy or restore its contents.
  if IsBackup and SameDirectory(Source, UpdateData) then begin Result := True; exit; end;
  DestinationAttributes := GetFileAttributes(Destination);
  if ((GetFileAttributes(Source) and $400) <> 0) or
      ((DestinationAttributes <> $FFFFFFFF) and ((DestinationAttributes and $400) <> 0)) then begin
    Log('Update backup/restore refused a directory reparse point: ' + Source);
    exit;
  end;
  if not ForceDirectories(Destination) then exit;
  if not FindFirst(AddBackslash(Source) + '*', Entry) then begin
    Result := (GetLastError = 2) or (GetLastError = 18);
    exit;
  end;
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
        FromPath := AddBackslash(Source) + Entry.Name;
        ToPath := AddBackslash(Destination) + Entry.Name;
        if IsBackup and SameDirectory(FromPath, UpdateData) then continue;
        DestinationAttributes := GetFileAttributes(ToPath);
        if ((Entry.Attributes and $400) <> 0) or
            ((DestinationAttributes <> $FFFFFFFF) and ((DestinationAttributes and $400) <> 0)) then begin
          Log('Update backup/restore refused a reparse point: ' + FromPath);
          exit;
        end;
        if (Entry.Attributes and $10) <> 0 then begin
          if not CopyInstallDirectory(FromPath, ToPath, IsBackup) then exit;
        end else begin
          // An unchanged file can be read-only or held by another instance; it needs no restoration.
          if not IsBackup and FileExists(ToPath) then
            if GetSHA256OfFile(FromPath) = GetSHA256OfFile(ToPath) then continue;
          if not FileCopy(FromPath, ToPath, False) then begin
            Log('Update backup/restore could not copy: ' + FromPath + ' -> ' + ToPath);
            exit;
          end;
        end;
      end;
    until not FindNext(Entry);
    Result := GetLastError = 18; // ERROR_NO_MORE_FILES; other enumeration errors mean an incomplete copy.
  finally
    FindClose(Entry);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not UpdateMode then exit;
  if not UpdateValid or not SameDirectory(ExpandConstant('{app}'), UpdateInstall) then
    Result := '업데이트 설치 경로가 기존 설치 경로와 일치하지 않습니다.'
  else if not WaitForOrigin then Result := UpdateError;
  if (Result = '') and not BackupReady then begin
    UpdateBackup := ExpandConstant('{tmp}\ProgramManager-previous');
    Log('Backing up the registered installation before replacing files.');
    try
      if Pos(Uppercase(AddBackslash(UpdateInstall)), Uppercase(AddBackslash(UpdateBackup))) <> 1 then
        BackupReady := CopyInstallDirectory(UpdateInstall, UpdateBackup, True);
    except
      Log('Could not finish the installation backup: ' + GetExceptionMessage);
    end;
    if not BackupReady then
      Result := '기존 프로그램 파일을 백업하지 못해 업데이트를 중단했습니다. 설치 로그를 확인하세요.';
  end;
  if Result <> '' then begin
    UpdateError := Result;
    if UpdateValid then WriteUpdateResult('failed', UpdateError);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if UpdateValid and (CurStep = ssInstall) then UpdateFilesStarted := True;
  if CurStep = ssDone then UpdateSucceeded := True;
end;

procedure DeinitializeSetup;
var
  ResultCode: Integer;
  DataArgument, Exe: String;
  Restored: Boolean;
begin
  try
    if not UpdateValid then exit;
    if not WaitForOrigin then begin
      WriteUpdateResult('failed', UpdateError);
      exit; // The original process is still running; do not start a second instance.
    end;
    if not UpdateSucceeded and UpdateFilesStarted then begin
      Log('Restoring the previous application files after an incomplete update.');
      Restored := False;
      try
        if BackupReady then Restored := CopyInstallDirectory(UpdateBackup, UpdateInstall, False);
      except
        Log('Could not restore the previous application files: ' + GetExceptionMessage);
      end;
      if not Restored then begin
        WriteUpdateResult('failed', '업데이트 실패 후 기존 파일을 완전히 복구하지 못했습니다. 설치 파일을 직접 실행해 복구하세요.');
        MsgBox('Program Manager 파일을 완전히 복구하지 못했습니다.' + #13#10 +
          '설치 파일을 직접 실행해 복구하세요.' + #13#10 + '설치 로그: ' + UpdateLog, mbError, MB_OK);
        exit;
      end;
      UpdateError := '업데이트를 완료하지 못해 이전 프로그램 파일을 복구했습니다. 설치 로그를 확인하세요.';
    end;
    if UpdateSucceeded then WriteUpdateResult('succeeded', 'Program Manager {#AppVersion} 업데이트가 완료되었습니다.')
    else WriteUpdateResult('failed', UpdateError);
    // This event runs after file installation or rollback, immediately before Setup terminates.
    Exe := AddBackslash(UpdateInstall) + '{#AppExe}';
    DataArgument := UpdateData;
    if Copy(DataArgument, Length(DataArgument), 1) = '\' then DataArgument := DataArgument + '.';
    if not FileExists(Exe) or not Exec(Exe, '--tray --data-dir "' + DataArgument + '"',
        UpdateInstall, SW_HIDE, ewNoWait, ResultCode) then begin
      WriteUpdateResult('failed', '업데이트 후 Program Manager를 다시 실행하지 못했습니다. 설치 로그를 확인하세요.');
      MsgBox('Program Manager를 다시 실행하지 못했습니다.' + #13#10 + '설치 로그: ' + UpdateLog, mbError, MB_OK);
    end;
  finally
    if OriginProcess <> 0 then CloseHandle(OriginProcess);
  end;
end;
