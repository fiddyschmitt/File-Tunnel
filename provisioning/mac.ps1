<#
.SYNOPSIS
    The lab's Mac, from this PC: its setup, and the two Android emulators it runs for the tests.

.DESCRIPTION
    The Mac (settings.yml, `mac:`) is a test machine itself and runs the Android emulators the tests
    use as their Android clients. This script keeps the control here, so settings.yml stays the one
    place you edit: each run copies mac\ to ~/<mac.project_dir> on the Mac and runs it there over SSH,
    with the lab's key.

        .\mac.ps1 setup     Lets the lab's key in to the Mac (asks for the Mac's password, once, if
                            none of your keys is accepted there), then runs mac/setup.sh: the Android
                            SDK and AVD, what the Android rows push into the emulators, and the Mac's
                            SMB share. Run it once, and after changing `mac:` or `accounts.mac_smb`.
                            Safe to repeat.
        .\mac.ps1 up        Starts both emulators, or refreshes a pair whose networking went stale
                            (after the Mac or this PC restarts). Leaves a healthy pair alone.
        .\mac.ps1 halt      Stops both.
        .\mac.ps1 status    Are both running and on the LAN?
        .\mac.ps1 ssh       A shell on the Mac.

.EXAMPLE
    .\mac.ps1 setup
    .\mac.ps1 up
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('setup', 'up', 'halt', 'status', 'ssh')]
    [string]$Command
)

# The native commands below report failure by exit code, which is checked. 'Stop' would make Windows
# PowerShell treat anything they print on stderr (sdkmanager's progress, say) as a terminating error
# whenever this script's output is redirected.
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

if (-not $Command) {
    Get-Help $PSCommandPath -Detailed
    exit 1
}

# settings.yml, read with the Ruby that comes with Vagrant (Windows PowerShell has no YAML reader).
$vagrant = Get-Command vagrant -ErrorAction SilentlyContinue
if (-not $vagrant) { throw "Vagrant isn't installed here (or isn't on the PATH)." }
$ruby = Join-Path (Split-Path (Split-Path $vagrant.Source)) 'embedded\mingw64\bin\ruby.exe'
$settings = (& $ruby -ryaml -rjson -e 'puts YAML.load_file(ARGV[0]).to_json' settings.yml) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "couldn't read settings.yml" }
$mac = $settings.mac
$target = "$($mac.user)@$($mac.host)"
$dir = $mac.project_dir

# The lab's key. The Vagrantfile makes it on first use too.
$key = $settings.ssh_key -replace '^~', $env:USERPROFILE
if (-not (Test-Path $key)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $key) -ErrorAction Stop | Out-Null
    & ssh-keygen -q -t ed25519 -N '""' -C 'ft test lab' -f $key   # '""': an empty passphrase, as Windows PowerShell passes it
    if ($LASTEXITCODE -ne 0) { throw "ssh-keygen couldn't make $key" }
}
$pubkey = (Get-Content "$key.pub" -Raw -ErrorAction Stop).Trim()
$ssh = @('-i', $key, '-o', 'StrictHostKeyChecking=accept-new', '-o', 'ConnectTimeout=10', '-o', 'LogLevel=ERROR')

if ($Command -eq 'setup') {
    & ssh @ssh -o BatchMode=yes $target true 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Letting the lab's key in to $target (with one of your keys, or its password):" -ForegroundColor Cyan
        & ssh -o StrictHostKeyChecking=accept-new $target "umask 077; mkdir -p ~/.ssh; grep -qxF '$pubkey' ~/.ssh/authorized_keys 2>/dev/null || echo '$pubkey' >> ~/.ssh/authorized_keys"
        if ($LASTEXITCODE -ne 0) { throw "couldn't add the lab's key on $target" }
    }
}

if ($Command -eq 'ssh') {
    & ssh @ssh -t $target
    exit $LASTEXITCODE
}

# Every run: the Mac gets this folder's mac\.
& ssh @ssh -o BatchMode=yes $target "mkdir -p ~/$dir/mac"
if ($LASTEXITCODE -ne 0) { throw "can't reach $target with the lab's key: run .\mac.ps1 setup first" }
& scp @ssh -q (Get-ChildItem mac -File).FullName "${target}:$dir/mac/"
if ($LASTEXITCODE -ne 0) { throw "couldn't copy mac\ to ${target}:$dir/mac" }

# The settings each script needs, as environment variables, each single-quoted for the Mac's shell.
function Quote([string]$s) { "'" + ($s -replace "'", "'\''") + "'" }
$emulators = $mac.emulators
$vars = [ordered]@{
    FT_AVD          = $emulators.avd
    FT_IMAGE        = $emulators.system_image
    FT_EMU_VERSION  = $emulators.emulator.version
    FT_EMU_ZIP      = $emulators.emulator.zip
    FT_EMU_SHA256   = $emulators.emulator.sha256
    FT_EMU_ARGS     = [string]$emulators.emulator.args
    FT_PORTS        = ($emulators.ports -join ' ')
    FT_BRIDGE       = $emulators.bridge_interface
    FT_SMB_USER     = $settings.accounts.mac_smb.user
    FT_SMB_PASSWORD = $settings.accounts.mac_smb.password
}
$envs = ($vars.GetEnumerator() | ForEach-Object { "$($_.Key)=$(Quote $_.Value)" }) -join ' '

$script = if ($Command -eq 'setup') { 'setup.sh' } else { "emulators.sh $Command" }
Write-Host "$script   (on $target)" -ForegroundColor Cyan
& ssh @ssh $target "export $envs; bash ~/$dir/mac/$script"
exit $LASTEXITCODE
