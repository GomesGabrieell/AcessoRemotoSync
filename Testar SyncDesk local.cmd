@echo off
powershell.exe -NoProfile -File "%~dp0scripts\teste-local.ps1"
if errorlevel 1 pause
