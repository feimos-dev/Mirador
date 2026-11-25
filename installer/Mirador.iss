#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define Bundle "..\artifacts\bundle"
#define ServiceName "MiradorHelper"

[Setup]
AppId={{CE00D9D4-6F37-49A0-B17D-16BAA7531E77}
AppName=Mirador
AppVersion={#AppVersion}
AppVerName=Mirador {#AppVersion}
AppPublisher=Finn Zeruhn
AppPublisherURL=https://github.com/feimos-dev/Mirador
DefaultDirName={autopf}\Mirador
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UsePreviousTasks=no
CloseApplications=yes
RestartApplications=no
OutputDir=..\artifacts
OutputBaseFilename=Mirador-{#AppVersion}-setup
SetupIconFile=..\src\Mirador.App\Assets\Mirador.ico
UninstallDisplayIcon={app}\Mirador.exe
UninstallDisplayName=Mirador
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
MinVersion=10.0.19041
#ifdef SignTool
SignTool={#SignTool}
SignToolRetryCount=0
#endif

[InstallDelete]
Type: filesandordirs; Name: "{app}\layer"
Type: filesandordirs; Name: "{app}\standin"
Type: filesandordirs; Name: "{app}\tools"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\monado"
Type: filesandordirs; Name: "{app}\builds"

[Files]
Source: "{#Bundle}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Mirador"; Filename: "{app}\Mirador.exe"

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\Mirador"; ValueType: string; ValueName: "AppPath"; ValueData: "{app}\Mirador.exe"; Flags: uninsdeletevalue uninsdeletekeyifempty

[Run]
Filename: "{app}\Mirador.exe"; Description: "Start Mirador"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
const
  ImplicitLayers = 'SOFTWARE\Khronos\Vulkan\ImplicitLayers';
  OpenXrKey = 'SOFTWARE\Khronos\OpenXR\1';
  AvailableRuntimes = 'SOFTWARE\Khronos\OpenXR\1\AvailableRuntimes';
  MiradorKey = 'SOFTWARE\Mirador';
  QuitMirador = 'Mirador is running. Quit it from its tray icon (Quit), then press Retry.';

function MiradorQuit: Boolean;
begin
  while CheckForMutexes('Local\Mirador') do
    if SuppressibleMsgBox(QuitMirador, mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  Result := True;
end;

function InitializeSetup: Boolean;
begin
  Result := MiradorQuit;
end;

function InitializeUninstall: Boolean;
begin
  Result := MiradorQuit;
end;

function Sc(const Args: String): Integer;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

function ServiceExists: Boolean;
begin
  Result := Sc('query {#ServiceName}') = 0;
end;

procedure StopHelper;
var
  I, Code: Integer;
begin
  if not ServiceExists then
    Exit;
  Sc('stop {#ServiceName}');
  for I := 1 to 60 do
  begin
    if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "if ((Get-Service {#ServiceName}).Status -eq ''Stopped'') { exit 0 } else { exit 1 }"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
      Exit;
    Sleep(500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{autopf}\Mirador')) <> 0 then
  begin
    Result := 'Mirador installs only to ' + ExpandConstant('{autopf}\Mirador') + ': its helper service runs from there as SYSTEM, so only administrators may change that folder.';
    Exit;
  end;

  StopHelper;
  Result := '';
end;

function HasSuffix(const Text, Suffix: String): Boolean;
begin
  Result := (Length(Text) >= Length(Suffix))
    and (Copy(Text, Length(Text) - Length(Suffix) + 1, Length(Suffix)) = Suffix);
end;

procedure RemoveOtherLayers;
var
  Names: TArrayOfString;
  I: Integer;
  Ours, Name: String;
begin
  Ours := Lowercase(ExpandConstant('{app}\layer\mirador_direct_mode.json'));
  if RegGetValueNames(HKLM64, ImplicitLayers, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      Name := Lowercase(Names[I]);
      if (Name <> Ours) and (HasSuffix(Name, '\mirador_direct_mode.json') or HasSuffix(Name, '\dreamair_vk_layer.json')) then
        RegDeleteValue(HKLM64, ImplicitLayers, Names[I]);
    end;
end;

procedure HelperStep(const What: String; Code: Integer);
begin
  if Code <> 0 then
    SuppressibleMsgBox('Mirador''s helper service couldn''t be ' + What + ' (sc.exe error ' + IntToStr(Code) + '). ' +
      'Restart Windows and run this setup again; until then Mirador can''t do the steps that need an administrator.',
      mbError, MB_OK, IDOK);
end;

procedure InstallHelper;
var
  BinPath: String;
begin
  BinPath := '"\"' + ExpandConstant('{app}\Mirador.Helper.exe') + '\""';
  if ServiceExists then
    HelperStep('set up', Sc('config {#ServiceName} binPath= ' + BinPath + ' start= auto DisplayName= "Mirador helper"'))
  else
    HelperStep('installed', Sc('create {#ServiceName} binPath= ' + BinPath + ' start= auto DisplayName= "Mirador helper"'));
  Sc('description {#ServiceName} "Does the steps Mirador needs an administrator for."');
  HelperStep('started', Sc('start {#ServiceName}'));
end;

function IsMiradorsManifest(const Manifest: String): Boolean;
var
  Monado, Builds: String;
begin
  Monado := Lowercase(ExpandConstant('{app}\monado\'));
  Builds := Lowercase(ExpandConstant('{app}\builds\'));
  Result := ((Copy(Lowercase(Manifest), 1, Length(Monado)) = Monado) or (Copy(Lowercase(Manifest), 1, Length(Builds)) = Builds))
    and HasSuffix(Lowercase(Manifest), '\openxr_monado.json');
end;

procedure RemoveMiradorsRuntimes;
var
  Names: TArrayOfString;
  I: Integer;
begin
  if RegGetValueNames(HKLM64, AvailableRuntimes, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if IsMiradorsManifest(Names[I]) then
        RegDeleteValue(HKLM64, AvailableRuntimes, Names[I]);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RemoveOtherLayers;
    InstallHelper;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Active, Recorded: String;
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  if not Exec(ExpandConstant('{app}\Mirador.exe'), '--remove-sign-in', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('Mirador.exe --remove-sign-in could not be started.')
  else if Code <> 0 then
    Log(Format('Mirador.exe --remove-sign-in: exit code %d (the start at sign-in could not be removed).', [Code]));

  if RegQueryStringValue(HKLM64, OpenXrKey, 'ActiveRuntime', Active) and IsMiradorsManifest(Active) then
  begin
    if SuppressibleMsgBox('Monado is the active OpenXR runtime. Switch back to the runtime that was active before?',
      mbConfirmation, MB_YESNO, IDYES) = IDYES then
    begin
      StopHelper;
      if not RegQueryStringValue(HKLM64, MiradorKey, 'PreviousActiveRuntime', Recorded) then
        Recorded := '';
      if not Exec(ExpandConstant('{app}\Mirador.Helper.exe'), '--restore-runtime', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        Code := -1;
      if (Recorded <> '') and RegQueryStringValue(HKLM64, OpenXrKey, 'ActiveRuntime', Active) and IsMiradorsManifest(Active) then
        RegWriteStringValue(HKLM64, OpenXrKey, 'ActiveRuntime', Recorded);
    end;

    if RegQueryStringValue(HKLM64, OpenXrKey, 'ActiveRuntime', Active) and IsMiradorsManifest(Active) then
      RegDeleteValue(HKLM64, OpenXrKey, 'ActiveRuntime');
  end;
  RemoveMiradorsRuntimes;

  StopHelper;
  RegDeleteValue(HKLM64, ImplicitLayers, ExpandConstant('{app}\layer\mirador_direct_mode.json'));
  Sc('delete {#ServiceName}');
  RegDeleteValue(HKLM64, MiradorKey, 'PreviousActiveRuntime');
  RegDeleteKeyIncludingSubkeys(HKLM64, MiradorKey + '\ElevatedMonado');
  RegDeleteKeyIncludingSubkeys(HKLM64, MiradorKey + '\ElevatedMonadoFiles');
  RegDeleteKeyIncludingSubkeys(HKLM64, MiradorKey + '\ApprovedBuilds');
end;
