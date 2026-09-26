# SETUP-LINUX.md — Развёртывание RoleplayerDiscordBot на Linux VPS

Пошаговая инструкция для Ubuntu 22.04 LTS (или Debian 11/12). Подходит для любого
хостинга — shneider.host, timeweb, Hetzner, и т.п. Проверено на 1 vCPU / 2 GB / KVM.

---

## 1. Подготовка системы

```bash
# Обновить пакеты
sudo apt update && sudo apt upgrade -y

# Зависимости. dotnet-sdk-8.0 нужен только для сборки, на проде — runtime.
sudo apt install -y git curl wget unzip nginx apache2-utils \
    dotnet-sdk-8.0 aspnetcore-runtime-8.0 \
    openjdk-21-jre-headless \
    zram-tools
# zram-tools включит сжатый swap в RAM — на 2 GB это даёт ~1 GB эффективно сверху.

# Проверки
dotnet --version    # 8.x
java -version       # 21+
git --version
```

### Deno (для yt-cipher)

```bash
curl -fsSL https://deno.land/install.sh | sh
# Добавить в PATH для пользователя rpbot
echo 'export DENO_INSTALL="/home/rpbot/.deno"' >> /home/rpbot/.bashrc
echo 'export PATH="$DENO_INSTALL/bin:$PATH"' >> /home/rpbot/.bashrc
```

---

## 2. Создать пользователя и каталоги

```bash
sudo useradd -r -m -d /home/rpbot -s /bin/bash rpbot
sudo mkdir -p /opt/rpbot /var/lib/rpbot /var/log/rpbot
sudo chown -R rpbot:rpbot /opt/rpbot /var/lib/rpbot /var/log/rpbot
```

Каталоги:
- `/opt/rpbot` — репозиторий (бинарь RPBot, Lavalink.jar, yt-cipher).
- `/var/lib/rpbot` — данные бота: `Settings/`, `Data/`, `Logs/` (через `RPBOT_DATA_DIR`).
- `/var/log/rpbot` — для `journalctl` не нужен, но удобно складывать внешние логи (например, nginx access).

---

## 3. Развернуть репозиторий

```bash
sudo -u rpbot -i
cd /opt/rpbot

git clone https://github.com/elmer-grey/RoleplayerDiscordBot.git .
# ВАЖНО: submodule yt-cipher
git submodule update --init --recursive

# Скачать Lavalink.jar 4.x (не в репо — слишком большой)
wget -O Lavalink/Lavalink.jar \
    https://github.com/lavalink-devs/Lavalink/releases/download/4.0.7/Lavalink.jar

# Подготовить yt-cipher (один раз)
cd yt-cipher
deno run --allow-read --allow-write scripts/patch-ejs.ts
cd ..
```

---

## 4. Конфигурация

### 4.1. Lavalink/application.yml

```bash
sudo -u rpbot cp /opt/rpbot/Lavalink/application.yml.example \
              /opt/rpbot/Lavalink/application.yml
sudo -u rpbot nano /opt/rpbot/Lavalink/application.yml
```

**Один раз** запустить Lavalink руками для получения YouTube OAuth refreshToken:

```bash
sudo -u rpbot java -jar /opt/rpbot/Lavalink/Lavalink.jar
# В консоли появится ссылка вида https://accounts.google.com/o/oauth2/...
# Открыть в браузере → авторизоваться → скопировать refreshToken из лога
# Ctrl+C, вставить refreshToken в application.yml
```

### 4.2. config.json бота

Бот создаст `config.json` автоматически при первом запуске. Или перенесите с
текущей машины (Windows → Linux):

```bash
# На Windows: %LOCALAPPDATA%\RPBot\Settings\
# На Linux бот ожидает: $RPBOT_DATA_DIR/Settings/ = /var/lib/rpbot/Settings/
sudo scp config.json serverconfigs.json master_guide_*.txt Pastes.txt \
    rpbot@<server>:/tmp/

sudo -u rpbot mkdir -p /var/lib/rpbot/Settings
sudo cp /tmp/*.json /tmp/*.txt /var/lib/rpbot/Settings/
sudo chown -R rpbot:rpbot /var/lib/rpbot/Settings
# Бот сам нормализует абсолютные Windows-пути в относительные при первом старте.
```

**Обязательные поля в config.json:**

```json
{
  "BotToken": "<ваш discord bot token>",
  "GuildIDs": [<id сервера>],
  "Music": {
    "Enabled": true,
    "Password": "rpbot_lavalink_password",
    "AutoStart": false,
    "Host": "127.0.0.1",
    "Port": 2333,
    "JarPath": "../Lavalink/Lavalink.jar",
    "ConfigPath": "../Lavalink/application.yml",
    "YtCipherAutoStart": false,
    "YtCipherPath": "../yt-cipher",
    "YtCipherPort": 8001
  }
}
```

