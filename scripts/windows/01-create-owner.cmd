@echo off
chcp 65001 >nul
cd /d "%~dp0"
Club.Server.exe --create-owner
if errorlevel 1 echo Owner setup failed. Close the running server first, or use 00-start.cmd to create the owner in the browser.
pause
