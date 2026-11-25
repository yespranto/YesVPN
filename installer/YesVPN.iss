; YesVPN Inno Setup Installer Script
; Builds a Windows x64 installer that registers YesVPN.Service, adds Start Menu entry,
; installs bundled sing-box + Wintun + licenses, upgrades cleanly, and fully uninstalls.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "YesVPN"
#define AppPublisher "Pranto Biswas"
#define AppURL "https://www.yespranto.com"
#define AppEmail "hello@yespranto.com"
#define AppExeName "YesVPN.App.exe"
#define ServiceName "YesVPNService"
#define ServiceExeName "YesVPN.Service.exe"

[Setup]
AppId={{8D4F67E3-92B4-4F91-A7E2-5C21B9840A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL=mailto:{#AppEmail}
AppUpdatesURL={#AppURL}
AppContact={#AppEmail}
LicenseFile=..\LICENSE
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=YesVPN-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\YesVPN.App\Assets\yesvpn.ico
UninstallDisplayIcon={app}\App\{#AppExeName}
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start YesVPN minimized to tray when Windows signs in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\app\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\App\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\App\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "YesVPN"; ValueData: """{app}\App\{#AppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\Service\{#ServiceExeName}\"""" start= auto DisplayName= ""YesVPN Tunnel Service"""; Flags: runhidden
Filename: "sc.exe"; Parameters: "description {#ServiceName} ""Manages the YesVPN SOCKS5 TUN tunnel and kill switch."""; Flags: runhidden
Filename: "sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000"; Flags: runhidden
Filename: "sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden
Filename: "{app}\App\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "KillYesVpnApp"
Filename: "sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopYesVpnService"
Filename: "sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteYesVpnService"
Filename: "taskkill.exe"; Parameters: "/F /IM sing-box.exe"; Flags: runhidden; RunOnceId: "KillSingBox"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-NetFirewallRule -DisplayName 'YesVPN_*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule"""; Flags: runhidden; RunOnceId: "CleanupFirewallRules"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\YesVPN"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill.exe', '/F /IM {#ServiceExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill.exe', '/F /IM sing-box.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
end;
