# Requires the .NET 10 SDK on the build machine.
param(
    [uri]$Endpoint = 'ws://127.0.0.1:5050/ws'
)

$ErrorActionPreference = 'Stop'
if (-not $Endpoint.IsAbsoluteUri -or -not $Endpoint.Host -or $Endpoint.UserInfo -or $Endpoint.Query -or $Endpoint.Fragment -or
    $Endpoint.AbsolutePath -cne '/ws' -or ($Endpoint.Scheme -ne 'wss' -and
        ($Endpoint.Scheme -ne 'ws' -or $Endpoint.IdnHost -notin @('127.0.0.1', '::1', 'localhost')))) {
    throw 'Specify a WSS /ws endpoint or a loopback WS /ws endpoint for local testing.'
}

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$project = Join-Path $root 'integration/installer/MusicSync.Patch.csproj'
$version = dotnet msbuild $project -nologo -getProperty:Version
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Could not read the MusicSync version.'
}

$stage = Join-Path $dist ("together-" + [Guid]::NewGuid().ToString('N'))
$endpointFile = Join-Path $stage 'endpoint.txt'
$destination = Join-Path $dist "MusicSync-Together-$version-Windows-x64.exe"
$null = New-Item -ItemType Directory -Path $stage -Force
try {
    [System.IO.File]::WriteAllText($endpointFile, $Endpoint.AbsoluteUri)
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $stage `
        -p:DebugType=None -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true "-p:DefaultEndpointFile=$endpointFile"
    if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }

    Move-Item -LiteralPath (Join-Path $stage 'MusicSync.Patch.exe') -Destination $destination -Force
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
