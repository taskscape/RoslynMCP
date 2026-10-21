[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$requiredFiles = @(
    'installer\CSharpMCP.iss',
    'scripts\Build-Installer.ps1',
    'scripts\Test-InstallerPackage.ps1',
    'scripts\Install-RoslynSkills.ps1',
    'scripts\Register-CSharpMcpClients.ps1',
    'scripts\Unregister-CSharpMcpClients.ps1'
)

foreach ($relativePath in $requiredFiles)
{
    $fullPath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf))
    {
        throw "Required installer asset '$relativePath' is missing."
    }
}

$parseErrors = [System.Collections.Generic.List[string]]::new()
foreach ($scriptPath in @($requiredFiles | Where-Object { $_ -like '*.ps1' }))
{
    $tokens = $null
    $errors = $null
    [void] [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repositoryRoot $scriptPath),
        [ref] $tokens,
        [ref] $errors)
    foreach ($parseError in @($errors))
    {
        $parseErrors.Add("${scriptPath}:$($parseError.Extent.StartLineNumber): $($parseError.Message)")
    }
}

if ($parseErrors.Count -gt 0)
{
    throw "Installer PowerShell parsing failed:`n$($parseErrors -join [Environment]::NewLine)"
}

$installerDefinition = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\CSharpMCP.iss') -Raw
$buildHelper = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts\Build-Installer.ps1') -Raw
$registrationHelper = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts\Register-CSharpMcpClients.ps1') -Raw
$requiredInstallerEvidence = @(
    'PrivilegesRequired=lowest',
    'CSharpMcp.Server.exe',
    '..\.agents\skills\*',
    'Register-CSharpMcpClients.ps1',
    '[UninstallRun]',
    'SKIPPOSTINSTALL',
    'CreateDownloadPage',
    'RequiredDotNetSdkIsInstalled',
    'ExecAndCaptureOutput',
    'CreateDotNetSdkSelectionDirectory',
    'DotNetHostHasCompatibleSdk',
    "'--version'",
    'Found .NET SDK compatible with global.json',
    'DownloadDotNetSdkInstaller',
    'DotNetSdkDownloadPage.Add',
    'b2618a69a4ae385eb03bde0de89468881318c6338b14e67574d691e145a7ce1c',
    "'runas'",
    "'/install /quiet /norestart'",
    'ResultCode = 3010',
    'function PrepareToInstall'
)

foreach ($evidence in $requiredInstallerEvidence)
{
    if ($installerDefinition.IndexOf($evidence, [StringComparison]::OrdinalIgnoreCase) -lt 0)
    {
        throw "Installer definition does not contain required evidence '$evidence'."
    }
}

