@echo off
chcp 65001 >nul
cd /d "%~dp0"
if "%~1"=="" goto usage
echo Stop the server first. Operations after this backup will be absent from the restored database.
echo The current database will be preserved separately. Sessions will require manual resumption.
choice /c YN /n /m "Restore this backup? Y = yes, N = no: "
if errorlevel 2 exit /b
Club.Server.exe --restore-backup "%~f1"
if errorlevel 1 echo Restore failed. Copy the error above.
pause
exit /b
:usage
echo Drag a backup .db file onto this command file to restore it.
pause
