using System;
using System.Collections.Generic;
using System.Text.Json;
using Discord;
using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Зеркало Telegram→Discord: чистые функции разбора/форматирования.
/// ConvertToDiscord (entities→Markdown), BuildMirrorFooter/BuildPostLink,
/// ExtractLargestPhotoFileId, ParseChannelPosts (только channel_post).
/// </summary>
public class TelegramMirrorServiceTests
{
    private static List<TelegramMirrorService.TgMirrorEntity> E(string type, int offset, int length, string? url = null) =>
        new() { new TelegramMirrorService.TgMirrorEntity { Type = type, Offset = offset, Length = length, Url = url } };

    // --- ConvertToDiscord ---

    [Fact]
    public void ConvertToDiscord_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TelegramMirrorService.ConvertToDiscord(null, null));
        Assert.Equal(string.Empty, TelegramMirrorService.ConvertToDiscord("", null));
    }

    [Fact]
    public void ConvertToDiscord_NoEntities_ReturnsTextAsIs()
    {
        Assert.Equal("привет мир", TelegramMirrorService.ConvertToDiscord("привет мир", null));
        Assert.Equal("привет мир", TelegramMirrorService.ConvertToDiscord("привет мир", new List<TelegramMirrorService.TgMirrorEntity>()));
    }

    [Fact]
    public void ConvertToDiscord_Wrappers()
    {
        // bold "мир" в "привет мир"
        Assert.Equal("привет **мир**", TelegramMirrorService.ConvertToDiscord("привет мир", E("bold", 7, 3)));
        Assert.Equal("*наклон*", TelegramMirrorService.ConvertToDiscord("наклон", E("italic", 0, 6)));
        Assert.Equal("__подчёрк__", TelegramMirrorService.ConvertToDiscord("подчёрк", E("underline", 0, 7)));
        Assert.Equal("~~зачёрк~~", TelegramMirrorService.ConvertToDiscord("зачёрк", E("strikethrough", 0, 6)));
        Assert.Equal("`код`", TelegramMirrorService.ConvertToDiscord("код", E("code", 0, 3)));
        Assert.Equal("```sql блок```", TelegramMirrorService.ConvertToDiscord("sql блок", E("pre", 0, 8)));
        Assert.Equal("||спойлер||", TelegramMirrorService.ConvertToDiscord("спойлер", E("spoiler", 0, 7)));
    }

    [Fact]
    public void ConvertToDiscord_TextLink_UsesUrl()
    {
        Assert.Equal("[сайт](https://example.com)",
            TelegramMirrorService.ConvertToDiscord("сайт", E("text_link", 0, 4, "https://example.com")));
    }

    [Fact]
    public void ConvertToDiscord_UrlEntity()
    {
        // абсолютный URL — как есть
        Assert.Equal("https://t.me/x", TelegramMirrorService.ConvertToDiscord("https://t.me/x", E("url", 0, 14)));
        // не-URL — в <>
        Assert.Equal("<замечание>", TelegramMirrorService.ConvertToDiscord("замечание", E("url", 0, 9)));
    }

    [Fact]
    public void ConvertToDiscord_UnknownEntity_PassesTextThrough()
    {
        // hashtag/bot_command и пр. — текст без обёртки
        Assert.Equal("@admin", TelegramMirrorService.ConvertToDiscord("@admin", E("mention", 0, 6)));
    }

    [Fact]
    public void ConvertToDiscord_NestedBoldInsideItalic()
    {
        // "жирный курсив": italic поверх всей фразы, bold внутри
        var ents = new List<TelegramMirrorService.TgMirrorEntity>
        {
            new() { Type = "italic", Offset = 0, Length = 13 },
            new() { Type = "bold", Offset = 7, Length = 6 },
        };
        Assert.Equal("*жирный **курсив***", TelegramMirrorService.ConvertToDiscord("жирный курсив", ents));
    }

    [Fact]
    public void ConvertToDiscord_CyrillicOffsetsMatchUtf16()
    {
        // ТГ-entities offset в UTF-16-юнитах; для кириллицы (BMP) индексы = символы.
        Assert.Equal("Привет **мир**", TelegramMirrorService.ConvertToDiscord("Привет мир", E("bold", 7, 3)));
    }

    [Fact]
    public void ConvertToDiscord_PlainTextAroundEntities()
    {
        // "до " + bold"внутри" + " после"
        Assert.Equal("до **внутри** после", TelegramMirrorService.ConvertToDiscord("до внутри после", E("bold", 3, 6)));
    }

    // --- BuildMirrorFooter / BuildPostLink ---

    [Fact]
    public void BuildPostLink_TrimsTrailingSlash()
    {
        Assert.Equal("https://t.me/my_channel/42", TelegramMirrorService.BuildPostLink("https://t.me/my_channel///", 100, 42));
    }

    [Fact]
    public void BuildPostLink_EmptyBase_ReturnsNull()
    {
        Assert.Null(TelegramMirrorService.BuildPostLink(null, 100, 42));
        Assert.Null(TelegramMirrorService.BuildPostLink("", 100, 42));
        Assert.Null(TelegramMirrorService.BuildPostLink("   ", 100, 42));
    }

    [Fact]
    public void BuildMirrorFooter_WithAndWithoutLink()
    {
        var with = TelegramMirrorService.BuildMirrorFooter("https://t.me/ch", -100123, 77);
        Assert.Contains("Дублирование сообщения из группы Telegram", with);
        Assert.Contains("https://t.me/ch/77", with);
        Assert.StartsWith("\n\n— 🔁", with);

        var without = TelegramMirrorService.BuildMirrorFooter(null, -100123, 77);
        Assert.Contains("Дублирование сообщения из группы Telegram", without);
        Assert.DoesNotContain("http", without);
    }

    [Fact]
    public void BuildPostLink_PrivateInvite_BuildsInternalLink()
    {
        // Инвайт-ссылка приватного канала не комбинируется с messageId —
        // строится служебная ссылка t.me/c/<internal>/<msg>.
        Assert.Equal("https://t.me/c/3875990529/12",
            TelegramMirrorService.BuildPostLink("https://t.me/+2nyT6Ly0eZVhM2Iy", -1003875990529, 12));
    }

    [Fact]
    public void BuildPostLink_PrivateInvite_WithPositiveChatId_ReturnsNull()
    {
        Assert.Null(TelegramMirrorService.BuildPostLink("https://t.me/+hash", 12345, 3));
    }

    // --- BuildMirrorEmbed ---

    [Fact]
    public void BuildMirrorEmbed_HasTitleDescriptionFooterLink()
    {
        var posts = new List<TelegramMirrorService.TgChannelPost>
        {
            new() { MessageId = 42, ChatId = -1003875990529, Text = "Текст поста", DateUnix = 730000000 }
        };
        var e = TelegramMirrorService.BuildMirrorEmbed("https://t.me/+2nyT6Ly0eZVhM2Iy", -1003875990529, posts, null, mediaAttached: false);
        Assert.Equal("Новый пост в ТГ-канале!", e.Title);
        Assert.Equal("Текст поста", e.Description);
        Assert.Equal("🔁 Дублирование из Telegram", e.Footer?.Text);
        Assert.Equal("https://t.me/c/3875990529/42", e.Url);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(730000000), e.Timestamp);
        Assert.Null(e.Image);
    }

    [Fact]
    public void BuildMirrorEmbed_PhotoAttachment_UsesAttachmentUrl()
    {
        var posts = new List<TelegramMirrorService.TgChannelPost>
        {
            new() { MessageId = 5, ChatId = -100123, Text = "каптион", DateUnix = 730000000 }
        };
        var e = TelegramMirrorService.BuildMirrorEmbed(null, -100123, posts, "фото1.jpg", mediaAttached: false);
        Assert.NotNull(e.Image);
        Assert.Equal("attachment://фото1.jpg", e.Image?.Url);
        Assert.Null(e.Url);
    }

    [Fact]
    public void BuildMirrorEmbed_ExtraPhotosLeft_AddsTelegramNote()
    {
        var posts = new List<TelegramMirrorService.TgChannelPost>
        {
            new() { MessageId = 7, ChatId = -100123, Text = "альбом", DateUnix = 730000000 }
        };
        var e = TelegramMirrorService.BuildMirrorEmbed(null, -100123, posts, "фото1.jpg",
            mediaAttached: false, extraPhotosLeft: true);
        Assert.NotNull(e.Image);
        Assert.Equal("attachment://фото1.jpg", e.Image?.Url);
        Assert.Contains("Остальные изображения можно просмотреть в Telegram", e.Description);
    }

    [Fact]
    public void BuildMirrorEmbed_MediaNotAttached_NotesFailure()
    {
        var posts = new List<TelegramMirrorService.TgChannelPost>
        {
            new() { MessageId = 1, ChatId = -100123, Text = "", HasOtherMedia = true, MediaKind = "video", DateUnix = 730000000 }
        };
        var e = TelegramMirrorService.BuildMirrorEmbed(null, -100123, posts, null, mediaAttached: false);
        Assert.Contains("дополнительный материал остался в Telegram", e.Description);
        Assert.Equal("*(без текста)*" == e.Description, false);
    }

    // --- BuildModifyMessageJson ---

    [Fact]
    public void BuildModifyMessageJson_EncodesEmbedAndKeepsAttachments()
    {
        // Правка сообщения с фото идёт сырым REST PATCH: вложения передаются по id,
        // иначе attachment:// в embed не резолвится и картинка уезжает над embed'ом.
        var embed = new EmbedBuilder()
            .WithTitle("Новый пост в ТГ-канале!")
            .WithDescription("текст «кавычки»")
            .WithUrl("https://t.me/c/3875990529/42")
            .WithImageUrl("attachment://фото1.jpg")
            .WithFooter("🔁 Дублирование из Telegram")
            .WithTimestamp(DateTimeOffset.FromUnixTimeSeconds(730000000))
            .WithColor(new Color(0x2AABEE))
            .Build();

        var json = TelegramMirrorService.BuildModifyMessageJson(
            new[] { embed },
            new List<(ulong Id, string Filename)> { (1111, "фото1.jpg") });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("", root.GetProperty("content").GetString());
        var e = root.GetProperty("embeds")[0];
        Assert.Equal("Новый пост в ТГ-канале!", e.GetProperty("title").GetString());
        Assert.Equal("attachment://фото1.jpg", e.GetProperty("image").GetProperty("url").GetString());
        Assert.Equal("🔁 Дублирование из Telegram", e.GetProperty("footer").GetProperty("text").GetString());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(730000000),
            DateTimeOffset.Parse(e.GetProperty("timestamp").GetString()!));
        Assert.Equal(0x2AABEE, e.GetProperty("color").GetInt64());
        Assert.Equal(1111, root.GetProperty("attachments")[0].GetProperty("id").GetInt64());
        Assert.Equal("фото1.jpg", root.GetProperty("attachments")[0].GetProperty("filename").GetString());
    }

    [Fact]
    public void BuildModifyMessageJson_WithoutAttachments_OmitsArray()
    {
        var embed = new EmbedBuilder()
            .WithDescription("текст")
            .WithTimestamp(DateTimeOffset.FromUnixTimeSeconds(730000000))
            .WithColor(new Color(0x2AABEE))
            .Build();
        var json = TelegramMirrorService.BuildModifyMessageJson(new[] { embed }, null);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("attachments", out _));
    }

    // --- ExtractLargestPhotoFileId ---

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void ExtractLargestPhotoFileId_PicksByFileSize()
    {
        var arr = Json("[{\"file_id\":\"small\",\"file_size\":900},{\"file_id\":\"big\",\"file_size\":5000},{\"file_id\":\"mid\",\"file_size\":2000}]");
        Assert.Equal("big", TelegramMirrorService.ExtractLargestPhotoFileId(arr));
    }

    [Fact]
    public void ExtractLargestPhotoFileId_MissingFileSizes_FallsBackToLastElement()
    {
        // Без file_size size считается 0: побеждает последний элемент (>= лучший).
        var arr = Json("[{\"file_id\":\"a\"},{\"file_id\":\"b\"}]");
        Assert.Equal("b", TelegramMirrorService.ExtractLargestPhotoFileId(arr));
    }

    [Fact]
    public void ExtractLargestPhotoFileId_EmptyArray_ReturnsNull()
    {
        var arr = Json("[]");
        Assert.Null(TelegramMirrorService.ExtractLargestPhotoFileId(arr));
    }

    // --- ParseChannelPosts ---

    [Fact]
    public void ParseChannelPosts_ParsesChannelPost()
    {
        var json = """
        {
          "result": [
            {
              "update_id": 11,
              "channel_post": {
                "message_id": 101,
                "chat": { "id": -100123 },
                "from": { "id": 555, "is_bot": false },
                "text": "Привет, **мир**",
                "entities": [ { "type": "bold", "offset": 8, "length": 5 } ]
              }
            }
          ]
        }
        """;
        var posts = TelegramMirrorService.ParseChannelPosts(json);
        var post = Assert.Single(posts);
        Assert.Equal(11, post.UpdateId);
        Assert.Equal(101, post.MessageId);
        Assert.Equal(-100123, post.ChatId);
        Assert.Equal(555, post.AuthorId);
        Assert.False(post.AuthorIsBot);
        Assert.Equal("Привет, **мир**", post.Text);
        Assert.NotNull(post.Entities);
        Assert.Equal("bold", Assert.Single(post.Entities!).Type);
        Assert.Null(post.PhotoFileId);
        Assert.False(post.HasOtherMedia);
        Assert.Null(post.MediaGroupId);
    }

    [Fact]
    public void ParseChannelPosts_IgnoresMessageAndParsesEdited()
    {
        var json = """
        {
          "result": [
            { "update_id": 1, "message": { "message_id": 5, "text": "не канал" } },
            { "update_id": 2, "edited_channel_post": { "message_id": 9, "chat": { "id": -100123 }, "text": "правка" } }
          ]
        }
        """;
        var posts = TelegramMirrorService.ParseChannelPosts(json);
        var post = Assert.Single(posts);
        Assert.Equal(2, post.UpdateId);
        Assert.Equal(9, post.MessageId);
        Assert.Equal("правка", post.Text);
        Assert.True(post.IsEdit);
    }

    [Fact]
    public void ParseChannelPosts_NewPost_IsNotEdit()
    {
        var json = """
        { "result": [ { "update_id": 5, "channel_post": { "message_id": 10, "chat": { "id": -100123 }, "text": "новый" } } ] }
        """;
        var post = Assert.Single(TelegramMirrorService.ParseChannelPosts(json));
        Assert.False(post.IsEdit);
    }

    [Fact]
    public void ParseChannelPosts_MixedNewAndEdit_BothParsed()
    {
        var json = """
        {
          "result": [
            { "update_id": 7, "channel_post": { "message_id": 20, "chat": { "id": -100123 }, "text": "a" } },
            { "update_id": 8, "edited_channel_post": { "message_id": 20, "chat": { "id": -100123 }, "text": "a2" } }
          ]
        }
        """;
        var posts = TelegramMirrorService.ParseChannelPosts(json);
        Assert.Equal(2, posts.Count);
        Assert.False(posts[0].IsEdit);
        Assert.True(posts[1].IsEdit);
        Assert.Equal("a2", posts[1].Text);
    }

    [Fact]
    public void ParseChannelPosts_CaptionPhotoAndOtherMedia()
    {
        var json = """
        {
          "result": [
            {
              "update_id": 3,
              "channel_post": {
                "message_id": 55,
                "chat": { "id": -100123 },
                "caption": "фото пост",
                "caption_entities": [],
                "photo": [ { "file_id": "small", "file_size": 10 }, { "file_id": "big", "file_size": 9999 } ],
                "media_group_id": "77"
              }
            },
            {
              "update_id": 4,
              "channel_post": { "message_id": 56, "chat": { "id": -100123 }, "video": {} }
            }
          ]
        }
        """;
        var posts = TelegramMirrorService.ParseChannelPosts(json);
        Assert.Equal(2, posts.Count);
        Assert.Equal("big", posts[0].PhotoFileId);
        Assert.Equal("фото пост", posts[0].Text);
        Assert.Equal("77", posts[0].MediaGroupId);
        Assert.Null(posts[1].PhotoFileId);
        Assert.True(posts[1].HasOtherMedia);
        Assert.False(posts[0].HasOtherMedia);
    }

    [Fact]
    public void ParseChannelPosts_EmptyOrInvalid_ReturnsEmpty()
    {
        Assert.Empty(TelegramMirrorService.ParseChannelPosts(""));
        Assert.Empty(TelegramMirrorService.ParseChannelPosts("   "));
        Assert.Empty(TelegramMirrorService.ParseChannelPosts("{}"));
        Assert.Empty(TelegramMirrorService.ParseChannelPosts("{\"result\":{}}"));
    }

    [Fact]
    public void ParseChannelPosts_MarksBotAuthor()
    {
        var json = """
        { "result": [ { "update_id": 9, "channel_post": { "message_id": 1, "chat": { "id": -100123 },
          "from": { "id": 42, "is_bot": true }, "text": "анонс" } } ] }
        """;
        var post = Assert.Single(TelegramMirrorService.ParseChannelPosts(json));
        Assert.True(post.AuthorIsBot);
        Assert.Equal(42, post.AuthorId);
    }
}
