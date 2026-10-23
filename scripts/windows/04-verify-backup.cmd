@echo off
chcp 65001 >nul
cd /d "%~dp0"
if "%~1"=="" goto usage
Club.Server.exe --verify-backup "%~f1"
if errorlevel 1 echo Backup verification failed. Do not restore this file. Copy the error above.
pause
exit /b
:usage
echo Drag a backup .db file onto this command file to verify it.
pause
