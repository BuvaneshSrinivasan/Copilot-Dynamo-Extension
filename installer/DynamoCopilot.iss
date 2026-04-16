; DynamoCopilot – Inno Setup 6 installer script
;
; Requires: Inno Setup 6  (https://jrsoftware.org/isinfo.php)
;
; Build via:
;   .\build-installer.ps1            (recommended – builds DLLs first)
;   iscc installer\DynamoCopilot.iss  (manual – dist\ must already exist)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName      "DynamoCopilot"
#define AppPublisher "BimEra"
#define ServerUrl    "https://radiant-determination-production.up.railway.app"
; DLLs are published here by build-installer.ps1 before iscc runs
#define DistDir      "dist"

; ── Setup metadata ────────────────────────────────────────────────────────────

[Setup]
AppId={{7A3E2F14-C591-4D8B-A7F2-90B3E1D54C6A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#ServerUrl}
AppSupportURL={#ServerUrl}
; DLLs go to %AppData%\DynamoCopilot – no system-wide installation needed
DefaultDirName={userappdata}\DynamoCopilot
; Hide the destination page – users should not change this path
DisableDirPage=yes
; "Start Menu" group is shown but DynamoCopilot has no standalone exe,
; so leave it empty (no shortcuts created below)
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Output
OutputDir=Output
OutputBaseFilename=DynamoCopilot-Setup
; Compression
Compression=lzma2/ultra64
SolidCompression=yes
; Run as admin so we can write ViewExtensionDefinition XMLs to Program Files
PrivilegesRequired=admin
; Revit is a 64-bit application installed under C:\Program Files (not x86).
; Without these two lines {pf} resolves to C:\Program Files (x86) and all
; DirExists checks in the [Code] section fail on every Revit machine.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
; Show the version in the uninstaller / Add-Remove Programs
VersionInfoVersion={#AppVersion}
UninstallDisplayName={#AppName} {#AppVersion}

; ── Languages ─────────────────────────────────────────────────────────────────

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ── Files – DLLs ─────────────────────────────────────────────────────────────
; Both TFMs are always installed. The ViewExtensionDefinition XML for each
; Dynamo version points to whichever TFM that Dynamo requires.

[Files]
; Revit 2022-2024 / Dynamo Sandbox 2.x  →  net48
Source: "{#DistDir}\net48\*"; \
  DestDir: "{userappdata}\DynamoCopilot\net48"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; Revit 2025-2026 / Dynamo Sandbox 3.x  →  net8.0-windows
Source: "{#DistDir}\net8.0-windows\*"; \
  DestDir: "{userappdata}\DynamoCopilot\net8.0-windows"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; ── Pascal Script ─────────────────────────────────────────────────────────────

[Code]

// ── Constants ────────────────────────────────────────────────────────────────

const
  ProductionUrl  = 'https://radiant-determination-production.up.railway.app';
  LocalServerUrl = 'http://localhost:8080';
  MaxHistory     = 40;

// ── Dynamo install table ──────────────────────────────────────────────────────

type
  TInstall = record
    ViewExtDir : String;
    Tfm        : String;
  end;

// Returns all Dynamo viewExtensions folders that actually exist on this machine,
// paired with the TFM those Dynamo builds require.
// Inno Setup Pascal Script does not support nested procedures, so each candidate
// path is checked inline with an explicit DirExists guard.
function DetectDynamoInstalls: array of TInstall;
var
  PF      : String;
  Results : array of TInstall;
  N       : Integer;
  Paths   : array of String;
  Tfms    : array of String;
  I       : Integer;
begin
  PF := ExpandConstant('{commonpf64}');  // Revit is 64-bit → C:\Program Files, not (x86)
  N  := 0;
  SetArrayLength(Results, 0);

  // Build candidate list: path + required TFM
  SetArrayLength(Paths, 8);
  SetArrayLength(Tfms,  8);
  Paths[0] := PF + '\Autodesk\Revit 2026\AddIns\DynamoForRevit\viewExtensions'; Tfms[0] := 'net8.0-windows';
  Paths[1] := PF + '\Autodesk\Revit 2025\AddIns\DynamoForRevit\viewExtensions'; Tfms[1] := 'net8.0-windows';
  Paths[2] := PF + '\Autodesk\Revit 2024\AddIns\DynamoForRevit\viewExtensions'; Tfms[2] := 'net48';
  Paths[3] := PF + '\Autodesk\Revit 2023\AddIns\DynamoForRevit\viewExtensions'; Tfms[3] := 'net48';
  Paths[4] := PF + '\Autodesk\Revit 2022\AddIns\DynamoForRevit\viewExtensions'; Tfms[4] := 'net48';
  Paths[5] := PF + '\Dynamo\Dynamo Core\3\viewExtensions';                       Tfms[5] := 'net8.0-windows';
  Paths[6] := PF + '\Dynamo\Dynamo Core\2.19\viewExtensions';                    Tfms[6] := 'net48';
  Paths[7] := PF + '\Dynamo\Dynamo Core\2.18\viewExtensions';                    Tfms[7] := 'net48';

  for I := 0 to 7 do
  begin
    if DirExists(Paths[I]) then
    begin
      SetArrayLength(Results, N + 1);
      Results[N].ViewExtDir := Paths[I];
      Results[N].Tfm        := Tfms[I];
      N := N + 1;
    end;
  end;

  Result := Results;
end;

// ── settings.json ─────────────────────────────────────────────────────────────

// Writes (or overwrites) settings.json in %AppData%\DynamoCopilot\.
// useLocalServer is always false — this is a production install.
procedure WriteSettingsJson;
var
  Dir  : String;
  Path : String;
  Json : String;
begin
  Dir  := ExpandConstant('{userappdata}\DynamoCopilot');
  Path := Dir + '\settings.json';

  Json :=
    '{' + #13#10 +
    '  "serverUrl": "' + ProductionUrl + '",' + #13#10 +
    '  "maxHistoryMessages": ' + IntToStr(MaxHistory) + ',' + #13#10 +
    '  "useLocalServer": false,' + #13#10 +
    '  "localServerUrl": "' + LocalServerUrl + '"' + #13#10 +
    '}';

  SaveStringToFile(Path, Json, False);
end;

// ── ViewExtensionDefinition XMLs ──────────────────────────────────────────────

// Writes the Dynamo manifest XML for one Dynamo install.
// AssemblyPath is the absolute path to the DLL in %AppData%\DynamoCopilot\<tfm>\.
procedure WriteXml(ViewExtDir, Tfm: String);
var
  Base    : String;
  DllPath : String;
  Xml     : String;
  Dest    : String;
begin
  Base    := ExpandConstant('{userappdata}\DynamoCopilot');
  DllPath := Base + '\' + Tfm + '\DynamoCopilot.Extension.dll';

  Xml :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<ViewExtensionDefinition>' + #13#10 +
    '  <AssemblyPath>' + DllPath + '</AssemblyPath>' + #13#10 +
    '  <TypeName>DynamoCopilot.Extension.DynamoCopilotViewExtension</TypeName>' + #13#10 +
    '</ViewExtensionDefinition>';

  Dest := ViewExtDir + '\DynamoCopilot_ViewExtensionDefinition.xml';
  SaveStringToFile(Dest, Xml, False);
end;

// ── Install hook ──────────────────────────────────────────────────────────────

procedure CurStepChanged(CurStep: TSetupStep);
var
  Installs   : array of TInstall;
  I          : Integer;
  Registered : Integer;
begin
  if CurStep <> ssPostInstall then Exit;

  WriteSettingsJson;

  Installs   := DetectDynamoInstalls;
  Registered := 0;

  for I := 0 to GetArrayLength(Installs) - 1 do
  begin
    WriteXml(Installs[I].ViewExtDir, Installs[I].Tfm);
    Registered := Registered + 1;
  end;

  if Registered = 0 then
    MsgBox(
      'DynamoCopilot was installed, but no Dynamo installation was found.' + #13#10 + #13#10 +
      'To register manually, create a file called' + #13#10 +
      'DynamoCopilot_ViewExtensionDefinition.xml' + #13#10 +
      'in your Dynamo viewExtensions folder with this content:' + #13#10 + #13#10 +
      '<ViewExtensionDefinition>' + #13#10 +
      '  <AssemblyPath>' +
        ExpandConstant('{userappdata}') + '\DynamoCopilot\<tfm>\DynamoCopilot.Extension.dll' +
      '</AssemblyPath>' + #13#10 +
      '  <TypeName>DynamoCopilot.Extension.DynamoCopilotViewExtension</TypeName>' + #13#10 +
      '</ViewExtensionDefinition>',
      mbInformation, MB_OK);
end;

// ── Uninstall hook ────────────────────────────────────────────────────────────

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Installs : array of TInstall;
  I        : Integer;
  XmlFile  : String;
begin
  if CurUninstallStep <> usUninstall then Exit;

  Installs := DetectDynamoInstalls;
  for I := 0 to GetArrayLength(Installs) - 1 do
  begin
    XmlFile := Installs[I].ViewExtDir + '\DynamoCopilot_ViewExtensionDefinition.xml';
    if FileExists(XmlFile) then
      DeleteFile(XmlFile);
  end;
end;
