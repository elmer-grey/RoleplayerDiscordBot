using System;
using System.IO;
using System.Linq;
using System.Text;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Демо-тест: показывает как сейчас работает Шаг 4 «Преимущества»
/// end-to-end (без Discord). Печатает все промежуточные состояния в stdout —
/// они появятся в логе dotnet test при verbose-выводе. Параллельно пишет
/// UTF-8 (без BOM) копию в файл `demo_report.txt` в каталоге теста —
/// чтобы просматривать результат в любом редакторе без проблем с кодировкой
/// терминала.
///
/// Сценарий: проходим Шаг 4 для клана Бруха (клaновые дисциплины) —>
/// 4.2 Факты —> 4.3 Добродетели —> Готово к Шагу 5.
/// Плюс отдельный мини-сценарий для Каитифа (свободный ввод имён).
/// </summary>
public class VampireAdvantagesDemoTest : IDisposable
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
    public void Demo_FullWalkthrough_Brujah()
    {
        // 1) Игрок завершил Шаги 1-3. Начинаем Шаг 4.
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
        Say("=== Старт Шага 4 (Бруха) ===");
        Say($"Клан={draft.Clan}, Поколение={draft.Generation}, Голод={draft.Hunger}");
        Say($"Каталог: дисциплины пул={VampireAdvantagesCatalog.DisciplinePool}, " +
            $"факты пул={VampireAdvantagesCatalog.BackgroundPool}, " +
            $"добродетели пул={VampireAdvantagesCatalog.VirtuePool}, " +
            $"кэп на поле={VampireAdvantagesCatalog.PerFieldCap}");
        Say("");

        // 2) Шаг 4.1 — Дисциплины.
        Say("=== Шаг 4.1 старт (игрок видит это в DM) ===");
        Say(VampireAdvantagesResolver.BuildDisciplinesStatusMessage(draft));
        Say("");
        DumpDisciplines(draft, "Шаг 4.1 — пустой (0/3)");

        // 3) Игрок поднимает Стойкость (Бруха имеют Быстроту, Стойкость, Могущество).
        var slots = VampireAdvantagesCatalog.GetDisciplineSlots(draft.Clan);
        Say($"Доступные клановые дисциплины: {string.Join(", ", slots)}");
        Say("");

        BumpDisc(draft, slots[0], 1);
        BumpDisc(draft, slots[1], 1);
        BumpDisc(draft, slots[2], 1);
        Say("=== После распределения по 1 в каждую клановую дисциплину ===");
        Say(VampireAdvantagesResolver.BuildDisciplinesStatusMessage(draft));
        Say("");
        DumpDisciplines(draft, "Шаг 4.1 — 1/1/1 (распределено 3/3)");

        // 4) Попытка превысить кэп.
        var cap = VampireAdvantagesResolver.IncrementDiscipline(draft, slots[0]);
        BumpDisc(draft, slots[0], 1); // 3
        BumpDisc(draft, slots[0], 1); // 4
        var over = VampireAdvantagesResolver.IncrementDiscipline(draft, slots[0]);
        Say("=== Попытка поднять первую дисциплину до 5 (потолок), потом ещё ===");
        Say($"  текущее «{slots[0]}»: {VampireAdvantagesResolver.GetDisciplineValue(draft, slots[0])}");
        Say($"  Failure={over.Failure}, Message={over.Message}");
        Say("");
        BumpDisc(draft, slots[0], 1); // довести до 5
        Say($"  сейчас: {VampireAdvantagesResolver.GetDisciplineValue(draft, slots[0])} (кэп {VampireAdvantagesCatalog.PerFieldCap})");
        Say("");
        Say(VampireAdvantagesResolver.BuildDisciplinesStatusMessage(draft));
        Say("");

        // 5) ResetProgress на Шаге 4.1.
        VampireAdvantagesResolver.ResetProgress(draft);
        Say("=== После ResetProgress (Шаг 4 — сброс всего) ===");
        Say($"  «{slots[0]}»={VampireAdvantagesResolver.GetDisciplineValue(draft, slots[0])} (сброшено)");
        Say($"  Spent={VampireAdvantagesResolver.TotalDisciplineSpent(draft)}");
        Say("");

        // 6) Заполним заново, но ассиметрично: 2/1/0 (потрачено 3).
        BumpDisc(draft, slots[0], 2);
        BumpDisc(draft, slots[1], 1);
        Say("=== Шаг 4.1 — 2/1/0 (потрачено 3/3) ===");
        Say(VampireAdvantagesResolver.BuildDisciplinesStatusMessage(draft));
        Say("");
        Say("=== Шаг 4.1 завершён ===");
        Say("");

        // 7) Шаг 4.2 — Факты биографии.
        Say("=== Шаг 4.2 старт ===");
        Say(VampireAdvantagesResolver.BuildBackgroundsStatusMessage(draft));
        Say("");

        VampireAdvantagesResolver.AddBackground(draft, "Соучастники");
        Say(VampireAdvantagesResolver.BuildBackgroundsStatusMessage(draft));
        Say("");
        DumpBackgrounds(draft, "Шаг 4.2 — после добавления 1 факта");

        // Повысим ранг
        VampireAdvantagesResolver.IncrementBackground(draft, "Соучастники");
        Say("=== После +1 ранг «Соучастники» ===");
        Say(VampireAdvantagesResolver.BuildBackgroundsStatusMessage(draft));
        Say("");

        // Добавим ещё пару фактов с разными рангами.
        VampireAdvantagesResolver.AddBackground(draft, "Ресурсы");
        VampireAdvantagesResolver.AddBackground(draft, "Контакты");
        Say("=== После добавления 3 фактов ===");
        Say(VampireAdvantagesResolver.BuildBackgroundsStatusMessage(draft));
        Say("");
        DumpBackgrounds(draft, "Шаг 4.2 — 3/2/1 (распределено 6… нет, поправим)");

        // Перераспределим: пул 5, ранги 3+1+1=5.
        VampireAdvantagesResolver.IncrementBackground(draft, "Соучастники"); // 3
        Say($"  После +1 к Соучастники: {VampireAdvantagesResolver.GetBackgroundRank(draft, "Соучастники")} (потрачено 5/5)");
        Say("");

        // Попытка добавить ещё один факт — должен быть отказ (пул исчерпан).
        var ex = VampireAdvantagesResolver.AddBackground(draft, "Влияние");
        Say("=== Попытка добавить ещё один факт (должно быть отвергнуто) ===");
        Say($"  Failure={ex.Failure}, Message={ex.Message}");
        Say("");

        // Переименование факта.
        var rn = VampireAdvantagesResolver.RenameBackground(draft, "Ресурсы", "Богатство");
        Say($"=== Переименование «Ресурсы» → «Богатство»: {rn.Message} ===");
        Say(VampireAdvantagesResolver.BuildBackgroundsStatusMessage(draft));
        Say("");

        // Попытка понизить ранг факта с 1 — должна быть ошибка.
        var rankDown = VampireAdvantagesResolver.DecrementBackground(draft, "Контакты");
        Say("=== Попытка понизить ранг факта «Контакты» с 1 ===");
        Say($"  Failure={rankDown.Failure}, Message={rankDown.Message}");
        Say("");

        // Удаление факта.
        var rm = VampireAdvantagesResolver.RemoveBackground(draft, "Контакты");
        Say($"=== Удаление «Контакты»: {rm.Message} ===");
        Say(VampireAdvantagesResolver.BuildBackgroundsStatusMessage(draft));
        Say("");
        DumpBackgrounds(draft, "Шаг 4.2 — после переименования и удаления");

        Say("=== Шаг 4.2 завершён ===");
        Say("");

        // 8) Шаг 4.3 — Добродетели.
        Say("=== Шаг 4.3 старт ===");
        Say(VampireAdvantagesResolver.BuildVirtuesStatusMessage(draft));
        Say("");
        DumpVirtues(draft, "Шаг 4.3 — пустой (1/1/1 база, пул 7)");

        // 4 попытки поднять Смелость с базы 1 → пройдут (до 5),
        // 5-я и 6-я попытки — AboveCap, вернут Failure.
        int spentCourage = 0;
        for (int i = 0; i < 6; i++)
        {
            var r = VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueCourage);
            Say($"  попытка {i + 1}: {(r.IsSuccess ? $"OK → {VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueCourage)} (потрачено {VampireAdvantagesResolver.TotalVirtueSpent(draft)})" : $"FAIL [{r.Failure}] {r.Message}")}");
            if (r.IsSuccess) spentCourage++;
        }
        Say($"=== После 6 попыток поднять Смелость (1→5 = 4 успешные, 2 отказа) ===");
        Say($"  Смелость={VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueCourage)}, потрачено на Смелость: {spentCourage}");
        Say("");

        // Оставшиеся 3 пункта из пула: в Самоконтроль (1→3) — потрачено 2.
        BumpVirtue(draft, VampireParameterCatalog.VirtueSelfControl, 2);
        Say($"=== После +2 в Самоконтроль (1→3), Совесть пока на базе 1 ===");
        Say(VampireAdvantagesResolver.BuildVirtuesStatusMessage(draft));
        Say("");
        // Последний 1 пункт из пула: в Совесть (1→2) — потрачено 1.
        var inC = VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience);
        Say($"=== Последний пункт — в Совесть (1→2): {inC.Message} ===");
        Say(VampireAdvantagesResolver.BuildVirtuesStatusMessage(draft));
        Say("");
        // Снова попытка — пул исчерпан.
        var exPool = VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience);
        Say($"=== Попытка поднять Совесть ещё (пул исчерпан) ===");
        Say($"  Failure={exPool.Failure}, Message={exPool.Message}");
        Say("");
        Say("=== Шаг 4.3 завершён (потрачено 7/7: 4 Смелость + 2 Самоконтроль + 1 Совесть) ===");
        Say("");

        // 9) Сводное состояние Шага 4.
        Say("=== Шаг 4 полностью завершён ===");
        Say($"  Дисциплины: {VampireAdvantagesResolver.IsDisciplinesComplete(draft)}");
        Say($"  Факты: {VampireAdvantagesResolver.IsBackgroundsComplete(draft)}");
        Say($"  Добродетели: {VampireAdvantagesResolver.IsVirtuesComplete(draft)}");
        Say("");
        Say("  Доступна кнопка «Далее → Шаг 5 (финал)».");
        Say("");

        // 10) Полный сброс через ResetAll.
        VampireAdvantagesResolver.ResetAll(draft);
        Say("=== После ResetAll ===");
        Say($"  Дисциплины: spent={VampireAdvantagesResolver.TotalDisciplineSpent(draft)}, complete={VampireAdvantagesResolver.IsDisciplinesComplete(draft)}");
        Say($"  Факты: spent={VampireAdvantagesResolver.TotalBackgroundSpent(draft)}, complete={VampireAdvantagesResolver.IsBackgroundsComplete(draft)}");
        Say($"  Добродетели: spent={VampireAdvantagesResolver.TotalVirtueSpent(draft)}, complete={VampireAdvantagesResolver.IsVirtuesComplete(draft)}");
        Say($"  Клан сохранён: «{draft.Clan}», Generation={draft.Generation}");
        Say("");

        // 11) Заполняем заново для красивого финала.
        BumpDisc(draft, slots[0], 1);
        BumpDisc(draft, slots[1], 1);
        BumpDisc(draft, slots[2], 1);
        VampireAdvantagesResolver.AddBackground(draft, "Соучастники");
        VampireAdvantagesResolver.AddBackground(draft, "Ресурсы");
        VampireAdvantagesResolver.AddBackground(draft, "Контакты");
        // Добродетели: 7 пунктов = Смелость 1→5 (4) + Самоконтроль 1→3 (2) + Совесть 1→2 (1).
        BumpVirtue(draft, VampireParameterCatalog.VirtueCourage, 4);
        BumpVirtue(draft, VampireParameterCatalog.VirtueSelfControl, 2);
        BumpVirtue(draft, VampireParameterCatalog.VirtueConscience, 1);

        Say("=== Шаг 4 финальное распределение (Бруха) ===");
        Say("");
        Say(VampireAdvantagesResolver.BuildDisciplinesStatusMessage(draft));
        Say("");
        Say(VampireAdvantagesResolver.BuildBackgroundsStatusMessage(draft));
        Say("");
        Say(VampireAdvantagesResolver.BuildVirtuesStatusMessage(draft));
        Say("");
        DumpDisciplines(draft, "Шаг 4 — финальный UI (4.1)");
        DumpBackgrounds(draft, "Шаг 4 — финальный UI (4.2)");
        DumpVirtues(draft, "Шаг 4 — финальный UI (4.3)");

        FlushReport();
        Say($"=== Отчёт записан в: {Path.GetFullPath(ReportPath)} ===");
    }

    [Fact]
    public void Demo_CaitiffDisciplines()
    {
        // Мини-сценарий для Каитифа: дисциплины — свободный ввод.
        var draft = new VampireCharacter
        {
            CharacterId = Guid.NewGuid(),
            PlayerId = 222,
            Clan = "Каитиф",
            Generation = 13,
        };
        Say("=== Каитиф — Шаг 4.1 (свободный ввод имён дисциплин) ===");
        Say(VampireAdvantagesResolver.BuildDisciplinesStatusMessage(draft));
        Say("");

        // По дефолту три слота «Дисциплина 1/2/3».
        var slots = VampireAdvantagesCatalog.GetDisciplineSlots(draft.Clan);
        Say($"  Слоты по умолчанию: {string.Join(", ", slots)}");
        Say("");

        // Переименуем слот 1 в «Анимализм» ДО распределения очков.
        var ren = VampireAdvantagesResolver.RenameCaitiffDiscipline(draft, "Дисциплина 1", "Анимализм");
        Say($"  Переименование пустого слота «Дисциплина 1» → «Анимализм»: {ren.Message}");
        Assert.True(ren.IsSuccess, $"Ожидался успех, а получили: {ren.Message}");
        Assert.Equal(0, VampireAdvantagesResolver.GetDisciplineValue(draft, "Анимализм"));
        Say($"  Значение «Анимализм» после переименования: {VampireAdvantagesResolver.GetDisciplineValue(draft, "Анимализм")} (создано с 0)");
        Say("");

        BumpDisc(draft, "Анимализм", 2);
        BumpDisc(draft, "Дисциплина 2", 1);
        Say("=== После распределения: Анимализм=2, второй=1 (потрачено 3/3) ===");
        Say(VampireAdvantagesResolver.BuildDisciplinesStatusMessage(draft));
        Say("");
        DumpDisciplines(draft, "Каитиф — UI после переименования");

        // Переименование заполненного слота: значение должно сохраниться.
        var ren2 = VampireAdvantagesResolver.RenameCaitiffDiscipline(draft, "Дисциплина 2", "Прорицание");
        Say($"=== Переименование заполненного слота «Дисциплина 2» (знач. 1) → «Прорицание»: {ren2.Message} ===");
        Say($"  Значение «Прорицание» после переименования: {VampireAdvantagesResolver.GetDisciplineValue(draft, "Прорицание")} (сохранено)");
        Say("");

        // Конфликт имён: попытка назвать один слот именем другого.
        var conflict = VampireAdvantagesResolver.RenameCaitiffDiscipline(draft, "Дисциплина 3", "Анимализм");
        Say($"=== Попытка назвать «Дисциплина 3» → «Анимализм» (уже занято): {conflict.Message} ===");
        Say("");

        // Защита резолвера: переименование работает только для Каитифа (UI-кнопки ✎
        // для не-Каитифов не выводятся, но резолвер всё равно обязан отказать).
        var brujah = new VampireCharacter { CharacterId = Guid.NewGuid(), Clan = "Бруха" };
        var forbid = VampireAdvantagesResolver.RenameCaitiffDiscipline(brujah, "Стойкость", "Сила");
        Say("=== Проверка защиты резолвера: переименование для Бруха (UI-кнопки ✎ тут нет, но резолвер обязан отказать) ===");
        Say($"  Failure={forbid.Failure}, Message={forbid.Message}");
        Say("");
        FlushReport();
    }

    // ── Хелперы ─────────────────────────────────────────────────────

    private static void FlushReport()
    {
        lock (_lock) { _reportWriter?.Flush(); }
    }

    private void BumpDisc(VampireCharacter d, string name, int times)
    {
        for (int i = 0; i < times; i++) VampireAdvantagesResolver.IncrementDiscipline(d, name);
    }

    private void BumpVirtue(VampireCharacter d, string name, int times)
    {
        for (int i = 0; i < times; i++) VampireAdvantagesResolver.IncrementVirtue(d, name);
    }

    private void DumpDisciplines(VampireCharacter draft, string caption)
    {
        Say($"---- UI Шага 4.1 ({caption}) ----");
        var c = VampireWizardComponents.BuildForDisciplinesStep(draft);
        DumpComponent(c);
    }

    private void DumpBackgrounds(VampireCharacter draft, string caption)
    {
        Say($"---- UI Шага 4.2 ({caption}) ----");
        var c = VampireWizardComponents.BuildForBackgroundsStep(draft);
        DumpComponent(c);
    }

    private void DumpVirtues(VampireCharacter draft, string caption)
    {
        Say($"---- UI Шага 4.3 ({caption}) ----");
        var c = VampireWizardComponents.BuildForVirtuesStep(draft);
        DumpComponent(c);
    }

    private void DumpComponent(global::Discord.MessageComponent c)
    {
        var rows = c.Components.OfType<global::Discord.ActionRowComponent>().ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            SayRaw($"  ряд {i + 1}: ");
            foreach (var cmp in row.Components)
            {
                if (cmp is global::Discord.SelectMenuComponent sm)
                {
                    var idStr = sm.CustomId ?? "";
                    if (idStr.Length > 50) idStr = idStr.Substring(0, 49) + "…";
                    var placeholder = (sm.Placeholder?.ToString() ?? "").Replace("\n", " ");
                    if (placeholder.Length > 40) placeholder = placeholder.Substring(0, 39) + "…";
                    SayRaw($"[SelectMenu «{placeholder}», id={idStr}, opts={sm.Options.Count}] ");
                }
                else if (cmp is global::Discord.ButtonComponent btn)
                {
                    var idStr = btn.CustomId ?? "";
                    if (idStr.Length > 50) idStr = idStr.Substring(0, 49) + "…";
                    SayRaw($"[Button «{btn.Label}» ({btn.Style}), id={idStr}] ");
                }
            }
            Say("");
        }
        Say($"  всего рядов: {rows.Count}");
        Say("");
    }
}
