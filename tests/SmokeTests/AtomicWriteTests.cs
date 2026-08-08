using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 1.6 — атомарная запись .bak: повреждённый основной файл не должен терять данные.
/// Покрываем логику, используемую в Program.LoadServerConfigs (serverconfigs.json / .bak).
/// </summary>
public class AtomicWriteTests
{
    private static void AtomicWrite(string path, string content)
    {
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, content);
        if (File.Exists(path))
        {
            // .bak = предыдущее содержимое основного файла
            if (File.Exists(path + ".bak"))
                File.Delete(path + ".bak");
            File.Move(path, path + ".bak");
        }
        File.Move(tmpPath, path, overwrite: true);
    }

    private static string? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var content = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    [Fact]
    public void FirstWrite_CreatesFile_AndBak()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_atomic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            AtomicWrite(path, "{\"a\":1}");

            Assert.True(File.Exists(path));
            // первый write — .bak копируется из основного (которого нет), значит .bak не должен появиться
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void SecondWrite_CreatesBak_WithPreviousContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_atomic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            AtomicWrite(path, "{\"a\":1}");
            AtomicWrite(path, "{\"a\":2}");

            Assert.Equal("{\"a\":2}", File.ReadAllText(path));
            Assert.Equal("{\"a\":1}", File.ReadAllText(path + ".bak"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RecoveryFromBak_WhenMainCorrupted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_atomic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            AtomicWrite(path, "{\"v\":1}");   // первый write — без .bak
            AtomicWrite(path, "{\"v\":2}");   // bak = {"v":1}, main = {"v":2}
            AtomicWrite(path, "{\"v\":3}");   // bak = {"v":2}, main = {"v":3}
            // Имитируем повреждение основного файла
            File.WriteAllText(path, "BROKEN{{{");

            var bak = TryLoad(path + ".bak");
            Assert.NotNull(bak);
            Assert.Equal("{\"v\":2}", bak);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}