@echo off
setlocal
set "MUSICSYNC_LAUNCH_DIR=%~dp0"

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -Command ^
    "$ErrorActionPreference = 'Stop';" ^
    "$port = 9222;" ^
    "$client = Join-Path $env:MUSICSYNC_LAUNCH_DIR 'MusicSync.Client.exe';" ^
    "$yandexName = -join [char[]](0x042F,0x043D,0x0434,0x0435,0x043A,0x0441,0x20,0x041C,0x0443,0x0437,0x044B,0x043A,0x0430);" ^
    "$yandex = Join-Path $env:LOCALAPPDATA ('Programs\YandexMusic\' + $yandexName + '.exe');" ^
    "$session = (Get-Process -Id $PID).SessionId;" ^
    "function Test-YandexDebugArguments {" ^
    "    $filter = 'Name = ' + [char]39 + $yandexName + '.exe' + [char]39;" ^
    "    $debugProcesses = @(Get-CimInstance Win32_Process -Filter $filter -ErrorAction SilentlyContinue | Where-Object {" ^
    "        $_.ExecutablePath -eq $yandex -and $_.SessionId -eq $session -and" ^
    "        $_.CommandLine -notmatch '(?:^|\s)\x22?--type=' -and" ^
    "        $_.CommandLine -match '(?:^|\s)(?:--remote-debugging-port=9222|\x22--remote-debugging-port=9222\x22)(?=\s|$)' -and" ^
    "        $_.CommandLine -match '(?:^|\s)(?:--remote-debugging-address=127\.0\.0\.1|\x22--remote-debugging-address=127\.0\.0\.1\x22)(?=\s|$)'" ^
    "    });" ^
    "    return $debugProcesses.Count -eq 1;" ^
    "}" ^
    "function Test-SonataEndpoint {" ^
    "    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue);" ^
    "    $confirmed = $false;" ^
    "    foreach ($listener in $listeners) {" ^
    "        if ($listener.LocalAddress -notin '127.0.0.1','::1') {" ^
    "            throw 'Port 9222 is not limited to loopback. Exit the app using the port, then retry.';" ^
    "        }" ^
    "        if ($listener.OwningProcess -eq 0) { continue };" ^
    "        $owner = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue;" ^
    "        if ($owner.Path -ne $yandex -or $owner.SessionId -ne $session) {" ^
    "            throw 'Port 9222 is in use by another app. Close the app using this port, then retry.';" ^
    "        }" ^
    "        if ($listener.LocalAddress -eq '127.0.0.1') { $confirmed = $true };" ^
    "    }" ^
    "    if (-not $confirmed -and -not (Test-YandexDebugArguments)) {" ^
    "        if ($listeners.Count -gt 0) { throw 'Port 9222 could not be verified. Exit the app using the port, then retry.' };" ^
    "        return $false;" ^
    "    }" ^
    "    try {" ^
    "        $json = $http.GetStringAsync('http://127.0.0.1:9222/json/list').GetAwaiter().GetResult();" ^
    "        if (-not $json.TrimStart().StartsWith('[')) { return $false };" ^
    "        $targets = ConvertFrom-Json -InputObject $json;" ^
    "        $pages = @($targets | Where-Object { $_.type -ceq 'page' -and $_.url -match '^music-application://desktop(?:[/?#]|$)' });" ^
    "        if ($pages.Count -ne 1) { return $false };" ^
    "        $page = $pages[0];" ^
    "        return ($page.id -cmatch '^[A-Za-z0-9_-]{1,128}$' -and" ^
    "            $page.webSocketDebuggerUrl -ceq ('ws://127.0.0.1:9222/devtools/page/' + $page.id));" ^
    "    } catch { return $false }" ^
    "}" ^
    "try {" ^
    "    if (-not (Test-Path -LiteralPath $client -PathType Leaf)) { throw 'Extract the entire MusicSync portable ZIP and run Start-Sonata.cmd beside MusicSync.Client.exe.' };" ^
    "    if (Get-Process -Name 'MusicSync.Client' -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session) {" ^
    "        throw 'MusicSync is already running. Exit MusicSync from its tray menu, then run Start-Sonata.cmd again.';" ^
    "    }" ^
    "    if (-not (Test-Path -LiteralPath $yandex -PathType Leaf)) { throw 'The official Yandex Music installation was not found under LocalAppData\Programs\YandexMusic.' };" ^
    "    Add-Type -AssemblyName System.Net.Http;" ^
    "    $handler = New-Object System.Net.Http.HttpClientHandler;" ^
    "    $handler.UseProxy = $false; $handler.AllowAutoRedirect = $false; $handler.UseCookies = $false;" ^
    "    $http = [System.Net.Http.HttpClient]::new($handler);" ^
    "    $http.Timeout = [TimeSpan]::FromSeconds(1); $http.MaxResponseContentBufferSize = 65536;" ^
    "    $ready = Test-SonataEndpoint;" ^
    "    if (-not $ready) {" ^
    "        if (Get-Process -Name $yandexName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session -and $_.Path -eq $yandex }) {" ^
    "            throw 'Yandex Music is running without a valid Sonata session. Exit Yandex Music from its tray menu, then run Start-Sonata.cmd again.';" ^
    "        }" ^
    "        Write-Host 'Starting Yandex Music in Sonata mode...';" ^
    "        Start-Process -FilePath $yandex -ArgumentList '--remote-debugging-address=127.0.0.1','--remote-debugging-port=9222' -WorkingDirectory (Split-Path -Parent $yandex);" ^
    "        $wait = [Diagnostics.Stopwatch]::StartNew();" ^
    "        do { Start-Sleep -Milliseconds 500; $ready = Test-SonataEndpoint } until ($ready -or $wait.Elapsed.TotalSeconds -ge 20);" ^
    "        if (-not $ready) { throw 'Yandex Music did not expose its local Sonata session. Exit it from the tray menu and retry.' };" ^
    "    }" ^
    "    Start-Process -FilePath $client -ArgumentList '--sonata-port=9222' -WorkingDirectory $env:MUSICSYNC_LAUNCH_DIR;" ^
    "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" ^
    "finally { if ($http) { $http.Dispose() } }"

if errorlevel 1 (
    echo.
    pause
    exit /b 1
)
