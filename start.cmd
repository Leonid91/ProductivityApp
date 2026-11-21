@echo off
rem Starts ProductivityApp from this folder, without installing it (Windows): double-click this file.
rem The first launch writes its log to %LOCALAPPDATA%\ProductivityApp\first-launch.txt
set "exe="
for %%f in ("%~dp0release\ProductivityApp-*-windows-x64.exe") do set "exe=%%~ff"
if not defined exe (
  echo Not found: release\ProductivityApp-^<version^>-windows-x64.exe
  pause
  exit /b 1
)
start "" "%exe%"
