@echo off
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-profiler.ps1" %*
exit /b %ERRORLEVEL%
