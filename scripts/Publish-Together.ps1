# Requires the .NET 10 SDK on the build machine.
param(
    [Parameter(Mandatory)][uri]$Endpoint
)

$ErrorActionPreference = 'Stop'
if (-not $Endpoint.IsAbsoluteUri -or -not $Endpoint.Host -or $Endpoint.UserInfo -or $Endpoint.Query -or $Endpoint.Fragment -or
    $Endpoint.AbsolutePath -cne '/ws' -or ($Endpoint.Scheme -ne 'wss' -and
        ($Endpoint.Scheme -ne 'ws' -or $Endpoint.IdnHost -notin @('127.0.0.1', '::1', 'localhost')))) {
    throw 'Specify a WSS /ws endpoint or a loopback WS /ws endpoint for local testing.'
}

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$installer = Join-Path $root 'integration/installer'
$project = Join-Path $installer 'MusicSync.Patch.csproj'
$agent = Join-Path $root 'src/MusicSync.Agent/MusicSync.Agent.csproj'
$version = dotnet msbuild $project -nologo -getProperty:Version
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Could not read the MusicSync version.'
}

$zipName = "MusicSync-Together-$version-Windows-x64-candidate.zip"
$stage = Join-Path $dist ("together-" + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'package'
$stagedAgent = Join-Path $stage 'agent'
$stagedZip = Join-Path $stage $zipName
$destination = Join-Path $dist $zipName
$null = New-Item -ItemType Directory -Path $stage -Force
try {
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $package `
        -p:DebugType=None -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }

    dotnet publish $agent -c Release -r win-x64 --self-contained true -o $stagedAgent `
        -p:DebugType=None -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }

    Copy-Item -LiteralPath (Join-Path $stagedAgent 'MusicSync.Agent.exe') -Destination $package
    foreach ($file in @('Install.cmd', 'Uninstall.cmd', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path $installer $file) -Destination $package
    }
    $electron = Join-Path $package 'electron'
    $null = New-Item -ItemType Directory -Path $electron
    foreach ($file in @('bootstrap.cjs', 'main.cjs', 'preload.cjs', 'renderer.js', 'renderer.css')) {
        Copy-Item -LiteralPath (Join-Path $root "integration/electron/$file") -Destination $electron
    }
    $config = @{ endpoint = $Endpoint.AbsoluteUri } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText((Join-Path $package 'config.json'), $config)

    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $stagedZip -CompressionLevel Optimal
    Move-Item -LiteralPath $stagedZip -Destination $destination -Force
    Get-FileHash -LiteralPath $destination -Algorithm SHA256
}
finally {
    $stageFull = (Resolve-Path -LiteralPath $stage).Path
    if ((Split-Path -Parent $stageFull) -eq (Resolve-Path -LiteralPath $dist).Path -and
        (Split-Path -Leaf $stageFull) -match '^together-[0-9a-f]{32}$' -and
        -not (Get-Item -LiteralPath $stageFull).LinkType) {
        Remove-Item -LiteralPath $stageFull -Recurse -Force
    }
}