> `AutoStart: false` — Lavalink и yt-cipher запускаются как отдельные systemd-юниты
> (см. шаг 5). Бот только подключается к `127.0.0.1:2333`.
> Если хочешь встроенный запуск — оставь `AutoStart: true`, тогда юнит
> `lavalink.service` не нужен.

---

## 5. systemd units

```bash
sudo cp /opt/rpbot/deploy/systemd/rpbot.service     /etc/systemd/system/
sudo cp /opt/rpbot/deploy/systemd/lavalink.service  /etc/systemd/system/
sudo systemctl daemon-reload

# Включить автозапуск
sudo systemctl enable --now lavalink.service
sudo systemctl enable --now rpbot.service

# Проверить
systemctl status rpbot --no-pager
systemctl status lavalink --no-pager
```

---

## 6. Проверка портов

```bash
ss -lntp | grep -E ':(2333|8001|5057)'
# 2333 — Lavalink (слушает бот)
# 8001 — yt-cipher (если поднят отдельно)
# 5057 — WebDashboard (на 127.0.0.1, не виден снаружи — это правильно)
```

---

## 7. Просмотр логов

### Основной способ — journalctl

```bash
# Лог бота в реальном времени (Ctrl+C для выхода)
journalctl -u rpbot -f

# Только ошибки
journalctl -u rpbot -p err --since today

# За последний час
journalctl -u rpbot -s "1 hour ago"

# Lavalink
journalctl -u lavalink -f

# Без пагинации (less)
journalctl -u rpbot --no-pager -n 100
```

journalctl хранит логи в бинарном виде в `/var/log/journal/`. По умолчанию systemd
ограничивает размер до ~4 GB или 15% диска. Если хочется persistent хранение:

```bash
sudo mkdir -p /var/log/journal
sudo systemd-tmpfiles --create --prefix /var/log/journal
sudo systemctl restart systemd-journald
```

### Файловые логи (BotLogger)

Дополнительно бот пишет в `/var/lib/rpbot/Logs/yyyyMMdd/`:

```bash
# Общий лог бота (стартап + рантайм в одном файле)
tail -f /var/lib/rpbot/Logs/$(date +%Y%m%d)/run.log

# Только музыка
tail -f /var/lib/rpbot/Logs/$(date +%Y%m%d)/Music.log

# Lavalink
tail -f /opt/rpbot/Lavalink/logs/spring.log
```

> Суточная ротация: бот создаёт новую папку при перезапуске после `LogDayResolver.CutoffHour`
> (по умолчанию 06:00). Это встроено в BotLogger (`feat(logs): суточные папки`).

### WebDashboard (опционально)

Если хочется смотреть логи в браузере:

```bash
# SSH-туннель (без изменений в nginx)
ssh -L 5057:127.0.0.1:5057 rpbot@<server>
# Открыть http://127.0.0.1:5057 в браузере

# Или через nginx + Basic Auth (см. deploy/nginx/rpbot-dashboard.conf)
```

---

## 8. Ротация логов

Lavalink по умолчанию пишет в `Lavalink/logs/spring.log` без ротации. На 30 GB SSD
это не критично, но настрой logback на всякий случай:

```bash
sudo nano /opt/rpbot/Lavalink/logback.xml
# Добавить <rollingPolicy class="ch.qos.logback.core.rolling.SizeAndTimeBasedRollingPolicy">
# с maxFileSize=50MB и maxHistory=7
```

Логи бота в `BotLogger` уже ротируются по 20 МБ автоматически.

---

## 9. Обновление бота

```bash
sudo systemctl stop rpbot
sudo -u rpbot -i
cd /opt/rpbot
git pull
git submodule update --remote
# Если менялись .cs — пересобрать
cd /opt/rpbot/RPBot
dotnet publish -c Release -o /tmp/rpbot-publish
sudo cp /tmp/rpbot-publish/RPBot.dll /opt/rpbot/RPBot/
sudo chown rpbot:rpbot /opt/rpbot/RPBot/RPBot.dll
exit
sudo systemctl start rpbot
journalctl -u rpbot -f
```

---

## 10. Частые проблемы

| Симптом | Причина | Решение |
|---|---|---|
| `Lavalink.jar не найден` | Не скачан | Шаг 3 |
| `deno не найден` | Не установлен / не в PATH | Шаг 1 |
| `Application.Init` висит | Нет `RPBOT_NO_UI=1` в service-файле | Проверить `/etc/systemd/system/rpbot.service` |
| `HttpListener: prefix rejected` | На Linux задан `host=*` или пустой | В конфиге WebDashboard задать `host=127.0.0.1` (по умолчанию уже так после `feat(home)`) |
| OOM-killer убивает бот | MemoryMax слишком низкий | `sudo systemctl edit rpbot.service` → `MemoryMax=1800M` |
| Диск кончился | Lavalink лог | logback-ротация, шаг 8 |
| Бот молчит в Discord | Неправильный BotToken / GuildIDs | Проверить `config.json` и `journalctl -u rpbot -p err` |