<#
.SYNOPSIS
    Is the lab ready for a test run? Runs each machine's smoke test, over the LAN, the way the tests
    reach it.

.DESCRIPTION
    For each VM in settings.yml that is running, its smoke test over SSH at its LAN address with the
    lab's key: `sudo /opt/ft/smoke-test.sh` on Linux, C:\ft-lab\smoke-test.ps1 on Windows. Then the
    Mac's emulators (.\mac.ps1 status). A VM that is off is listed, not checked.

    A share that dropped shows as a WARN line: the tests mount it again before they run, or
    `vagrant ssh <name> -c "sudo /opt/ft/mounts.sh"` does it now.

    Exit code 1 if anything failed.

.PARAMETER NoMac
    Skip the Mac.
#>
param([switch]$NoMac)

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

$vagrant = Get-Command vagrant -ErrorAction SilentlyContinue
if (-not $vagrant) { throw "Vagrant isn't installed here (or isn't on the PATH)." }
$ruby = Join-Path (Split-Path (Split-Path $vagrant.Source)) 'embedded\mingw64\bin\ruby.exe'
$settings = (& $ruby -ryaml -rjson -e 'puts YAML.load_file(ARGV[0]).to_json' settings.yml) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "couldn't read settings.yml" }
$key = $settings.ssh_key -replace '^~', $env:USERPROFILE
$ssh = @('-i', $key, '-o', 'StrictHostKeyChecking=no', '-o', 'UserKnownHostsFile=/dev/null',
         '-o', 'ConnectTimeout=10', '-o', 'LogLevel=ERROR', '-o', 'BatchMode=yes')

# Which VMs are running.
$states = @{}
foreach ($line in (& vagrant status --machine-readable)) {
    $fields = $line -split ','
    if ($fields.Count -ge 4 -and $fields[2] -eq 'state') { $states[$fields[1]] = $fields[3] }
}

$failed = $false
foreach ($m in $settings.machines) {
    Write-Host ''
    if ($states[$m.name] -ne 'running') {
        Write-Host "$($m.name) ($($m.ip)): $($states[$m.name]) - not checked" -ForegroundColor Yellow
        continue
    }
    Write-Host "$($m.name) ($($m.role), $($m.ip))" -ForegroundColor Cyan
    if ($m.os -eq 'windows') {
        $out = & ssh @ssh "$($settings.accounts.windows.user)@$($m.ip)" 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\ft-lab\smoke-test.ps1'
    } else {
        $out = & ssh @ssh "$($settings.accounts.linux.user)@$($m.ip)" 'sudo /opt/ft/smoke-test.sh'
    }
    $code = $LASTEXITCODE
    foreach ($line in $out) {
        $color = if ($line -like 'FAIL*') { 'Red' } elseif ($line -like 'WARN*') { 'Yellow' } else { 'Gray' }
        Write-Host $line -ForegroundColor $color
    }
    if ($code -ne 0) {
        $failed = $true
        if (-not $out) { Write-Host "FAIL  no SSH at $($m.ip) with the lab's key" -ForegroundColor Red }
    }
}

if (-not $NoMac) {
    Write-Host ''
    Write-Host "$($settings.mac.name) ($($settings.mac.host)): the Android emulators" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'mac.ps1') status
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}

Write-Host ''
if ($failed) { Write-Host 'Not ready: see the FAIL lines above.' -ForegroundColor Red; exit 1 }
Write-Host 'Ready.' -ForegroundColor Green
exit 0
