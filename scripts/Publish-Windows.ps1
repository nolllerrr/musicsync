# Requires the .NET 10 SDK on the build machine.
param([switch]$ClientOnly)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$clientProject = Join-Path $root 'src/MusicSync.Client/MusicSync.Client.csproj'
$version = dotnet msbuild $clientProject -nologo -getProperty:Version
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Could not read the MusicSync version.'
}
$clientName = if ($ClientOnly) { "MusicSync-$version-Windows-x64" } else { 'MusicSync-Windows-x64' }
$client = Join-Path $dist $clientName
$server = Join-Path $dist 'MusicSync-Server-Windows-x64'
$zipName = "MusicSync-$version-Windows-x64-portable.zip"
$zip = Join-Path $dist $zipName

# Stage each publish in a fresh directory so deleted runtime files cannot leak into a rebuild.
$stage = Join-Path $dist ("publish-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage -Force
$stagedClient = Join-Path $stage 'client'
$stagedServer = Join-Path $stage 'server'
try {
    dotnet publish $clientProject -c Release -r win-x64 --self-contained true -o $stagedClient -p:DebugType=None `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'docs/TESTING.md') -Destination (Join-Path $stagedClient 'TESTING.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-Sonata.cmd') -Destination $stagedClient
    $destinations = @($client)
    if (-not $ClientOnly) {
        dotnet publish (Join-Path $root 'src/MusicSync.Server/MusicSync.Server.csproj') -c Release -r win-x64 --self-contained true -o $stagedServer -p:DebugType=None
        if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
        Copy-Item -LiteralPath (Join-Path $root 'docs/TESTING.md') -Destination (Join-Path $stagedServer 'TESTING.md')
        $destinations += $server
    }
    $stagedZip = Join-Path $stage $zipName
    Compress-Archive -Path (Join-Path $stagedClient '*') -DestinationPath $stagedZip -CompressionLevel Optimal

    $distFull = (Resolve-Path -LiteralPath $dist).Path
    foreach ($path in $destinations) {
        if (Test-Path -LiteralPath $path) {
            $full = (Resolve-Path -LiteralPath $path).Path
            if ((Split-Path -Parent $full) -ne $distFull -or (Get-Item -LiteralPath $full).LinkType) {
                throw "Unsafe publish destination $full"
            }
            Remove-Item -LiteralPath $full -Recurse -Force
        }
    }
    Move-Item -LiteralPath $stagedClient -Destination $client
    if (-not $ClientOnly) {
        Move-Item -LiteralPath $stagedServer -Destination $server
    }
    Move-Item -LiteralPath $stagedZip -Destination $zip -Force
    Get-FileHash -LiteralPath $zip -Algorithm SHA256
}
finally {
    $stageFull = (Resolve-Path -LiteralPath $stage).Path
    if ((Split-Path -Parent $stageFull) -eq (Resolve-Path -LiteralPath $dist).Path -and
        (Split-Path -Leaf $stageFull) -match '^publish-[0-9a-f]{32}$' -and
        -not (Get-Item -LiteralPath $stageFull).LinkType) {
        Remove-Item -LiteralPath $stageFull -Recurse -Force
    }
}
