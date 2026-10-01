using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Регрессионные тесты парсинга custom_id кнопок выбора лимита人数а
/// для временной голосовой комнаты. Только парсер — реальное создание
/// канала проверяется живым ботом.
/// </summary>
public class VoiceChannelLimitParserTests
{
    [Theory]
    [InlineData("voice_limit:2", 2)]
    [InlineData("voice_limit:3", 3)]
    [InlineData("voice_limit:4", 4)]
    [InlineData("voice_limit:5", 5)]
    [InlineData("voice_limit:6", 6)]
    [InlineData("voice_limit:7", 7)]
    public void TryParseLimit_Valid_ReturnsTrueWithLimit(string customId, int expected)
    {
        var ok = VoiceChannelCommands.TryParseLimit(customId, out var limit);
        Assert.True(ok);
        Assert.Equal(expected, limit);
    }

    [Theory]
    [InlineData("voice_limit:1", 1)]   // ниже MinLimit (2)
    [InlineData("voice_limit:8", 8)]   // выше MaxLimit (7)
    [InlineData("voice_limit:0", 0)]
    [InlineData("voice_limit:-3", -3)]
    [InlineData("voice_limit:abc", 0)]
    [InlineData("voice_limit:", 0)]
    [InlineData("music_pause", 0)]     // другой префикс
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void TryParseLimit_Invalid_ReturnsFalse(string? customId, int parsedLimit)
    {
        var ok = VoiceChannelCommands.TryParseLimit(customId!, out var limit);
        Assert.False(ok);
        // Нам важно лишь, что метод НЕ подтверждает валидность. Parsed-значение
        // (если int.TryParse его распарсил) может сохраниться в out — это намеренно,
        // чтобы можно было понять, ЧТО именно пришло в кнопке.
        Assert.Equal(parsedLimit, limit);
    }

    [Fact]
    public void MinAndMaxLimit_AreConsistentWithButtons()
    {
        Assert.True(VoiceChannelCommands.MinLimit >= 2);
        Assert.True(VoiceChannelCommands.MaxLimit <= 99); // Discord VoiceChannel user limit cap is 99
        Assert.True(VoiceChannelCommands.MinLimit < VoiceChannelCommands.MaxLimit);
    }

    [Fact]
    public void ButtonPrefix_IsVoiceLimitColon()
    {
        Assert.Equal("voice_limit:", VoiceChannelCommands.ButtonPrefix);
    }
}
