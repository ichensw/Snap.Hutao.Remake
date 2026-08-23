param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$DotnetPath = "E:\SoftwareEnvironment\dotnet\dotnet.exe",
    [string]$IsccPath = "E:\SoftwareEnvironment\InnoSetup\ISCC.exe"
)

$ErrorActionPreference = "Stop"

$RepoDir = $PSScriptRoot
$ProjectDir = Join-Path $RepoDir "src\Snap.Hutao.Remastered\Snap.Hutao.Remastered"
$Project = Join-Path $ProjectDir "Snap.Hutao.Remastered.csproj"
$TempProject = Join-Path $ProjectDir "Snap.Hutao.Remastered.Unpackaged.csproj"
$Manifest = Join-Path $ProjectDir "Package.appxmanifest"
$PublishDir = Join-Path $RepoDir "Installer\Publish"
$OutputDir = Join-Path $RepoDir "publish"
$IssFile = Join-Path $RepoDir "Installer\installer.iss"

if (-not $Version) {
    [xml]$ManifestXml = Get-Content -LiteralPath $Manifest -Encoding UTF8
    $Version = $ManifestXml.Package.Identity.Version
}

if (-not ($Version -match '^\d+\.\d+\.\d+\.\d+$')) {
    throw "Version must be like 1.20.1.0"
}

Write-Host "Building Snap.Hutao.Remastered $Version"

$projectText = Get-Content -LiteralPath $Project -Encoding UTF8 -Raw
$projectText = $projectText -replace '(?m)^\s*<AppxManifest Include="Package(\.development)?\.appxmanifest".*\r?\n', ''
Set-Content -LiteralPath $TempProject -Encoding UTF8 -Value $projectText

try {
    & $DotnetPath build $TempProject `
        -c $Configuration `
        --self-contained true `
        -p:Platform=x64 `
        -p:WindowsAppSDKSelfContained=true `
        -p:WindowsPackageType=None `
        -p:AppxPackage=false `
        -p:AppxPackageSigningEnabled=false `
        -p:AppxBundle=Never

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    $BinDir = Join-Path $ProjectDir "bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64"
    if (-not (Test-Path -LiteralPath (Join-Path $BinDir "Snap.Hutao.Remastered.exe"))) {
        throw "Build output not found: $BinDir"
    }

    if (Test-Path -LiteralPath $PublishDir) {
        Remove-Item -LiteralPath $PublishDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
    Copy-Item -Path (Join-Path $BinDir "*") -Destination $PublishDir -Recurse -Force
    Get-ChildItem -LiteralPath $PublishDir -Recurse -Filter "Snap.Hutao.Remastered.Unpackaged.*" | Remove-Item -Force

    if (-not (Test-Path -LiteralPath $OutputDir)) {
        New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    }

    & $IsccPath "/dMyAppVersion=$Version" $IssFile

    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE"
    }

    $Installer = Join-Path $OutputDir "Snap.Hutao.Remastered-$Version-Setup.exe"
    Get-Item -LiteralPath $Installer
}
finally {
    if (Test-Path -LiteralPath $TempProject) {
        Remove-Item -LiteralPath $TempProject -Force
    }
}
