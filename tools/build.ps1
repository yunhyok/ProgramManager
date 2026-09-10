[CmdletBinding()]
param([string]$InnoCompiler = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'src\ProgramManager\ProgramManager.csproj'
$publishRoot = Join-Path $repoRoot 'artifacts\publish'

if (!$InnoCompiler) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (!$InnoCompiler -or !(Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Install Inno Setup 6, or provide -InnoCompiler with the full ISCC.exe path.'
}
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

Push-Location $repoRoot
try {
    Invoke-Dotnet build $projectPath -c Release
    Invoke-Dotnet run --project 'tests\ProgramManager.Checks\ProgramManager.Checks.csproj' -c Release -f net8.0
    Invoke-Dotnet build 'tests\ProgramManager.Checks\ProgramManager.Checks.csproj' -c Release -f net48
    & (Join-Path $repoRoot 'tests\ProgramManager.Checks\bin\Release\net48\ProgramManager.Checks.exe')
    if ($LASTEXITCODE -ne 0) { throw 'The .NET Framework 4.8 checks failed.' }
    Invoke-Dotnet run --project 'tests\ProgramManager.DesktopChecks\ProgramManager.DesktopChecks.csproj' -c Release -f net8.0-windows
    Invoke-Dotnet build 'tests\ProgramManager.DesktopChecks\ProgramManager.DesktopChecks.csproj' -c Release -f net48
    & (Join-Path $repoRoot 'tests\ProgramManager.DesktopChecks\bin\Release\net48\ProgramManager.DesktopChecks.exe')
    if ($LASTEXITCODE -ne 0) { throw 'The .NET Framework desktop checks failed.' }
    Invoke-Dotnet run --project 'tests\ProgramManager.DesktopChecks\ProgramManager.DesktopChecks.csproj' -c Release -f net8.0-windows --no-build -- --layout-check (Join-Path $repoRoot 'artifacts\layout\net8')
    & (Join-Path $repoRoot 'tests\ProgramManager.DesktopChecks\bin\Release\net48\ProgramManager.DesktopChecks.exe') --layout-check (Join-Path $repoRoot 'artifacts\layout\net48')
    if ($LASTEXITCODE -ne 0) { throw 'The .NET Framework UI layout checks failed.' }
    & (Join-Path $repoRoot 'tests\ProgramManager.Checks\Check-CrossRuntime.ps1') -Configuration Release

    foreach ($target in 'win10-x64', 'win7') {
        $outputPath = [IO.Path]::GetFullPath((Join-Path $publishRoot $target))
        if ([IO.Path]::GetDirectoryName($outputPath) -ne [IO.Path]::GetFullPath($publishRoot)) {
            throw "Publish output is outside the expected directory: $outputPath"
        }
        if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }
        if ($target -eq 'win10-x64') {
            Invoke-Dotnet publish $projectPath -c Release -f net8.0-windows -r win-x64 --self-contained true -o $outputPath
        } else {
            Invoke-Dotnet publish $projectPath -c Release -f net48 --self-contained false -o $outputPath
        }
        if (!(Test-Path -LiteralPath (Join-Path $outputPath 'ProgramManager.exe')) -or
            !(Test-Path -LiteralPath (Join-Path $outputPath 'guide.html'))) {
            throw "Published executable or offline help is missing in $outputPath."
        }
    }

    & $InnoCompiler (Join-Path $repoRoot 'installer\ProgramManager.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Windows 10/11 installer compilation failed.' }
    & $InnoCompiler '/DLegacy' (Join-Path $repoRoot 'installer\ProgramManager.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Windows 7 installer compilation failed.' }

    $installerRoot = Join-Path $repoRoot 'artifacts\installers'
    $installers = @('ProgramManager-Setup-0.4.0.exe', 'ProgramManager-Setup-0.4.0-win7.exe')
    $hashes = foreach ($name in $installers) {
        $file = Get-Item -LiteralPath (Join-Path $installerRoot $name)
        if ($file.Length -eq 0) { throw "Installer is empty: $name" }
        $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), $file.Name
    }
    $hashes | Set-Content -LiteralPath (Join-Path $installerRoot 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Program Manager 0.4.0 installers: $installerRoot"
} finally {
    Pop-Location
}
