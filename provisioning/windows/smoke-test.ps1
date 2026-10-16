<#
    File Tunnel test lab: checks what the tests rely on, on this Windows machine. Vagrant runs it after
    every `vagrant up`, `reload` and `provision`; check.ps1 runs it over the LAN; run it any time with
    `powershell -ExecutionPolicy Bypass -File C:\ft-lab\smoke-test.ps1`. Exits 1 on any FAIL.
#>
$ErrorActionPreference = 'Continue'
$script:failed = $false
function Pass([string]$what) { Write-Output "PASS  $what" }
function Warn([string]$what) { Write-Output "WARN  $what" }
function Fail([string]$what) { Write-Output "FAIL  $what"; $script:failed = $true }
function Check([string]$what, [scriptblock]$test) {
    $ok = $false
    try { $ok = [bool](& $test) } catch { }
    if ($ok) { Pass $what } else { Fail $what }
}

$os = Get-CimInstance Win32_OperatingSystem
$addresses = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '10.0.2.*' -and $_.IPAddress -ne '127.0.0.1' }).IPAddress -join ', '
Write-Output "$($env:COMPUTERNAME) ($addresses): $($os.Caption) build $($os.BuildNumber)"

Check 'sshd running' { (Get-Service sshd).Status -eq 'Running' }
Check 'SSH sessions get PowerShell' { (Get-ItemProperty 'HKLM:\SOFTWARE\OpenSSH').DefaultShell -like '*powershell.exe' }
Check 'firewall off' { -not (Get-NetFirewallProfile | Where-Object Enabled) }
Check 'RDP on (3389)' {
    (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server').fDenyTSConnections -eq 0 -and
    (Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction SilentlyContinue)
}
Check 'NFS client running (mount.exe), on reserved ports' {
    (Get-Command mount.exe -ErrorAction SilentlyContinue) -and
    (Get-Service NfsClnt).Status -eq 'Running' -and
    (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\ClientForNFS\CurrentVersion\Default').UseReservedPorts -eq 1
}
Check 'SMB share \\<this machine>\Shared' { Get-SmbShare -Name Shared -ErrorAction SilentlyContinue }
Check "the PC's C:\ at \\vboxsvr\c_drive" { Test-Path '\\vboxsvr\c_drive\Windows' }
Check 'C:\Temp\ft writable' {
    $probe = "C:\Temp\ft\.ft-smoke.$PID"
    Set-Content $probe 'x'; Remove-Item $probe; $true
}

# The account logs on at boot, and RunRemote starts in that session: give both a couple of minutes.
$deadline = (Get-Date).AddMinutes(3)
do {
    $session = (query user 2>$null) -match '\bconsole\b.*\bActive\b'
    $udp = Get-NetUDPEndpoint -LocalPort 8888 -ErrorAction SilentlyContinue
    if ($session -and $udp) { break }
    Start-Sleep -Seconds 5
} while ((Get-Date) -lt $deadline)
if ($session) { Pass 'the account is logged on at the console' } else { Fail 'the account is logged on at the console' }
if ($udp) { Pass 'RunRemote listening (UDP 8888)' } else { Fail 'RunRemote listening (UDP 8888)' }

if ($script:failed) { exit 1 }
exit 0
