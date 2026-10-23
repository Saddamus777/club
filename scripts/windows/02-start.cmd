@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "Club.Server.exe" goto missing
echo Starting Club. Keep this window open. Press Ctrl+C to stop the server.
echo The browser will open when the server is ready. If it does not, use 03-open.cmd.
Club.Server.exe --open-browser
if errorlevel 1 echo The server could not start. If another server window is open, use 03-open.cmd there. Otherwise copy the error above.
pause
exit /b
:missing
echo Club.Server.exe was not found. Extract the entire ZIP before starting the application.
pause
