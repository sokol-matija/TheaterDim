@echo off
REM Double-click this to install TheaterDim. It runs install.ps1 (which self-elevates).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
