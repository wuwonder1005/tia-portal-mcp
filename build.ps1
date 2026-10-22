# Build script for TIA Portal MCP Server.
# Compiles bin\TiaMcpServer.exe with the .NET Framework compiler that ships with
# Windows. Locates Siemens.Engineering.dll from the TIA Openness installation.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$webSys = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Web.Extensions.dll'

function Find-OpennessDll {
    $hits = @()

    # 1) Openness registry (any view, highest version first)
    foreach ($view in @('Registry64', 'Registry32')) {
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view)
            $openness = $base.OpenSubKey('SOFTWARE\Siemens\Automation\Openness')
            if ($openness) {
                foreach ($ver in $openness.GetSubKeyNames() | Sort-Object -Descending) {
                    $api = $openness.OpenSubKey("$ver\PublicAPI")
                    if ($api) {
                        foreach ($av in $api.GetSubKeyNames() | Sort-Object -Descending) {
                            $e = $api.OpenSubKey($av)
                            if ($e) {
                                $dll = $e.GetValue('Siemens.Engineering')
                                if ($dll -and (Test-Path $dll)) { $hits += $dll }
                            }
                        }
                    }
                }
            }
            $base.Dispose()
        } catch {}
    }

    # 2) TiaPortalLocation environment variable
    $env2 = [Environment]::GetEnvironmentVariable('TiaPortalLocation')
    if ($env2) {
        $cand = Join-Path $env2 'PublicAPI\V20\Siemens.Engineering.dll'
        if (Test-Path $cand) { $hits += $cand }
    }

    # 3) common install roots
    foreach ($r in @('C:\Program Files\Siemens\Automation','C:\Program Files (x86)\Siemens\Automation','D:\Program Files\Siemens\Automation','D:\Program Files (x86)\Siemens\Automation','E:\Program Files\Siemens\Automation')) {
        if (Test-Path $r) {
            foreach ($p in Get-ChildItem $r -Directory -Filter 'Portal V*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending) {
                $cand = Join-Path $p.FullName 'PublicAPI\V20\Siemens.Engineering.dll'
                if (Test-Path $cand) { $hits += $cand }
            }
        }
    }
    return $hits | Select-Object -First 1
}

$dll = Find-OpennessDll
if (-not $dll) {
    Write-Host 'ERROR: Siemens.Engineering.dll not found. Install TIA Portal V20 + Openness, or set TiaPortalLocation.'
    exit 1
}
Write-Host "Using $dll"

$src = Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { '"' + $_.FullName + '"' }
$out = Join-Path $outDir 'TiaMcpServer.exe'
$icon = Join-Path $root 'assets\app.ico'
$args = @("/nologo", "/codepage:65001", "/platform:x64", "/target:exe",
           "/win32icon:`"$icon`"", "/r:`"$dll`"", "/r:`"$webSys`"", "/out:`"$out`"" ) + $src
& $csc @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "BUILD OK: $out"
