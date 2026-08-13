# Раунд 6 — слепые пятна

Файловый аудит `PredictionService`, `PointsService`, `PointsUserIndex`, `MusicStats`,
`SafeJsonIO` и обработчиков Discord-кнопок/модалок. Без полного прогона бота —
только статический разбор и узкие unit-тесты на поведение.

## TL;DR

| # | Зона                          | Вердикт | Что делать                                                                                                                          |
| - | ----------------------------- | ------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| 1 | `_pendingBetUi` по user-id    | баг     | Под `pred_bet_add_modal` лежит stale `SocketMessageComponent` если мастер делает ту же ставку из другой гильдии. Чистить по `(guild,user,channel)`. |
| 2 | `_pendingCreateChoiceUi`      | баг     | `pred_outcomes` кладёт `SocketMessageComponent` в словарь, но нет ни создания, ни очистки — пользователь пересылает устаревшее сообщение. |
| 3 | `PlaceBetAsync` race          | баг     | Между `if (DateTimeOffset.UtcNow >= p.BetsCloseAtUtc)` и `p.Sync.WaitAsync()` нет блокировки. Двойной запрос = 1 ставка + 1 «опоздал». |
| 4 | `BuildComponents` `bet_modal` | баг     | После модального `pred_bet_modal` показывается `pred_bet_confirm` в канал — но он открыт даже когда `p.BotOfflineAtUtc.HasValue`, без скрытия. |
| 5 | History/Stats файлы           | баг     | `SaveHistoryAsync`, `SaveStatsAsync`, `SaveAchievementsAsync` идут через `File.WriteAllTextAsync` напрямую. Без `.tmp` + `File.Move`. |
| 6 | `MusicStats`                  | малый   | Тоже прямой `File.WriteAllTextAsync` + `File.Move`. Сценарий асинхронной инвалидации `_filePath` (после Dispose?) не покрыт. |
| 7 | Cancel race                   | баг     | `CancelAsync` берёт `p.Sync.WaitAsync()`, читает `p.Bets`, кладёт refunds, удаляет из `_active`. Между ними — `await SendMessageAsync` без блокировки. |
| 8 | Resolve race                  | баг     | В `ResolveAsync` блокировка снимается до `_active.TryRemove`. Если `UpdateMessageAsync` ещё не закончился, PlaceBet его подхватит. |
| 9 | `_pendingBetUi` cleanup       | баг     | При обнулении активного прогноза (cancel/resolve) и при Disconnected pending-UI не удаляется → кнопка «Продолжить» ведёт в никуда. |
| 10 | GameSession storage           | расслед.| `GameSessionCommands._sessions` хранится как `ConcurrentDictionary<ulong,…>` без записи на диск — это уже известно, просто отметим. |
| 11 | `OfflineEvents` cap           | малый   | Нет ограничения на размер списка: при 50 reconnect-циклах в минуту список начнёт раздувать embed. |
| 12 | `pred_resolve` после cancel   | баг     | Если кнопка `pred_resolve` кликнута после `pred_cancel`, обращение к `ResolveAsync` вернёт «Прогноз уже завершён», но `component.UpdateAsync` уже мог затереть кнопки у **другого** активного сообщения. |

---

## Подробности

### 1. `_pendingBetUi` протекает между гильдиями / пользователями

`Program.cs:301`:

```csharp
_pendingBetUi[$"{guildId}:{component.User.Id}"] = component;
```

Словарь чистится по тому же ключу в `pred_bet_modal` и `pred_bet_add_modal`, **но**:

- Если у пользователя открыт «продолжить» в одной гильдии, и он параллельно
  нажал «сделать ставку» в другой (поддерживается одна гильдия на сервер,
  но модалка может лететь долго) — ключ перезатрётся, а старая кнопка
  удалится уже после отправки `RespondAsync` для второй.
- При `Disconnect`/`Shutdown` записи не чистятся. После рестарта
  `SocketMessageComponent` мёртв, но лежит в словаре и съедает память.

**Тест:** нужен `Bug_10_PendingBetUi_Stale_Entry_Cleanup`.

### 2. `_pendingCreateChoiceUi` — мёртвая переменная

`Program.cs:97` объявляет `ConcurrentDictionary<string, SocketMessageComponent?> _pendingCreateChoiceUi`,
но в коде он **никогда не записывается**. `pred_outcomes` идёт напрямую к `HandlePredictionOutcomesButton`.
Похоже на рефактор-заготовку, которую забыли убрать или подключить.
Если по смыслу должен работать как `_pendingBetUi` — этого нет, и modal может прилетать
на «уже отменённый» компонент.

**Тест:** нужен `Bug_11_PendingCreateChoiceUi_ConsistentLifecycle` (или просто удалить).

### 3. Race в `PlaceBetAsync` — клин между монитором и PlaceBet

`PredictionService.cs:807-818`:

