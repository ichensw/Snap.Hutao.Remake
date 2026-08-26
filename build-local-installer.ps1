param(
    [string]$Version = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$DotnetPath = "E:\SoftwareEnvironment\dotnet\dotnet.exe",
    [string]$IsccPath = "E:\SoftwareEnvironment\InnoSetup\ISCC.exe",
    [bool]$StopRunning = $true
)

$ErrorActionPreference = "Stop"

$RepoDir = $PSScriptRoot
$ProjectDir = Join-Path $RepoDir "src\Snap.Hutao.Remastered\Snap.Hutao.Remastered"
$Project = Join-Path $ProjectDir "Snap.Hutao.Remastered.csproj"
$PublishDir = Join-Path $RepoDir "Installer\Publish"
$OutputDir = Join-Path $RepoDir "publish"
$IssFile = Join-Path $RepoDir "Installer\installer.iss"
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

function Assert-UnderRepo {
    param([string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRepo = [System.IO.Path]::GetFullPath($RepoDir)
    if (-not $fullPath.StartsWith($fullRepo, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete or overwrite path outside repo: $fullPath"
    }

    return $fullPath
}

if (-not (Test-Path -LiteralPath $DotnetPath)) {
    throw "dotnet not found: $DotnetPath"
}

if (-not (Test-Path -LiteralPath $IsccPath)) {
    throw "Inno Setup ISCC not found: $IsccPath"
}

$Version = Resolve-Version $Version

if ($StopRunning) {
    Get-Process -Name "Snap.Hutao.Remastered" -ErrorAction SilentlyContinue | Stop-Process -Force
}

Write-Host "Building installer for Snap.Hutao.Remastered $Version ($Configuration)"

if (Test-Path -LiteralPath $BinDir) {
    Remove-Item -LiteralPath (Assert-UnderRepo $BinDir) -Recurse -Force
}

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

if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath (Assert-UnderRepo $PublishDir) -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
Copy-Item -Path (Join-Path $BinDir "*") -Destination $PublishDir -Recurse -Force

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}

& $IsccPath "/dMyAppVersion=$Version" $IssFile

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$Installer = Join-Path $OutputDir "Snap.Hutao.Remastered-$Version-Setup.exe"
if (-not (Test-Path -LiteralPath $Installer)) {
    throw "Installer not found: $Installer"
}

Get-Item -LiteralPath $Installer
