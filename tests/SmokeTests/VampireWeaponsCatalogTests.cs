using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Покрывает файловые операции над словарём оружия VtM V20.
/// EnsureSeeded должен скопировать шаблон из embedded-ресурса при первом
/// запуске; повторный вызов не должен ломаться.
/// </summary>
[Collection("BotConfig")]
public class VampireWeaponsCatalogTests : IsolatedDataTestBase
{
    public VampireWeaponsCatalogTests() : base("vtm_weapons_catalog") { }

    [Fact]
    public void EnsureSeeded_CreatesFile_WhenMissing()
    {
        // Запускаем из настоящего пути — BotConfig.GetDataDirectory().
        // Если файл уже был от прошлого запуска — это нормально, EnsureSeeded
        // не перезаписывает, лишь возвращает true.
        var (ok, path) = VampireWeaponsCatalog.EnsureSeeded();
        Assert.True(ok, "EnsureSeeded должен вернуть ok=true при наличии ресурса.");
        Assert.True(File.Exists(path), $"Файл словаря должен существовать: {path}");
    }

    [Fact]
    public void Load_ReturnsNonEmptyWeapons()
    {
        VampireWeaponsCatalog.EnsureSeeded();
        var doc = VampireWeaponsCatalog.Load();
        Assert.NotEmpty(doc.Weapons);

        // Базовые ожидания V20 — минимум fists + огнестрел.
        var fists = System.Array.Find(doc.Weapons, w => w.Id == "fists");
        Assert.NotNull(fists);
        Assert.Equal("light", fists!.DamageType);
        Assert.True(fists.AddsStrength);

        var pistol = System.Array.Find(doc.Weapons, w => w.Id == "pistol_9mm");
        Assert.NotNull(pistol);
        Assert.Equal(3, pistol!.Damage);
        Assert.Equal("lethal", pistol.DamageType);
        Assert.False(pistol.AddsStrength, "Огнестрел не должен добавлять силу.");
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        VampireWeaponsCatalog.EnsureSeeded();
        Assert.NotNull(VampireWeaponsCatalog.Find("KNIFE"));
        Assert.NotNull(VampireWeaponsCatalog.Find("knife"));
        Assert.Null(VampireWeaponsCatalog.Find("nonexistent_weapon_xyz"));
    }
}