```csharp
if (DateTimeOffset.UtcNow >= p.BetsCloseAtUtc)
{
    p.IsLocked = true;
    await UpdateMessageAsync(p, showLocked: true).ConfigureAwait(false);
    ...
    return (false, "Время приёма ставок истекло.");
}
```

Между проверкой времени и `p.Sync.WaitAsync()` (строка 824) окно открыто.
Monitor в `MonitorLoopAsync:1445` мог в это же время поставить `p.IsLocked = true`,
но `PlaceBetAsync` уже прошёл проверку и попадёт в `try` блок, где
сначала спишет points (через `TrySpend`), затем обнаружит `existing.OutcomeId != outcomeId`
или спокойно запишет ставку, потому что локальная копия `p.IsLocked` не была перечитана.

**Сценарий (без перезагрузки):** если быстрый пользователь делает ставку в момент `T == BetsCloseAtUtc`,
а монитор отрабатывает каждые 10 сек — между ними ставка пройдёт. После раунда 5
монитор сокращает `delaySeconds = 1` за 15 сек до закрытия, что уменьшает окно, но не закрывает его.

**Тест:** нужен `Race6_LockVsPlaceBet_AtomicallyExcludes`. Эмулирует два потока:
один гоняет цикл `PlaceBetAsync`, второй — `MonitorLoop` (через reflection выставляет `IsLocked`).
Без lock'а вокруг «check time → commit bet» assert может упасть.

### 4. `pred_bet_confirm` показывается даже когда бот offline

`HandlePredictionBetButton:280`:

```csharp
var balance = _pointsService.GetBalance(guildId, component.User.Id);
var cb = new ComponentBuilder()
    .WithButton($"Продолжить (баланс: {balance})", customId: $"pred_bet_confirm:{guildId}", style: ButtonStyle.Primary);
```

Кнопка показывается независимо от `_active[guildId].BotOfflineAtUtc`. После нажатия →
`HandlePredictionBetConfirmButton` шлёт модалку → пользователь заполняет сумму →
`PlaceBetAsync` проверит `if (p.IsLocked)` и вернёт ошибку.

**Лучше:** перед `RespondAsync` проверять `p.BotOfflineAtUtc.HasValue` и отвечать
«Бот в данный момент недоступен, ставка не будет принята» с указанием `~N мин` до возвращения.

### 5. History/Stats файлы без `.tmp` + rename

`PredictionService.cs:1533, 1710, 1943` — `File.WriteAllTextAsync` напрямую.
Если процесс упадёт в момент сереализации, файл окажется обрезанным.
Для `predictions_state.json` уже используется `SafeJsonIO.WriteAtomicAsync`,
но три другие (`predictions_history.json`, `predictions_stats.json`,
`predictions_achievements.json`) — нет.

**Тест:** нужен `Bug_12_HistorySaves_AreAtomic` — переписать через `SafeJsonIO` и добавить проверку.

### 6. `MusicStats.IncrementAsync`

`MusicStats.cs:48` использует свой `tmpPath` + `File.Move`. Не через `SafeJsonIO`,
но семантически эквивалентно. ОК как есть, просто отметим.

### 7. CancelAsync — refunds под локом, но удаление из `_active` уже за пределами

`PredictionService.cs:1110-1156`:

```csharp
await p.Sync.WaitAsync().ConfigureAwait(false);
try { foreach (...) _points.Add(...); } finally { p.Sync.Release(); }

try { var channel = ...; await channel.SendMessageAsync(...); } catch { }

await AddToHistoryAsync(p, ...);  // тоже async, без лока

_active.TryRemove(guildId, out _);
_activeChannels.TryRemove(guildId, out _);
```

Между `Release` и `TryRemove` — окно, в котором:
- другой поток может вызвать `PlaceBetAsync`, тот сделает `TryGetValue` и получит `p`,
  войдёт в `Sync.WaitAsync` и проверит `IsLocked == false` (cancel ещё не выставил),
  спишет points и запишет ставку.
- Только потом `TryRemove` снесёт `p`, но ставка уже зафиксирована.

**Сценарий:** мастер жмёт «Отменить прогноз» ровно когда пользователь успел отправить
модалку ставки. Пользователь видит «принято», но cancel всё равно приводит к возврату
его ставки через `foreach (var bet in p.Bets.Values) _points.Add(...)` — потому что
в словаре лежит та же ставка. Баланс на этом этапе: `current - amount + amount = current`,
то есть формально ничего не теряется. **Но** — отменили прогноз, а пользователь продолжает
видеть в embed'е свою ставку как «активную» до тех пор, пока бот не отдаст новый embed
без stats'ов. Это визуальная, а не финансовая проблема.

Тем не менее — реальный баг в том, что `p.Sync.Release()` происходит **до**
`_active.TryRemove`. Если в это окно попадёт параллельный `ResolveAsync`,
оба увидят `IsLocked == false`, оба пройдут проверку `p.IsResolved`,
один сделает payouts, второй бросит исключение при `_active.TryAdd` (он уже удалён).
Это маловероятно (cancel обычно идёт от админа, resolve — от создателя), но при
автоматической cancel через `_active.Remove` + одновременном resolve — реально.

