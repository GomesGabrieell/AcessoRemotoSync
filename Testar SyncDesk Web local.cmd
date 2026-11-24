@echo off
powershell.exe -NoProfile -File "%~dp0scripts\teste-web-local.ps1"
if errorlevel 1 pause
