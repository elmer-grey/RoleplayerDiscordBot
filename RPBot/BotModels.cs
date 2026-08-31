using Discord.WebSocket;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RPBot
{
    public class ServerConfig
    {
        public ulong GuildID { get; set; }
        public ulong ModerateChannelID { get; set; }
        public ulong WelcomeChannelID { get; set; }
        public ulong GeneralRGChannelID { get; set; }
        public ulong RollChannelID { get; set; }
        public ulong StatsChannelID { get; set; }
        public ulong RecordChannelID { get; set; }
        public string WelcomeMessage { get; set; } = string.Empty;
        public string LineMessage { get; set; } = string.Empty;
        public ulong DefaultRoleID { get; set; }
        // Роль мастера на сервере, имеющая доступ к управлению игровыми сессиями и очередью
        public ulong? MasterRoleId { get; set; } = null;
// Роль "суперпользователя" на сервере, имеющая расширенные права управления ботом
public ulong? SuperUserRoleId { get; set; } = null;
        // Включить фильтр мата для этого сервера
        public bool SwearFilterEnabled { get; set; } = false;
        // Доп. список слов для фильтрации на уровне сервера (если пуст — используются BotConfig.DefaultSwearWords)
        public List<string> SwearWords { get; set; } = new List<string>();
// Включены ли игровые прогнозы/ставки и начисление костяшек на этом сервере
public bool PredictionsEnabled { get; set; } = true;
        // Использовать ли картинки при выводе бросков, если они доступны
        public bool RollPicturesEnabled { get; set; } = true;
// Голосовой канал события (event), в котором начисляются костяшки
public ulong EventVoiceChannelID { get; set; }

        // === TELEGRAM (уведомления о событиях) ===
        public bool TelegramEnabled { get; set; } = false;
        public string? TelegramBotToken { get; set; } = null;
        public long TelegramChatId { get; set; } = 0;
            public int TelegramMessageThreadId { get; set; } = 0;

        /// <summary>
        /// Маппинг Discord userId (строка) → имя мастера для Google Sheets.
        /// Если задано — используется вместо DisplayName при записи статистики.
        /// </summary>
        public Dictionary<string, string> MasterNameMap { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Включена ли авто-отправка памятки мастеру при выдаче MasterRoleId.
        /// </summary>
        public bool MasterGuideEnabled { get; set; } = true;

        /// <summary>
        /// Кастомный шаблон памятки мастеру (если пусто — используется встроенный шаблон).
        /// </summary>
        public string MasterGuideTemplate { get; set; } = string.Empty;

        /// <summary>
        /// Кулдаун повторной отправки памятки одному пользователю (часы). По умолчанию 168 часов (7 дней).
        /// </summary>
        public int MasterGuideCooldownHours { get; set; } = 168;
    }

        // Modal handling moved inside Program class

    public enum StartupType
    {
        FirstStart,
        Restart,
        Reconnect
    }

public interface IBotController
{
bool ShouldExit { get; }
bool ShouldRestart { get; }
Task RestartAsync();
Task StopAsync();

// Управление конфигурациями серверов (доступно из UI)
Task<Dictionary<ulong, ServerConfig>> GetAllServerConfigsAsync();
Task<ServerConfig?> GetServerConfigAsync(ulong guildId);
Task SetServerConfigValueAsync(ulong guildId, string key, string? value = null, ulong? channelId = null, bool? toggle = null);
Task ResetServerConfigAsync(ulong guildId);
Task ReloadServerConfigsAsync();
}

/// <summary>
/// Результат проверки здоровья системы
/// </summary>
public class SystemHealthCheck
{
public string SystemName { get; set; } = string.Empty;
public bool IsHealthy { get; set; }
public string Message { get; set; } = string.Empty;
}
}