### 8. ResolveAsync — лок снимается до `_active.TryRemove`

`PredictionService.cs:1008-1085`:

```csharp
finally { p.Sync.Release(); }   // ← лок отпущен

// SendMessageAsync, UpdateUserStatsAfterResolution, AddToHistoryAsync — без лока

_active.TryRemove(guildId, out _);
```

Между `Release` и `TryRemove`:
- PlaceBetAsync: `TryGetValue` → получает `p` → `IsLocked == true` (выставлен в начале Resolve) → отказ. ОК.
- CancelAsync: `TryGetValue` → `p` → `IsResolved == true` (выставлен в 937) → отказ «уже завершён». ОК.

То есть в Resolve у нас сейчас **нет** открытого race'а — проверки идут в правильном порядке
(IsResolved проверяется до всех мутаций). Это, в отличие от Cancel, безопасно.

### 9. `_pendingBetUi` при Disconnected

`OnClientDisconnected` ничего не чистит в `_pendingBetUi`. Если в момент дисконнекта у пользователя
висит «Продолжить (баланс: N)» — он может нажать → `HandlePredictionBetConfirmButton` откроет
модалку → пользователь введёт сумму → `PlaceBetAsync` откажет. Это **не баг**, но UX-плохо.

### 10. GameSession storage — нет персистентности

`GameSessionCommands._sessions` (найдено в `RollSessionCountingTests.cs`) — статический
`ConcurrentDictionary`, **не пишется на диск**. После рестарта бота все сессии теряются,
а `RollsCount` у дашборда обнуляется. Это уже обсуждалось в раунде 3, но до сих пор не решено.

**Тест:** уже покрыт в `RollSessionCountingTests`, но smoke-сценарий «рестарт бота посреди сессии»
в LIVE-SMOKE-CHECKLIST отсутствует.

### 11. `OfflineEvents` cap

`ActivePrediction.OfflineEvents = new List<OfflineEvent>()` — без ограничения размера.
Embed строится через `if (events.Count > 8) events = events.TakeLast(8)`, так что
размер embed'а нормальный, но **сам список растёт**. При 1000 реконнектов в embed
всегда видны последние 8, но в файле это лежит как 1000 записей. Не критично,
но стоит добавить `if (p.OfflineEvents.Count > MaxOfflineEvents) p.OfflineEvents.RemoveAt(0);`
в обоих местах записи (OnClientDisconnected, Shutdown, OnClientReadyForAnnouncements).

### 12. `pred_resolve` после `pred_cancel`

Когда мастер жмёт «Отменить» → бот делает `component.UpdateAsync` с `Components = new ComponentBuilder().Build()`.
В этот момент embed **уже удалён** (CancelAsync вызывает `DeleteMessageAsync`),
и `UpdateAsync` бросит `Discord.Net.HttpException: Unknown Message`. Этот случай
оборачивается в `try { } catch { }`, но `_pendingBetUi` для других пользователей
уже не обнулился.

---

## Что делать

**Критично (фикс):**
- #1 — добавить `_pendingBetUi` cleanup в OnClientDisconnected/Shutdown
- #3 — взять `p.Sync` **до** проверки `>= BetsCloseAtUtc` в `PlaceBetAsync`
- #5 — перевести `SaveHistoryAsync`, `SaveStatsAsync`, `SaveAchievementsAsync` на `SafeJsonIO.WriteAtomicAsync`

**Желательно (тесты):**
- `Race6_LockVsPlaceBet_AtomicallyExcludes` — регрессия #3
- `Bug_10_PendingBetUi_Stale_Entry_Cleanup` — регрессия #1
- `Bug_12_HistorySaves_AreAtomic` — регрессия #5

**Минор (чистка):**
- #2 — удалить `_pendingCreateChoiceUi` или подключить
- #11 — cap на `OfflineEvents`
- #4 — скрывать «Продолжить» при offline

**За рамками:**
- #10 (GameSession persistence) — большой отдельный раунд
- #12 — безопасен благодаря `try/catch`

---

## Сводка по тестам

Покрытие в текущей матрице (`tests/SmokeTests/`):

- Round 1-3: server configs, atomic write, prediction offline-events/shift.
- Round 4: prediction history persistence + atomic.
- Round 5: per-session rolls + button hiding.

Что ещё **нет** покрытия для:
- `PlaceBetAsync` race с монитором (#3)
- `_pendingBetUi` lifecycle (#1)
- History/Stats atomic save (#5)
- `_pendingCreateChoiceUi` существование (#2)
- `OfflineEvents` cap (#11)

Все четыре приоритета покрываются ≤ 60 строками unit-тестов + 1 фиксом на 3 строки.