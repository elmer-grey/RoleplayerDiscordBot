using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Плейсхолдеры в welcome_message ({mention}/{server}/{user}) — настройка
/// приветствия через /settings. Регрессия: ранее текст был только зашитым,
/// поле WelcomeMessage в UserJoined вообще не читалось.
/// </summary>
public class WelcomeMessagePlaceholderTests
{
    private static string Apply(string text, string mention, string server, string user) =>
        (string?)typeof(Program).GetMethod("ApplyWelcomePlaceholders",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public)!
            .Invoke(null, new object[] { text, mention, server, user }) ?? "";

    [Fact]
    public void Placeholders_AreSubstituted()
    {
        var result = Apply(
            "{mention}, привет на {server}! Твой ник: {user}.",
            "@новичок", "Костёр на распутье", "Elmer");

        Assert.Equal("@новичок, привет на Костёр на распутье! Твой ник: Elmer.", result);
    }

    [Fact]
    public void LegacyPlaceholders_AreSubstituted()
    {
        // Старые конфиги welcome_message с плейсхолдерами {user.Mention}/{guild.Name}
        // годами не подставлялись — теперь должны отработать как {mention}/{server}.
        var result = Apply(
            "{user.Mention}, добро пожаловать на {guild.Name}!", "@u", "S", "n");

        Assert.Equal("@u, добро пожаловать на S!", result);
    }

    [Fact]
    public void UnknownPlaceholders_AreKept()
    {
        // Незнакомые плейсхолдеры не ломают текст (не заменяются, не удаляются).
        var result = Apply(
            "{mention} / {unknown} / {SERVER}", "@u", "S", "n");

        Assert.Equal("@u / {unknown} / {SERVER}", result);
    }
}