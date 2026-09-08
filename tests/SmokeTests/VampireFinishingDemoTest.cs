using System;
using System.IO;
using System.Linq;
using System.Text;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Демо-тест: показывает как сейчас работает Шаг 5 «Последние штрихи»
/// end-to-end (без Discord). Печатает все промежуточные состояния в stdout —
/// они появятся в логе dotnet test при verbose-выводе. Параллельно пишет
/// UTF-8 (без BOM) копию в файл `demo_report.txt` в каталоге теста.
///
/// Сценарий: после Шага 4 (добродетели распределены) Бруха распределяет
/// 15 свободных пунктов по правилам V20 стр. 86:
///   Хар 5 / Спос 2 / Диск 7 / Факт 1 / Доброд 2.
/// Человечность и Воля рассчитываются автоматически из добродетелей.
///
/// Плюс отдельный мини-сценарий для Каитифа (свободный ввод имён).
/// </summary>
public class VampireFinishingDemoTest : IDisposable
{
    private static readonly object _lock = new();
    private static StreamWriter? _reportWriter;

    private StreamWriter GetWriter()
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
            return _reportWriter;
        }
    }

    private static readonly string ReportPath = Path.Combine(
        AppContext.BaseDirectory, "demo_finishing_report.txt");

    private void Say(string line)
    {
        Console.WriteLine(line);
        try { GetWriter().WriteLine(line); }
        catch (Exception ex) { Console.Error.WriteLine($"[demo-report error] {ex.Message}"); }
    }

    public void Dispose() { /* writer глобальный, живёт до конца процесса */ }

    public VampireFinishingDemoTest()
    {
        // Параллельные демо делят один writer под lock — сначала очищаем файл.
        lock (_lock)
        {
            try
            {
                _reportWriter?.Dispose();
                _reportWriter = null;
                if (File.Exists(ReportPath)) File.Delete(ReportPath);
            }
            catch { /* второй поток может опередить — не страшно */ }
        }
    }

    [Fact]
    public void Demo_Freebies_Brujah()
    {
        // 1) Игрок завершил Шаги 1-4. Добродетели уже распределены на Шаге 4.3.
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
        // Базовые значения добродетелей (как они идут с Шага 1).
        draft.Virtues[VampireParameterCatalog.VirtueConscience] = 1;
        draft.Virtues[VampireParameterCatalog.VirtueSelfControl] = 1;
        draft.Virtues[VampireParameterCatalog.VirtueCourage] = 1;

        // После Шага 4.3 (демонстрационно) — пусть игрок вкатал по 2 в каждую добродетель.
        for (int i = 0; i < 2; i++)
        {
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience);
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueSelfControl);
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueCourage);
        }

        Say("=== Старт Шага 5 (Бруха) ===");
        Say($"Клан={draft.Clan}, Поколение={draft.Generation}, Голод={draft.Hunger}");
        Say($"Пул freebie: {VampireFinishingResolver.FreebiePool}");
        Say($"Цены: хар {VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Attribute)}, " +
            $"спос {VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Ability)}, " +
            $"диск {VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Discipline)}, " +
            $"факт {VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Background)}, " +
            $"доброд {VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Virtue)}");
        Say("");

        // 2) Стартовое сообщение Шага 5 (как видит игрок в DM).
        Say("=== Шаг 5 старт (игрок видит это в DM) ===");
        Say(VampireFinishingResolver.BuildStatusMessage(draft));
        Say("");

        // 3) Базовая формула Чел/Воли до любых трат.
        Say($"Человечность (исходно): {VampireFinishingResolver.ComputeHumanity(draft)}");
        Say($"Воля (исходно): {VampireFinishingResolver.ComputeWillpower(draft)}");
        Say("");

        // 4) Траты: диск +1 (Стойкость, -7) → пул 8.
        var slot0 = VampireAdvantagesCatalog.GetDisciplineSlots(draft.Clan)[0];
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Discipline, slot0, out _);
        Say($"=== Трата -7: +1 в дисциплину «{slot0}» ===");
        Say($"  остаток пула: {VampireFinishingResolver.RemainingFreebies(draft)}");
        Say($"  «{slot0}» теперь: {VampireFinishingResolver.ReadFieldValue(draft, VampireFinishingResolver.FreebieTarget.Discipline, slot0)}");
        Say("");

        // 5) Хар: +1 в Силу (-5) → пул 3.
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _);
        Say("=== Трата -5: +1 в Силу ===");
        Say($"  остаток пула: {VampireFinishingResolver.RemainingFreebies(draft)}");
        Say($"  «Сила» теперь: {VampireFinishingResolver.ReadFieldValue(draft, VampireFinishingResolver.FreebieTarget.Attribute, "Сила")}");
        Say("");

        // 6) Спос: +1 в Атлетику (-2) → пул 1.
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика", out _);
        Say("=== Трата -2: +1 в Атлетику ===");
        Say($"  остаток пула: {VampireFinishingResolver.RemainingFreebies(draft)}");
        Say("");

        // 7) Факт: +1 в Состояние (-1) → пул 0.
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Background, "Состояние", out _);
        Say("=== Трата -1: +1 в Состояние ===");
        Say($"  остаток пула: {VampireFinishingResolver.RemainingFreebies(draft)}");
        Say("");

        // 8) Попытка превысить пул — должно быть отвергнуто.
        var rej = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Virtue, VampireParameterCatalog.VirtueCourage, out _);
        Say($"=== Попытка вкатить ещё добродетель при пустом пуле ===");
        Say($"  результат: IsSuccess={rej.IsSuccess}, Failure={rej.Failure}");
        Say($"  сообщение: {rej.Message}");
        Say("");

        // 9) Сброс и новая попытка — теперь с тратами на добродетели, чтобы показать пересчёт Чел/Воли.
        VampireFinishingResolver.ResetFreebies(draft);
        Say("=== Сброс всех трат (Reset) ===");
        Say($"  остаток пула: {VampireFinishingResolver.RemainingFreebies(draft)}");
        Say($"  Человечность (после сброса): {VampireFinishingResolver.ComputeHumanity(draft)}");
        Say($"  Воля (после сброса): {VampireFinishingResolver.ComputeWillpower(draft)}");
        Say("");

        // 10) Теперь распределяем иначе: добродетели (Смелость) +1, +1 → растёт и Чел, и Воля.
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Virtue, VampireParameterCatalog.VirtueCourage, out var affects1);
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Virtue, VampireParameterCatalog.VirtueCourage, out var affects2);
        Say("=== Трата -4: +2 в Смелость ===");
        Say($"  Смелость: {VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueCourage)}");
        Say($"  Человечность (формула): {VampireFinishingResolver.ComputeHumanity(draft)}");
        Say($"  Воля (формула): {VampireFinishingResolver.ComputeWillpower(draft)}");
        Say($"  affects1={affects1}, affects2={affects2}");
        Say("");

        // 11) Финальное сообщение (как видит игрок).
        Say("=== Финальное сообщение Шага 5 ===");
        Say(VampireFinishingResolver.BuildStatusMessage(draft));
        Say("");

        // 11a) Трата на Чел/Волю свободными пунктами (V20 стр. 86).
        VampireFinishingResolver.ResetFreebies(draft);
        Say("=== Сброс и траты на Чел (-2) + Воля (-1×3) ===");
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        Say($"  потрачено: -2 (Чел) + -1×3 (Воля) = -5");
        Say($"  остаток пула: {VampireFinishingResolver.RemainingFreebies(draft)}");
        Say($"  HumanityBonus={draft.HumanityBonus}, WillpowerBonus={draft.WillpowerBonus}");
        Say(VampireFinishingResolver.BuildStatusMessage(draft));
        Say("");

        // 11b) Кэп Чел/Воли (нельзя вкатить выше 10).
        var rejH = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        Say($"=== Попытка ещё +1 на Чел: IsSuccess={rejH.IsSuccess}, Failure={rejH.Failure} ===");
        var rejW = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        Say($"=== Попытка ещё +1 на Волю: IsSuccess={rejW.IsSuccess}, Failure={rejW.Failure} ===");
        Say("");

        // 11c) Доводим до кэпа и пробуем пробить.
        VampireFinishingResolver.ResetFreebies(draft);
        // Базовые добродетели = 3, формула Чел=6, Воля=3.
        // Вкатываем +4 на Чел и +7 на Волю (всё что влезает).
        for (int i = 0; i < 4; i++)
            VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        for (int i = 0; i < 7; i++)
            VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        Say($"=== После бонусов: Чел={VampireFinishingResolver.ComputeHumanity(draft)}, Воля={VampireFinishingResolver.ComputeWillpower(draft)} ===");
        var rejH2 = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        Say($"=== Кэп Чел: IsSuccess={rejH2.IsSuccess}, Failure={rejH2.Failure}, msg={rejH2.Message} ===");
        var rejW2 = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        Say($"=== Кэп Воли: IsSuccess={rejW2.IsSuccess}, Failure={rejW2.Failure}, msg={rejW2.Message} ===");
        Say(VampireFinishingResolver.BuildStatusMessage(draft));
        Say("");

        // 12) Пересчёт после сброса.
        VampireFinishingResolver.ResetFreebies(draft);
        Say("=== Сброс перед переходом на Шаг 6 ===");
        Say($"  остаток пула: {VampireFinishingResolver.RemainingFreebies(draft)}");
        Say($"  Человечность (финальная): {VampireFinishingResolver.ComputeHumanity(draft)}");
        Say($"  Воля (финальная): {VampireFinishingResolver.ComputeWillpower(draft)}");
        Say("");
        Say("=== Демо Шага 5 завершено ===");
    }

    [Fact]
    public void Demo_AllocateOnEmptySlot_Caitiff()
    {
        // Каитиф: пустые слоты во всём, но правила те же — пул 15.
        var draft = new VampireCharacter
        {
            CharacterId = Guid.NewGuid(),
            PlayerId = 222,
            PlayerName = "Каитиф",
            Clan = "Каитиф",
            Generation = 13,
            Hunger = 1,
        };
        // Каитиф не имеет клановых дисциплин — дисциплины пусты.
        // Должна работать раскладка на любой атрибут/способность с 0.
        Say("=== Старт Шага 5 (Каитиф, без клановых дисциплин) ===");
        Say(VampireFinishingResolver.BuildStatusMessage(draft));
        Say("");

        // Атрибут с 0: трата должна сработать.
        var r1 = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _);
        Say($"=== Трата -5 на «Сила» (с 0): {r1.Message} ===");
        Say($"  Сила теперь: {draft.Attributes["Сила"]}");
        Say("");

        // Способность с 0: трата должна сработать.
        var r2 = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика", out _);
        Say($"=== Трата -2 на «Атлетика» (с 0): {r2.Message} ===");
        Say($"  Атлетика теперь: {VampireFinishingResolver.ReadFieldValue(draft, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика")}");
        Say("");

        // Дисциплина: Каитиф может выбрать любую. Просто трата на пустой ключ.
        draft.Disciplines["Производство"] = 0; // не было в каталоге, но для Каитифа — допустимо
        var r3 = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Discipline, "Производство", out _);
        Say($"=== Трата -7 на новую дисциплину «Производство» (с 0): {r3.Message} ===");
        Say($"  остаток пула: {VampireFinishingResolver.RemainingFreebies(draft)}");
        Say("");

        // Слабость Каитифа — показываем формулой, что это вне freebie-пула.
        Say($"=== Слабость Каитифа: «{VampireParameterCatalog.GetClanFlawShort(draft.Clan)}» (см. Шаг 6) ===");
        Say($"  длинная формулировка: «{VampireClanFlawCatalog.GetClanFlawLong(draft.Clan)}»");
        Say("");

        Say("=== Демо Шага 5 (Каитиф) завершено ===");
    }
}
