# Чек-лист ручной проверки RPBot на живой машине

> Зачем: часть изменений (Discord-события, Telegram, Lavalink, файловые сторы на проде)
> нельзя проверить в unit-тестах — нужен живой бот.
> Каждый шаг = конкретное действие + где смотреть результат.
>
> Предусловия:
> - чистый рестарт бота (`Ctrl+C` → запуск заново);
> - открыты два лога: `<DATA>/Logs/run.log` (живой поток) и Discord-канал логов бота;
> - в `serverconfigs.json` для тестового сервера заполнены: TelegramEnabled/BotToken/ChatId,
>   MasterRoleId, EventVoiceChannelID, RollChannelID.

---

## Раунд 1 (SafeJsonIO, modal DRY, HTML cache)

### 1.1 serverconfigs.json переживает крэш во время записи

1. Открыть `<DATA>/Settings/serverconfigs.json`.
2. Любой командой, которая меняет конфиг (например `/setwelcome`), запустить запись.
3. **Не дожидаясь окончания** — `kill -9` PID процесса (`Stop-Process -Id <PID>` без `-Force`).
4. Запустить бота снова.
5. **Ожидаемо:** загружается либо старый, либо новый конфиг. НЕ пустой, НЕ битый JSON.

### 1.2 MasterGuideHistory.json переживает крэш

1. Выдать роль мастера пользователю через `/mastergrant` или конфиг.
2. Бот отправляет памятку мастеру, `master_guide_history.json` обновляется.
3. `kill -9` во время следующей команды, трогающей этот файл.
4. **Ожидаемо:** история читается без падения.

### 1.3 Modal DRY — оба модальных пути работают

1. `/masterremove` → модалка "Введите username".
2. `/mastergrant` → модалка "Введите username".
3. **Ожидаемо:** оба диалога идентичны по UI, без дрейфа текста/полей.

### 1.4 EventAnnouncer — отображение MSK-времени

1. Запустить `!event new`, настроить время старта.
2. **Ожидаемо:** в анонсе указано время в MSK (UTC+3), а не локальное или UTC.

### 1.5 WebDashboard — HTML кэш

1. Открыть `http://localhost:<port>/`.
2. Сделать `Ctrl+F5` (полный reload).
3. **Ожидаемо:** страница загружается ≤ 100мс (HTML из кэша, не генерируется каждый раз).

---

## Раунд 2 (Music stores, Lavalink retry, cross-session guard)

### 2.1 MusicPlaylistStore — атомарная запись

1. Создать плейлист: `/music playlist create Test`.
2. Добавить треки.
3. `kill -9` бота.
4. Перезапуск.
5. `/music playlist show Test` — **должен** показать все треки. Не должно быть "плейлист пуст".

### 2.2 MusicQueueStore — атомарная запись

1. Зайти в voice-канал, `/music play <url>` — трек встаёт в очередь.
2. `kill -9`.
3. Перезапуск. Трек должен либо доиграть (если Lavalink помнит), либо быть в очереди.

### 2.3 Lavalink retry при холодном старте

1. Выключить Lavalink (`Ctrl+C` в его терминале, или убить процесс).
2. Запустить бот.
3. **Ожидаемо:** в логе видно:
   ```
   [Music] Попытка запуска Lavalink #1/3…
   [Music] Lavalink не ответил, повтор через 2с…
   [Music] Попытка запуска Lavalink #2/3…
   [Music] Lavalink не ответил, повтор через 4с…
   [Music] Попытка запуска Lavalink #3/3…
   [Music] ⚠ Lavalink не поднялся — аудиосервис стартует в degraded-режиме
   ```
4. После ручного запуска Lavalink — `ReconnectLoop` подхватит его.

### 2.4 Cross-session bleed guard

1. Начать две игровые сессии на разных каналах одного сервера (`/rg open` ×2).
2. Нажать кнопку "Следующий ход" в **первой** сессии.
3. **Ожидаемо:** обрабатывается только первая сессия. Если по какой-то причине нажата кнопка от второй — лог содержит:
   ```
   [RESTART] Кнопка пришла с чужого message_id=... (ожидался ... для сессии ...) — игнорирую.
   ```
4. Пользователь видит ephemeral: "❌ Эта кнопка принадлежит другой сессии."

---

## Раунд 3 (logger fallback, StopAsync idempotent, SafeShutdown)

### 3.1 Logger fallback при недоступном BotLogger

1. Вручную заблокировать файл `Logs/<session>/System.log` (`Open-File` другим процессом с эксклюзивной блокировкой).
2. Сделать любое действие, которое пишет в лог.
3. **Ожидаемо:** НЕ должно быть stack trace от unhandled exception. В худшем случае — строка в stderr / run.log.

### 3.2 WebDashboardService.StopAsync идемпотентен

1. Запустить бот с включённым веб-дашбордом.
2. `Ctrl+C` → дождаться завершения.
3. **Ожидаемо:** нет `ObjectDisposedException` от HttpListener в логе.
4. Повторный `Ctrl+C` (если остался MainLoop) — тоже чисто.

