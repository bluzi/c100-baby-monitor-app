; The first install, and only the first install — the Windows answer to the Mac's .dmg.
;
; After this, the app updates itself from the .zip published beside this setup (UPD-3/5): it never
; runs an installer again, and it never asks anyone for anything.
;
; **This is a PER-USER install, and that is not a preference — it is what keeps the updater working.**
; It lands in %LOCALAPPDATA%\Programs\BabyMonitor, a directory the app can write to itself, so the
; swap that applies an update (UPD-5w) needs no elevation. Installed into Program Files, that swap
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

; Deliberately NOT a "start with Windows" task. WIN-8: the app offers that itself, once, in words, and
; **never turns it on by itself** — an installer checkbox nobody read is exactly the way a monitor
; ends up in a startup list its owner never chose.

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The session, the settings and the learned tuning live in %LOCALAPPDATA%\BabyMonitor and are NOT
; removed: an uninstall to reinstall a fix must not cost a parent their sign-in and their alarm
; tuning. Only what this installer put on disk is taken away.
Type: filesandordirs; Name: "{app}"
