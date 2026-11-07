# MusicSync 0.1.9

MusicSync добавляет раздел «Вместе» в Яндекс Музыку для Windows. Ведущий создаёт комнату, а слушатель входит по коду. Звук воспроизводится локально на каждом компьютере. Сервер передаёт состояние воспроизведения между участниками через WebSocket.

## Запуск в Windows

Запустите `MusicSync-Together-0.1.9-Windows-x64.exe`, укажите адрес сервера и нажмите «Установить патч». После установки откройте «Вместе» в боковом меню Яндекс Музыки. [Установка и проверка на двух ПК](docs/TOGETHER.md)

## Сборка из исходников

Выполните команды из корня проекта на Windows с .NET 10 SDK.

```powershell
dotnet build .\MusicSync.slnx -c Release
dotnet test .\MusicSync.slnx -c Release --no-build
.\scripts\Publish-Together.ps1
```

Установщик для Windows x64 появится в `dist`.

- [Локальный запуск сервера и проверка](docs/TESTING.md)
- [Архитектура и протокол](docs/ARCHITECTURE.md)
- [Развёртывание и откат на VPS](docs/VPS_DEPLOY.md)
