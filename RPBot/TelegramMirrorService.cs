using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot
{
    /// <summary>
    /// Зеркало постов Telegram-канала → Discord.
    /// </summary>
    /// <remarks>
    /// <para>Бот, публикующий анонсы событий в Telegram (TelegramNotifier), теперь может
    /// дополнительно читать канал через Bot API getUpdates (long polling) и отправлять
    /// посты, появившиеся в канале (владелец, не сам бот), в указанный Discord-канал
    /// сервера с припиской «дублирование из группы Telegram» и ссылкой.</para>
    /// <para>Анонсы не дублируются: Telegram не доставляет боту его собственные
    /// сообщения в getUpdates (двойная защита — фильтр по author_user.id == bot id).</para>
    /// <para>Конфигурация (ServerConfig сервера): TelegramEnabled + TelegramBotToken +
    /// TelegramChatId — те же, что для анонсов; дополнительно:
    /// TelegramMirrorEnabled — вкл/выкл зеркала; TelegramMirrorChannelID — Discord-канал
    /// приёма; TelegramMirrorLink — база ссылки на канал (например
    /// https://t.me/my_channel), используется в приписке.</para>
    /// <para>Ограничения: зеркалятся текст, текст+фото (и caption-текст вложений);
    /// непонятные медиа переносятся как пометка. Offset хранится в
    /// Data/telegram_mirror_offsets.json — посты, прочитанные до перезапуска,
    /// повторно не приходят; первый запуск стартует «с хвоста» (offset=-1), чтобы
    /// не заливать старую историю.</para>
    /// <para>Правки постов (edited_channel_post) редактируют ранее отправленное
    /// Discord-сообщение (ModifyMessageAsync) вместо создания дубля; пара
    /// «пост ТГ → Discord-сообщение» хранится в Data/telegram_mirror_links.json
    /// (cap 1000 постов на токен). Правка поста без сохранённой пары уходит
    /// как новый пост, чтобы ничего не потерялось.</para>
    /// </remarks>
    public sealed class TelegramMirrorService : IDisposable
    {
        /// <summary>Цель зеркалирования: сервер + токен + ТГ-канал + Discord-канал.</summary>
        public sealed record MirrorTarget(ulong GuildId, string Token, long ChatId, ulong ChannelId, string? LinkBase);

        /// <summary>Пара «пост Telegram → уже отправленное Discord-сообщение» для правок.</summary>
        public sealed record DiscordPostRef(ulong ChannelId, ulong MessageId, string? FileName = null);

        /// <summary>Сущность форматирования Telegram (entities из getUpdates).</summary>
        public sealed class TgMirrorEntity
        {
            public string Type { get; set; } = "";
            public int Offset { get; set; }
            public int Length { get; set; }
            public string? Url { get; set; }
        }

        /// <summary>channel_post из getUpdates, нормализованный для зеркала.</summary>
        public sealed class TgChannelPost
        {
            public long UpdateId { get; set; }
            public int MessageId { get; set; }
            public long ChatId { get; set; }
            public long AuthorId { get; set; }
            public bool AuthorIsBot { get; set; }
            /// <summary>Правка существующего поста (edited_channel_post). Обрабатывается
            /// редактированием ранее отправленного Discord-сообщения, а не новой отправкой.</summary>
            public bool IsEdit { get; set; }
            public string Text { get; set; } = "";
            public List<TgMirrorEntity>? Entities { get; set; }
            public string? PhotoFileId { get; set; }
            public bool HasOtherMedia { get; set; }
            public string? MediaGroupId { get; set; }
            /// <summary>Unix-время публикации поста (поле date из Telegram).</summary>
            public long DateUnix { get; set; }
            /// <summary>file_id основного медиа (видео/аудио/документ и т.п.) для прикрепления файлом.</summary>
            public string? MediaFileId { get; set; }
            /// <summary>Тип медиа: video / document / animation / audio / voice / video_note / sticker.</summary>
            public string MediaKind { get; set; } = "";
            /// <summary>Имя файла медиа (для document/audio — от Telegram, для остальных — заглушка).</summary>
            public string MediaName { get; set; } = "";
        }

        private readonly DiscordSocketClient _client;
        private readonly Func<IReadOnlyList<MirrorTarget>> _targetsProvider;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        /// <summary>Discord-токен бота для сырого REST PATCH правки сообщения
        /// (у DiscordSocketClient 3.20.2 нет публичного Token).</summary>
        private readonly string? _discordBotToken;
        private readonly string _offsetsPath;
        private bool _disposed;

        /// <summary>Долгий опрос Telegram (официальный потолок 50 с).</summary>
        private const int TelegramLongPollTimeoutSeconds = 50;

        private readonly Dictionary<string, long> _offsetsByToken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _botUserIdByTokenHash = new(StringComparer.Ordinal);

        // tokenHash → chatId → tgMessageId → ссылка на отправленное Discord-сообщение.
        private readonly Dictionary<string, Dictionary<long, Dictionary<long, DiscordPostRef>>> _linksByToken =
            new(StringComparer.Ordinal);
        /// <summary>Пост-ссылки, которые храним на токен (ограничение файла на диске).</summary>
        private const int LinksPerTokenCap = 1000;
        private string LinksPath => Path.Combine(
            Path.GetDirectoryName(_offsetsPath)!, "telegram_mirror_links.json");

        /// <summary>Найти ранее отправленное Discord-сообщение для поста ТГ.</summary>
        public bool TryGetDiscordRef(string token, long chatId, long tgMessageId, out DiscordPostRef discordRef)
        {
            discordRef = new DiscordPostRef(0, 0);
            var hash = TokenHash(token);
            if (_linksByToken.TryGetValue(hash, out var byChat) &&
                byChat.TryGetValue(chatId, out var byMsg) &&
                byMsg.TryGetValue(tgMessageId, out var r))
            {
                discordRef = r;
                return true;
            }
            return false;
        }

        /// <summary>Запомнить пару «пост ТГ → Discord-сообщение» и сохранить на диск.</summary>
        public void StoreDiscordRef(string token, long chatId, long tgMessageId, DiscordPostRef discordRef)
        {
            var hash = TokenHash(token);
            if (!_linksByToken.TryGetValue(hash, out var byChat))
                _linksByToken[hash] = byChat = new Dictionary<long, Dictionary<long, DiscordPostRef>>();
            if (!byChat.TryGetValue(chatId, out var byMsg))
                byChat[chatId] = byMsg = new Dictionary<long, DiscordPostRef>();
            byMsg[tgMessageId] = discordRef;
            SaveLinks();
        }

        public TelegramMirrorService(DiscordSocketClient client, Func<IReadOnlyList<MirrorTarget>> targetsProvider,
            string? discordBotToken = null, HttpClient? httpClient = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _targetsProvider = targetsProvider ?? throw new ArgumentNullException(nameof(targetsProvider));
            _discordBotToken = discordBotToken;
            if (httpClient != null)
            {
                _http = httpClient;
                _ownsHttp = false;
            }
            else
            {
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(TelegramLongPollTimeoutSeconds + 25) };
                _ownsHttp = true;
            }
            var dataRoot = BotConfig.GetDataRootDirectory();
            _offsetsPath = Path.Combine(dataRoot, "Data", "telegram_mirror_offsets.json");
        }

        /// <summary>Время следующей разрешённой попытки getMe по токену (после неудачи).</summary>
        private readonly Dictionary<string, DateTime> _getMeNextTryUtc = new(StringComparer.Ordinal);

        /// <summary>Основной цикл зеркала. Блокирующий — запускать в фоновой задаче.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                LoadOffsets();
                LoadLinks();
                while (!ct.IsCancellationRequested)
                {
                    var targets = _targetsProvider()
                        .Where(t => !string.IsNullOrWhiteSpace(t.Token) && t.ChatId != 0 && t.ChannelId != 0)
                        .ToList();

                    if (targets.Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var group in targets.GroupBy(t => t.Token, StringComparer.Ordinal))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            await PollTokenAsync(group.Key, group.ToList(), ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            // TaskCanceledException от таймаута HttpClient (75 с) сюда тоже попадает:
                            // сеть к api.telegram.org «мигает» (DPI), запрос висит в чёрной дыре и
                            // умирает по таймауту. Это НЕ отмена сервиса — логируем и продолжаем.
                            BotLogger.Error(LogCategory.System, $"[tg_mirror] {ex.GetType().Name}: {ex.Message}");
                            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* штатная остановка */ }
        }

        private async Task PollTokenAsync(string token, List<MirrorTarget> targets, CancellationToken ct)
        {
            var botUserId = await EnsureBotUserIdAsync(token, ct).ConfigureAwait(false);
            var chatToTarget = new Dictionary<long, MirrorTarget>();
            foreach (var t in targets)
            {
                chatToTarget[t.ChatId] = t; // при нескольких серверах на один канал побеждает последняя цель
            }

            var offset = _offsetsByToken.TryGetValue(token, out var o) ? o : -1;
            var json = await GetUpdatesAsync(token, offset, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return;

            var posts = ParseChannelPosts(json);
            if (posts.Count == 0)
            {
                BotLogger.Debug(LogCategory.System,
                    $"[tg_mirror] poll: json {json.Length} байт, channel_post не найдены (updates других типов)");
                return;
            }

            var maxUpdateId = posts.Max(p => p.UpdateId);
            _offsetsByToken[token] = maxUpdateId + 1;
            SaveOffsets();

            // Отправляем боту-автору? Анонсы бота Telegram не присылает, но фильтр
            // по author id — вторая линия защиты от дублей. Если getMe ещё не удался
            // (botUserId == 0), фильтр НЕ применяем: канальные посты анонимны
            // (from отсутствует → AuthorId == 0) и при 0 != 0 ложно дропались бы
            // все человеческие посты, пока id бота не определён.
            var foreign = botUserId != 0
                ? posts.Where(p => p.AuthorId != botUserId && !p.AuthorIsBot).ToList()
                : posts.ToList();
            if (foreign.Count == 0)
            {
                BotLogger.Debug(LogCategory.System,
                    $"[tg_mirror] poll: получено постов={posts.Count}, все от бота (не дублируем) chats=[{string.Join(",", posts.Select(p => p.ChatId).Distinct())}]");
                return;
            }
            if (botUserId == 0)
                BotLogger.Debug(LogCategory.System,
                    "[tg_mirror] poll: getMe ещё не удался — фильтр автора отключён, посты обрабатываются как есть");

            var linksChanged = false;
            foreach (var group in foreign.GroupBy(p => p.ChatId))
            {
                if (!chatToTarget.TryGetValue(group.Key, out var target))
                {
                    BotLogger.Debug(LogCategory.System,
                        $"[tg_mirror] poll: пост из чужого chat={group.Key} (нет цели) — пропускаем");
                    continue; // другой канал — не наш
                }
                var groupPosts = group.OrderBy(p => p.MessageId).ToList();

                // Правки: редактируем ранее отправленные Discord-сообщения.
                foreach (var e in groupPosts.Where(p => p.IsEdit))
                {
                    try { linksChanged |= await EditMirrorAsync(target, token, e).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        BotLogger.Error(LogCategory.System,
                            $"[tg_mirror] ошибка правки в Discord: {ex.GetType().Name}: {ex.Message} guild={target.GuildId} channel={target.ChannelId} tg_msg={e.MessageId}");
                    }
                }

                var fresh = groupPosts.Where(p => !p.IsEdit).ToList();
                // Медиагруппы (альбомы) склеиваем; одиночные посты отправляем по одному.
                var albums = fresh.Where(p => !string.IsNullOrEmpty(p.MediaGroupId))
                    .GroupBy(p => p.MediaGroupId!).ToList();
                var singles = fresh.Where(p => string.IsNullOrEmpty(p.MediaGroupId));
                foreach (var s in singles)
                {
                    try { linksChanged |= await SendMirrorAsync(target, token, new[] { s }).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        BotLogger.Error(LogCategory.System,
                            $"[tg_mirror] ошибка отправки в Discord: {ex.GetType().Name}: {ex.Message} guild={target.GuildId} channel={target.ChannelId}");
                    }
                }
                foreach (var album in albums)
                {
                    try { linksChanged |= await SendMirrorAsync(target, token, album.OrderBy(p => p.MessageId).ToList()).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        BotLogger.Error(LogCategory.System,
                            $"[tg_mirror] ошибка отправки в Discord: {ex.GetType().Name}: {ex.Message} guild={target.GuildId} channel={target.ChannelId}");
                    }
                }
            }
            if (linksChanged) SaveLinks();
        }

        private async Task<long> EnsureBotUserIdAsync(string token, CancellationToken ct)
        {
            var hash = TokenHash(token);
            if (_botUserIdByTokenHash.TryGetValue(hash, out var existing)) return existing;
                // Неудачный getMe не кэшируем навсегда: повторная попытка не раньше, чем через
                // 5 минут (чтобы при мёртвой сети не стопорить каждый poll на 75-секундном таймауте).
                if (_getMeNextTryUtc.TryGetValue(hash, out var nextTry) && DateTime.UtcNow < nextTry) return 0;

            var id = 0L;
            try
            {
                var json = await PostTelegramAsync(token, "getMe", new Dictionary<string, string>(), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json ?? "");
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("id", out var idEl))
                {
                    id = idEl.GetInt64();
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.System, $"[tg_mirror] getMe не удался: {ex.Message}");
                    _getMeNextTryUtc[hash] = DateTime.UtcNow.AddSeconds(300);
                    return 0;
            }
            _botUserIdByTokenHash[hash] = id;
            return id;
        }

        /// <summary>Отправка (или правка) в Discord. Возвращает id отправленного сообщения или null.</summary>
        private async Task<ulong?> PostToDiscordAsync(MirrorTarget target, string text, Embed[] embeds)
        {
            var channel = _client.GetChannel(target.ChannelId);
            switch (channel)
            {
                case SocketThreadChannel thread:
                    return (await thread.SendMessageAsync(text, embeds: embeds).ConfigureAwait(false)).Id;
                case SocketTextChannel textChannel:
                    return (await textChannel.SendMessageAsync(text, embeds: embeds).ConfigureAwait(false)).Id;
                default:
                    BotLogger.Error(LogCategory.System,
                        $"[tg_mirror] канал {target.ChannelId} не текстовый/не найден (guild={target.GuildId}) — пост зеркала пропущен.");
                    return null;
            }
        }

        /// <summary>
        /// Отправка поста (или альбома) в Discord-канал зеркала. Возвращает true,
        /// если отправка удалась и ссылка «пост ТГ → Discord-сообщение» сохранена.
        /// Оформление: embed «Новый пост в ТГ-канале!» с текстом (+ пометки медиа),
        /// footer — время публикации (Discord рендерит в локальной зоне зрителя) и ссылка
        /// в заголовке. Показывается только первое фото (картинкой embed'а); при 2+ фото
        /// в тексте пометка «остальные можно просмотреть в Telegram». Видео/аудио/документ
        /// идут отдельным сообщением сразу после embed'а (вложения одного сообщения
        /// Discord рисует выше embed'ов — иначе не соблюсти порядок).
        /// </summary>
        private async Task<bool> SendMirrorAsync(MirrorTarget target, string token, IReadOnlyList<TgChannelPost> posts)
        {
            // Порядок Discord: обычные вложения всегда рисуются ВЫШЕ embed'ов, поэтому фото
            // проводим через embed'ы. Показываем самое первое фото (картинка текстового
            // embed'а); остальные не отправляем — в тексте пометка «смотреть в Telegram».
            var streamsToDispose = new List<Stream>();
            string? firstPhotoName = null;
            var photoAtt = (FileAttachment?)null;
            var photoCount = posts.Count(p => p.PhotoFileId != null);

            foreach (var p in posts.Where(p => p.PhotoFileId != null).OrderBy(p => p.MessageId))
            {
                var att = await TryDownloadPhotoAsync(token, p, 1).ConfigureAwait(false);
                if (att is not { } fetched) continue;
                firstPhotoName = fetched.FileName;
                photoAtt = fetched;
                streamsToDispose.Add(fetched.Stream);
                break;
            }

            // Медиа (видео/аудио/документ...) уйдёт отдельным сообщением после embed'а.
            var media = await TryDownloadMediaAsync(token, posts).ConfigureAwait(false);

            // Текстовый embed: только первое фото картинкой внутри; остальные (если были)
            // не отправляются — в тексте пометка «можно просмотреть в Telegram».
            var embed = BuildMirrorEmbed(target.LinkBase, target.ChatId, posts,
                photoAttachmentName: firstPhotoName, media != null,
                extraPhotosLeft: photoCount > 1);

            ulong? sentId;
            if (photoAtt != null)
            {
                sentId = await PostFilesToDiscordAsync(target, new[] { photoAtt.Value }, new[] { embed }).ConfigureAwait(false);
            }
            else
            {
                sentId = await PostToDiscordAsync(target, "", new[] { embed }).ConfigureAwait(false);
            }

            foreach (var s in streamsToDispose) s.Dispose();
            if (sentId == null)
            {
                media?.Dispose();
                return false;
            }

            // Альбом: несколько постов ТГ → одно Discord-сообщение; правка любого
            // поста альбома редактирует это сообщение.
            foreach (var post in posts)
                StoreDiscordRef(token, target.ChatId, post.MessageId,
                    new DiscordPostRef(target.ChannelId, sentId.Value, firstPhotoName));

            // Медиафайл — вторым сообщением сразу после embed-сообщения.
            if (media != null)
            {
                try
                {
                    await PostMediaFollowUpAsync(target, media).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    BotLogger.Error(LogCategory.System,
                        $"[tg_mirror] медиа-сообщение tg_msg={posts[0].MessageId} не отправлено: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    media.Dispose();
                }
            }

            BotLogger.Info(LogCategory.System,
                $"[tg_mirror] отправлено в Discord guild={target.GuildId} channel={target.ChannelId} tg_msg={posts[0].MessageId} файл={firstPhotoName ?? "-"} медиа={media != null} embeds=1 discord_msg={sentId}");
            return true;
        }

        /// <summary>Медиафайл вторым сообщением сразу после embed-сообщения. Discord рисует
        /// вложения одного сообщения выше embed'ов, поэтому порядок «текст → медиа»
        /// возможен только двумя сообщениями.</summary>
        private async Task PostMediaFollowUpAsync(MirrorTarget target, MediaDownload media)
        {
            var channel = _client.GetChannel(target.ChannelId);
            switch (channel)
            {
                case SocketThreadChannel thread:
                    await thread.SendFileAsync(new FileAttachment(media.Stream, media.FileName), "").ConfigureAwait(false);
                    break;
                case SocketTextChannel textChannel:
                    await textChannel.SendFileAsync(new FileAttachment(media.Stream, media.FileName), "").ConfigureAwait(false);
                    break;
                default:
                    BotLogger.Error(LogCategory.System,
                        $"[tg_mirror] канал {target.ChannelId} не текстовый/не найден (guild={target.GuildId}) — медиа-сообщение пропущено.");
                    return;
            }
            BotLogger.Info(LogCategory.System,
                $"[tg_mirror] медиа отправлено вторым сообщением guild={target.GuildId} channel={target.ChannelId} файл={media.FileName}");
        }

        /// <summary>Скачивание одного пост-фото для вложения. Имя — фото{N}.{ext из file_path}.</summary>
        private async Task<FileAttachment?> TryDownloadPhotoAsync(string token, TgChannelPost post, int index)
        {
            try
            {
                var url = await GetFileUrlAsync(token, post.PhotoFileId!).ConfigureAwait(false);
                if (url == null) return null;
                var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (resp.Content.Headers.ContentLength is > MaxAttachmentBytes)
                {
                    resp.Dispose();
                    return null;
                }
                var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var ext = GuessPhotoExtension(resp.Content.Headers.ContentType?.MediaType);
                return new FileAttachment(stream, $"фото{index}.{ext}");
            }
            catch (Exception ex)
            {
                BotLogger.Debug(LogCategory.System,
                    $"[tg_mirror] фото tg_msg={post.MessageId} не прикреплено: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static string GuessPhotoExtension(string? contentType) => contentType switch
        {
            "image/png" => "png",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => "jpg"
        };

        /// <summary>Максимум вложения Discord по умолчанию: 10 МБ.</summary>
        private const long MaxAttachmentBytes = 10 * 1024 * 1024;

        /// <summary>Скачанное вложение (стрим + имя файла) для отправки в Discord.</summary>
        private sealed class MediaDownload : IDisposable
        {
            public Stream Stream { get; }
            public string FileName { get; }
            public MediaDownload(Stream stream, string fileName) { Stream = stream; FileName = fileName; }
            public void Dispose() { Stream.Dispose(); }
        }

        /// <summary>
        /// Попытка скачать основное медиа пачки (видео/аудио/документ и т.д.) для прикрепления
        /// файлом. Стикеры не прикрепляем; больше 10 МБ — не прикрепляем (останется пометка).
        /// </summary>
        private async Task<MediaDownload?> TryDownloadMediaAsync(string token, IReadOnlyList<TgChannelPost> posts)
        {
            var post = posts.FirstOrDefault(p => !string.IsNullOrEmpty(p.MediaFileId) && p.MediaKind != "sticker");
            if (post == null) return null;
            try
            {
                var url = await GetFileUrlAsync(token, post.MediaFileId!).ConfigureAwait(false);
                if (url == null) return null;
                var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (resp.Content.Headers.ContentLength is > MaxAttachmentBytes)
                {
                    resp.Dispose();
                    return null;
                }
                var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                return new MediaDownload(stream, GuessMediaFileName(post));
            }
            catch (Exception ex)
            {
                BotLogger.Debug(LogCategory.System,
                    $"[tg_mirror] медиа {post.MediaKind} не прикреплено (guild download fail): {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Имя файла Discord-вложения по медиа-посту. Оригинальное название
        /// (title трека / file_name) санитизируется: недопустимые в имени файла Windows
        /// символы (кавычки, |, :, *) замещаются подчёркиванием, длина капится 64;
        /// при пустом названии — заглушка по типу медиа («звук.mp3», «видео.mp4»...).
        /// Без расширения — добавляется по типу, иначе Discord не распознает медиа.</summary>
        public static string GuessMediaFileName(TgChannelPost p)
        {
            var name = (p.MediaName ?? "").Trim();
            if (name.Length > 0)
            {
                var badChars = Path.GetInvalidFileNameChars();
                name = new string(name.Select(ch => badChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
                if (name.Length > 64)
                {
                    // Кап 64 — но расширение сохраняем на конце (иначе теряется тип файла).
                    var ext = Path.GetExtension(name);
                    name = name[..Math.Max(1, 64 - ext.Length)].TrimEnd() + ext;
                }
                // Точка-имя (".", "..") и пустой результат — не годятся.
                if (name.Replace("_", "").Trim('.').Length > 0)
                {
                    if (name.IndexOf('.') < 0)
                        name += p.MediaKind switch
                        {
                            "audio" or "voice" => ".mp3",
                            "video" or "animation" or "video_note" => ".mp4",
                            "sticker" => ".webp",
                            _ => ".bin"
                        };
                    return name;
                }
            }
            return p.MediaKind switch
            {
                "video" => "видео.mp4",
                "animation" => "анимация.mp4",
                "audio" => "звук.mp3",
                "voice" => "голос.ogg",
                "video_note" => "видеозаметка.mp4",
                "document" => "документ.bin",
                _ => "вложение.bin"
            };
        }

        /// <summary>
        /// Собрать Discord-embed поста зеркала: заголовок-ссылка на пост ТГ, тело — текст,
        /// первое фото — картинка из вложения (attachment://<имя>), footer — метка источника;
        /// время публикации задаю отдельно (Timestamp рендерится у каждого зрителя
        /// в его локальной зоне).
        /// </summary>
        public static Embed BuildMirrorEmbed(
            string? linkBase, long chatId, IReadOnlyList<TgChannelPost> posts, string? photoAttachmentName, bool mediaAttached,
            bool extraPhotosLeft = false)
        {
            var text = BuildMirrorText(posts, mediaAttached, extraPhotosLeft);
            var link = BuildPostLink(linkBase, chatId, posts[0].MessageId);

            var dateUnix = posts
                .Where(p => p.DateUnix > 0)
                .Select(p => p.DateUnix)
                .DefaultIfEmpty(0)
                .Min();
            // Unix-время по определению UTC; Discord сериализует DateTimeOffset с офсетом
            // и рендерит footer-время в локальной зоне каждого зрителя.
            var timestamp = dateUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(dateUnix)
                : DateTimeOffset.UtcNow;

            var eb = new EmbedBuilder()
                .WithTitle("Новый пост в ТГ-канале!")
                .WithColor(new Color(0x2A, 0xAB, 0xEE)) // телеграмный голубой
                .WithDescription(string.IsNullOrWhiteSpace(text) ? "*(без текста)*" : text)
                .WithFooter("🔁 Дублирование из Telegram")
                .WithTimestamp(timestamp);
            if (!string.IsNullOrWhiteSpace(link)) eb.WithUrl(link);
            if (!string.IsNullOrWhiteSpace(photoAttachmentName))
                eb.WithImageUrl($"attachment://{photoAttachmentName}");
            return eb.Build();
        }

        /// <summary>Отправка сообщения с несколькими вложениями и embed'ами.</summary>
        private async Task<ulong?> PostFilesToDiscordAsync(MirrorTarget target, FileAttachment[] attachments, Embed[] embeds)
        {
            var channel = _client.GetChannel(target.ChannelId);
            switch (channel)
            {
                case SocketThreadChannel thread:
                    return (await thread.SendFilesAsync(attachments, "", embeds: embeds).ConfigureAwait(false)).Id;
                case SocketTextChannel textChannel:
                    return (await textChannel.SendFilesAsync(attachments, "", embeds: embeds).ConfigureAwait(false)).Id;
                default:
                    BotLogger.Error(LogCategory.System,
                        $"[tg_mirror] канал {target.ChannelId} не текстовый/не найден (guild={target.GuildId}) — пост зеркала пропущен.");
                    return null;
            }
        }

        /// <summary>JSON-полезная нагрузка правки Discord-сообщения: content + embeds +
        /// сохранение существующих вложений (по id). Discord.Net 3.20.2 не сериализует
        /// поле attachments при изменении сообщения, а embed-картинка attachment://
        /// резолвится только при явной передаче вложений — поэтому правки с фото
        /// идут через сырой REST PATCH.</summary>
        public static string BuildModifyMessageJson(IReadOnlyList<Embed> embeds,
            IReadOnlyList<(ulong Id, string Filename)>? keepAttachments)
        {
            var embedsJson = new JsonArray();
            foreach (var e in embeds)
            {
                var jo = new JsonObject();
                if (!string.IsNullOrWhiteSpace(e.Title)) jo["title"] = e.Title;
                if (!string.IsNullOrWhiteSpace(e.Description)) jo["description"] = e.Description;
                if (!string.IsNullOrWhiteSpace(e.Url)) jo["url"] = e.Url;
                if (e.Timestamp is { } ts) jo["timestamp"] = ts.ToString("o");
                if (e.Color is { } col) jo["color"] = (long)col.RawValue;
                if (e.Footer is { } f && !string.IsNullOrWhiteSpace(f.Text))
                    jo["footer"] = new JsonObject { ["text"] = f.Text };
                if (e.Image is { } img && !string.IsNullOrWhiteSpace(img.Url))
                    jo["image"] = new JsonObject { ["url"] = img.Url };
                embedsJson.Add(jo);
            }

            var payload = new JsonObject
            {
                ["content"] = "",
                ["embeds"] = embedsJson
            };
            if (keepAttachments != null)
            {
                var atts = new JsonArray();
                foreach (var (id, filename) in keepAttachments)
                    atts.Add(new JsonObject { ["id"] = (long)id, ["filename"] = filename });
                payload["attachments"] = atts;
            }
            return payload.ToJsonString();
        }

        /// <summary>Сырая правка Discord-сообщения через REST API (в обход Discord.Net:
        /// тот не умеет передавать attachments при изменении сообщения).</summary>
        private async Task ModifyMessageRestAsync(string botToken, ulong channelId, ulong messageId, string json)
        {
            using var req = new HttpRequestMessage(HttpMethod.Patch,
                $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}");
            req.Headers.UserAgent.ParseAdd("DiscordBot (https://github.com/elmer-grey/RoleplayerDiscordBot, 1.0)");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (body.Length > 300) body = body[..300];
                throw new HttpRequestException($"Discord PATCH {channelId}/{messageId}: [{(int)resp.StatusCode}] {body}");
            }
        }

        /// <summary>
        /// Правка ранее отправленного поста: пересобираем embed и
        /// ModifyMessageAsync по сохранённой паре. Нет пары → отправляем как новый пост.
        /// Возвращает true, если Discord был изменён (правка или fallback-отправка).
        /// </summary>
        private async Task<bool> EditMirrorAsync(MirrorTarget target, string token, TgChannelPost post)
        {
            if (!TryGetDiscordRef(token, target.ChatId, post.MessageId, out var dref))
            {
                // Правка пришла раньше первой успешной отправки (например, маппинг
                // потерялся) — уходим в обычную отправку, чтобы пост не потерялся.
                BotLogger.Info(LogCategory.System,
                    $"[tg_mirror] правка tg_msg={post.MessageId} без пары в Discord — отправляю как новый пост (guild={target.GuildId})");
                return await SendMirrorAsync(target, token, new[] { post }).ConfigureAwait(false);
            }

            var channel = _client.GetChannel(dref.ChannelId);

            // Фото при правке не перезакачиваем — вложения сохраняются при Modify.
            // Список фото берём из кэша либо REST-загрузкой оригинала (картинки-вложения).
            IMessage? orig = null;
            if (channel is ISocketMessageChannel socketChannel)
                orig = socketChannel.GetCachedMessage(dref.MessageId);
            if (orig == null && channel is IMessageChannel fetchChannel)
                orig = await fetchChannel.GetMessageAsync(dref.MessageId).ConfigureAwait(false);

            string? firstPhoto = null;
            var extraPhotosLeft = false;
            List<IAttachment>? keepAtts = null;
            if (orig != null)
            {
                keepAtts = orig.Attachments.ToList();
                var imageAtts = keepAtts
                    .Where(a => a.ContentType != null &&
                                a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                firstPhoto = imageAtts.FirstOrDefault()?.Filename;
                extraPhotosLeft = imageAtts.Count > 1;
            }

            var embeds = new List<Embed>(1)
            {
                BuildMirrorEmbed(target.LinkBase, target.ChatId, new[] { post },
                    photoAttachmentName: firstPhoto, mediaAttached: true,
                    extraPhotosLeft: extraPhotosLeft)
            };

            if (channel is IMessageChannel msgChannel)
            {
                // Правка обновляет embed'ы целиком: описание и время поста. Discord.Net 3.20.2
                // не сериализует attachments при изменении сообщения, а embed-картинка
                // attachment:// резолвится только с ними — идём сырым REST PATCH с передачей
                // вложений по id (порядок «embed сверху, картинка внутри» сохраняется).
                var botToken = _discordBotToken;
                if (orig != null && !string.IsNullOrEmpty(botToken))
                {
                    var keep = keepAtts!.Select(a => (a.Id, a.Filename)).ToList();
                    await ModifyMessageRestAsync(botToken!, dref.ChannelId, dref.MessageId,
                        BuildModifyMessageJson(embeds, keep)).ConfigureAwait(false);
                }
                else
                {
                    // Оригинал недоступен (кэш пуст и REST не удался): вложения не знаем —
                    // обычная правка без attachment:// в embed (картинка останется карточкой).
                    await msgChannel.ModifyMessageAsync(dref.MessageId, props =>
                    {
                        props.Content = "";
                        props.Embeds = embeds.ToArray();
                    }).ConfigureAwait(false);
                }
            }
            else
            {
                BotLogger.Error(LogCategory.System,
                    $"[tg_mirror] правка tg_msg={post.MessageId}: Discord-канал {dref.ChannelId} не найден (guild={target.GuildId}) — пропущено.");
                return false;
            }

            BotLogger.Info(LogCategory.System,
                $"[tg_mirror] правка применена в Discord guild={target.GuildId} channel={dref.ChannelId} tg_msg={post.MessageId} discord_msg={dref.MessageId}");
            return true;
        }

        /// <summary>Текст поста/альбома. mediaAttached: медиафайл пачки отправлен вторым
        /// сообщением после embed'а; иначе фиксируем, что материал остался в Telegram.
        /// extraPhotosLeft: в посте было 2+ фото, но показано только первое.</summary>
        private static string BuildMirrorText(IReadOnlyList<TgChannelPost> posts, bool mediaAttached = false, bool extraPhotosLeft = false)
        {
            var parts = new List<string>();
            foreach (var post in posts)
            {
                var line = ConvertToDiscord(post.Text, post.Entities);
                if (post.PhotoFileId == null && post.HasOtherMedia && string.IsNullOrWhiteSpace(line))
                {
                    line = mediaAttached
                        ? $"📹 {DescribeMedia(post)} отправлен следующим сообщением."
                        : "📎 Пост с медиафайлом (дополнительный материал остался в Telegram).";
                }
                else if (post.PhotoFileId == null && post.HasOtherMedia && !string.IsNullOrWhiteSpace(line))
                {
                    line += mediaAttached
                        ? $"\n📹 {DescribeMedia(post)} отправлен следующим сообщением."
                        : "\n(дополнительный материал остался в Telegram)";
                }
                if (!string.IsNullOrWhiteSpace(line)) parts.Add(line);
            }
            if (extraPhotosLeft)
                parts.Add("🖼 Остальные изображения можно просмотреть в Telegram.");
            var joined = string.Join("\n\n", parts);
            // 4096 — лимит Discord на описание embed'а; запас на пометки.
            if (joined.Length > 3900) joined = joined.Substring(0, 3900) + "…";
            return joined;
        }

        private static string DescribeMedia(TgChannelPost p) => p.MediaKind switch
        {
            "video" => "видео",
            "animation" => "анимация (GIF)",
            "audio" => "аудио",
            "voice" => "голосовое",
            "video_note" => "видеозаметка (кружок)",
            "document" => "документ",
            "sticker" => "стикер",
            _ => "медиафайл"
        };

        /// <summary>
        /// Приписка «дублирование из группы Telegram» со ссылкой на конкретный пост
        /// (если база ссылки задана) или просто с пометкой.
        /// </summary>
        public static string BuildMirrorFooter(string? linkBase, long chatId, int messageId)
        {
            var link = BuildPostLink(linkBase, chatId, messageId);
            if (!string.IsNullOrWhiteSpace(link))
                return $"\n\n— 🔁 Дублирование сообщения из группы Telegram: {link}";
            return "\n\n— 🔁 Дублирование сообщения из группы Telegram.";
        }

        /// <summary>
        /// Ссылка на пост: для публичного канала — база + messageId;
        /// для приватного (инвайт вида t.me/+hash) — служебная https://t.me/c/&lt;internal&gt;/&lt;msg&gt;,
        /// потому что инвайт-ссылка с messageId не комбинируется.
        /// </summary>
        public static string? BuildPostLink(string? linkBase, long chatId, int messageId)
        {
            if (string.IsNullOrWhiteSpace(linkBase)) return null;
            var trimmed = linkBase.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(trimmed)) return null;
            if (trimmed.Contains("t.me/+", StringComparison.OrdinalIgnoreCase))
            {
                // Приватный канал: https://t.me/c/<internal_chat_id>/<message_id>, откроется участникам.
                if (chatId < 0)
                {
                    var digits = Math.Abs(chatId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (digits.StartsWith("100", StringComparison.Ordinal)) digits = digits.Substring(3);
                    return $"https://t.me/c/{digits}/{messageId}";
                }
                return null;
            }
            return $"{trimmed}/{messageId}";
        }

        /// <summary>
        /// Конвертация текста Telegram (text/caption + entities) в Markdown-текст Discord.
        /// Поддержка: bold, italic, underline, strikethrough, code, pre, spoiler,
        /// text_link, url, текстовые остаётся как есть (mentions/hashtags/bot_command).
        /// </summary>
        public static string ConvertToDiscord(string? text, List<TgMirrorEntity>? entities)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (entities == null || entities.Count == 0) return text;

            return RenderRange(text, entities, 0, text.Length);
        }

        private static string RenderRange(string text, List<TgMirrorEntity> entities, int start, int end)
        {
            if (start >= end) return "";
            var inRange = entities
                .Where(e => e.Offset >= start && e.Offset < end && e.Length > 0 && e.Offset + e.Length <= end)
                .OrderBy(e => e.Offset)
                .ThenByDescending(e => e.Length)
                .ToList();
            if (inRange.Count == 0) return text.Substring(start, end - start);

            var sb = new StringBuilder();
            var cursor = start;
            var consumed = new HashSet<TgMirrorEntity>();
            foreach (var e in inRange)
            {
                if (cursor > e.Offset)
                {
                    consumed.Add(e); // пересечение с предыдущей сущностью — внутреннее содержимое уже выведено
                    continue;
                }
                consumed.Add(e);
                sb.Append(text, cursor, e.Offset - cursor);
                var inner = entities
                    .Where(x => x != e && !consumed.Contains(x) &&
                                x.Offset >= e.Offset && x.Offset + x.Length <= e.Offset + e.Length)
                    .ToList();
                var innerText = RenderRange(text, inner, e.Offset, Math.Min(e.Offset + e.Length, end));
                sb.Append(WrapEntity(e, innerText));
                cursor = Math.Min(e.Offset + e.Length, end);
            }
            if (cursor < end) sb.Append(text, cursor, end - cursor);
            return sb.ToString();
        }

        private static string WrapEntity(TgMirrorEntity e, string inner)
        {
            if (string.IsNullOrEmpty(inner)) return inner;
            switch (e.Type)
            {
                case "bold": return $"**{inner}**";
                case "italic": return $"*{inner}*";
                case "underline": return $"__{inner}__";
                case "strikethrough": return $"~~{inner}~~";
                case "code": return $"`{inner}`";
                case "pre": return $"```{inner}```";
                case "spoiler": return $"||{inner}||";
                case "text_link": return $"[{inner}]({e.Url})";
                case "url":
                    return Uri.IsWellFormedUriString(inner, UriKind.Absolute) ? inner : $"<{inner}>";
                default:
                    return inner; // mention, hashtag, bot_command, text_mention, email, phone_number и пр.
            }
        }

        /// <summary>Разбор ответа getUpdates: только channel_post.</summary>
        public static List<TgChannelPost> ParseChannelPosts(string json)
        {
            var result = new List<TgChannelPost>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("result", out var updates) || updates.ValueKind != JsonValueKind.Array) return result;

            foreach (var update in updates.EnumerateArray())
            {
                var postEl = default(JsonElement);
                var isEdit = false;
                if (update.TryGetProperty("channel_post", out postEl))
                {
                    // новый пост канала
                }
                else if (update.TryGetProperty("edited_channel_post", out postEl))
                {
                    isEdit = true; // правка поста канала
                }
                else continue;
                if (postEl.ValueKind != JsonValueKind.Object) continue;

                var post = new TgChannelPost { IsEdit = isEdit };
                if (update.TryGetProperty("update_id", out var updId)) post.UpdateId = updId.GetInt64();
                if (postEl.TryGetProperty("message_id", out var mid)) post.MessageId = mid.GetInt32();
                if (postEl.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var chatId))
                    post.ChatId = chatId.GetInt64();
                if (postEl.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.Number)
                    post.DateUnix = dateEl.GetInt64();
                if (postEl.TryGetProperty("from", out var from))
                {
                    if (from.TryGetProperty("id", out var fromId)) post.AuthorId = fromId.GetInt64();
                    if (from.TryGetProperty("is_bot", out var fromBot)) post.AuthorIsBot = fromBot.GetBoolean();
                }
                if (postEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    post.Text = textEl.GetString() ?? "";
                else if (postEl.TryGetProperty("caption", out var capEl) && capEl.ValueKind == JsonValueKind.String)
                    post.Text = capEl.GetString() ?? "";

                if (postEl.TryGetProperty("entities", out var ents) && ents.ValueKind == JsonValueKind.Array)
                    post.Entities = ParseEntities(ents);
                else if (postEl.TryGetProperty("caption_entities", out var cents) && cents.ValueKind == JsonValueKind.Array)
                    post.Entities = ParseEntities(cents);

                if (postEl.TryGetProperty("photo", out var photoEl) && photoEl.ValueKind == JsonValueKind.Array)
                    post.PhotoFileId = ExtractLargestPhotoFileId(photoEl);

                // Основное медиа (не фото): берём первый встреченный тип с file_id —
                // Telegram кладёт в объект канального поста ровно один ключ медиа.
                foreach (var kind in new[] { "video", "document", "animation", "audio", "voice", "video_note", "sticker" })
                {
                    if (!postEl.TryGetProperty(kind, out var mEl) || mEl.ValueKind != JsonValueKind.Object) continue;
                    if (!mEl.TryGetProperty("file_id", out var fidEl) || fidEl.ValueKind != JsonValueKind.String) continue;
                    post.MediaFileId = fidEl.GetString();
                    post.MediaKind = kind;
                    // Имя файла для Discord-вложения: человекочитаемое название, где есть:
                    // аудио — title (название трека), иначе file_name; у видео/GIF/документа
                    // — file_name. Оба пусты → GuessMediaFileName подставит заглушку.
                    if (kind == "audio")
                    {
                        if (mEl.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String &&
                            tEl.GetString() is { } t && t.Trim().Length > 0)
                            post.MediaName = t.Trim();
                        else if (mEl.TryGetProperty("file_name", out var fnEl) && fnEl.ValueKind == JsonValueKind.String)
                            post.MediaName = fnEl.GetString() ?? "";
                    }
                    else if (kind is "document" or "video" or "animation" &&
                             mEl.TryGetProperty("file_name", out var fNameEl) && fNameEl.ValueKind == JsonValueKind.String)
                        post.MediaName = fNameEl.GetString() ?? "";
                    break;
                }

                post.HasOtherMedia = postEl.TryGetProperty("video", out _) || postEl.TryGetProperty("document", out _) ||
                                     postEl.TryGetProperty("animation", out _) || postEl.TryGetProperty("audio", out _) ||
                                     postEl.TryGetProperty("sticker", out _) || postEl.TryGetProperty("voice", out _) ||
                                     postEl.TryGetProperty("video_note", out _);

                if (postEl.TryGetProperty("media_group_id", out var mg) && mg.ValueKind == JsonValueKind.String)
                    post.MediaGroupId = mg.GetString();

                result.Add(post);
            }
            return result;
        }

        private static List<TgMirrorEntity> ParseEntities(JsonElement arr)
        {
            var list = new List<TgMirrorEntity>();
            foreach (var e in arr.EnumerateArray())
            {
                var entity = new TgMirrorEntity();
                if (e.TryGetProperty("type", out var t)) entity.Type = t.GetString() ?? "";
                if (e.TryGetProperty("offset", out var off)) entity.Offset = off.GetInt32();
                if (e.TryGetProperty("length", out var len)) entity.Length = len.GetInt32();
                if (e.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                    entity.Url = url.GetString();
                list.Add(entity);
            }
            return list;
        }

        /// <summary>Самый крупный размер фото из массива sizes Telegram → file_id.</summary>
        public static string? ExtractLargestPhotoFileId(JsonElement photoArray)
        {
            string? best = null;
            long bestSize = -1;
            foreach (var p in photoArray.EnumerateArray())
            {
                long size = 0;
                if (p.TryGetProperty("file_size", out var fs) && fs.ValueKind == JsonValueKind.Number)
                {
                    try { size = fs.GetInt64(); } catch { size = 0; }
                }
                if (best == null || size >= bestSize)
                {
                    bestSize = size;
                    if (p.TryGetProperty("file_id", out var fid) && fid.ValueKind == JsonValueKind.String)
                        best = fid.GetString();
                }
            }
            return best;
        }

        private async Task<string?> GetUpdatesAsync(string token, long offset, CancellationToken ct)
        {
            var args = new Dictionary<string, string>
            {
                ["offset"] = offset.ToString(),
                ["timeout"] = TelegramLongPollTimeoutSeconds.ToString(),
                // Правки постов приходят отдельным типом апдейта edited_channel_post —
                // без него в подписке EditMirrorAsync недостижим.
                ["allowed_updates"] = "[\"channel_post\",\"edited_channel_post\"]",
            };
            var json = await PostTelegramAsync(token, "getUpdates", args, ct).ConfigureAwait(false);
            if (json == null) return null;

            // 409: другой процесс уже делает getUpdates на этом токене — не спамим.
            if (json.Contains("\"error_code\":409"))
            {
                BotLogger.Info(LogCategory.System, "[tg_mirror] getUpdates: 409 Conflict (кто-то ещё читает этот токен) — ждём.");
                await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                return null;
            }
            // 429: уважаем retry_after.
            if (json.Contains("\"retry_after\""))
            {
                var (_, retry) = TryGetRetryAfter(json);
                await Task.Delay(TimeSpan.FromSeconds(retry + 1), ct).ConfigureAwait(false);
                return null;
            }
            return json;
        }

        private static (bool Ok, int Retry) TryGetRetryAfter(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("parameters", out var p) &&
                    p.TryGetProperty("retry_after", out var r)) return (true, r.GetInt32());
            }
            catch { }
            return (false, 5);
        }

        /// <summary>POST к Bot API с form-url-encoded параметрами; возвращает json или null.</summary>
        private async Task<string?> PostTelegramAsync(string token, string method, Dictionary<string, string> args, CancellationToken ct)
        {
            var url = $"https://api.telegram.org/bot{token}/{method}";
            using var content = new FormUrlEncodedContent(args);
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        private async Task<string?> GetFileUrlAsync(string token, string fileId)
        {
            try
            {
                var json = await PostTelegramAsync(token, "getFile", new Dictionary<string, string> { ["file_id"] = fileId }, CancellationToken.None).ConfigureAwait(false);
                if (json == null) return null;
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("file_path", out var pathEl))
                {
                    var path = pathEl.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                        return $"https://api.telegram.org/file/bot{token}/{path}";
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.System, $"[tg_mirror] getFile не удался: {ex.Message}");
            }
            return null;
        }

        /// <summary>Читаем сохранённые offset-ы (по хэшу токена, чтобы не хранить секреты).</summary>
        private void LoadOffsets()
        {
            try
            {
                if (!File.Exists(_offsetsPath)) return;
                var json = File.ReadAllText(_offsetsPath);
                if (string.IsNullOrWhiteSpace(json)) return;
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("offsets", out var offs) || offs.ValueKind != JsonValueKind.Object) return;
                foreach (var prop in offs.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                        _offsetsByToken[prop.Name] = prop.Value.GetInt64();
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.System, $"[tg_mirror] ошибка чтения offsets: {ex.Message}");
            }
        }

        private void SaveOffsets()
        {
            try
            {
                var dir = Path.GetDirectoryName(_offsetsPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var items = _offsetsByToken.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"    \"{kv.Key}\": {kv.Value}");
                var content = "{\n  \"offsets\": {\n" + string.Join(",\n", items) + "\n  }\n}";
                File.WriteAllText(_offsetsPath, content);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.System, $"[tg_mirror] ошибка записи offsets: {ex.Message}");
            }
        }

        private void LoadLinks()
        {
            try
            {
                var path = LinksPath;
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return;
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("links", out var root) || root.ValueKind != JsonValueKind.Object) return;
                foreach (var tok in root.EnumerateObject())
                {
                    if (tok.Value.ValueKind != JsonValueKind.Object) continue;
                    var byChat = new Dictionary<long, Dictionary<long, DiscordPostRef>>();
                    foreach (var chat in tok.Value.EnumerateObject())
                    {
                        if (!long.TryParse(chat.Name, out var chatId) || chat.Value.ValueKind != JsonValueKind.Object) continue;
                        var byMsg = new Dictionary<long, DiscordPostRef>();
                        foreach (var msg in chat.Value.EnumerateObject())
                        {
                            if (!long.TryParse(msg.Name, out var tgMsgId) || msg.Value.ValueKind != JsonValueKind.Object) continue;
                            if (!msg.Value.TryGetProperty("c", out var cEl) ||
                                !msg.Value.TryGetProperty("m", out var mEl) ||
                                cEl.ValueKind != JsonValueKind.Number || mEl.ValueKind != JsonValueKind.Number) continue;
                            try
                            {
                                string? fn = msg.Value.TryGetProperty("f", out var fEl) && fEl.ValueKind == JsonValueKind.String
                                    ? fEl.GetString()
                                    : null;
                                byMsg[tgMsgId] = new DiscordPostRef(cEl.GetUInt64(), mEl.GetUInt64(), fn);
                            }
                            catch (FormatException) { /* мусорные значения пропускаем */ }
                        }
                        byChat[chatId] = byMsg;
                    }
                    _linksByToken[tok.Name] = byChat;
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.System, $"[tg_mirror] ошибка чтения links: {ex.Message}");
            }
        }

        private void SaveLinks()
        {
            try
            {
                var path = LinksPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.Append("{\n  \"links\": {\n");
                var firstToken = true;
                foreach (var tok in _linksByToken.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    // Cap: не даём файлу расти бесконечно — старейшие посты выпадают.
                    foreach (var kv in tok.Value)
                    {
                        if (kv.Value.Count <= LinksPerTokenCap) continue;
                        var toRemove = kv.Value.Keys.OrderBy(x => x).Take(kv.Value.Count - LinksPerTokenCap).ToList();
                        foreach (var id in toRemove) kv.Value.Remove(id);
                    }
                    var byMsgs = new List<string>();
                    foreach (var chatKv in tok.Value.OrderBy(kv => kv.Key))
                    {
                        var msgItems = chatKv.Value.OrderBy(kv => kv.Key)
                            .Select(kv =>
                            {
                                var fn = kv.Value.FileName != null
                                    ? ", \"f\": \"" + kv.Value.FileName.Replace("\\", "\\\\").Replace("\"", "\"\"") + "\""
                                    : "";
                                return $"        \"{kv.Key}\": {{ \"c\": {kv.Value.ChannelId}, \"m\": {kv.Value.MessageId}{fn} }}";
                            });
                        if (msgItems.Any())
                            byMsgs.Add($"      \"{chatKv.Key}\": {{\n" + string.Join(",\n", msgItems) + "\n      }");
                    }
                    if (!byMsgs.Any())
                    {
                        _linksByToken.Remove(tok.Key); // пустой токен не сериализуем
                        continue;
                    }
                    if (!firstToken) sb.Append(",\n");
                    sb.Append("    \"" + tok.Key + "\": {\n" + string.Join(",\n", byMsgs) + "\n    }");
                    firstToken = false;
                }
                sb.Append("\n  }\n}");
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.System, $"[tg_mirror] ошибка записи links: {ex.Message}");
            }
        }

        /// <summary>Короткий SHA-256-префикс токена для ключей offsets.</summary>
        public static string TokenHash(string token)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(token));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString(0, 24);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsHttp)
            {
                try { _http.Dispose(); } catch { }
            }
        }
    }
}
