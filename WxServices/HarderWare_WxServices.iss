; HarderWare WxServices — Inno Setup script
;
; Packages the release\ staging directory (produced by Build-Release.ps1)
; into a single HarderWare_WxServices_Setup.exe installer.
;
; Prerequisites (not installed by this installer):
;   - .NET 8 Runtime
;   - SQL Server Express
;   - WSL + wgrib2
;   - Miniconda with the wxvis conda environment
;   - Docker Desktop (optional, for observability stack)
;
; Build:
;   1. Run .\Build-Release.ps1 to populate the release\ directory.
;   2. Open this file in Inno Setup Compiler and click Build, or run:
;      "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" HarderWare_WxServices.iss

#ifndef AppVer
  #define AppVer "1.0.0"
#endif

[Setup]
AppName=HarderWare WxServices
AppVersion={#AppVer}
AppPublisher=Paul H. Harder II
DefaultDirName=C:\HarderWare
DefaultGroupName=HarderWare
UninstallDisplayIcon={app}\WxManager\WxManager.exe
OutputBaseFilename=HarderWare_WxServices_Setup
OutputDir=installer_output
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
DisableProgramGroupPage=yes
LicenseFile=
InfoBeforeFile=PRE-INSTALL.txt

; Shown on the final wizard page. Markdown renders as plain text in Notepad,
; which is fine — the file is written to be readable either way.
[Messages]
FinishedHeadingLabel=Setup complete — please read INSTALL.md before starting the services
FinishedLabelNoIcons=Installation placed the HarderWare WxServices files in [name]. Before starting any services, read INSTALL.md (installed in the application directory). You can open it in Notepad, WordPad, Visual Studio Code, or any text editor — it is plain text with some Markdown formatting. The installer can open it for you below.
FinishedLabel=Installation placed the HarderWare WxServices files in [name]. Before starting any services, read INSTALL.md (installed in the application directory). You can open it in Notepad, WordPad, Visual Studio Code, or any text editor — it is plain text with some Markdown formatting. The installer can open it for you below.

[Dirs]
Name: "{app}\Logs"
Name: "{app}\plots"
Name: "{app}\temp"

[Files]
; Shared configuration
Source: "release\appsettings.shared.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "release\log4net.shared.config";   DestDir: "{app}"; Flags: ignoreversion

; Documentation
Source: "release\INSTALL.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "release\DESIGN.md";  DestDir: "{app}"; Flags: ignoreversion

; Windows services
Source: "release\services\WxParser.Svc\*";  DestDir: "{app}\services\WxParser.Svc";  Flags: ignoreversion recursesubdirs
Source: "release\services\WxReport.Svc\*";  DestDir: "{app}\services\WxReport.Svc";  Flags: ignoreversion recursesubdirs
Source: "release\services\WxMonitor.Svc\*"; DestDir: "{app}\services\WxMonitor.Svc"; Flags: ignoreversion recursesubdirs
Source: "release\services\WxVis.Svc\*";     DestDir: "{app}\services\WxVis.Svc";     Flags: ignoreversion recursesubdirs

; Desktop applications
Source: "release\WxManager\*"; DestDir: "{app}\WxManager"; Flags: ignoreversion recursesubdirs
Source: "release\WxViewer\*";  DestDir: "{app}\WxViewer";  Flags: ignoreversion recursesubdirs

; Python scripts
Source: "release\WxVis\*"; DestDir: "{app}\WxVis"; Flags: ignoreversion

; Bundled tools (wgrib2 Linux binary, executed via WSL)
Source: "release\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion

; Observability stack (Docker Compose)
Source: "release\observability\*"; DestDir: "{app}\observability"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\WxManager";        Filename: "{app}\WxManager\WxManager.exe"
Name: "{group}\WxViewer";         Filename: "{app}\WxViewer\WxViewer.exe"
Name: "{group}\Uninstall WxServices"; Filename: "{uninstallexe}"
Name: "{commondesktop}\WxManager"; Filename: "{app}\WxManager\WxManager.exe"; Tasks: desktopicon
Name: "{commondesktop}\WxViewer";  Filename: "{app}\WxViewer\WxViewer.exe";  Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcuts"; GroupDescription: "Additional shortcuts:"

[Run]
; Register Windows services after file installation.
Filename: "sc.exe"; Parameters: "create WxParserSvc  binPath= ""{app}\services\WxParser.Svc\WxParser.Svc.exe""";   Flags: runhidden; StatusMsg: "Registering WxParserSvc..."
Filename: "sc.exe"; Parameters: "create WxReportSvc  binPath= ""{app}\services\WxReport.Svc\WxReport.Svc.exe""";   Flags: runhidden; StatusMsg: "Registering WxReportSvc..."
Filename: "sc.exe"; Parameters: "create WxMonitorSvc binPath= ""{app}\services\WxMonitor.Svc\WxMonitor.Svc.exe"""; Flags: runhidden; StatusMsg: "Registering WxMonitorSvc..."
Filename: "sc.exe"; Parameters: "create WxVisSvc     binPath= ""{app}\services\WxVis.Svc\WxVis.Svc.exe""";         Flags: runhidden; StatusMsg: "Registering WxVisSvc..."

; Open INSTALL.md in Notepad so the customer sees the setup guide immediately.
; Checked by default — customers without VS Code or another Markdown viewer can
; still read it (the file is plain text with Markdown formatting).
Filename: "notepad.exe"; Parameters: """{app}\INSTALL.md"""; Description: "View installation guide (INSTALL.md)"; Flags: nowait postinstall skipifsilent

; Launch WxManager for first-run configuration.
Filename: "{app}\WxManager\WxManager.exe"; Description: "Launch WxManager to configure the system"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
; Stop and remove services on uninstall.
Filename: "sc.exe"; Parameters: "stop WxParserSvc";    Flags: runhidden; RunOnceId: "StopParser"
Filename: "sc.exe"; Parameters: "stop WxReportSvc";    Flags: runhidden; RunOnceId: "StopReport"
Filename: "sc.exe"; Parameters: "stop WxMonitorSvc";   Flags: runhidden; RunOnceId: "StopMonitor"
Filename: "sc.exe"; Parameters: "stop WxVisSvc";       Flags: runhidden; RunOnceId: "StopVis"
Filename: "sc.exe"; Parameters: "delete WxParserSvc";  Flags: runhidden; RunOnceId: "DeleteParser"
Filename: "sc.exe"; Parameters: "delete WxReportSvc";  Flags: runhidden; RunOnceId: "DeleteReport"
Filename: "sc.exe"; Parameters: "delete WxMonitorSvc"; Flags: runhidden; RunOnceId: "DeleteMonitor"
Filename: "sc.exe"; Parameters: "delete WxVisSvc";     Flags: runhidden; RunOnceId: "DeleteVis"

[Code]
// Update InstallRoot in appsettings.shared.json to match the chosen install directory.
procedure UpdateInstallRoot();
var
  ConfigPath: string;
  Content: AnsiString;
  ContentStr, OldValue, NewValue: string;
begin
  ConfigPath := ExpandConstant('{app}\appsettings.shared.json');
  if FileExists(ConfigPath) then
  begin
    if LoadStringFromFile(ConfigPath, Content) then
    begin
      ContentStr := String(Content);
      OldValue := '"InstallRoot": "C:\\HarderWare"';
      NewValue := '"InstallRoot": "' + ExpandConstant('{app}') + '"';
      StringChangeEx(ContentStr, OldValue, NewValue, True);
      SaveStringToFile(ConfigPath, AnsiString(ContentStr), False);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    UpdateInstallRoot();
end;