$globalJson = Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json
$requiredSdkVersion = [string] $globalJson.sdk.version
$requiredSdkRollForward = [string] $globalJson.sdk.rollForward
$requiredSdkAllowPrerelease = if ([bool] $globalJson.sdk.allowPrerelease) { 'true' } else { 'false' }
if ($installerDefinition.IndexOf("#define DotNetSdkVersion `"$requiredSdkVersion`"", [StringComparison]::Ordinal) -lt 0)
{
    throw "Installer prerequisite version does not match global.json SDK '$requiredSdkVersion'."
}

if ($installerDefinition.IndexOf("#define DotNetSdkRollForward `"$requiredSdkRollForward`"", [StringComparison]::Ordinal) -lt 0 -or
    $installerDefinition.IndexOf("#define DotNetSdkAllowPrerelease `"$requiredSdkAllowPrerelease`"", [StringComparison]::OrdinalIgnoreCase) -lt 0)
{
    throw 'Installer SDK selection policy does not match global.json.'
}

foreach ($compilerDefine in @('DotNetSdkVersion', 'DotNetSdkRollForward', 'DotNetSdkAllowPrerelease'))
{
    if ($buildHelper.IndexOf("/D$compilerDefine=", [StringComparison]::Ordinal) -lt 0)
    {
        throw "Installer build does not pass the global.json SDK setting '$compilerDefine' to Inno Setup."
    }
}

# Inno Setup invokes PrepareToInstall before it installs files or evaluates [Run].
# Keeping the prerequisite in that event therefore gates both client registrations.
if ($installerDefinition.IndexOf('function PrepareToInstall', [StringComparison]::Ordinal) -lt 0 -or
    $installerDefinition.IndexOf('[Run]', [StringComparison]::Ordinal) -lt 0)
{
    throw 'The SDK prerequisite must use PrepareToInstall to gate the client-registration [Run] entries.'
}

if ($buildHelper.IndexOf(".config\dotnet-tools.json", [StringComparison]::OrdinalIgnoreCase) -lt 0)
{
    throw 'Installer build does not stage the optional ApiCompat tool manifest beside the server.'
}

foreach ($clientName in @('codex', 'claude'))
{
    if ($registrationHelper.IndexOf("'$clientName'", [StringComparison]::OrdinalIgnoreCase) -lt 0)
    {
        throw "Registration helper does not discover the $clientName client."
    }
}

foreach ($operation in @("@('mcp', 'get', 'csharp_roslyn')", "'mcp', 'add'"))
{
    if ($registrationHelper.IndexOf($operation, [StringComparison]::Ordinal) -lt 0)
    {
        throw "Registration helper is missing the non-destructive operation '$operation'."
    }
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CSharpMCP-InstallerTest-" + [guid]::NewGuid().ToString('N'))
$temporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try
{
    $skillsProfile = Join-Path $temporaryRoot 'skills-profile'
    $sourceSkillNames = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.agents\skills') -Directory | Sort-Object Name | Select-Object -ExpandProperty Name)
    $preservedCodexSkill = $sourceSkillNames[0]
    $preservedClaudeSkill = $sourceSkillNames[1]
    $incompleteClaudeSkill = $sourceSkillNames[2]
    $codexPreservedContent = "---`nname: $preservedCodexSkill`ndescription: Existing Codex skill marker.`n---`nDo not replace."
    $claudePreservedContent = "---`nname: $preservedClaudeSkill`ndescription: Existing Claude skill marker.`n---`nDo not replace."

    $codexPreservedPath = Join-Path $skillsProfile ".agents\skills\$preservedCodexSkill\SKILL.md"
    $claudePreservedPath = Join-Path $skillsProfile ".claude\skills\$preservedClaudeSkill\SKILL.md"
    $claudeIncompletePath = Join-Path $skillsProfile ".claude\skills\$incompleteClaudeSkill"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $codexPreservedPath), (Split-Path -Parent $claudePreservedPath), $claudeIncompletePath | Out-Null
    Set-Content -LiteralPath $codexPreservedPath -Value $codexPreservedContent -NoNewline
    Set-Content -LiteralPath $claudePreservedPath -Value $claudePreservedContent -NoNewline
    Set-Content -LiteralPath (Join-Path $claudeIncompletePath 'user-marker.txt') -Value 'preserve incomplete-directory content' -NoNewline

    $initialSkillSummary = @(& (Join-Path $repositoryRoot 'scripts\Install-RoslynSkills.ps1') `
        -Client Both `
        -Scope User `
        -UserProfilePath $skillsProfile `
        -SkipExisting)
    if (-not $?)
    {
        throw 'Dual-client skill installation verification failed.'
    }

    if ($initialSkillSummary.Count -ne 2 -or
        ($initialSkillSummary | Measure-Object -Property Installed -Sum).Sum -ne 66 -or
        ($initialSkillSummary | Measure-Object -Property Preserved -Sum).Sum -ne 2)
    {
        throw 'Dual-client skill installation did not report the expected installed and preserved counts.'
    }

    foreach ($skillRoot in @((Join-Path $skillsProfile '.agents\skills'), (Join-Path $skillsProfile '.claude\skills')))
    {
        $installedSkillDirectories = @(Get-ChildItem -LiteralPath $skillRoot -Directory)
        $installedEntryPoints = @($installedSkillDirectories | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') -PathType Leaf })
        if ($installedSkillDirectories.Count -ne $sourceSkillNames.Count -or $installedEntryPoints.Count -ne $sourceSkillNames.Count)
        {
            throw "Skill root '$skillRoot' does not contain all $($sourceSkillNames.Count) valid skills."
        }
    }

    if ((Get-Content -LiteralPath $codexPreservedPath -Raw) -cne $codexPreservedContent -or
        (Get-Content -LiteralPath $claudePreservedPath -Raw) -cne $claudePreservedContent)
    {
        throw 'Existing Codex or Claude SKILL.md content was overwritten.'
    }

    if (-not (Test-Path -LiteralPath (Join-Path $claudeIncompletePath 'SKILL.md') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $claudeIncompletePath 'user-marker.txt') -PathType Leaf))
    {
        throw 'An incomplete Claude skill directory was not repaired while preserving unrelated content.'
    }

    $fakeLog = Join-Path $temporaryRoot 'client-arguments.log'
    $fakeServerPath = Join-Path $temporaryRoot 'server with spaces.exe'
    Set-Content -LiteralPath $fakeServerPath -Value 'Isolated registration fixture; never executed.'
    $fakeCodex = @'
@echo off
if /I "%1 %2 %3"=="mcp get csharp_roslyn" (
  if "%FAKE_MCP_EXISTING%"=="error" goto lookup_error
  if "%FAKE_MCP_EXISTING%"=="transient" goto transient_lookup
  if "%FAKE_MCP_EXISTING%"=="1" (
    echo Command: %FAKE_MCP_SERVER%
    exit /b 0
  )
  if exist "%FAKE_MCP_LOG%.codex-added" (
    echo Command: %FAKE_MCP_SERVER%
    exit /b 0
  )
  echo Error: No MCP server named 'csharp_roslyn' found. 1>&2
  exit /b 1
)
echo CODEX %*>>"%FAKE_MCP_LOG%"
echo added>"%FAKE_MCP_LOG%.codex-added"
exit /b 0
:lookup_error
echo Temporary configuration lock 1>&2
exit /b 2
:transient_lookup
if exist "%FAKE_MCP_LOG%.codex-retry" goto recovered
echo retry>"%FAKE_MCP_LOG%.codex-retry"
goto lookup_error
:recovered
echo Command: %FAKE_MCP_SERVER%
exit /b 0
'@
    $fakeClaude = @'
@echo off
if /I "%1 %2 %3"=="mcp get csharp_roslyn" (
  if "%FAKE_MCP_EXISTING%"=="error" goto lookup_error
  if "%FAKE_MCP_EXISTING%"=="transient" goto transient_lookup
  if "%FAKE_MCP_EXISTING%"=="1" (
    echo Command: %FAKE_MCP_SERVER%
    exit /b 0
  )
  if exist "%FAKE_MCP_LOG%.claude-added" (
    echo Command: %FAKE_MCP_SERVER%
    exit /b 0
  )
  echo No MCP server found with name: csharp_roslyn 1>&2
  exit /b 1
)
echo CLAUDE %*>>"%FAKE_MCP_LOG%"
echo added>"%FAKE_MCP_LOG%.claude-added"
exit /b 0
:lookup_error
echo Temporary configuration lock 1>&2
exit /b 2
:transient_lookup
if exist "%FAKE_MCP_LOG%.claude-retry" goto recovered
echo retry>"%FAKE_MCP_LOG%.claude-retry"
goto lookup_error
:recovered
echo Command: %FAKE_MCP_SERVER%
exit /b 0
'@
    Set-Content -LiteralPath (Join-Path $temporaryRoot 'codex.cmd') -Value ($fakeCodex -replace '\r?\n', "`r`n") -Encoding Ascii
    Set-Content -LiteralPath (Join-Path $temporaryRoot 'claude.cmd') -Value ($fakeClaude -replace '\r?\n', "`r`n") -Encoding Ascii

    $previousPath = $env:PATH
    $previousFakeLog = $env:FAKE_MCP_LOG
    $previousFakeExisting = $env:FAKE_MCP_EXISTING
    $previousFakeServer = $env:FAKE_MCP_SERVER
    try
    {
        $env:PATH = "$temporaryRoot;$previousPath"
        $env:FAKE_MCP_LOG = $fakeLog
        $env:FAKE_MCP_EXISTING = '0'
        $env:FAKE_MCP_SERVER = $fakeServerPath
        & (Join-Path $repositoryRoot 'scripts\Register-CSharpMcpClients.ps1') `
            -ServerPath $fakeServerPath `
            -Client Both `
            -ToolGroups all `
            -InstallSkills `
            -SkillsUserProfilePath $skillsProfile `
            -StatePath (Join-Path $temporaryRoot 'registration-state.json')
        if (-not $?)
        {
            throw 'Fake-client registration verification failed.'
        }
    }
    finally
    {
        $env:PATH = $previousPath
        $env:FAKE_MCP_LOG = $previousFakeLog
        $env:FAKE_MCP_EXISTING = $previousFakeExisting
        $env:FAKE_MCP_SERVER = $previousFakeServer
    }

    $registrationState = Get-Content -LiteralPath (Join-Path $temporaryRoot 'registration-state.json') -Raw | ConvertFrom-Json
    $skillRegistration = @($registrationState.Registrations | Where-Object { $_.Client -eq 'Skills' })
    if ($skillRegistration.Count -ne 1 -or $skillRegistration[0].Status -ne 'already-installed')
    {
        throw 'Client registration did not record that both client skill catalogs were already complete.'
    }

    $recordedArguments = Get-Content -LiteralPath $fakeLog -Raw
    $expectedArguments = @(
        'CODEX mcp add csharp_roslyn --env CSHARPMCP_TOOL_GROUPS=all --',
        'CLAUDE mcp add --transport stdio --scope user --env CSHARPMCP_TOOL_GROUPS=all csharp_roslyn --'
    )
    foreach ($expectedArgument in $expectedArguments)
    {
        if ($recordedArguments.IndexOf($expectedArgument, [StringComparison]::OrdinalIgnoreCase) -lt 0)
        {
            throw "Registration helper did not issue expected arguments '$expectedArgument'."
        }
    }

    try
    {
        $env:PATH = "$temporaryRoot;$previousPath"
        $env:FAKE_MCP_LOG = $fakeLog
        $env:FAKE_MCP_EXISTING = '1'
        $env:FAKE_MCP_SERVER = $fakeServerPath
        & (Join-Path $repositoryRoot 'scripts\Register-CSharpMcpClients.ps1') `
            -ServerPath $fakeServerPath `
            -Client Both `
            -StatePath (Join-Path $temporaryRoot 'existing-registration-state.json')
        if (-not $?)
        {
            throw 'Existing-registration preservation verification failed.'
        }
    }
    finally
    {
        $env:PATH = $previousPath
        $env:FAKE_MCP_LOG = $previousFakeLog
        $env:FAKE_MCP_EXISTING = $previousFakeExisting
        $env:FAKE_MCP_SERVER = $previousFakeServer
    }

    $existingState = Get-Content -LiteralPath (Join-Path $temporaryRoot 'existing-registration-state.json') -Raw | ConvertFrom-Json
    $existingClientRegistrations = @($existingState.Registrations | Where-Object { $_.Client -in @('Codex', 'Claude') })
    if ($existingClientRegistrations.Count -ne 2 -or @($existingClientRegistrations | Where-Object { $_.Status -ne 'already-configured' }).Count -ne 0)
    {
        throw 'The registration helper did not preserve both pre-existing client registrations.'
    }

    try
    {
        $env:PATH = "$temporaryRoot;$previousPath"
        $env:FAKE_MCP_LOG = $fakeLog
        $env:FAKE_MCP_SERVER = $fakeServerPath
        $env:FAKE_MCP_EXISTING = '1'
        & (Join-Path $repositoryRoot 'scripts\Register-CSharpMcpClients.ps1') -ServerPath $fakeServerPath -Client Both -StatePath (Join-Path $temporaryRoot 'registration-state.json')
        $repeatState = Get-Content -LiteralPath (Join-Path $temporaryRoot 'registration-state.json') -Raw | ConvertFrom-Json
        if (@($repeatState.Registrations | Where-Object { $_.InstalledByInstaller }).Count -ne 2)
        {
            throw 'A repeated registration lost installer ownership.'
        }

        $beforeFailure = Get-Content -LiteralPath $fakeLog -Raw
        $env:FAKE_MCP_EXISTING = 'error'
        $failedStatePath = Join-Path $temporaryRoot 'failed-registration-state.json'
        & (Join-Path $repositoryRoot 'scripts\Register-CSharpMcpClients.ps1') -ServerPath $fakeServerPath -Client Both -StatePath $failedStatePath
        $failedState = Get-Content -LiteralPath $failedStatePath -Raw | ConvertFrom-Json
        if (@($failedState.Registrations | Where-Object Status -eq 'discovery-failed').Count -ne 2 -or
            $failedState.Diagnostics.Count -ne 6 -or
            (Get-Content -LiteralPath $fakeLog -Raw) -cne $beforeFailure -or
            -not (Test-Path -LiteralPath "$failedStatePath.jsonl"))
        {
            throw 'Failed discovery must retry, log each failure, and avoid modifying client registrations.'
        }

        $env:FAKE_MCP_EXISTING = 'transient'
        $recoveredStatePath = Join-Path $temporaryRoot 'recovered-registration-state.json'
        & (Join-Path $repositoryRoot 'scripts\Register-CSharpMcpClients.ps1') -ServerPath $fakeServerPath -Client Both -StatePath $recoveredStatePath
        $recoveredState = Get-Content -LiteralPath $recoveredStatePath -Raw | ConvertFrom-Json
        if (@($recoveredState.Registrations | Where-Object Status -eq 'already-configured').Count -ne 2 -or
            $recoveredState.Diagnostics.Count -ne 2 -or (Get-Content -LiteralPath $fakeLog -Raw) -cne $beforeFailure)
        {
            throw 'Transient discovery errors must recover without issuing duplicate add commands.'
        }

        $env:FAKE_MCP_EXISTING = '0'
        $env:FAKE_MCP_LOG = Join-Path $temporaryRoot 'verification-arguments.log'
        $env:FAKE_MCP_SERVER = "$fakeServerPath.extra"
        $verificationStatePath = Join-Path $temporaryRoot 'verification-registration-state.json'
        & (Join-Path $repositoryRoot 'scripts\Register-CSharpMcpClients.ps1') -ServerPath $fakeServerPath -Client Both -StatePath $verificationStatePath
        $verificationState = Get-Content -LiteralPath $verificationStatePath -Raw | ConvertFrom-Json
        if (@($verificationState.Registrations | Where-Object Status -eq 'verification-failed').Count -ne 2 -or
            @($verificationState.Registrations | Where-Object InstalledByInstaller).Count -ne 0)
        {
            throw 'Successful add commands must not claim ownership of an unverified server command.'
        }
    }
    finally
    {
        $env:PATH = $previousPath
        $env:FAKE_MCP_LOG = $previousFakeLog
        $env:FAKE_MCP_EXISTING = $previousFakeExisting
        $env:FAKE_MCP_SERVER = $previousFakeServer
    }
}
finally
{
    if (-not $temporaryRoot.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to remove non-temporary installer test path '$temporaryRoot'."
    }

    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host 'Validated Inno Setup packaging, complete non-destructive Codex/Claude skill deployment, and client registration contracts.'
