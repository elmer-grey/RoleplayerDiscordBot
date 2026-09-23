using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot
{
    /// <summary>
    /// Сервис памятки мастера.
    /// Хранит шаблоны в файлах Settings/master_guide_<guildId>.txt.
    /// При первом запуске создаёт файл с документацией и шаблоном-примером.
    /// </summary>
    public static class MasterGuideService
    {
            // Шаблоны лежат в <DataRoot>/Settings/ на всех машинах одинаково.
            // Пути строятся через BotConfig.ResolvePath внутри EnsureTemplateFile/LoadTemplate,
            // здесь держим только имя папки для совместимости сигнатуры GetTemplatePath.
            private const string FilePrefix = "master_guide_";
            private const string FileExtension = ".txt";

            /// <summary>
            /// Гарантирует наличие файла шаблона для указанного сервера.
            /// Если файл отсутствует — создаёт его с шаблоном и комментариями.
            /// </summary>
        public static string EnsureTemplateFile(ulong guildId)
        {
                    // ResolvePath → %LOCALAPPDATA%\RPBot\Settings\master_guide_<id>.txt.
                    // Раньше путь был относительным, и File.Exists + File.WriteAllText шли в
                    // текущую рабочую директорию / AppContext.BaseDirectory, из-за чего
                    // на одной машине появлялись копии сразу в двух местах.
                    var path = BotConfig.ResolvePath(GetTemplatePath(guildId));
                    if (File.Exists(path))
                        return path;

                    try
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        File.WriteAllText(path, GetDefaultTemplateContent(), new UTF8Encoding(false));
                        BotLogger.Info(LogCategory.System, $"[MasterGuide] Создан файл шаблона: {path}");
            }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.System, $"[MasterGuide] Не удалось создать файл шаблона {path}: {ex.Message}");
                    }

                    return path;
                }

        /// <summary>
        /// Массовое создание файлов шаблонов для всех серверов.
        /// Вызывается при запуске бота после загрузки конфигурации.
        /// </summary>
        public static void EnsureAllTemplates(System.Collections.Generic.IEnumerable<ulong> guildIds)
        {
            if (guildIds == null) return;
            foreach (var guildId in guildIds)
            {
                try { EnsureTemplateFile(guildId); }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.System, $"[MasterGuide] Ошибка при обработке сервера {guildId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Загружает шаблон из файла. Возвращает null, если файл отсутствует.
        /// Возвращает ТОЛЬКО не-комментарные строки (без #) — без документации.
        /// </summary>
        public static string? LoadTemplate(ulong guildId)
        {
                    // GetTemplatePath возвращает относительный путь ("Settings/master_guide_<id>.txt").
                    // Резолвим через BotConfig.ResolvePath, чтобы получить каноничную директорию
                    // (%LOCALAPPDATA%\RPBot\Settings\) — иначе на других машинах (или при сборке .exe не из bin\)
                    // шаблоны искались бы относительно AppContext.BaseDirectory и появлялись дубли в bin\...\Settings\.
                    var resolvedPath = BotConfig.ResolvePath(GetTemplatePath(guildId));
                    if (!File.Exists(resolvedPath))
                        return null;

                    try
                    {
                        var raw = File.ReadAllText(resolvedPath, Encoding.UTF8);
                        var stripped = StripComments(raw);
                        return string.IsNullOrWhiteSpace(stripped) ? null : stripped.Trim();
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.System, $"[MasterGuide] Не удалось прочитать шаблон {resolvedPath}: {ex.Message}");
                        return null;
                    }
                }

        /// <summary>
        /// Рендерит шаблон: заменяет плейсхолдеры на реальные значения.
        /// Если template == null — используется встроенный шаблон.
        /// </summary>
        public static string Render(string? template, SocketGuild guild, SocketGuildUser master, ServerConfig config)
        {
            var raw = string.IsNullOrWhiteSpace(template) ? GetBuiltinTemplate(guild, master, config) : template;

                    // ✅ pred-parallelization: EventVoiceChannelID помечен как Obsolete,
                    // но {SessionChannel} placeholder используется в уже созданных шаблонах — сохраняем обратную совместимость.
        #pragma warning disable CS0618
                    return raw
                        .Replace("{GuildName}", guild.Name ?? "(без названия)")
                        .Replace("{MasterMention}", master.Mention)
                        .Replace("{MasterName}", master.DisplayName ?? master.Username ?? "Мастер")
                        .Replace("{RollChannel}", ChannelOrPlaceholder(config.RollChannelID, guild))
                        .Replace("{RecordChannel}", ChannelOrPlaceholder(config.RecordChannelID, guild))
                        .Replace("{StatsChannel}", ChannelOrPlaceholder(config.StatsChannelID, guild))
                        .Replace("{SessionChannel}", ChannelOrPlaceholder(config.EventVoiceChannelID, guild))
                        .Replace("{MasterRoleMention}", MasterRoleMention(config, guild));
        #pragma warning restore CS0618
            // Плейсхолдер {MasterRoleMention} оставлен для обратной совместимости с уже созданными файлами,
            // но в дефолтном шаблоне не используется (в ЛС упоминания ролей не работают).
        }

        /// <summary>
        /// Встроенный шаблон — используется как fallback и как пример в созданном файле.
        /// </summary>
        public static string GetBuiltinTemplate(SocketGuild guild, SocketGuildUser master, ServerConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Привет, {master.Mention}! 👋");
            sb.AppendLine($"Тебе выдали роль мастера на сервере **{guild.Name}**.");
            sb.AppendLine();
            sb.AppendLine("Основные команды мастера:");
            sb.AppendLine("• `/start` — запустить игровую сессию");
            sb.AppendLine("• Кнопки в сообщении сессии: пауза / продолжить / изменить / завершить");
            sb.AppendLine("• Кнопка `🎲 Броски` — включить/выключить сбор бросков");
            sb.AppendLine();
            sb.AppendLine("Полезные каналы:");
            sb.AppendLine($"• Канал бросков: {ChannelOrPlaceholder(config.RollChannelID, guild)}");
            sb.AppendLine($"• Канал записей: {ChannelOrPlaceholder(config.RecordChannelID, guild)}");
            sb.AppendLine($"• Канал статистики: {ChannelOrPlaceholder(config.StatsChannelID, guild)}");
                    // ✅ pred-parallelization: EventVoiceChannelID — deprecated, но плейсхолдер оставлен для обратной совместимости с шаблонами.
        #pragma warning disable CS0618
                    sb.AppendLine($"• Канал игровых сессий (deprecated): {ChannelOrPlaceholder(config.EventVoiceChannelID, guild)}");
        #pragma warning restore CS0618
                    sb.AppendLine();
                    sb.AppendLine("Если что-то не работает, обратись к администратору сервера.");
                    return sb.ToString();
                }

        public static string GetTemplatePath(ulong guildId)
        {
                    // Возвращаем путь вида "Settings/master_guide_<id>.txt".
                    // Это НЕ абсолютный путь — фактическая директория будет вычислена через BotConfig.ResolvePath
                    // в момент чтения файла. Так шаблоны живут в %LOCALAPPDATA%\RPBot\Settings\ на всех машинах
                    // и не создаются дублями в bin\...\Settings\.
                    return Path.Combine(BotConfig.SettingsFolderName, $"{FilePrefix}{guildId}{FileExtension}");
                }

        private static string ChannelOrPlaceholder(ulong channelId, SocketGuild guild)
        {
            if (channelId == 0) return "не настроен";
            var text = guild.GetTextChannel(channelId);
            if (text != null) return text.Mention;
            var voice = guild.GetVoiceChannel(channelId);
            if (voice != null) return voice.Mention;
            return "не настроен";
        }

        /// <summary>
        /// Упоминание роли мастера. Получаем роль через guild.GetRole,
        /// чтобы корректно отобразить упоминание (иначе Discord.Net не найдёт роль в кеше).
        /// </summary>
        private static string MasterRoleMention(ServerConfig config, SocketGuild guild)
        {
            if (!config.MasterRoleId.HasValue || config.MasterRoleId.Value == 0)
                return "не настроена";

            try
            {
                var role = guild.GetRole(config.MasterRoleId.Value);
                return role != null ? role.Mention : $"<@&{config.MasterRoleId.Value}>";
            }
            catch
            {
                return $"<@&{config.MasterRoleId.Value}>";
            }
        }

        /// <summary>
        /// Удаляет строки-комментарии (начинающиеся с #) и пустые строки.
        /// </summary>
        private static string StripComments(string content)
        {
            var sb = new StringBuilder();
            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed))
                    continue;
                sb.AppendLine(line.TrimEnd());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Содержимое файла, создаваемого при первом запуске.
        /// Документация (комментарии) + пример рабочего шаблона.
        /// </summary>
        private static string GetDefaultTemplateContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Памятка мастера");
            sb.AppendLine("#");
            sb.AppendLine("# Этот файл — шаблон сообщения, которое бот отправит мастеру в ЛС");
            sb.AppendLine("# при выдаче роли мастера. Вы можете изменить текст как угодно.");
            sb.AppendLine("#");
            sb.AppendLine("# ─── ДОСТУПНЫЕ ПЛЕЙСХОЛДЕРЫ ───────────────────────────────────");
            sb.AppendLine("#   {GuildName}         — название сервера (например: My RP Server)");
            sb.AppendLine("#   {MasterMention}     — упоминание мастера (@User)");
            sb.AppendLine("#   {MasterName}        — имя мастера без @");
            sb.AppendLine("#   {RollChannel}       — канал бросков (#rolls или \"не настроен\")");
            sb.AppendLine("#   {RecordChannel}     — канал записей (#records или \"не настроен\")");
            sb.AppendLine("#   {StatsChannel}      — канал статистики (#stats или \"не настроен\")");
            sb.AppendLine("#   {SessionChannel}    — канал игровых сессий или \"не настроен\"");
            sb.AppendLine("#   {MasterRoleMention} — упоминание роли мастера (@Master)");
            sb.AppendLine("#");
            sb.AppendLine("# ─── КАК ПОЛЬЗОВАТЬСЯ ─────────────────────────────────────────");
            sb.AppendLine("# 1. Строки, начинающиеся с # — комментарии, они НЕ отправляются.");
            sb.AppendLine("# 2. Чтобы изменить текст памятки — отредактируйте строки НИЖЕ.");
            sb.AppendLine("# 3. Используйте плейсхолдеры из списка выше в фигурных скобках.");
            sb.AppendLine("# 4. Если в файле останутся только комментарии — бот использует");
            sb.AppendLine("#    встроенный шаблон по умолчанию.");
            sb.AppendLine("#");
            sb.AppendLine("# ─── ПРИМЕР (можно редактировать или удалить) ─────────────────");
            sb.AppendLine();
            sb.AppendLine("Привет, {MasterMention}! 👋");
            sb.AppendLine();
            sb.AppendLine("Тебе выдали роль мастера на сервере **{GuildName}**.");
            sb.AppendLine();
            sb.AppendLine("Основные команды:");
            sb.AppendLine("• `/start` — запустить игровую сессию");
            sb.AppendLine("• Кнопки в сообщении сессии: пауза / продолжить / изменить / завершить");
            sb.AppendLine("• Кнопка `🎲 Броски` — включить/выключить сбор бросков");
            sb.AppendLine();
            sb.AppendLine("Полезные каналы:");
            sb.AppendLine("• Броски: {RollChannel}");
            sb.AppendLine("• Записи: {RecordChannel}");
            sb.AppendLine("• Статистика: {StatsChannel}");
            sb.AppendLine();
            sb.AppendLine("Удачных игр, {MasterName}! 🎲");

            return sb.ToString();
        }
    }
}
