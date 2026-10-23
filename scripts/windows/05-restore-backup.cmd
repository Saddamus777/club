@echo off
chcp 65001 >nul
cd /d "%~dp0"
if "%~1"=="" goto usage
echo Сначала остановите сервер. Операции после даты копии исчезнут из активной базы.
echo Текущая база будет сохранена отдельно. Сеансы потребуют ручного возобновления.
choice /c YN /n /m "Восстановить копию? Y = да, N = нет: "
if errorlevel 2 exit /b
Club.Server.exe --restore-backup "%~f1"
if errorlevel 1 echo Восстановление не завершилось. Сохраните сообщение об ошибке.
pause
exit /b
:usage
echo Перетащите файл резервной копии .db на этот файл.
pause
