# Builds LockX-Setup-<version>.exe into .\dist
#   .\build.ps1            run tests, publish self-contained, compile the installer
#   .\build.ps1 -SkipTests
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content src\LockX\LockX.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (-not $SkipTests) {
    dotnet test tests\LockX.Core.Tests --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src\LockX\LockX.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

New-Item -ItemType Directory -Force dist | Out-Null
& $iscc /Qp "/DAppVersion=$version" installer\LockX.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

Get-Item "dist\LockX-Setup-$version.exe" | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
