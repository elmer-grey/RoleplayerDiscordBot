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

## Раунд 5 (per-session rolls, prediction buttons hide, online/offline announce)

### 5.1 Lavalink воспроизводит YouTube-треки (после апгрейда плагина 1.18.2)

1. Зайти в voice-канал.
2. `/music play <youtube_url>`.
3. **Ожидаемо:** трек играет. В `Music.log` строки `yt-cipher готов` / `Lavalink готов` и `Track started`.
4. Если `❌ Трек не найден` — см. лог `Lavalink/logs/spring.log` (искать `WARN youtube-plugin`, `cipher url`).

### 5.2 Bug 2 — счётчик бросков по сессиям (не на гильдию)

1. На сервере открыть ДВЕ сессии: `/rg open DnD` и `/rg open Pathfinder` в разных каналах.
2. В каждой включить сбор бросков.
3. Сделать 1 бросок в DnD.
4. **Ожидаемо:** в дашборде `/api/sessions` у DnD `Бросков:1`, у Pathfinder `Бросков:0`. Глобальный счётчик (в stats) = 1.

### 5.3 Bug 3 — бросок при паузе не блокируется, но не идёт в сессию

1. DnD активна, Pathfinder на паузе (`/rg pause Pathfinder`).
2. Сделать бросок.
3. **Ожидаемо:** бот пишет результат. В дашборде DnD `Бросков +1`, Pathfinder `Бросков` без изменений.
4. Поставить обе сессии на паузу, сделать бросок.
5. **Ожидаемо:** явное сообщение «Бросок засчитан только в глобальный счётчик, в сессии он не пойдёт». `Rolls.log` пишет ставку без session-id.

### 5.4 Bug 4 — тумблер сбора бросков изолирован на сессию

1. Две сессии, у обеих `TrackRolls=true`.
2. `/rg toggle Pathfinder` → false.
3. **Ожидаемо:** у Pathfinder в дашборде `Сбор бросков: выкл`, у DnD — по-прежнему `вкл`.
4. Сделать бросок.
5. **Ожидаемо:** DnD инкрементит, Pathfinder — нет.

### 5.5 Bug 5 — BetsCloseAtUtc сдвигается на время offline

1. Создать прогноз `/predict new "Test" 1min`.
2. Через 30 сек — `kill -9` бота.
3. Через 30 сек — запустить заново.
4. **Ожидаемо:** приём ставок НЕ закрылся. В `Predict.log` видно `OFFLINE_SHIFT minutes=...`. Embed в канале содержит поле `🛰️ Состояние бота` с записью `Offline` и обновлённой дедлайной.

### 5.6 Bug 6 — онлайн/оффлайн оповещения в канале прогноза

1. Прогноз активен (приём ставок идёт).
2. `Ctrl+C` → бот ушёл на реконнект.
3. **Ожидаемо:** в канале прогноза появилось сообщение `⚠️ Бот ушёл на реконнект/перезагрузку. Сбор костяшек приостановлен. Кнопки скрыты, время будет пересчитано.`
4. Бот вернулся (`Ready`).
5. **Ожидаемо:** сообщение `✅ Бот снова в сети — прогноз активен`. Кнопки вернулись.

### 5.6.b Bug 6 — различение реконнекта и полного offline

1. Прогноз активен.
2. Убить сеть на 30 сек (`iptables`-эквивалент или физически).
3. **Ожидаемо:** сообщение `⚠️ Бот отключился полностью (offline).` (НЕ «рестарт»).
4. Сеть вернулась — `✅ Бот снова в сети`.

### 5.7 Bug 7 — кнопки скрываются во время offline

1. Прогноз активен, кнопки видны.
2. `Ctrl+C` бот (рестарт).
3. **Ожидаемо:** в embed прогноза кнопок НЕТ. После возврата бота — кнопки снова на месте.

### 5.8 Полный аудит раунда 5

1. Все 7 сценариев выше — ОК.
2. `dotnet test tests/SmokeTests/SmokeTests.csproj` — 88/88 passed (73 + 15 новых).

---

## Раунд 6 (race conditions, atomic saves, _pendingBetUi cleanup)

### 6.1 Bug 1 — кнопки «Продолжить» чистятся на resolve/cancel

1. Нажать `pred_bet` → `pred_bet_confirm`. Появилось ephemeral с кнопкой.
2. **Не нажимая** модалку, через 5 сек сделать `/predict resolve` (или `/predict cancel`).
3. **Ожидаемо:** при следующем нажатии кнопки `pred_bet_confirm` (или закрытии модалки) — НЕ появляется `Приложение не ответило вовремя`. Словарь `_pendingBetUi` очищен (`run.log` не содержит `stale delete original response` ошибок).

### 6.2 Bug 1 — кнопки чистятся при отключении/перезапуске

1. Открыть модалку `pred_bet_modal` (через `pred_bet_confirm`).
2. **Не отправляя** форму, `Ctrl+C` бота → `Ready` (рестарт).
3. **Ожидаемо:** после возврата бота — никаких висящих `pred_bet_confirm` кнопок. При попытке нажать — кнопка либо удалена, либо ответ — `InteractionNotReplied`-type safe error, без падений.

### 6.3 Bug 3 — PlaceBetAsync не пропускает ставки в окне гонки

1. Создать прогноз `/predict new "RaceTest" 1min`.
2. На 59-й секунде — 50 одновременных ставок от разных юзеров (или бот-команд `/predict bet 50` ×50 быстро).
3. **Ожидаемо:** не больше 1 ставки прошло после `BetsCloseAtUtc`. `Predict.log` содержит ровно одно `LOCK` от `PlaceBetAsync` (или от MonitorLoopAsync — но не две серии ставок).
4. Прогноз закрыт ровно в `BetsCloseAtUtc` или сразу после — НЕ через 10+ секунд из-за race.

### 6.4 Bug 5 — атомарная запись history/stats/achievements

1. `kill -9` бота прямо во время `/predict resolve` (когда пишутся файлы).
2. Перезапуск.
3. **Ожидаемо:** все три файла валидны:
   - `predictions_history.json`
   - `predictions_stats.json`
   - `predictions_achievements.json`
4. `.tmp` файлов НЕ остаётся в `<DATA>`.

### 6.5 Полный аудит раунда 6

1. Все 4 сценария выше — ОК.
2. `dotnet test tests/SmokeTests/SmokeTests.csproj` — 92/92 passed (88 + 4 новых R6).

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
| О.9 | Повторный запуск | `predictions_state.json`, `music_queues.json`, `serverconfigs.json`, `event_announcements.json`, `predictions_history.json`, `predictions_stats.json`, `predictions_achievements.json` — все валидные JSON |
| О.10 | `dotnet test tests/SmokeTests/SmokeTests.csproj` | 92/92 passed (61 round 1-4 + 27 round 5 + 4 round 6) |
