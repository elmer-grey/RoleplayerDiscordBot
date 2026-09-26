# Применение локального патча к submodule yt-cipher

YouTube периодически начинает блокировать запросы к `player.js` через Cloudflare
(ошибка в Discord: "Трек не найден" при `play`, в логах Lavalink —
`cipher url failed`, `All clients failed to load the item`).

Патч добавляет `User-Agent` от Android-YouTube в `yt-cipher/src/playerCache.ts` —
Cloudflare пропускает такие запросы.

## Использование

После `git submodule update --init --recursive`:

**Windows (PowerShell):**
```powershell
pwsh tools/apply-yt-cipher-patch.ps1
```

**Linux / macOS (bash):**
```bash
bash tools/apply-yt-cipher-patch.sh
```

Оба скрипта идемпотентны — повторный запуск пропускает работу, если маркер
`.yt-cipher-patch-applied` уже создан.

## Где лежит маркер

`/.yt-cipher-patch-applied` в корне репозитория. Добавлен в `.gitignore`.

## Когда нужно обновить патч

Если `yt-cipher` обновился (новая версия submodule) и в `src/playerCache.ts`
структура изменилась — патч может перестать применяться. В этом случае:

1. Запустить скрипт — он пожалуется на `git apply --check`.
2. Обновить `tools/patches/yt-cipher-cloudflare-ua.patch` под новую структуру:
   ```bash
   cd yt-cipher
   # Вручную привести src/playerCache.ts к нужному виду.
   git diff src/playerCache.ts > ../tools/patches/yt-cipher-cloudflare-ua.patch
   ```