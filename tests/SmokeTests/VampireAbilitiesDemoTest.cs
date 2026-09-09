using System;
using System.IO;
using System.Linq;
using System.Text;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Демо-тест: показывает как сейчас работает Шаг 3 «Способности 13/9/5»
/// end-to-end (без Discord). Печатает все промежуточные состояния в stdout —
/// они появятся в логе dotnet test при verbose-выводе. Параллельно пишет
/// UTF-8 (без BOM) копию в файл `demo_report.txt` в корне репо — чтобы
/// просматривать результат в любом редакторе без проблем с кодировкой
/// терминала.
/// </summary>
public class VampireAbilitiesDemoTest : IDisposable
{
    private static readonly string ReportPath = Path.Combine(
        AppContext.BaseDirectory, "demo_report.txt");

    private static StreamWriter? _reportWriter;
    private static readonly object _lock = new();

    private static void Say(string line)
    {
        Console.WriteLine(line);
        try
        {
            lock (_lock)
            {
                if (_reportWriter == null)
                {
                    _reportWriter = new StreamWriter(
                        new FileStream(ReportPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false))
                    { AutoFlush = true };
                }
                _reportWriter.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[demo-report error] {ex.Message}");
        }
    }

    private static void SayRaw(string text)
    {
        Console.Write(text);
        try
        {
            lock (_lock)
            {
                if (_reportWriter == null)
                {
                    _reportWriter = new StreamWriter(
                        new FileStream(ReportPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false))
                    { AutoFlush = true };
                }
                _reportWriter.Write(text);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[demo-report error] {ex.Message}");
        }
    }

    public void Dispose()
    {
        // ничего — writer глобальный и живёт до конца процесса
    }

    [Fact]
    public void Demo_FullWalkthrough()
    {
        // 1) Игрок ввёл /vampire_create и прошёл Шаг 1.
        var draft = new VampireCharacter
        {
            CharacterId = Guid.NewGuid(),
            PlayerId = 111,
            PlayerName = "Elmer",
            Clan = "Бруха",
            Generation = 13,
            Hunger = 1,
            Concept = "Одиночка, ищущий искупления",
            Nature = "Судья",
            Demeanor = "Конформист",
        };
        Say("=== После Шага 1 (концепция) ===");
        Say($"Клан={draft.Clan}, Поколение={draft.Generation}, Голод={draft.Hunger}");
        Say($"Концепт: «{draft.Concept}»");
        Say($"Натура={draft.Nature}, Маска={draft.Demeanor}");
        Say("");

        // 2) Шаг 2 завершён.
        draft.AttributesStruct = new VampireAttributes
        {
            Strength = 7, Dexterity = 5, Stamina = 3,
            Charisma = 5, Manipulation = 3, Appearance = 3,
            Perception = 5, Intelligence = 3, Wits = 5
        };
        draft.AttributesPriority = "PhysicalPrimary";
        Say("=== После Шага 2 (характеристики 7/5/3) ===");
        Say($"Приоритет: {draft.AttributesPriority}");
        Say("");

        // 3) Шаг 3 старт.
        Say("=== Шаг 3 старт (игрок видит это в DM) ===");
        Say("");
        Say(VampireAbilitiesResolver.BuildAbilitiesStatusMessage(draft));
        Say("");
        DumpComponents(draft, "Шаг 3 — пустой (до выбора приоритета)");

        // 4) Игрок выбирает приоритет TalentsPrimary.
        var sel = VampireAbilitiesResolver.SetPriority(draft, VampireAbilityPriority.TalentsPrimary);
        Assert.True(sel.IsSuccess, sel.Message);
        Say("=== После выбора приоритета TalentsPrimary ===");
        Say("");
        Say(VampireAbilitiesResolver.BuildAbilitiesStatusMessage(draft));
        Say("");
        DumpComponents(draft, "Шаг 3 — приоритет выбран");

        // 5) Игрок поднимает несколько способностей.
        Bump(draft, "Атлетика", 2);
        Bump(draft, "Бдительность", 3);
        Bump(draft, "Драка", 1);
        Bump(draft, "Скрытность", 1);
        Bump(draft, "Фехтование", 1);
        Bump(draft, "Ремесло", 1);
        Bump(draft, "Расследование", 1);
        Bump(draft, "Медицина", 1);
        Say("=== После распределения 11 пунктов ===");
        Say("");
        Say(VampireAbilitiesResolver.BuildAbilitiesStatusMessage(draft));
        Say("");
        DumpComponents(draft, "Шаг 3 — частично распределены");

        // 6) Специализации. По V20 стр. 101 — только при значении ≥ 4.
        // Сначала проверим, что попытка задать спец-цию при < 4 отвергается.
        var reject = VampireAbilitiesResolver.SetSpecialization(draft, "Атлетика", "бег");
        Say("=== Попытка задать специализацию при значении < 4 (должно быть отвергнуто) ===");
        Say($"  Failure: {reject.Failure}");
        Say($"  Message: {reject.Message}");
        Say("");

        // Атлетика=2, Драка=1, Расследование=1 — тоже < 4.
        var reject2 = VampireAbilitiesResolver.SetSpecialization(draft, "Драка", "клинки");
        Say($"  Также отвергнуто Драка=клинки (Драка=1): Failure={reject2.Failure}");
        Say("");

        // Специализация способностей по V20 стр. 101 формально применяется
        // уже после Шага 5, где свободными пунктами поднимают способности
        // выше 3. На Шаге 3 максимум = 3, поэтому имитируем пост-Шаг-5
        // прямой записью в struct и пометкой «все freebie распределены».
        VampireFinishingResolver.MarkFreebiesExhausted(draft);
        draft.AbilitiesStruct.Бдительность = 4;
        var setSpec = VampireAbilitiesResolver.SetSpecialization(draft, "Бдительность", "эмпатия");
        Assert.True(setSpec.IsSuccess, setSpec.Message);
        Say($"  Бдительность поднята до {draft.AbilitiesStruct.Бдительность}, " +
            $"задана специализация: {setSpec.Message}");
        Say("");

        draft.AbilitiesStruct.Атлетика = 4;
        var setSpec2 = VampireAbilitiesResolver.SetSpecialization(draft, "Атлетика", "бег");
        Assert.True(setSpec2.IsSuccess, setSpec2.Message);
        Say($"  Атлетика поднята до {draft.AbilitiesStruct.Атлетика}, " +
            $"задана специализация: {setSpec2.Message}");
        Say("");

        Say("=== После задания специализаций ===");
        Say("");
        Say(VampireAbilitiesResolver.BuildAbilitiesStatusMessage(draft));
        Say("");
        DumpComponents(draft, "Шаг 3 — со специализациями");

        // 7) Демо ошибки.
        var tooHigh = VampireAbilitiesResolver.Increment(draft, "Бдительность");
        Assert.False(tooHigh.IsSuccess);
        Say("=== Демо отказа: попытка поднять Бдительность выше 3 ===");
        Say($"  Failure: {tooHigh.Failure}");
        Say($"  Message: {tooHigh.Message}");
        Say("");

        // 8) −1.
        var dec = VampireAbilitiesResolver.Decrement(draft, "Ремесло");
        Assert.True(dec.IsSuccess);
        Say("=== После −1 Ремесло ===");
        Say("");
        Say(VampireAbilitiesResolver.BuildAbilitiesStatusMessage(draft));
        Say("");

        // 9) ResetProgress (спец-ции характеристик сохраняются).
        draft.Specializations["Сила"] = "кулак";
        VampireAbilitiesResolver.ResetProgress(draft);
        Say("=== После ResetProgress (Шаг 3, приоритет сохранён) ===");
        Say($"  AbilitiesPriority = «{draft.AbilitiesPriority}»");
        Say($"  Атлетика = {draft.AbilitiesStruct.Атлетика}");
        Say($"  Спец-ция «Сила» (атрибут) сохранена: «{draft.Specializations.GetValueOrDefault("Сила")}»");
        Say($"  Спец-ция «Атлетика» очищена: contains = {draft.Specializations.ContainsKey("Атлетика")}");
        Say("");

        // 10) ResetAll.
        VampireAbilitiesResolver.SetPriority(draft, VampireAbilityPriority.SkillsPrimary);
        VampireAbilitiesResolver.Increment(draft, "Вождение");
        VampireAbilitiesResolver.ResetAll(draft);
        Say("=== После ResetAll ===");
        Say($"  AbilitiesPriority = «{draft.AbilitiesPriority}»");
        Say($"  Вождение = {draft.AbilitiesStruct.Вождение} (сброшено)");
        Say("");

        // 11) Полное завершение для примера кнопки «Далее → Шаг 4».
        FillAllAbilities(draft);
        Say("=== Шаг 3 полностью завершён (27 пунктов) ===");
        Say("");
        Say(VampireAbilitiesResolver.BuildAbilitiesStatusMessage(draft));
        Say("");
        DumpComponents(draft, "Шаг 3 — завершён, доступна кнопка «Далее → Шаг 4»");
        FlushReport();
        Say($"=== Отчёт записан в: {Path.GetFullPath(ReportPath)} ===");
    }

    private static void FlushReport()
    {
        lock (_lock)
        {
            _reportWriter?.Flush();
        }
    }

    private void Bump(VampireCharacter d, string name, int times)
    {
        for (int i = 0; i < times; i++) VampireAbilitiesResolver.Increment(d, name);
    }

    private void DumpComponents(VampireCharacter draft, string caption)
    {
        Say($"---- UI ({caption}) ----");
        var c = VampireWizardComponents.BuildForAbilitiesStep(draft);
        var rows = c.Components.OfType<Discord.ActionRowComponent>().ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            SayRaw($"  ряд {i + 1}: ");
            foreach (var cmp in row.Components)
            {
                            string idStr;
                            if (cmp is Discord.SelectMenuComponent sm0)
                            {
                                idStr = sm0.CustomId ?? "";
                                if (idStr.Length > 38) idStr = idStr.Substring(0, 37) + "…";
                                var placeholder = (sm0.Placeholder?.ToString() ?? "").Replace("\n", " ");
                                if (placeholder.Length > 32) placeholder = placeholder.Substring(0, 31) + "…";
                                SayRaw($"[SelectMenu «{placeholder}», id={idStr}, opts={sm0.Options.Count}] ");
                            }
                            else if (cmp is Discord.ButtonComponent btn0)
                            {
                                idStr = btn0.CustomId ?? "";
                                if (idStr.Length > 38) idStr = idStr.Substring(0, 37) + "…";
                                SayRaw($"[Button «{btn0.Label}» ({btn0.Style}), id={idStr}] ");
                            }
                        }
                        Say("");
        }
        Say($"  всего рядов: {rows.Count}");
        Say("");
    }

    /// <summary>
    /// Заполнить способности по приоритету KnowledgesSecondary (Знания=13, остальные 9/5).
    /// В SumulateCycle резолвер сам остановится при превышении бюджета.
    /// </summary>
    private static void FillAllAbilities(VampireCharacter draft)
    {
        VampireAbilitiesResolver.SetPriority(draft, VampireAbilityPriority.KnowledgesSecondary);
        foreach (var name in VampireAbilitiesCatalog.Talents)
            while (VampireAbilitiesResolver.Increment(draft, name).IsSuccess) { }
        foreach (var name in VampireAbilitiesCatalog.Skills)
            while (VampireAbilitiesResolver.Increment(draft, name).IsSuccess) { }
        foreach (var name in VampireAbilitiesCatalog.Knowledges)
            while (VampireAbilitiesResolver.Increment(draft, name).IsSuccess) { }
    }
}
