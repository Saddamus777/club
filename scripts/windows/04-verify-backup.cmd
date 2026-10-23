@echo off
chcp 65001 >nul
cd /d "%~dp0"
if "%~1"=="" goto usage
Club.Server.exe --verify-backup "%~f1"
if errorlevel 1 echo Копия не прошла проверку. Не используйте её для восстановления.
pause
exit /b
:usage
echo Перетащите файл резервной копии .db на этот файл.
pause
