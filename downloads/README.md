# Готовый комплект Windows

[Скачать ZIP](https://github.com/Saddamus777/club/blob/main/downloads/club-prototype-0.1-win-x64.zip?raw=true)

Это локальный прототип 0.1 для Windows 11 x64, с включённым .NET runtime.
Не требуется VS Code или самостоятельная сборка. В архиве есть `READ-ME.md` и команды:

1. `01-create-owner.cmd` — создать владельца со своим паролем.
2. `02-start.cmd` — запустить сервер, оставить окно открытым.
3. `03-open.cmd` — открыть интерфейс.

Полная [инструкция испытания](../docs/windows-test.md).
Проверка файла в PowerShell: `Get-FileHash .\club-prototype-0.1-win-x64.zip -Algorithm SHA256`.
Сравните результат с соседним файлом `.zip.sha256`.

Архив не содержит пользовательских данных и стандартных паролей.
Это версия учёта: блокировка Windows и завершение игр ещё не подключены.
Кросс-сборка выполнена в Linux; запуск на реальной Windows требует проверки.
