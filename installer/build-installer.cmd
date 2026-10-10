@echo off
rem Double-click to build installer\output\RustSandbox-Setup-<version>.exe
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
pause
