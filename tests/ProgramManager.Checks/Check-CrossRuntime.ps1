param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$checkDirectory = $PSScriptRoot
$net8 = Join-Path $checkDirectory "bin\$Configuration\net8.0\ProgramManager.Checks.dll"
$net48 = Join-Path $checkDirectory "bin\$Configuration\net48\ProgramManager.Checks.exe"
if (!(Test-Path -LiteralPath $net8) -or !(Test-Path -LiteralPath $net48)) { throw 'Build the checks for both frameworks first.' }
$dotnet = (Get-Command dotnet).Source
foreach ($serverFramework in @('net8', 'net48')) {
    $checkRoot = Join-Path ([IO.Path]::GetTempPath()) ('ProgramManagerCross-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $checkRoot | Out-Null
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $server = $null
    try {
        $serverExe = if ($serverFramework -eq 'net8') { $dotnet } else { $net48 }
        $serverArgs = if ($serverFramework -eq 'net8') { '"' + $net8 + '" --serve "' + $checkRoot + '" ' + $port } else { '--serve "' + $checkRoot + '" ' + $port }
        $server = Start-Process -FilePath $serverExe -ArgumentList $serverArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $checkRoot 'server.log') -RedirectStandardError (Join-Path $checkRoot 'server-error.log')
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (!(Test-Path -LiteralPath (Join-Path $checkRoot 'pairing.txt'))) {
            if ($server.HasExited) { throw (Get-Content -LiteralPath (Join-Path $checkRoot 'server-error.log') -Raw) }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Cross-runtime host did not become ready.' }
            Start-Sleep -Milliseconds 100
        }
        if ($serverFramework -eq 'net8') { & $net48 --client $checkRoot }
        else { & $dotnet $net8 --client $checkRoot }
        if ($LASTEXITCODE -ne 0) { throw "Client failed against $serverFramework host." }
        Write-Output "PASS: $serverFramework host to other runtime client"
    }
    finally {
        Set-Content -LiteralPath (Join-Path $checkRoot 'stop') -Value 'stop'
        if ($server -and !$server.HasExited -and !$server.WaitForExit(10000)) { $server.Kill(); $server.WaitForExit() }
        $resolved = [IO.Path]::GetFullPath($checkRoot)
        $temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (!$resolved.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path -Leaf $resolved).StartsWith('ProgramManagerCross-')) { throw 'Unexpected cross-runtime check directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
