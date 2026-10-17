<#
    File Tunnel test lab: a Windows machine.

    Vagrant runs this inside the VM (over WinRM, elevated) when it first creates the VM, and again on
    every `vagrant provision`. Each step sets a state, so a re-run changes nothing unless something
    drifted. When a step needs a restart (the NFS client), it leaves C:\ft-lab\restart-required, and
    the Vagrantfile restarts the VM.

    The tests drive the machine over SSH (PowerShell, password login), and start ft through RunRemote,
    inside the logged-on session: that is where \\tsclient (RDP drive redirection), the NFS drive and
    saved SMB credentials live, none of which an SSH session can see.

    Inputs (from the Vagrantfile): LAB_PUBKEY, the lab's SSH public key; LAB_USER and LAB_PASSWORD,
    the account the tests use (the box's own).
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

foreach ($name in 'LAB_PUBKEY', 'LAB_USER', 'LAB_PASSWORD') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set (the Vagrantfile passes it)" }
}
$user = $env:LAB_USER
New-Item -ItemType Directory -Force -Path 'C:\ft-lab' | Out-Null

# --- OpenSSH Server, running and starting at boot -------------------------------------------------
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null
}
Set-Service sshd -StartupType Automatic
Start-Service sshd

# --- SSH sessions get PowerShell, the shell the tests write their commands in ---------------------
if (-not (Test-Path 'HKLM:\SOFTWARE\OpenSSH')) { New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' | Out-Null }
Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Type String `
    -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"

# --- The lab's key opens the account (for scripts and for you; the tests use the password) --------
# Whichever file sshd reads the account's keys from: Windows' stock sshd_config sends administrators
# to ProgramData\ssh\administrators_authorized_keys, but this box's config doesn't, so there it's the
# account's own .ssh\authorized_keys. (Vagrant runs this script as that account.)
$conf = & "$env:SystemRoot\System32\OpenSSH\sshd.exe" -T -C "user=$user,host=localhost,addr=127.0.0.1"
if ($LASTEXITCODE -ne 0) { throw 'sshd -T failed' }
$keys = (($conf | Where-Object { $_ -like 'authorizedkeysfile *' }) -split ' ')[1]
$keys = $keys -replace '__PROGRAMDATA__', $env:ProgramData -replace '/', '\'
if (-not [IO.Path]::IsPathRooted($keys)) { $keys = Join-Path $env:USERPROFILE $keys }
$have = if (Test-Path $keys) { @(Get-Content $keys) } else { @() }
if ($have -notcontains $env:LAB_PUBKEY) {
    New-Item -ItemType Directory -Force -Path (Split-Path $keys) | Out-Null
    Set-Content -Path $keys -Value ($have + $env:LAB_PUBKEY) -Encoding ascii
}
if ($keys -like "$env:ProgramData\*") {
    # sshd ignores the shared file unless only Administrators and SYSTEM can reach it.
    icacls.exe $keys /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed on $keys" }
}

# --- No firewall ---------------------------------------------------------------------------------
# The tests connect from the PC into ft's port forwards on these machines, on whatever ports a test
# picks (5001-5999 and more), as well as SSH, SMB, RDP and RunRemote's UDP 8888. A scoped rule for
# those ports proved unreliable here, and this is an isolated lab, so the firewall is off.
Set-NetFirewallProfile -All -Enabled False

# --- Names resolve through VirtualBox's DNS relay, which asks the PC ------------------------------
# VirtualBox's DHCP hands the NAT adapter the DNS servers the PC had when the VM started, and Windows
# keeps them after they change (a VPN on the PC connecting or disconnecting). VirtualBox's relay,
# 10.0.2.3, asks the PC's resolver at the time of each query instead (--natdnshostresolver1).
$nat = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like '10.0.2.*' } | Select-Object -First 1
if ($nat -and ((Get-DnsClientServerAddress -InterfaceIndex $nat.InterfaceIndex -AddressFamily IPv4).ServerAddresses -join ',') -ne '10.0.2.3') {
    Set-DnsClientServerAddress -InterfaceIndex $nat.InterfaceIndex -ServerAddresses '10.0.2.3'
    Clear-DnsClientCache
}

# --- RDP: the RDP rows connect in, as the account, to its logged-on session -----------------------
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
Set-Service TermService -StartupType Automatic

# --- The NFS client: the Nfs rows map X: to the Linux server's export with mount.exe --------------
# Anonymous UID/GID 0, so mounting needs no credentials. A reserved (privileged) source port, because
# the Linux export is 'secure' (no 'insecure' option): without it the mount hangs, then fails with
# "Network Error 53". The client starts only after a restart (whatever the installer says), and reads
# its settings when it starts, so any change here asks for one.
$nfsChanged = $false
foreach ($feature in 'ServicesForNFS-ClientOnly', 'ClientForNFS-Infrastructure') {
    if ((Get-WindowsOptionalFeature -Online -FeatureName $feature).State -ne 'Enabled') {
        Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart | Out-Null
        $nfsChanged = $true
    }
}
$nfs = 'HKLM:\SOFTWARE\Microsoft\ClientForNFS\CurrentVersion\Default'
if (-not (Test-Path $nfs)) { New-Item -Path $nfs -Force | Out-Null }
foreach ($setting in @{ AnonymousUid = 0; AnonymousGid = 0; UseReservedPorts = 1 }.GetEnumerator()) {
    if ((Get-ItemProperty $nfs -Name $setting.Key -ErrorAction SilentlyContinue).($setting.Key) -ne $setting.Value) {
        New-ItemProperty $nfs -Name $setting.Key -Value $setting.Value -PropertyType DWord -Force | Out-Null
        $nfsChanged = $true
    }
}
# Installed but not running yet (a run that stopped before its restart, say): it needs one too.
if ((Get-Service NfsClnt -ErrorAction SilentlyContinue).Status -ne 'Running') { $nfsChanged = $true }
if ($nfsChanged) { New-Item -ItemType File -Force -Path 'C:\ft-lab\restart-required' | Out-Null }

# --- C:\Temp\ft, and the SMB share \\<this machine>\Shared --------------------------------------
New-Item -ItemType Directory -Force -Path 'C:\Temp\ft\Shared' | Out-Null
if (-not (Get-SmbShare -Name Shared -ErrorAction SilentlyContinue)) {
    New-SmbShare -Name Shared -Path 'C:\Temp\ft\Shared' -FullAccess Everyone | Out-Null
}
icacls.exe 'C:\Temp\ft\Shared' /grant 'Everyone:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'icacls failed on C:\Temp\ft\Shared' }

# --- RunRemote: starts ft inside the logged-on session, when the tests ask (UDP 8888) -------------
# Installed from the PC's build (shared in as \\vboxsvr\runremote). It runs at logon, and every two
# minutes after that, so it comes back if it ever dies: a second copy exits at once while the first
# holds the port.
# It runs at normal priority, like a program the user starts. A task's default priority (7) is below
# normal, with low I/O and memory priority too, and everything RunRemote starts inherits it: ft then
# got no CPU at all for seconds at a time whenever the tests' own SSH sessions kept both CPUs busy.
$rrDir = 'C:\Temp\runremote'
$rrExe = Join-Path $rrDir 'server.exe'
$rrSource = '\\vboxsvr\runremote\server.exe'
if (-not (Test-Path $rrSource)) { throw "No RunRemote server at $rrSource (windows.runremote_dir in settings.yml)" }
New-Item -ItemType Directory -Force -Path $rrDir | Out-Null
$rrRunning = @(Get-Process server -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $rrExe })
$rrStale = -not (Test-Path $rrExe) -or (Get-FileHash $rrExe).Hash -ne (Get-FileHash $rrSource).Hash
if ($rrStale -or ($rrRunning | Where-Object { $_.PriorityClass -ne 'Normal' })) {
    Stop-ScheduledTask -TaskName RunRemote -ErrorAction SilentlyContinue
    $rrRunning | Stop-Process -Force
    Start-Sleep -Seconds 1
    if ($rrStale) { Copy-Item -Force $rrSource $rrExe }
}
$action = New-ScheduledTaskAction -Execute $rrExe -WorkingDirectory $rrDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 2)).Repetition
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Priority 4
Register-ScheduledTask -TaskName RunRemote -Action $action -Trigger $trigger -Principal $principal `
    -Settings $taskSettings -Description 'File Tunnel lab: RunRemote server (UDP 8888) in the logged-on session' `
    -Force | Out-Null
# Start it now if the account is logged on (it starts by itself at every logon).
Start-ScheduledTask -TaskName RunRemote -ErrorAction SilentlyContinue

# --- The account is logged on at the console after every boot -----------------------------------
# The box logs on by itself only twice (its unattend file's LogonCount is 2: the first boot and the
# rename). At the next boot Windows sees the count run out and switches autologon off, so the count
# goes, and a change here takes a restart to show.
$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$current = Get-ItemProperty -Path $winlogon
if ($current.AutoAdminLogon -ne '1' -or $current.DefaultUserName -ne $user -or
    $current.DefaultPassword -ne $env:LAB_PASSWORD -or $null -ne $current.AutoLogonCount) {
    Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Type String -Value '1'
    Set-ItemProperty -Path $winlogon -Name DefaultUserName -Type String -Value $user
    Set-ItemProperty -Path $winlogon -Name DefaultPassword -Type String -Value $env:LAB_PASSWORD
    Remove-ItemProperty -Path $winlogon -Name AutoLogonCount -ErrorAction SilentlyContinue
    New-Item -ItemType File -Force -Path 'C:\ft-lab\restart-required' | Out-Null
}

# --- Never sleeps (a sleeping test machine looks like a failing test) -----------------------------
powercfg.exe /change standby-timeout-ac 0
powercfg.exe /change hibernate-timeout-ac 0

# --- Summary ------------------------------------------------------------------------------------
$os = Get-CimInstance Win32_OperatingSystem
$sid = ((Get-LocalUser $user).SID.Value -split '-')[0..6] -join '-'
$restart = if (Test-Path 'C:\ft-lab\restart-required') { 'restarting to finish' } else { 'ready' }
Write-Host "$($env:COMPUTERNAME): $($os.Caption) build $($os.BuildNumber), machine SID $sid, sshd $((Get-Service sshd).Status), $restart"
