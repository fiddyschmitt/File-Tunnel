<#
.SYNOPSIS
    Builds ft for Android/Termux (linux-bionic-arm64, NativeAOT) from this working tree, on the lab's
    build VM.

.DESCRIPTION
    Boots the build VM (role: build in settings.yml) if it's off - creating it the first time, which
    takes a while - and runs /opt/ftbuild/build-bionic.sh there. That builds this repo's ft\ as it is
    on disk, uncommitted changes included, and writes the binary to linux.build.output_dir
    (R:\Temp\ft release\linux-bionic-arm64\ft), where the tests pick it up. A VM that was off is shut
    down again afterwards.

.PARAMETER KeepRunning
    Leave the build VM running afterwards (quicker next time; it takes 6 GB).
#>
param([switch]$KeepRunning)

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

$vagrant = Get-Command vagrant -ErrorAction SilentlyContinue
if (-not $vagrant) { throw "Vagrant isn't installed here (or isn't on the PATH)." }
$ruby = Join-Path (Split-Path (Split-Path $vagrant.Source)) 'embedded\mingw64\bin\ruby.exe'
$settings = (& $ruby -ryaml -rjson -e 'puts YAML.load_file(ARGV[0]).to_json' settings.yml) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "couldn't read settings.yml" }
$name = ($settings.machines | Where-Object role -eq 'build' | Select-Object -First 1).name
if (-not $name) { throw 'No machine with role: build in settings.yml' }

$state = ((& vagrant status $name --machine-readable) | Where-Object { $_ -like '*,state,*' } | Select-Object -First 1) -split ',' | Select-Object -Index 3
if ($state -ne 'running') {
    & vagrant up $name
    if ($LASTEXITCODE -ne 0) { throw "vagrant up $name failed" }
}

& vagrant ssh $name -c '/opt/ftbuild/build-bionic.sh'
$code = $LASTEXITCODE

if ($state -ne 'running' -and -not $KeepRunning) { & vagrant halt $name }
if ($code -ne 0) { Write-Host 'The build failed (see above).' -ForegroundColor Red }
exit $code
