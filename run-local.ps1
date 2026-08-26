param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$Version = "",
    [string]$DotnetPath = "E:\SoftwareEnvironment\dotnet\dotnet.exe",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$RepoDir = $PSScriptRoot
$ProjectDir = Join-Path $RepoDir "src\Snap.Hutao.Remastered\Snap.Hutao.Remastered"
$ExePath = Join-Path $ProjectDir "bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\Snap.Hutao.Remastered.exe"

Get-Process -Name "Snap.Hutao.Remastered" -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $NoBuild) {
    & (Join-Path $RepoDir "build-local.ps1") -Configuration $Configuration -Version $Version -DotnetPath $DotnetPath -StopRunning:$false
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath"
}

Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath)
Write-Host "Started $ExePath"
