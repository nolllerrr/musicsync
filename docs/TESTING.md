# Локальная проверка MusicSync «Вместе»

Запустите сервер из корня проекта на Windows с .NET 10 SDK.

```powershell
dotnet run --project .\src\MusicSync.Server -c Release --no-launch-profile
```

Во втором PowerShell проверьте, что сервер отвечает.

```powershell
Invoke-RestMethod http://127.0.0.1:5050/health
```

1. Соберите установщик и установите патч на копию Яндекс Музыки. [Проверка установщика](TOGETHER.md#сборка-и-проверка-установщика)
2. В установщике укажите `ws://127.0.0.1:5050/ws` и откройте «Вместе» в Яндекс Музыке.
3. Создайте комнату. Проверьте, что появился код и его можно скопировать. Закройте панель, откройте её снова и убедитесь, что код сохранился. Нажмите «Выйти».
4. При ошибке посмотрите `musicsync-together.log` в каталоге профиля Яндекс Музыки.

Для проверки синхронизации на двух ПК нужен общий WSS-сервер. [Сценарий для двух ПК](TOGETHER.md#проверка-комнаты-на-двух-пк)


## Автоматическая проверка групповой интеграции

```powershell
dotnet restore MusicSync.slnx
dotnet format MusicSync.slnx --verify-no-changes --no-restore
dotnet build MusicSync.slnx -c Release --no-restore -warnaserror
dotnet test MusicSync.slnx -c Release --no-build --logger trx --results-directory artifacts/verified-tests
node --test tests/MusicSync.Tests/*.test.cjs
Get-ChildItem integration/electron -File | Where-Object Extension -in '.js', '.cjs' | ForEach-Object { node --check $_.FullName }
./scripts/Publish-Together.ps1
```

`GroupIntegrationTests` запускает настоящий Kestrel с десятью RoomConnection и независимыми виртуальными плеерами. Проверяются Play/Pause, новый трек, перемотка, публичные ID, отказ одиннадцатому, адресная передача, новый ведущий и завершение. Отдельный тест заполняет 16 комнат/160 WebSocket-соединений и заменяет соединение при полной занятости. Это локальная проверка вместимости, не нагрузочный бенчмарк VPS.

| Сценарий | Автоматическое покрытие |
|---|---|
| 1 ведущий + 9 слушателей, общий RoomState | GroupIntegrationTests.TenRealConnectionsSynchronizeAndTransferToSelectedListener; ServerTests.TenParticipantsHaveUniquePublicIdsAndAllReceiveSharedState |
| Отказ одиннадцатому | Те же сетевые тесты |
| Обрыв/выход одного слушателя | ServerTests.DisconnectedListenerKeepsReservedSeatAndResumesWithSameId; ExplicitListenerLeaveFreesOnlyItsSeatAndBroadcastsMembership |
| Возврат прежним токеном | ServerTests.RealClientReconnectsAndGetsLatestSnapshotAfterNetworkDrop; DuplicateTokenReplacesConnectionWithoutAllocatingASecondSeat |
| Play/Pause, трек, перемотка | GroupIntegrationTests.TenRealConnectionsSynchronizeAndTransferToSelectedListener; GroupPlayerTests.LeaderAndNineListenersApplyTrackSeekPauseAndPlayIndependently |
| Медленный слушатель | GroupPlayerTests.SlowListenerUsesFreshServerPositionWithoutBlockingOthers; ServerTests.SlowPeerQueueOverflowDoesNotBlockOtherParticipants |
| Ошибка одного плеера | GroupPlayerTests.FailedListenerCannotChangeOtherPlayersAndRecoversOnNextTrack |
| Передача выбранному слушателю | GroupIntegrationTests.TenRealConnectionsSynchronizeAndTransferToSelectedListener |
| Конкурирующие запросы, accept/cancel | GroupIntegrationTests.ConcurrentOffersHaveOneWinnerAndOnlyTargetMayAcceptOrDecline |
| Разрыв/возврат ведущего | ServerTests.LeaderCanResumeDuringGraceWithoutLosingRoomOrIdentity; PlayerTests.ReconnectingLeaderKeepsNativeChangesMadeWhileOffline |
| Разрыв инициатора/адресата при согласии | GroupIntegrationTests.ParticipantDisconnectInvalidatesConsentAndResumePreservesIdentity (2 варианта) |
| Завершение комнаты | ServerTests.LeaderLeaveEndsEveryListenerAndInvalidatesAllSessions; LeaderGraceExpiryEndsAllRemainingListeners |
| Пара из двух участников | ServerTests.CreatesRoomJoinsOneListenerAndRestrictsActions; PlayerTests.MutualRoleSwapKeepsPlaybackAndTransfersNativeControl |
| Смена роли во время загрузки/перемотки | PlayerTests.LeadershipChangeDuringLoadOrSeekCannotPublishThePreviousRolesState (парная/адресная, 4 варианта) |
| Размер кадра, объединение отчётов, истечение согласия | GroupIntegrationTests.MaximumLegalSnapshotFitsEightKiBIncludingTenReportsAndTransfer; ReportsCoalesceAndConsentExpiresWithoutPlaybackChanges |

JavaScript проверяет roster 1/2/5/10, ID/дедупликацию, reconnect, смену роли, фокус/прокрутку, доверенный фрейм, GUID и поля команд на участке preload/main/agent. AgentStateTests проверяет публичный JSON и очистку согласия во время обрыва.

В локальной интеграции дополнительно выполнен headless Edge layout-тест: 1280×800, 400×600, 240×420, светлая/тёмная темы, 2/10 участников. Исходные renderer.js/CSS исполнены с тестовым мостом; только проверка origin подменена для HTML-стенда. Результаты и снимки: `artifacts/ui-check/`. Это не запуск Electron или реальной Яндекс Музыки.

## Ручная приёмка на реальных компьютерах

Используйте отдельную проверенную копию Яндекс Музыки 5.126.1 и новый патч на каждом ПК. Сервер v2 несовместим с выпущенными клиентами v1; обновление должно быть согласованным.

1. Создайте комнату с двумя, затем с десятью ПК; сравните трек, паузу и позицию после загрузки и перемотки. Проверьте слышимое расхождение и разные задержки сети.
2. Проверьте медленную загрузку и недоступный трек на одном аккаунте: остальные продолжают слушать. Восстановите плеер и нажмите повтор синхронизации.
3. Отключите сеть слушателя и ведущего, верните их до 90 секунд, проверьте место/ID/роль без дублей. При отсутствии ведущего более grace комната завершается.
4. Передайте управление одному из девяти, подтвердите только на выбранном ПК. Проверьте отмену/отклонение, истечение 30 секунд, обрыв связи до подтверждения, смену роли во время загрузки/перемотки. Прежний ведущий не меняет общую комнату.
5. Проверьте светлую/тёмную тему, минимальное окно и DPI 100/125/150/200%, скролл десяти участников, клавиатуру и доступность кнопок.
6. Установите/снимите патч на отдельной копии, проверьте перезапуск, штатное обновление, восстановление файлов по SHA-256 и повторную установку. Из этой интеграции реальная установка не запускалась.
7. На целевом WSS-сервере проверьте сертификат, доверенный прокси/IP-квоты, переподключение и ресурсы при ожидаемой занятости. Docker/Caddy runtime не проверен локально без запущенного daemon; деплой требует отдельного разрешения.

Полный отчёт: [GROUP_ROOMS_INTEGRATION_REPORT.md](GROUP_ROOMS_INTEGRATION_REPORT.md).
