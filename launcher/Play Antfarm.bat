@echo off
REM Double-click this.
REM
REM It starts a local Antfarm server, opens the live view panel in your
REM browser, and drops you straight into the world.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0antfarm-launcher.ps1"
if errorlevel 1 pause
