# RoleplayerDiscordBot

Discord-бот для ролевых серверов: броски кубиков, сессии, прогнозы, мастер-гайд, точки, лава-музыка, телеметрия.

## Документация

- **[SETUP.md](SETUP.md)** — первый запуск после переноса на новую машину (клонирование, .NET/Java/Deno, Lavalink, OAuth).
- **[LIVE-SMOKE-CHECKLIST.md](LIVE-SMOKE-CHECKLIST.md)** — ручной чек-лист приёмки после изменений (раунды 1–6 + общий smoke).

## Состав репозитория

| Папка/файл | Назначение |
|---|---|
| `RPBot/` | Исходный код C# (.NET 8). |
| `tests/SmokeTests/` | Юнит-тесты на dotnet test. |
| `Lavalink/` | Конфиг и плагины Lavalink (см. `Lavalink/application.yml.example`). |
| `yt-cipher/` | Git-submodule. Deno-сервис для расшифровки YouTube-подписей. |
| `Docs/` | Документация по UI-разметке. |
| `Settings/` *(на машине, не в репо)* | Секреты: `config.json`, `serverconfigs.json`, `Pastes.txt`. |
| `Data/`, `Logs/` *(на машине, не в репо)* | Состояние и логи бота. |

## Кратко о боте

- **Стек:** .NET 8 + Discord.Net + Lavalink (Java 21) + yt-cipher (Deno).
- **Хранилище:** JSON-файлы в `Settings/` и `Data/`, атомарная запись через `SafeJsonIO`.
- **Кроссплатформенность:** код работает на Windows / Linux / macOS (пути определяются `OperatingSystem.IsWindows()`).

## Лицензия

Внутренний проект. Все права защищены.