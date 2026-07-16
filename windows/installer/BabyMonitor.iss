; The first install, and only the first install — the Windows answer to the Mac's .dmg.
;
; After this, the app updates itself from the .zip published beside this setup (UPD-3/5): it never
; runs an installer again, and it never asks anyone for anything.
;
; **This is a PER-USER install, and that is not a preference — it is what keeps the updater working.**
; It lands in %LOCALAPPDATA%\Programs\BabyMonitor, a directory the app can write to itself, so the
; swap that applies an update (UPD-10) needs no elevation. Installed into Program Files, that swap
; would need an administrator — which means a UAC prompt, at whatever hour the update lands, standing
; between a parent and a running monitor. This project does not put dialogs there.
;
; Built by CI (see .github/workflows/release.yml). To build it by hand:
;
;   iscc /DAppVersion=0.1.42 windows\installer\BabyMonitor.iss

#define AppName "Baby Monitor"
#define AppExe "BabyMonitor.exe"
#define AppPublisher "bluzi"
#define AppUrl "https://github.com/bluzi/c100-baby-monitor-app"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Never change this GUID: it is how Windows knows an install is an upgrade of the same app rather
; than a second copy of it.
AppId={{6F1C4C1E-7A2B-4C7E-9A1D-6B9E2C5A8D31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}

; No administrator, and therefore no UAC — see the note at the top of this file.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\BabyMonitor
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes

SetupIconFile=..\src\BabyMonitor.App\Assets\BabyMonitor.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=.
OutputBaseFilename=babymonitor-setup

; A reinstall over a running monitor asks before it closes anything. It must never quietly kill a
; watch — and Windows will not let us overwrite files the running app is holding open anyway.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\build\x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

; Deliberately NOT a "start with Windows" task. DESK-19: the app offers that itself, once, in words, and
; **never turns it on by itself** — an installer checkbox nobody read is exactly the way a monitor
; ends up in a startup list its owner never chose.

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// DESK-24: let the camera answer.
//
// The camera is not connected to — it is asked to punch back, and it answers from an ephemeral port of
// its own. Windows Firewall never sent anything to that port, so it drops the reply as unsolicited and
// the handshake dies at its first step, for ever, behind a tidy "reconnecting in 15s". A Mac and a
// phone have no such filter; this is the PC's alone, and it makes an out-of-the-box install look like a
// working monitor that never sees anything.
//
// Adding the rule needs an administrator, and the install itself deliberately does not (see the top of
// this file: elevation here would mean elevation for updates, and a UAC prompt at 3am). So this asks
// once, at install time, when a person is actually present — and takes no for an answer: if the prompt
// is declined the app still installs, still runs, and says what is wrong itself (DESK-24) rather than
// pretending. That is why the result is not checked beyond logging it.
procedure AllowCameraToAnswer();
var
  ResultCode: Integer;
  Rule: String;
begin
  Rule := ExpandConstant('name="{#AppName}" dir=in action=allow program="{app}\{#AppExe}" protocol=udp enable=yes profile=any');
  // Remove first: a reinstall must not stack duplicate rules under the same name.
  ShellExec('runas', 'netsh.exe', 'advfirewall firewall delete rule name="{#AppName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ShellExec('runas', 'netsh.exe', 'advfirewall firewall add rule ' + Rule, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('firewall: inbound rule added (exit ' + IntToStr(ResultCode) + ')')
  else
    Log('firewall: rule not added — the app will say so itself (DESK-24)');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    AllowCameraToAnswer();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  // Take the rule away with the app; leaving a firewall hole behind for an app that is gone is rude.
  if CurUninstallStep = usUninstall then
    ShellExec('runas', 'netsh.exe', 'advfirewall firewall delete rule name="{#AppName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

[UninstallDelete]
; The session, the settings and the learned tuning live in %LOCALAPPDATA%\BabyMonitor and are NOT
; removed: an uninstall to reinstall a fix must not cost a parent their sign-in and their alarm
; tuning. Only what this installer put on disk is taken away.
Type: filesandordirs; Name: "{app}"
