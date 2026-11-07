@echo off
setlocal
"%~dp0MusicSync.Patch.exe" uninstall %*
set "patch_result=%errorlevel%"
pause
exit /b %patch_result%
