; CSharpMCP per-user installer
; -----------------------------------------------------------------------------
; Compiled by scripts\Build-Installer.ps1, which supplies a self-contained
; Windows publish directory, product version, target architecture, and output.
; Existing MCP registrations and skill directories are preserved by the
; post-install PowerShell helper.
; -----------------------------------------------------------------------------

#ifndef PublishDir
  #define PublishDir "artifacts\server"
#endif

#ifndef OutputDir
  #define OutputDir "output"
#endif

#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif

#ifndef VersionInfoVersion
  #define VersionInfoVersion "2.0.0.0"
#endif

#ifndef InstallerArchitecture
  #define InstallerArchitecture "x64compatible"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "CSharpMCP-2.0.0-win-x64-Setup"
#endif

; Keep this prerequisite pinned to global.json. The SHA-256 was calculated from
; Microsoft's release-metadata URL after its published SHA-512 and Authenticode
; signature were verified while maintaining this installer definition.
#ifndef DotNetSdkVersion
  #define DotNetSdkVersion "10.0.302"
#endif

#ifndef DotNetSdkRollForward
  #define DotNetSdkRollForward "latestPatch"
#endif

#ifndef DotNetSdkAllowPrerelease
  #define DotNetSdkAllowPrerelease "false"
#endif

#ifndef DotNetSdkInstallerName
  #define DotNetSdkInstallerName "dotnet-sdk-10.0.302-win-x64.exe"
#endif

#ifndef DotNetSdkUrl
  #define DotNetSdkUrl "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/dotnet-sdk-10.0.302-win-x64.exe"
#endif

#ifndef DotNetSdkSha256
  #define DotNetSdkSha256 "b2618a69a4ae385eb03bde0de89468881318c6338b14e67574d691e145a7ce1c"
#endif

#define AppName "CSharpMCP"
#define AppPublisher "Taskscape Ltd"
#define ServerExeName "CSharpMcp.Server.exe"

