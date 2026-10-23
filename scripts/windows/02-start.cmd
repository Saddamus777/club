@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo После сообщения "Now listening" откройте 03-open.cmd.
echo Для остановки сервера нажмите Ctrl+C в этом окне.
Club.Server.exe
if errorlevel 1 echo Сервер остановился с ошибкой. Скопируйте сообщение выше.
pause
