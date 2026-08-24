[CmdletBinding()]
param(
    [string] $InstallerPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$releaseVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($InstallerPath))
{
    $InstallerPath = Join-Path $repositoryRoot "installer\output\CSharpMCP-$releaseVersion-win-x64-Setup.exe"
}

$resolvedInstallerPath = [System.IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $resolvedInstallerPath -PathType Leaf))
{
    throw "The installer does not exist at '$resolvedInstallerPath'."
}

$smokePath = Join-Path ([System.IO.Path]::GetTempPath()) ("CSharpMCP-InstallerSmoke-" + [guid]::NewGuid().ToString('N'))
$smokePath = [System.IO.Path]::GetFullPath($smokePath)
$skillsProfilePath = "$smokePath-skills"
$setupLog = Join-Path ([System.IO.Path]::GetTempPath()) ("CSharpMCP-InstallerSmoke-" + [guid]::NewGuid().ToString('N') + '.log')
$installed = $false
try
{
    $setupArguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/NOICONS',
        "/DIR=`"$smokePath`"",
        "/LOG=`"$setupLog`"",
        '/SKIPPOSTINSTALL=1'
    )
    $setup = Start-Process -FilePath $resolvedInstallerPath -ArgumentList $setupArguments -Wait -PassThru
    if ($setup.ExitCode -ne 0)
    {
        throw "Installer smoke setup failed with exit code $($setup.ExitCode)."
    }

    $installed = $true
    $requiredFiles = @(
        'server\CSharpMcp.Server.exe',
        'server\.config\dotnet-tools.json',
        'config\installation.ini',
        'scripts\Install-RoslynSkills.ps1',
        'scripts\Register-CSharpMcpClients.ps1',
        'scripts\Unregister-CSharpMcpClients.ps1',
        'README.md'
    )
    foreach ($relativePath in $requiredFiles)
    {
        $installedFile = Join-Path $smokePath $relativePath
        if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf))
        {
            throw "Smoke installation is missing '$relativePath'."
        }
    }

    $installedSkills = @(Get-ChildItem -LiteralPath (Join-Path $smokePath '.agents\skills') -Directory)
    if ($installedSkills.Count -ne 34)
    {
        throw "Smoke installation contains $($installedSkills.Count) canonical skills instead of 34."
    }

    $packagedSkillSummary = @(& (Join-Path $smokePath 'scripts\Install-RoslynSkills.ps1') `
        -Client Both `
        -Scope User `
        -UserProfilePath $skillsProfilePath `
        -SkipExisting)
    if (-not $? -or $packagedSkillSummary.Count -ne 2 -or
        ($packagedSkillSummary | Measure-Object -Property Installed -Sum).Sum -ne 68)
    {
        throw 'The packaged helper did not install all 34 skills for both Codex and Claude Code.'
    }

    foreach ($skillRoot in @((Join-Path $skillsProfilePath '.agents\skills'), (Join-Path $skillsProfilePath '.claude\skills')))
    {
        $validSkills = @(Get-ChildItem -LiteralPath $skillRoot -Directory | Where-Object {
            Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') -PathType Leaf
        })
        if ($validSkills.Count -ne 34)
        {
            throw "The packaged helper installed $($validSkills.Count) valid skills at '$skillRoot' instead of 34."
        }
    }

    if (Test-Path -LiteralPath (Join-Path $smokePath 'config\registration-state.json'))
    {
        throw 'SKIPPOSTINSTALL unexpectedly created client registration state.'
    }

    $setupLogContent = Get-Content -LiteralPath $setupLog -Raw
    if ($setupLogContent.IndexOf('Found .NET SDK compatible with global.json', [StringComparison]::OrdinalIgnoreCase) -lt 0)
    {
        throw 'Installer smoke setup did not prove that a global.json-compatible SDK prerequisite was checked before installation.'
    }
}
finally
{
    if ($installed)
    {
        $uninstaller = Join-Path $smokePath 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller -PathType Leaf)
        {
            $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
            if ($uninstall.ExitCode -ne 0)
            {
                Write-Warning "Smoke uninstall exited with code $($uninstall.ExitCode)."
            }
        }
    }

    if (Test-Path -LiteralPath $setupLog -PathType Leaf)
    {
        Remove-Item -LiteralPath $setupLog -Force
    }

    if (Test-Path -LiteralPath $skillsProfilePath -PathType Container)
    {
        Remove-Item -LiteralPath $skillsProfilePath -Recurse -Force
    }
}

for ($attempt = 0; $attempt -lt 20 -and (Test-Path -LiteralPath $smokePath); $attempt++)
{
    Start-Sleep -Milliseconds 250
}

if (Test-Path -LiteralPath $smokePath)
{
    $remainingFiles = @(Get-ChildItem -LiteralPath $smokePath -Force -Recurse | Select-Object -ExpandProperty FullName)
    throw "Smoke uninstall left files under '$smokePath': $($remainingFiles -join '; ')"
}

Write-Host 'Installer smoke test passed: SDK prerequisite detection, payload, 34 Codex skills, 34 Claude skills, registration isolation, and clean uninstall were verified.'
