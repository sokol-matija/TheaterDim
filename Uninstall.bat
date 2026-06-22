@echo off
REM Double-click this to remove TheaterDim. It runs uninstall.ps1 (which self-elevates).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
