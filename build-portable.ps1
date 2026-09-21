[CmdletBinding()]
param([string]$Version = "2.0.0")

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$HostRoot = Join-Path $ProjectRoot "src\Host"
$InjectorRoot = Join-Path $ProjectRoot "src\Injector"
$NativeRoot = Join-Path $ProjectRoot "src\NativePayload"
$HostProject = Join-Path $HostRoot "GestureCompanionPointerHost.csproj"
$InjectorProject = Join-Path $InjectorRoot "GestureCompanionBridge.Injector.csproj"
$NativeProject = Join-Path $NativeRoot "GestureCompanionBridge.NativePayload.vcxproj"
$ReleaseRoot = Join-Path $ProjectRoot "release"
$PackageName = "GestureCompanion-v$Version-win-x64-portable"
$PackageRoot = Join-Path $ReleaseRoot $PackageName
$ZipPath = Join-Path $ReleaseRoot "$PackageName.zip"
$WorkRoot = Join-Path $ReleaseRoot ".build-$Version"

function Remove-ReleasePath([string]$Path) {
    $releasePrefix = [IO.Path]::GetFullPath($ReleaseRoot).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside release directory: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

function Find-MSBuild {
    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "MSBuild.exe was not found. Install the Visual Studio C++ Desktop Development workload."
}

New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
Remove-ReleasePath $WorkRoot
Remove-ReleasePath $PackageRoot
if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }

$HostPublish = Join-Path $WorkRoot "host"
$InjectorX86 = Join-Path $WorkRoot "injector-x86"
$InjectorX64 = Join-Path $WorkRoot "injector-x64"
New-Item -ItemType Directory -Path $HostPublish,$InjectorX86,$InjectorX64,$PackageRoot -Force | Out-Null

Write-Host "Publishing Gesture Companion host..."
& dotnet clean $HostProject -c Release -r win-x64 -nologo
if ($LASTEXITCODE -ne 0) { throw "Host clean failed." }
& dotnet publish $HostProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $HostPublish
if ($LASTEXITCODE -ne 0) { throw "Host publish failed." }

foreach ($runtime in @("win-x86", "win-x64")) {
    $output = if ($runtime -eq "win-x86") { $InjectorX86 } else { $InjectorX64 }
    Write-Host "Publishing $runtime injector..."
    & dotnet publish $InjectorProject -c Release -r $runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output
    if ($LASTEXITCODE -ne 0) { throw "$runtime injector publish failed." }
}

$MSBuild = Find-MSBuild
Write-Host "Building native Win32 payload..."
& $MSBuild $NativeProject /p:Configuration=Release /p:Platform=Win32 /p:LinkIncremental=false /nologo
if ($LASTEXITCODE -ne 0) { throw "Native Win32 build failed." }
Write-Host "Building native x64 payload..."
& $MSBuild $NativeProject /p:Configuration=Release /p:Platform=x64 /p:LinkIncremental=false /nologo
if ($LASTEXITCODE -ne 0) { throw "Native x64 build failed." }

Copy-Item -Path (Join-Path $HostPublish '*') -Destination $PackageRoot -Recurse -Force
Copy-Item (Join-Path $HostRoot "icon.ico") $PackageRoot -Force
$BridgeX86 = Join-Path $PackageRoot "Bridge\win-x86"
$BridgeX64 = Join-Path $PackageRoot "Bridge\win-x64"
New-Item -ItemType Directory -Path $BridgeX86,$BridgeX64 -Force | Out-Null
Copy-Item (Join-Path $InjectorX86 "GestureCompanionBridge.Injector.exe") $BridgeX86 -Force
Copy-Item (Join-Path $InjectorX64 "GestureCompanionBridge.Injector.exe") $BridgeX64 -Force
Copy-Item (Join-Path $NativeRoot "Release\GestureCompanionBridge.NativePayload.dll") $BridgeX86 -Force
Copy-Item (Join-Path $NativeRoot "x64\Release\GestureCompanionBridge.NativePayload.dll") $BridgeX64 -Force

foreach ($document in @(
    (Join-Path $ProjectRoot "docs\README.md"),
    (Join-Path $ProjectRoot "docs\QUICKSTART.md"),
    (Join-Path $ProjectRoot "docs\LICENSE")
)) {
    if (Test-Path -LiteralPath $document) { Copy-Item $document $PackageRoot -Force }
}

Compress-Archive -Path (Join-Path $PackageRoot '*') -DestinationPath $ZipPath -CompressionLevel Optimal -Force
Remove-ReleasePath $WorkRoot
Write-Host "Portable package created: $ZipPath"