### 3.3 SafeShutdown в BotUI

1. Запустить бот.
2. Закрыть TUI-окно крестиком.
3. **Ожидаемо:** в `crashes/` нет нового файла. В `run.log` последняя запись — "Бот завершает работу..." или аналог. НЕ "SafeShutdown outer error".

---

## Раунд 4 (ReconnectionService, BotLogger, TelegramNotifier, VoicePoints, PredictionService)

### 4.1 ReconnectionService — повторный Dispose

1. `kill -9` бота после первой попытки Dispose.
2. **Ожидаемо:** бот стартует заново без `NullReferenceException` на `_reconnectCts`.

### 4.2 ReconnectionService — переподключение после потери сети

1. Отключить интернет на 10 секунд.
2. Включить обратно.
3. **Ожидаемо:** бот автоматически восстанавливает соединение, `IsReconnectInProgress == false` после успеха.

### 4.3 BotLogger — запись после Shutdown

1. Вызвать `BotLogger.Shutdown("manual")` из любого места (например, добавить временный хук).
2. Вызвать `BotLogger.Info(...)`.
3. **Ожидаемо:** не падает, не пишет в категорийный файл (пути обнулены).

### 4.4 TelegramNotifier — network error

1. Поставить в `serverconfigs.json` `TelegramBotToken = "INVALID"`.
2. Запустить ивент.
3. **Ожидаемо:** в `run.log` строка вида:
   ```
   [Telegram] ... network error: ...
   ```
   НЕ stack trace от `HttpRequestException`.

### 4.5 TelegramNotifier — отмена при shutdown

1. Запустить длительную рассылку (например, `/event announce` для 10+ серверов).
2. `Ctrl+C` во время рассылки.
3. **Ожидаемо:** shutdown не висит, `OperationCanceledException` пробрасывается, цикл рассылки завершается.

### 4.6 TelegramNotifier — таймаут

1. Временно подменить `api.telegram.org` → `10.255.255.1` (RFC 6890, не маршрутизируется) через `/etc/hosts`.
2. Запустить ивент.
3. **Ожидаемо:** через 15 секунд (HttpClient.Timeout) — в логе `"timeout: ..."`. Бот продолжает работать.

### 4.7 VoicePointsService — отписка от событий

1. Запустить бот. Дождаться, пока `SeedExistingUsersInEventChannels` отработает.
2. Зайти в EventVoiceChannel под тестовым аккаунтом.
3. Подождать 5+ минут (один Tick).
4. **Ожидаемо:** в `Points.log`:
   ```
   VOICE_TRACK_START ...
   ```
5. Выйти из канала.
6. **Ожидаемо:** `VOICE_TRACK_STOP ...`.
7. `Ctrl+C` → проверить, что новый `VOICE_TRACK_*` НЕ появляется в логе после shutdown (даже если дискорд-клиент дёргает событие последний раз).

### 4.8 VoicePointsService — повторный Shutdown

1. Добавить тестовый вызов `voiceService.Shutdown()` дважды подряд.
2. **Ожидаемо:** нет `ObjectDisposedException`.

### 4.9 PredictionService — атомарная запись state

1. Создать прогноз `/predict new "Тест"`.
2. `kill -9` бота.
3. Перезапуск.
4. **Ожидаемо:** прогноз восстановился (`predictions_state.json` валидный, `MessageId`, `Bets` сохранены).

### 4.10 PredictionService — Shutdown идемпотентен

1. Вызвать `predictionService.Shutdown()` дважды.
2. **Ожидаемо:** нет NRE / ODE.

---

## Общий smoke после всех раундов

| # | Шаг | Ожидаемо |
|---|---|---|
| О.1 | Запуск бота | Все инициализации в `run.log` без ERROR |
| О.2 | `!ping` | Бот отвечает с uptime и числом гильдий |
| О.3 | `!roll 2d6` в RollChannel | Эмбед с результатом, лог `Rolls.log` |
| О.4 | `!event new` → заполнить → стартовать | Анонс ушёл в Telegram + Discord, лог `EventOps.log` |
| О.5 | `/rg open` → игра → `/rg close` | `Session.log` содержит ходы, `_sessions` очищается |
| О.6 | `/predict new` → ставки → resolve | `Predict.log` содержит LOCK/RESOLVE, очки начислены |
| О.7 | Открыть `http://localhost:<port>/` | Дашборд рендерит HTML из кэша |
| О.8 | `Ctrl+C` | Все сервисы чисто завершаются, `crashes/` пуст, `run.log` имеет финальный блок |
| О.9 | Повторный запуск | `predictions_state.json`, `music_queues.json`, `serverconfigs.json`, `event_announcements.json` — все валидные JSON |
| О.10 | `dotnet test tests/SmokeTests/SmokeTests.csproj` | 61/61 passed |
