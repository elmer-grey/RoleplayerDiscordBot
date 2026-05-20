# Чек-лист первого запуска после переноса на новую машину (Windows)

## 1. Клонирование репозитория

```powershell
git clone https://github.com/elmer-grey/RoleplayerDiscordBot.git
cd RoleplayerDiscordBot

# Обязательно: инициализировать submodule yt-cipher
git submodule update --init --recursive
```

---

## 2. Установка зависимостей

| Зависимость | Минимальная версия | Скачать |
|---|---|---|
| .NET SDK | 8.x | https://dotnet.microsoft.com/download/dotnet/8.0 |
| Java JDK | 21 (рекомендуется Eclipse Temurin) | https://adoptium.net |
| Deno | последняя | https://deno.land/#installation |

**Проверка после установки:**
```powershell
dotnet --version   # должно быть 8.x
java -version      # должно быть 21+
deno --version     # любая актуальная версия
```

> Если `java` не в PATH — пропиши переменную среды `JAVA_HOME` (путь к папке JDK, без `\bin`).

---

## 3. Скачать Lavalink.jar

Файл не хранится в git (слишком большой). Скачать версию **4.x**:

https://github.com/lavalink-devs/Lavalink/releases

Положить в папку: `Lavalink\Lavalink.jar`

---

## 4. Подготовить yt-cipher

```powershell
cd yt-cipher

# Патч нужен один раз после клонирования submodule
deno run --allow-read --allow-write scripts/patch-ejs.ts

cd ..
```

---

## 5. Создать Lavalink\application.yml

Скопировать образец и заполнить:

```powershell
copy Lavalink\application.yml.example Lavalink\application.yml
```

Открыть `Lavalink\application.yml` и заполнить `refreshToken` для OAuth:

1. Оставить `refreshToken: ""` (пустым)
2. Запустить Lavalink вручную один раз:
   ```powershell
   cd Lavalink
   java -jar Lavalink.jar
   ```
3. В консоли Lavalink появится ссылка вида `https://accounts.google.com/o/oauth2/...`
4. Открыть ссылку в браузере → авторизоваться в Google
5. Скопировать `refreshToken` из лога Lavalink
6. Вставить токен в `application.yml` в поле `oauth.refreshToken`
7. Остановить Lavalink (Ctrl+C) — теперь бот будет запускать его сам

---

## 6. Создать Settings\config.json

При первом запуске бот создаёт файл автоматически с дефолтными значениями.
Открыть `Settings\config.json` и заполнить обязательные поля:

```json
{
  "BotToken": "ВАШ_DISCORD_BOT_TOKEN",
  "GuildIDs": [ID_СЕРВЕРА_1, ID_СЕРВЕРА_2],
  "Music": {
    "Enabled": true,
    "Password": "rpbot_lavalink_password",
    "AutoStart": true,
    "JarPath": "Lavalink/Lavalink.jar",
    "YtCipherAutoStart": true,
    "YtCipherPath": "yt-cipher",
    "YtCipherPort": 8001
  }
}
```

> Все пути должны быть **относительными** (относительно папки с EXE).
> Если вдруг попали абсолютные пути — бот нормализует их автоматически при запуске.

---

## 7. Проверка портов

Убедиться что порты свободны перед запуском:

```powershell
netstat -ano | findstr ":2333"   # Lavalink
netstat -ano | findstr ":8001"   # yt-cipher
```

Если заняты — завершить процессы или изменить порты в `config.json` и `application.yml`.

---

## 8. Первый запуск

```powershell
cd RPBot
dotnet run
```

Или запустить через Visual Studio (F5).

При старте бот пройдёт этапы 0→5:
- **Этап 0** — создаст/проверит все файлы настроек
- **Этап 4** — запустит yt-cipher и Lavalink, дождётся их готовности
- **Этап 5** — покажет статус всех систем (Discord ✅, Lavalink ✅ и т.д.)

---

## Частые проблемы

| Симптом | Причина | Решение |
|---|---|---|
| `Lavalink.jar не найден` | Не скачан jar | Шаг 3 |
| `deno не найден` | Deno не установлен или не в PATH | Шаг 2, перезапустить терминал после установки |
| `Музыка: Lavalink не ответил` | Java не найдена или JAVA_HOME не задан | Шаг 2, проверить `java -version` |
| `yt-cipher не запускается` | Не выполнен patch-ejs | Шаг 4 |
| `Трек не найден` в Discord | Lavalink поднялся, но OAuth не настроен | Шаг 5, получить refreshToken |
| Порт 2333 занят | Остался старый процесс java | `taskkill /F /IM java.exe` |
