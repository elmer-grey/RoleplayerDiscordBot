# Copilot Instructions

## Project Guidelines
- Писать все сообщения git-коммитов на русском языке.
- Пользователь предпочитает русский язык и использует Terminal.Gui UI; конфиг должен сохраняться и редактироваться через UI и slash-команды.
- Пользователь явно не хочет отключать перенос строк ради прокрутки в BotUI; перенос строк должен сохраняться.
- Диалог подтверждения в BotUI должен работать с Y/N/Enter независимо от раскладки и с навигацией стрелками.
- Пользователь предпочитает подробные сообщения коммитов на русском языке.

## Активные ветки
- `feature/log-rendering-rework` — текущая. Последний коммит `91a3b72` (refactor: единый источник ServerConfig через ServerConfigResolver). Запушено в `origin`.

## Открытые планы
1. **Музыка** — добавить источники VK / SoundCloud / Spotify. Соседство треков разных источников — открытый вопрос.
2. **Игровая система** — заготовки лежат в `RPBot/bin/Debug/net8.0/Заготовки` («Важное 1», «Важное 2»).

## Известные проблемы окружения
- `github-mcp-server` в CLI получает `HTTP 403 forbidden: access denied` от `api.individual.githubcopilot.com/agents` — это серверная проблема аккаунта `elmer-grey`, не ошибка сеанса. На работу с git/кодом не влияет. Игнорировать.
- `Program.Memory is not enabled: Copilot token is required` — кросс-сессионная память отключена. Перезапустить CLI с включённым токеном, либо полагаться на `AGENTS.md`/`copilot-instructions.md`.

## Соглашения по конфигурации
- Единственный источник `ServerConfig` — `Program._serverConfigs` (instance) + `ServerConfigResolver` (Func<ulong, ServerConfig?>). Статический `Program.ServerConfigs` мёртв и **не должен** возвращаться. Все обращения через `Program.ServerConfigResolver?.Invoke(guildId) is { } cfg`.