[Setup]
AppId={{CB0C9573-135F-4AF8-A62B-294CF370BE78}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#InstallerArchitecture}
ArchitecturesInstallIn64BitMode={#InstallerArchitecture}
MinVersion=10.0.17763
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
CloseApplicationsFilter={#ServerExeName}
RestartApplications=no
UninstallDisplayIcon={app}\server\{#ServerExeName}
VersionInfoVersion={#VersionInfoVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
DotNetSdkDownloadCaption=Downloading .NET SDK {#DotNetSdkVersion}
DotNetSdkDownloadDescription=CSharpMCP requires this SDK and its MSBuild files to analyze .NET solutions.

[Tasks]
Name: "alltools"; Description: "Enable optional API compatibility and architecture tools for new MCP registrations"; GroupDescription: "Tool catalog:"; Flags: unchecked

[Dirs]
Name: "{app}\config"
Name: "{localappdata}\CSharpMCP\logs"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}\server"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
Source: "..\.agents\skills\*"; DestDir: "{app}\.agents\skills"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\Install-RoslynSkills.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\Register-CSharpMcpClients.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\Unregister-CSharpMcpClients.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[INI]
Filename: "{app}\config\installation.ini"; Section: "server"; Key: "name"; String: "csharp_roslyn"
Filename: "{app}\config\installation.ini"; Section: "server"; Key: "transport"; String: "stdio"
Filename: "{app}\config\installation.ini"; Section: "server"; Key: "command"; String: "{app}\server\{#ServerExeName}"
Filename: "{app}\config\installation.ini"; Section: "server"; Key: "tool_groups"; String: "default"; Check: not WizardIsTaskSelected('alltools')
Filename: "{app}\config\installation.ini"; Section: "server"; Key: "tool_groups"; String: "all"; Check: WizardIsTaskSelected('alltools')

[Icons]
Name: "{group}\CSharpMCP README"; Filename: "{app}\README.md"
Name: "{group}\CSharpMCP configuration"; Filename: "{app}\config"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Register-CSharpMcpClients.ps1"" -ServerPath ""{app}\server\{#ServerExeName}"" -StatePath ""{app}\config\registration-state.json"" -Client Both -InstallSkills"; StatusMsg: "Installing skills and registering CSharpMCP with Codex and Claude Code..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall and not WizardIsTaskSelected('alltools')
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Register-CSharpMcpClients.ps1"" -ServerPath ""{app}\server\{#ServerExeName}"" -StatePath ""{app}\config\registration-state.json"" -Client Both -InstallSkills -RestoreApiCompat -ToolGroups all"; StatusMsg: "Installing skills and registering the complete CSharpMCP catalog..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall and WizardIsTaskSelected('alltools')
Filename: "{app}\config\registration-state.json"; Description: "View MCP client registration results"; Flags: postinstall shellexec skipifsilent skipifdoesntexist

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Unregister-CSharpMcpClients.ps1"" -StatePath ""{app}\config\registration-state.json"""; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveCSharpMcpClientRegistrations"

[UninstallDelete]
Type: files; Name: "{app}\config\registration-state.json"
Type: files; Name: "{app}\config\installation.ini"
Type: dirifempty; Name: "{app}\config"
Type: dirifempty; Name: "{app}"
Type: dirifempty; Name: "{localappdata}\CSharpMCP\logs"
Type: dirifempty; Name: "{localappdata}\CSharpMCP"

[Code]
var
  DotNetSdkDownloadPage: TDownloadWizardPage;

function CreateDotNetSdkSelectionDirectory(var SelectionDirectory: String): Boolean;
var
  SelectionFile: String;
  SelectionJson: String;
begin
  Result := False;
  SelectionDirectory := AddBackslash(ExpandConstant('{tmp}')) + 'CSharpMCP-sdk-selection';
  SelectionFile := AddBackslash(SelectionDirectory) + 'global.json';
  SelectionJson :=
    '{' + #13#10 +
    '  "sdk": {' + #13#10 +
    '    "version": "{#DotNetSdkVersion}",' + #13#10 +
    '    "rollForward": "{#DotNetSdkRollForward}",' + #13#10 +
    '    "allowPrerelease": {#DotNetSdkAllowPrerelease}' + #13#10 +
    '  }' + #13#10 +
    '}';

  try
    if not ForceDirectories(SelectionDirectory) then
    begin
      Log(Format('Unable to create the .NET SDK selection directory %s.', [SelectionDirectory]));
      Exit;
    end;

    if not SaveStringToFile(SelectionFile, SelectionJson, False) then
    begin
      Log(Format('Unable to create the .NET SDK selection file %s.', [SelectionFile]));
      Exit;
    end;

    Result := True;
  except
    Log(Format('Unable to prepare .NET SDK selection from global.json: %s', [GetExceptionMessage]));
  end;
end;

function DotNetHostHasCompatibleSdk(const DotNetPath, SelectionDirectory: String): Boolean;
var
  ResultCode: Integer;
  Output: TExecOutput;
begin
  Result := False;
  if not FileExists(DotNetPath) then
    Exit;

  try
    if not ExecAndCaptureOutput(
      DotNetPath,
      '--version',
      SelectionDirectory,
      SW_SHOWNORMAL,
      ewWaitUntilTerminated,
      ResultCode,
      Output) then
    begin
      Log(Format('Unable to query .NET SDKs through %s (error %d).', [DotNetPath, ResultCode]));
      Exit;
    end;

    if (ResultCode <> 0) or Output.Error then
    begin
      Log(Format('No .NET SDK compatible with global.json was found through %s (exit %d).', [DotNetPath, ResultCode]));
      Exit;
    end;

    Log(Format('Found .NET SDK compatible with global.json through %s.', [DotNetPath]));
    Result := True;
  except
    Log(Format('Unable to inspect .NET SDKs through %s: %s', [DotNetPath, GetExceptionMessage]));
  end;
end;

function RequiredDotNetSdkIsInstalled(): Boolean;
var
  PathDotNet: String;
  SelectionDirectory: String;
begin
  Result := False;
  if not CreateDotNetSdkSelectionDirectory(SelectionDirectory) then
    Exit;

  { Check the supported system-wide host first, then common private/PATH hosts. }
  Result := DotNetHostHasCompatibleSdk(ExpandConstant('{pf64}\dotnet\dotnet.exe'), SelectionDirectory);
  if Result then
    Exit;

  Result := DotNetHostHasCompatibleSdk(ExpandConstant('{localappdata}\Microsoft\dotnet\dotnet.exe'), SelectionDirectory);
  if Result then
    Exit;

  PathDotNet := FileSearch('dotnet.exe', GetEnv('PATH'));
  if PathDotNet <> '' then
    Result := DotNetHostHasCompatibleSdk(PathDotNet, SelectionDirectory);
end;

function DownloadDotNetSdkInstaller(): String;
begin
  Result := '';
  DotNetSdkDownloadPage.Clear;
  DotNetSdkDownloadPage.Add(
    '{#DotNetSdkUrl}',
    '{#DotNetSdkInstallerName}',
    '{#DotNetSdkSha256}');
  DotNetSdkDownloadPage.Show;
  try
    try
      DotNetSdkDownloadPage.Download;
      Log('The .NET SDK installer download and SHA-256 verification succeeded.');
    except
      if DotNetSdkDownloadPage.AbortedByUser then
        Result := 'The .NET SDK download was cancelled.'
      else
        Result := Format(
          'Unable to download the required .NET SDK from Microsoft: %s', [GetExceptionMessage]);
    end;
  finally
    DotNetSdkDownloadPage.Hide;
  end;
end;

procedure InitializeWizard;
begin
  DotNetSdkDownloadPage := CreateDownloadPage(
    CustomMessage('DotNetSdkDownloadCaption'),
    CustomMessage('DotNetSdkDownloadDescription'),
    nil);
  DotNetSdkDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstallerPath: String;
  ResultCode: Integer;
begin
  Result := '';
  if RequiredDotNetSdkIsInstalled() then
    Exit;

  Result := DownloadDotNetSdkInstaller();
  if Result <> '' then
    Exit;

  InstallerPath := ExpandConstant('{tmp}\{#DotNetSdkInstallerName}');
  Log('Starting the Microsoft .NET SDK installer with elevation and silent-install switches.');
  if not ShellExec(
    'runas',
    InstallerPath,
    '/install /quiet /norestart',
    ExpandConstant('{tmp}'),
    SW_SHOWNORMAL,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := Format(
      'Unable to start the Microsoft .NET SDK installer with administrative privileges: %s', [SysErrorMessage(ResultCode)]);
    Exit;
  end;

  if ResultCode = 3010 then
  begin
    NeedsRestart := True;
    Result := 'The .NET SDK installation requires a restart. Restart Windows, then run CSharpMCP Setup again.';
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    Result := Format('The Microsoft .NET SDK installer failed with exit code %d.', [ResultCode]);
    Exit;
  end;

  if not RequiredDotNetSdkIsInstalled() then
  begin
    Result := 'The Microsoft installer completed, but .NET SDK {#DotNetSdkVersion} could not be detected. Review the Setup log before retrying.';
    Exit;
  end;

  Log('The required .NET SDK and MSBuild prerequisite is available before MCP client registration.');
end;

function ShouldRunPostInstall(): Boolean;
begin
  { The private switch supports package verification without changing client configuration. }
  Result := CompareText(ExpandConstant('{param:SKIPPOSTINSTALL|0}'), '1') <> 0;
end;
