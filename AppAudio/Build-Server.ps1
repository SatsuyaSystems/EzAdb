param(
    [Parameter(Mandatory)] [string] $ScrcpySource,
    [Parameter(Mandatory)] [string] $AndroidPlatform,
    [Parameter(Mandatory)] [string] $AndroidBuildTools,
    [Parameter(Mandatory)] [string] $Jdk,
    [Parameter(Mandatory)] [string] $Output,
    [switch] $SourceAlreadyPatched
)

$ErrorActionPreference = 'Stop'
$ScrcpySource = (Resolve-Path -LiteralPath $ScrcpySource).Path
$AndroidPlatform = (Resolve-Path -LiteralPath $AndroidPlatform).Path
$AndroidBuildTools = (Resolve-Path -LiteralPath $AndroidBuildTools).Path
$Jdk = (Resolve-Path -LiteralPath $Jdk).Path
$Output = [IO.Path]::GetFullPath($Output)
$scratch = (New-Item -ItemType Directory (Join-Path (Split-Path $Output) ([guid]::NewGuid().ToString('N'))) -Force).FullName
$generated = (New-Item -ItemType Directory (Join-Path $scratch 'generated/com/genymobile/scrcpy') -Force).FullName
$generatedRoot = Join-Path $scratch 'generated'
$classes = (New-Item -ItemType Directory (Join-Path $scratch 'classes')).FullName
$androidJar = Join-Path $AndroidPlatform 'android.jar'

function Run([string] $exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "Build command failed: $exe ($LASTEXITCODE)" }
}

if (!$SourceAlreadyPatched) {
    Push-Location $ScrcpySource
    try { Run git apply --no-index (Join-Path $PSScriptRoot 'scrcpy-5.0.1.patch') }
    finally { Pop-Location }
}

@'
package com.genymobile.scrcpy;
public final class BuildConfig {
    public static final boolean DEBUG = false;
    public static final String VERSION_NAME = "5.0.1";
}
'@ | Set-Content -Encoding utf8NoBOM (Join-Path $generated 'BuildConfig.java')

$server = Join-Path $ScrcpySource 'server/src/main'
$aidl = Join-Path $AndroidBuildTools 'aidl.exe'
$aidlRoot = Join-Path $server 'aidl'
Run $aidl "-o$generatedRoot" "-I$aidlRoot" (Join-Path $aidlRoot 'android/content/IOnPrimaryClipChangedListener.aidl')
Run $aidl "-o$generatedRoot" "-I$aidlRoot" "-p$(Join-Path $AndroidPlatform 'framework.aidl')" (Join-Path $aidlRoot 'android/view/IDisplayWindowListener.aidl')

$java = Join-Path $Jdk 'bin/java.exe'
$javac = Join-Path $Jdk 'bin/javac.exe'
$jar = Join-Path $Jdk 'bin/jar.exe'
$lambda = Join-Path $AndroidBuildTools 'core-lambda-stubs.jar'
$sources = @(Get-ChildItem -Recurse -File -Filter '*.java' -LiteralPath (Join-Path $server 'java'), $generatedRoot)
$sourceList = Join-Path $scratch 'sources.txt'
$sources | ForEach-Object { '"' + $_.FullName.Replace('\', '/') + '"' } | Set-Content -Encoding utf8NoBOM $sourceList
Run $javac -encoding UTF-8 -bootclasspath $androidJar -classpath "$lambda;$generatedRoot" -source 8 -target 8 -d $classes "@$sourceList"
$jarFile = Join-Path $scratch 'classes.jar'
Run $jar cf $jarFile -C $classes .
$dexJar = Join-Path $AndroidBuildTools 'lib/d8.jar'
$dex = Join-Path $scratch 'server.zip'
Run $java -cp $dexJar com.android.tools.r8.D8 --min-api 21 --lib $androidJar --output $dex $jarFile
Copy-Item -LiteralPath $dex -Destination $Output
Get-FileHash -LiteralPath $Output -Algorithm SHA256
