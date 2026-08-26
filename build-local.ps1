param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$Version = "",
    [string]$DotnetPath = "E:\SoftwareEnvironment\dotnet\dotnet.exe",
    [bool]$StopRunning = $true
)

$ErrorActionPreference = "Stop"

$RepoDir = $PSScriptRoot
$ProjectDir = Join-Path $RepoDir "src\Snap.Hutao.Remastered\Snap.Hutao.Remastered"
$Project = Join-Path $ProjectDir "Snap.Hutao.Remastered.csproj"
$BinDir = Join-Path $ProjectDir "bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64"
$ExePath = Join-Path $BinDir "Snap.Hutao.Remastered.exe"

function Resolve-Version {
    param([string]$InputVersion)

    if ([string]::IsNullOrWhiteSpace($InputVersion)) {
        [xml]$ProjectXml = Get-Content -LiteralPath $Project -Encoding UTF8
        $InputVersion = $ProjectXml.Project.PropertyGroup.Version | Select-Object -First 1
    }

    if ($InputVersion -match '^\d+\.\d+\.\d+$') {
        return "$InputVersion.0"
    }

    if ($InputVersion -match '^\d+\.\d+\.\d+\.\d+$') {
        return $InputVersion
    }

    throw "Version must be like 1.20.1 or 1.20.1.0"
}

function Stop-HutaoProcess {
    Get-Process -Name "Snap.Hutao.Remastered" -ErrorAction SilentlyContinue | Stop-Process -Force
}

if (-not (Test-Path -LiteralPath $DotnetPath)) {
    throw "dotnet not found: $DotnetPath"
}

$Version = Resolve-Version $Version

if ($StopRunning) {
    Stop-HutaoProcess
}

Write-Host "Building Snap.Hutao.Remastered $Version ($Configuration, unpackaged)"

& $DotnetPath build $Project `
    -c $Configuration `
    --self-contained true `
    -p:Platform=x64 `
    -p:EnableMsixPackaging=false `
    -p:WindowsAppSDKSelfContained=true `
    -p:WindowsPackageType=None `
    -p:AppxPackage=false `
    -p:AppxPackageSigningEnabled=false `
    -p:AppxBundle=Never `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Build output not found: $ExePath"
}

Get-Item -LiteralPath $ExePath
