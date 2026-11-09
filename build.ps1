# Builds shot.exe from select.cs and runs the behavioural self test.
# Usage:  pwsh -File build.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4) not found' }

Write-Host 'compiling select.cs -> shot.exe'
& $csc /nologo /target:winexe /optimize `
    /out:"$here\shot.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "$here\select.cs"
if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }

$report = Join-Path $env:TEMP 'dsh-screenshot-selftest.txt'
Remove-Item $report -ErrorAction SilentlyContinue
$process = Start-Process -FilePath "$here\shot.exe" -ArgumentList @('x', '--selftest', $report) -Wait -PassThru
Get-Content $report -Encoding utf8 | ForEach-Object { Write-Host $_ }
if ($process.ExitCode -ne 0) { throw "self test failed ($($process.ExitCode))" }
Write-Host 'OK'
