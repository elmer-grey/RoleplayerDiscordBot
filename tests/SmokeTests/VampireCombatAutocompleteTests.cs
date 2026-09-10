using System.Linq;
using RPBot.SlashModules;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="VampireRollAutocompleteHandler"/> и поисковых хелперов
/// <see cref="VampireManeuverCatalog"/>, к которым подключается автокомплит
/// справочных команд <c>/vampire_maneuver</c>, <c>/vampire_weapon</c>,
/// <c>/vampire_armor</c>.
/// </summary>
public class VampireCombatAutocompleteTests
{
    // ─── /vampire_maneuver ────────────────────────────────────────────────

    [Fact]
    public void Suggest_ManeuverName_NoInput_ReturnsCandidates()
    {
        // Без ввода пользователя — подсказки всё равно есть (Discord
        // показывает первые 25), все они содержат русское название манёвра
        // и в value идёт slug.
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_maneuver", "name", "");

        Assert.NotEmpty(results);
        // Первая подсказка — манёвр из каталога (label = русское, value = slug).
        Assert.Contains(results, r => r.Value == "укус" && r.Name == "Укус");
        Assert.Contains(results, r => r.Value == "клинч" && r.Name == "Клинч");
    }

    [Fact]
    public void Suggest_ManeuverName_PrefixMatchesByLabel()
    {
        // Ввод «Ук» — должны найтись «Укус» и «Уклонение» (обе начинаются на «Ук»).
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_maneuver", "name", "Ук");
        Assert.Contains(results, r => r.Value == "укус" && r.Name == "Укус");
        Assert.Contains(results, r => r.Value == "уклонение" && r.Name == "Уклонение");
    }

    [Fact]
    public void Suggest_ManeuverName_PrefixMatchesBySlug()
    {
        // Ввод «ук» — должен найти «Укус» (по slug).
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_maneuver", "name", "ук");
        Assert.Contains(results, r => r.Value == "укус" && r.Name == "Укус");
    }

    // ─── /vampire_weapon ──────────────────────────────────────────────────

    [Fact]
    public void Suggest_WeaponName_NoInput_ReturnsCandidates()
    {
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_weapon", "name", "");
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Value == "нож" && r.Name == "Нож");
        Assert.Contains(results, r => r.Value == "винтовка" && r.Name.StartsWith("Винтовка"));
    }

    [Fact]
    public void Suggest_WeaponName_PrefixMatchesByLabel()
    {
        // «Дроб» — найти «Дробовик (Remington 870, 12-й калибр)».
        // «Самозарядный дробовик» НЕ должен попасть, потому что его label
        // начинается с «С», не с «Д» (для него нужен другой префикс).
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_weapon", "name", "Дроб");
        Assert.Contains(results, r => r.Value == "дробовик");
        Assert.DoesNotContain(results, r => r.Value == "саморез_дробовик");
    }

    [Fact]
    public void Suggest_WeaponName_SamoreзДробовик_FoundByCorrectPrefix()
    {
        // Для самозарядного дробовика ищем по «Сам» (label «Самозарядный дробовик…»).
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_weapon", "name", "Сам");
        Assert.Contains(results, r => r.Value == "саморез_дробовик");
    }

    // ─── /vampire_armor ───────────────────────────────────────────────────

    [Fact]
    public void Suggest_ArmorName_NoInput_ReturnsCandidates()
    {
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_armor", "name", "");
        Assert.NotEmpty(results);
        // 5 классов брони, отсортированных по label.
        Assert.Equal(5, results.Count);
        Assert.Contains(results, r => r.Value == "класс_3" && r.Name.Contains("III"));
    }

    [Fact]
    public void Suggest_ArmorName_PrefixMatchesByLabel()
    {
        // «Класс» — все 5 классов.
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_armor", "name", "Класс");
        Assert.Equal(5, results.Count);
    }

    // ─── /vampire_damage (регрессия — поведение не сломалось) ──────────────

    [Fact]
    public void Suggest_DamageManeuver_StillReturnsManeuvers()
    {
        // /vampire_damage опция «манёвр» работала и до этого фикса — теперь
        // её подсказки приведены к (label, value) модели, проверим, что
        // возврат непустой и значения — те же slug'и.
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_damage", "манёвр", "");
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Value == "укус");
    }

    [Fact]
    public void Suggest_DamageWeapon_StillReturnsWeapons()
    {
        var results = VampireRollAutocompleteHandler.Suggest(
            "vampire_damage", "оружие", "");
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Value == "нож");
    }

    // ─── Find* в каталоге (фоллбэк по русскому имени) ────────────────────

    [Fact]
    public void FindMelee_AcceptsBothKeyAndRussianName()
    {
        // И по slug'у…
        Assert.NotNull(VampireManeuverCatalog.FindMelee("укус"));
        // …и по русскому названию из автокомплита.
        var byName = VampireManeuverCatalog.FindMelee("Укус");
        Assert.NotNull(byName);
        Assert.Equal("укус", byName!.Key);
    }

    [Fact]
    public void FindWeapon_AcceptsBothKeyAndRussianName()
    {
        Assert.NotNull(VampireManeuverCatalog.FindWeapon("нож"));
        var byName = VampireManeuverCatalog.FindWeapon("Нож");
        Assert.NotNull(byName);
        Assert.Equal("нож", byName!.Key);
    }

    [Fact]
    public void FindArmor_AcceptsBothKeyAndRussianName()
    {
        Assert.NotNull(VampireManeuverCatalog.FindArmor("класс_3"));
        var byName = VampireManeuverCatalog.FindArmor("Класс III — лёгкий бронежилет");
        Assert.NotNull(byName);
        Assert.Equal("класс_3", byName!.Key);
    }

    [Fact]
    public void FindMelee_UnknownInput_ReturnsNull()
    {
        Assert.Null(VampireManeuverCatalog.FindMelee("мегаукус"));
        Assert.Null(VampireManeuverCatalog.FindMelee(""));
        Assert.Null(VampireManeuverCatalog.FindMelee(null!));
    }

    [Fact]
    public void FindWeapon_UnknownInput_ReturnsNull()
    {
        Assert.Null(VampireManeuverCatalog.FindWeapon("бластер"));
        Assert.Null(VampireManeuverCatalog.FindWeapon(""));
    }

    [Fact]
    public void FindArmor_UnknownInput_ReturnsNull()
    {
        Assert.Null(VampireManeuverCatalog.FindArmor("класс_99"));
        Assert.Null(VampireManeuverCatalog.FindArmor(""));
    }

    // ─── Сохранена семантика существующих вызовов ─────────────────────────

    [Fact]
    public void FindMelee_KeyExact_StillReturns()
    {
        // Регрессия: после добавления фоллбэка старый путь по точному Key
        // продолжает работать.
        var m = VampireManeuverCatalog.FindMelee("клинч");
        Assert.NotNull(m);
        Assert.Equal("Клинч", m!.Name);
    }
}
