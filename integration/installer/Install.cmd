@echo off
setlocal
"%~dp0MusicSync.Patch.exe" install %*
set "patch_result=%errorlevel%"
pause
exit /b %patch_result%
