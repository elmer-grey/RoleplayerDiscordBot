using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// PointsService: конкурентные SaveAsync обязаны проходить без FNFE —
/// уникальный tmp-файл на сейв и чистый каталог после сохранений.
/// Регрессия на гонку points.json.tmp (VPS: 322–327 ошибок в день).
/// </summary>
public class PointsServiceConcurrentSaveTests : IDisposable
{
    private readonly string _tmpDir;

    public PointsServiceConcurrentSaveTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_points_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public async Task SaveAsync_ConcurrentCalls_LeaveNoTmpFiles()
    {
        var path = Path.Combine(_tmpDir, "points.json");
        var pts = new PointsService(path);
        pts.Add(295189463376855040UL, 111UL, 400);

        var tasks = Enumerable.Range(0, 8).Select(_ => pts.SaveAsync()).ToArray();
        await Task.WhenAll(tasks);

        Assert.True(File.Exists(path), "points.json должен существовать после сейвов");
        var json = File.ReadAllText(path);
        Assert.Contains("295189463376855040", json);

        var leftovers = Directory.GetFiles(_tmpDir, "*.tmp");
        Assert.True(leftovers.Length == 0,
            "После конкурентных сейвов в каталоге не должно оставаться *.tmp: "
            + string.Join(", ", leftovers));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentWithLoadAsync_ResultIsValid()
    {
        var path = Path.Combine(_tmpDir, "points.json");
        var pts = new PointsService(path);
        pts.Add(1UL, 2UL, 7);

        var ops = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            ops.Add(pts.SaveAsync());
            ops.Add(pts.LoadAsync());
        }
        await Task.WhenAll(ops);

        Assert.True(File.Exists(path));
        // Баланс мог быть перечитан из файла в LoadAsync, но 7 с пользователя 2
        // обязано дожить до файла (Add идёт до первого SaveAsync).
        var reload = new PointsService(path);
        await reload.LoadAsync();
        Assert.Equal(7, reload.GetBalance(1UL, 2UL));
    }
}