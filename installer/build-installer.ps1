param(
    [switch]$SkipDownloads,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repo 'src\ZFTP.App\ZFTP.App.csproj'
$updaterProject = Join-Path $repo 'src\ZFTP.Updater\ZFTP.Updater.csproj'
$installerProject = Join-Path $repo 'src\ZFTP.Installer\ZFTP.Installer.csproj'
$buildRoot = Join-Path $PSScriptRoot 'build'
$payload = Join-Path $buildRoot 'payload'
$artifacts = Join-Path $buildRoot 'artifacts'
$payloadZip = Join-Path $buildRoot 'payload.zip'
$prereqDir = Join-Path $PSScriptRoot 'prereqs'
$dist = Join-Path $repo 'dist'

if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionMatch = Select-String -Path $appProject -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if (-not $versionMatch) { throw 'Could not read the ZFTP version from ZFTP.App.csproj.' }
    $Version = $versionMatch.Matches[0].Groups[1].Value
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use numeric major.minor.patch format (for example, 2.7.6). Got: $Version"
}

$assemblyVersion = "$Version.0"

function Ensure-Download {
    param(
        [string]$Url,
        [string]$Path,
        [string]$Sha256
    )

    $valid = Test-Path $Path
    if ($valid) {
        $valid = (Get-FileHash $Path -Algorithm SHA256).Hash -eq $Sha256
    }
    if ($valid) { return }
    if ($SkipDownloads) { throw "Missing or invalid prerequisite: $Path" }

    Write-Host "Downloading $(Split-Path -Leaf $Path)..." -ForegroundColor Cyan
    curl.exe -L --fail --silent --show-error $Url -o $Path
    if ((Get-FileHash $Path -Algorithm SHA256).Hash -ne $Sha256) {
        Remove-Item $Path -Force -ErrorAction SilentlyContinue
        throw "Hash verification failed for $Path"
    }
}

Write-Host "Building ZFTP $Version custom installer" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $prereqDir, $buildRoot, $dist | Out-Null
Remove-Item $payload, $artifacts, $payloadZip -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $payload | Out-Null

Ensure-Download `
    'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi' `
    (Join-Path $prereqDir 'winfsp-x64.msi') `
    '073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A'

Ensure-Download `
    'https://download.microsoft.com/download/9f887fdb-93b3-4b8d-8c68-c68c37d991d8/248c8e1c-3dee-4902-b593-3aee3e9f64dc/windowsdesktop-runtime-8.0.30-win-x64.exe' `
    (Join-Path $prereqDir 'windowsdesktop-runtime-8-x64.exe') `
    '8BD710AFA5DE396C9EB2A3B68D00279B7B9ACA372A2443E9ACD4F48FFDEF3F2D'

Write-Host 'Publishing ZFTP app...' -ForegroundColor Cyan
dotnet publish $appProject -c Release -r win-x64 --self-contained false `
    -p:Version=$Version `
    -p:FileVersion=$assemblyVersion `
    -p:AssemblyVersion=$assemblyVersion `
    --artifacts-path $artifacts `
    -o $payload
if ($LASTEXITCODE -ne 0) { throw 'ZFTP app publish failed.' }

Write-Host 'Publishing updater...' -ForegroundColor Cyan
dotnet publish $updaterProject -c Release -r win-x64 --self-contained false `
    -p:Version=$Version `
    -p:FileVersion=$assemblyVersion `
    -p:AssemblyVersion=$assemblyVersion `
    --artifacts-path $artifacts `
    -o $payload
if ($LASTEXITCODE -ne 0) { throw 'ZFTP updater publish failed.' }

Write-Host 'Publishing branded uninstall helper...' -ForegroundColor Cyan
dotnet publish $installerProject -c Release -r win-x64 --self-contained false `
    -p:InstallerFlavor=UninstallOnly `
    -p:AssemblyName=ZFTP.Uninstall `
    -p:Version=$Version `
    -p:FileVersion=$assemblyVersion `
    -p:AssemblyVersion=$assemblyVersion `
    --artifacts-path $artifacts `
    -o $payload
if ($LASTEXITCODE -ne 0) { throw 'ZFTP uninstall helper publish failed.' }

Write-Host 'Packing embedded payload...' -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $payloadZip -CompressionLevel Optimal

$outputExe = Join-Path $dist "ZFTP-Setup-$Version.exe"
Remove-Item $outputExe -Force -ErrorAction SilentlyContinue

Write-Host 'Publishing the custom bootstrapper...' -ForegroundColor Cyan
dotnet publish $installerProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:AssemblyName="ZFTP-Setup-$Version" `
    -p:Version=$Version `
    -p:FileVersion=$assemblyVersion `
    -p:AssemblyVersion=$assemblyVersion `
    --artifacts-path $artifacts `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Custom installer publish failed.' }

$built = Join-Path $dist "ZFTP-Setup-$Version.exe"
if (-not (Test-Path $built)) { throw "Expected installer was not produced: $built" }

$item = Get-Item $built
$hash = (Get-FileHash $built -Algorithm SHA256).Hash
Write-Host ''
Write-Host "Built: $built" -ForegroundColor Green
Write-Host ("Size:  {0:N1} MiB" -f ($item.Length / 1MB))
Write-Host "SHA256: $hash"
