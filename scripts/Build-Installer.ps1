<#
.SYNOPSIS
    Publishes CSharpMCP and builds its Inno Setup installer.

.DESCRIPTION
    Runs repository verification unless explicitly skipped, creates an untrimmed
    self-contained Windows publish, validates the registration helpers, and
    compiles a versioned per-user installer with Inno Setup.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',

    [string] $Version = '',

    [string] $IsccPath = '',

    [switch] $SkipTests,

    [switch] $SkipInstallerSmokeTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$installerRoot = Join-Path $repositoryRoot 'installer'
$projectPath = Join-Path $repositoryRoot 'src\CSharpMcp.Server\CSharpMcp.Server.csproj'
$publishDirectory = Join-Path $installerRoot 'artifacts\server'
$outputDirectory = Join-Path $installerRoot 'output'
$installerDefinition = Join-Path $installerRoot 'CSharpMCP.iss'
$versionFile = Join-Path $repositoryRoot 'VERSION'
$globalJsonPath = Join-Path $repositoryRoot 'global.json'

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$dotNetSdkVersion = [string] $globalJson.sdk.version
$dotNetSdkRollForward = [string] $globalJson.sdk.rollForward
$dotNetSdkAllowPrerelease = if ([bool] $globalJson.sdk.allowPrerelease) { 'true' } else { 'false' }
if ([string]::IsNullOrWhiteSpace($dotNetSdkVersion) -or [string]::IsNullOrWhiteSpace($dotNetSdkRollForward))
{
    throw "global.json must specify sdk.version and sdk.rollForward before building the installer."
}

if ([string]::IsNullOrWhiteSpace($Version))
{
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf))
    {
        throw "The release version file does not exist at '$versionFile'."
    }

    $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$')
{
    throw "Version '$Version' must contain three or four numeric components."
}

function Reset-GeneratedDirectory
{
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $AllowedRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($AllowedRoot)
    $rootPrefix = $fullRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to reset generated directory outside '$fullRoot': '$fullPath'."
    }

    if (Test-Path -LiteralPath $fullPath)
    {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Resolve-Iscc
{
    param([string] $ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath))
    {
        $resolvedPath = [System.IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf))
        {
            throw "The supplied Inno Setup compiler does not exist: '$resolvedPath'."
        }

        return $resolvedPath
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command)
    {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles} 'Inno Setup\ISCC.exe'),
        (Join-Path ${env:ProgramFiles} 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )

    return $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

if (-not $SkipTests)
{
    Write-Host 'Running the authoritative CSharpMCP verification process...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Test-CSharpMcp.ps1')
    if (-not $?)
    {
        throw 'CSharpMCP verification failed.'
    }
}

Reset-GeneratedDirectory -Path $publishDirectory -AllowedRoot $installerRoot
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$versionParts = @($Version -split '\.')
while ($versionParts.Count -lt 4)
{
    $versionParts += '0'
}

$versionInfoVersion = $versionParts[0..3] -join '.'
$outputBaseFilename = "CSharpMCP-$Version-$Runtime-Setup"
$expectedInstaller = Join-Path $outputDirectory "$outputBaseFilename.exe"

Write-Host "Publishing self-contained server for $Runtime..." -ForegroundColor Cyan
$publishArguments = @(
    'publish', $projectPath,
    '--nologo',
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '--output', $publishDirectory,
    "-p:Version=$Version",
    "-p:FileVersion=$versionInfoVersion",
    "-p:AssemblyVersion=$versionInfoVersion",
    '-p:UseAppHost=true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    '-p:ContinuousIntegrationBuild=true',
    '-p:Deterministic=true'
)

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedServer = Join-Path $publishDirectory 'CSharpMcp.Server.exe'
if (-not (Test-Path -LiteralPath $publishedServer -PathType Leaf))
{
    throw "Publish completed without producing '$publishedServer'."
}

# Keep the optional ApiCompat manifest beside the deployed server so its upward manifest search works outside the source checkout.
$publishedToolConfiguration = Join-Path $publishDirectory '.config'
New-Item -ItemType Directory -Path $publishedToolConfiguration -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot '.config\dotnet-tools.json') -Destination $publishedToolConfiguration -Force

Write-Host 'Dry-running client discovery and registration helpers...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Register-CSharpMcpClients.ps1') -ServerPath $publishedServer -Client Both -InstallSkills -RestoreApiCompat -WhatIf
if (-not $?)
{
    throw 'The client registration helper failed its WhatIf verification.'
}

$iscc = Resolve-Iscc -ExplicitPath $IsccPath
if ([string]::IsNullOrWhiteSpace($iscc))
{
    throw 'ISCC.exe was not found. Install Inno Setup 6, add ISCC.exe to PATH, or pass -IsccPath.'
}

Write-Host "Compiling installer with '$iscc'..." -ForegroundColor Cyan
$compilerArguments = @(
    "/DPublishDir=$publishDirectory",
    "/DOutputDir=$outputDirectory",
    "/DAppVersion=$Version",
    "/DVersionInfoVersion=$versionInfoVersion",
    '/DInstallerArchitecture=x64compatible',
    "/DOutputBaseFilename=$outputBaseFilename",
    "/DDotNetSdkVersion=$dotNetSdkVersion",
    "/DDotNetSdkRollForward=$dotNetSdkRollForward",
    "/DDotNetSdkAllowPrerelease=$dotNetSdkAllowPrerelease",
    $installerDefinition
)

& $iscc @compilerArguments
if ($LASTEXITCODE -ne 0)
{
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $expectedInstaller -PathType Leaf))
{
    throw "Inno Setup completed without producing '$expectedInstaller'."
}

if (-not $SkipInstallerSmokeTest)
{
    Write-Host 'Smoke-testing the compiled installer without changing client registrations...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Test-InstallerPackage.ps1') -InstallerPath $expectedInstaller
    if (-not $?)
    {
        throw 'The compiled installer failed its isolated smoke test.'
    }
}

$installer = Get-Item -LiteralPath $expectedInstaller
Write-Host 'Installer created successfully.' -ForegroundColor Green
Write-Host "Artifact: $($installer.FullName)"
Write-Host "Size: $([Math]::Round($installer.Length / 1MB, 1)) MB"
