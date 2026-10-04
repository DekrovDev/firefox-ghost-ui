@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
set "ghost_exit=%errorlevel%"
echo.
pause
exit /b %ghost_exit%
