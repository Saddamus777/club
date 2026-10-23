@echo off
chcp 65001 >nul
cd /d "%~dp0"
Club.Server.exe --create-owner
if errorlevel 1 echo Не удалось создать владельца. Скопируйте сообщение об ошибке выше.
pause
