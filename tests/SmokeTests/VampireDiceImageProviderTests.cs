using System;
using System.IO;
using System.Linq;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты на <see cref="VampireDiceImageProvider"/>: подцепляет PNG-кубики d10
/// из %LocalAppData%\RPBot\Data\Numbers\d10_regular\ и d10_hunger\.
/// </summary>
public class VampireDiceImageProviderTests : IDisposable
{
    private readonly string _tempDir;

    public VampireDiceImageProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RPBot.DiceImageProvider.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, VampireDiceImageProvider.RegularFolder));
        Directory.CreateDirectory(Path.Combine(_tempDir, VampireDiceImageProvider.HungerFolder));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    private void MakeFakePng(string folder, int value)
    {
        File.WriteAllBytes(Path.Combine(_tempDir, folder, $"{value}.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    [Fact]
    public void Build_EmptyDice_ReturnsEmpty()
    {
        var result = VampireDiceImageProvider.Build(
            Array.Empty<int>(), Array.Empty<int>(), _tempDir);

        Assert.False(result.HasAny);
        Assert.Empty(result.Files);
        Assert.Empty(result.MissingValues);
    }

    [Fact]
    public void Build_RegularOnly_ProducesCorrectCount()
    {
        MakeFakePng(VampireDiceImageProvider.RegularFolder, 1);
        MakeFakePng(VampireDiceImageProvider.RegularFolder, 6);
        MakeFakePng(VampireDiceImageProvider.RegularFolder, 10);

        var result = VampireDiceImageProvider.Build(
            new[] { 1, 6, 10 }, Array.Empty<int>(), _tempDir);

        Assert.Equal(3, result.Files.Count);
        Assert.Empty(result.MissingValues);
        Assert.All(result.Files, f => Assert.StartsWith("reg_", f.FileName));
    }

    [Fact]
    public void Build_RegularFirst_ThenHunger_PrefixedCorrectly()
    {
        MakeFakePng(VampireDiceImageProvider.RegularFolder, 7);
        MakeFakePng(VampireDiceImageProvider.HungerFolder, 3);
        MakeFakePng(VampireDiceImageProvider.HungerFolder, 10);

        var result = VampireDiceImageProvider.Build(
            regularDice: new[] { 7 },
            hungerDice: new[] { 3, 10 },
            numbersDir: _tempDir);

        Assert.Equal(3, result.Files.Count);
        // regular идёт первым.
        Assert.StartsWith("reg_1_7.png", result.Files[0].FileName);
        Assert.StartsWith("hun_1_3.png", result.Files[1].FileName);
        Assert.StartsWith("hun_2_10.png", result.Files[2].FileName);
    }

    [Fact]
    public void Build_MissingFile_AddsToMissingValues()
    {
        MakeFakePng(VampireDiceImageProvider.RegularFolder, 5);
        // 7 не создаём — должно попасть в missing.

        var result = VampireDiceImageProvider.Build(
            new[] { 5, 7 }, Array.Empty<int>(), _tempDir);

        Assert.Single(result.Files);
        Assert.Single(result.MissingValues);
        Assert.Equal(7, result.MissingValues[0]);
    }

    [Fact]
    public void Build_FolderMissing_AllValuesMissing()
    {
        // Не создаём hunger-папку. Удаляем её на всякий случай.
        var hungerDir = Path.Combine(_tempDir, VampireDiceImageProvider.HungerFolder);
        if (Directory.Exists(hungerDir)) Directory.Delete(hungerDir, recursive: true);

        var result = VampireDiceImageProvider.Build(
            regularDice: Array.Empty<int>(),
            hungerDice: new[] { 1, 2, 3 },
            numbersDir: _tempDir);

        Assert.Empty(result.Files);
        Assert.Equal(new[] { 1, 2, 3 }, result.MissingValues);
    }

    [Fact]
    public void Build_OutOfRangeValue_Skipped()
    {
        MakeFakePng(VampireDiceImageProvider.RegularFolder, 5);
        // 0 и 11 — невалидные значения для d10.

        var result = VampireDiceImageProvider.Build(
            new[] { 5, 0, 11 }, Array.Empty<int>(), _tempDir);

        Assert.Single(result.Files);
        Assert.Empty(result.MissingValues);
    }

    [Fact]
    public void Build_NumbersDirNull_UsesLocalAppData()
    {
        // Не указываем numbersDir — должен брать LocalAppData.
        // Если там действительно есть PNG (мы их сгенерировали ранее) — HasAny == true.
        // Иначе — пустой результат, но без падения.
        var ex = Record.Exception(() =>
        {
            var result = VampireDiceImageProvider.Build(new[] { 1 }, Array.Empty<int>());
            // Просто проверяем, что не бросило.
            _ = result.HasAny;
        });
        Assert.Null(ex);
    }
}
