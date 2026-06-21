@echo off
REM Convenience launcher for build-release.ps1 - lets you run the release build by
REM double-clicking (or from cmd) without PowerShell execution-policy friction.
REM Any arguments are passed straight through, e.g.:  build-release.bat -Sha256

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" %*
set "ERR=%ERRORLEVEL%"

echo.
if %ERR% NEQ 0 (echo Build FAILED ^(exit %ERR%^).) else (echo Build complete.)
pause
exit /b %ERR%
