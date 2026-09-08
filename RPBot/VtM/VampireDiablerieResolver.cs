using System;
using System.Collections.Generic;

namespace RPBot.VtM;

/// <summary>
/// Чистая логика диаблери (Vampire: the Masquerade V20).
/// Не делает сетевых вызовов — только арифметика по правилам V20.
/// </summary>
/// <remarks>
/// <para>Источник правил: <c>v20_pages320-380.txt</c> стр. 310-311.</para>
/// <para>Ответ команды <c>/vampire diablerie</c> — справочный текст с итогами,
/// без побочных эффектов на листы (понижение поколения и потеря Человечности
/// применяет рассказчик вручную).</para>
/// </remarks>
public static class VampireDiablerieResolver
{
    /// <summary>Минимальное понижение поколения за успешное диаблери
    /// (V20 стр. 311: «...может понизить поколение персонажа более чем на одну ступень»).</summary>
    public const int GenerationDropMin = 1;

    /// <summary>Понижение поколения, если разница поколений ≥ 5 (V20 стр. 311: «более чем
    /// на одну ступень» — минимум две). Бонус за возраст 2000+ лет (3-4 ступени)
    /// остаётся на усмотрение рассказчика.</summary>
    public const int GenerationDropLarge = 2;

    /// <summary>Разница поколений, при которой понижение увеличено с +1 до +2.</summary>
    public const int GenerationGapLargeThreshold = 5;

    /// <summary>Снижение Человечности за сам факт диаблери (минимум).</summary>
    public const int HumanityLossFlat = 1;

    /// <summary>Минимальная длительность «чёрных полос» в ауре (годы).</summary>
    public const int AuraStainsYearsMin = 1;

    /// <summary>
    /// Результат расчёта диаблери.
    /// </summary>
    /// <param name="Success">true, если атакующий выпил жертву (полный успех).</param>
    /// <param name="AttackerGenerationBefore">Поколение атакующего до диаблери.</param>
    /// <param name="AttackerGenerationAfter">Поколение атакующего после диаблери.</param>
    /// <param name="GenerationDrop">На сколько поколений понижен атакующий (0 — диаблери не состоялся).</param>
    /// <param name="HumanityLossFlat">Базовое снижение Человечности за сам акт (1 — минимум).</param>
    /// <param name="EuphoriaDifficulty">Сложность проверки самоконтроля/инстинктов после успеха (10 − Человечность).</param>
    /// <param name="AuraStainsYears">Срок, в течение которого в ауре диаблериста остаются чёрные полосы.</param>
    /// <param name="Notes">Текстовые замечания для вывода (например «уязвим к атакам, сложность 2»).</param>
    public sealed record Result(
        bool Success,
        int AttackerGenerationBefore,
        int AttackerGenerationAfter,
        int GenerationDrop,
        int HumanityLossFlat,
        int EuphoriaDifficulty,
        int AuraStainsYears,
        IReadOnlyList<string> Notes);

    /// <summary>
    /// Рассчитать итог диаблери.
    /// </summary>
    /// <param name="attackerGeneration">Поколение атакующего (3..15).</param>
    /// <param name="victimGeneration">Поколение жертвы (3..15).</param>
    /// <param name="attackerHumanity">Человечность (или PathRating) атакующего (1..10).</param>
    /// <param name="success">true, если атакующий выпил жертву и не был прерван.</param>
    public static Result Resolve(
        int attackerGeneration,
        int victimGeneration,
        int attackerHumanity,
        bool success)
    {
        ValidateGeneration(attackerGeneration, nameof(attackerGeneration));
        ValidateGeneration(victimGeneration, nameof(victimGeneration));
        ValidateHumanity(attackerHumanity, nameof(attackerHumanity));

        var notes = new List<string>();

        if (!success)
        {
            notes.Add("Диаблери не состоялось — атакующий не довёл процесс до конца.");
            // При провале диаблери эйфории и чёрных полос нет.
            // Снижение Человечности — только если жертва уже частично выпита
            // (вызывающий код не передаёт частичный успех — намеренно упрощено).
            return new Result(
                Success: false,
                AttackerGenerationBefore: attackerGeneration,
                AttackerGenerationAfter: attackerGeneration,
                GenerationDrop: 0,
                HumanityLossFlat: 0,
                EuphoriaDifficulty: 0,
                AuraStainsYears: 0,
                Notes: notes);
        }

        var gap = attackerGeneration - victimGeneration;
        var drop = gap <= 0 ? 0 : gap >= GenerationGapLargeThreshold ? GenerationDropLarge : GenerationDropMin;
        var newGen = Math.Max(3, attackerGeneration - drop);

        var euphoriaDiff = Math.Max(2, 10 - attackerHumanity);
        var auraYears = Math.Max(AuraStainsYearsMin, newGen - victimGeneration);

        notes.Add(
            "Пока шёл акт диаблери, сложность любых атак против атакующего = 2.");
        notes.Add(
            $"Понижение поколения: {attackerGeneration} → {newGen} (−{drop}).");
        notes.Add(
            $"Чёрные полосы в ауре видны через «Чтение ауры», срок ≈ {auraYears} г.");
        notes.Add(
            $"Эйфория → проверка самоконтроля/инстинктов, сложность {euphoriaDiff}.");
        notes.Add(
            "Сам акт снижает Человечность на 1; при отягчающих обстоятельствах — проверка совести (сложность 8).");
        if (newGen - victimGeneration >= 3)
        {
            notes.Add("Часть Витэ жертвы может временно усилить Дисциплины атакующего до конца сцены.");
        }

        return new Result(
            Success: true,
            AttackerGenerationBefore: attackerGeneration,
            AttackerGenerationAfter: newGen,
            GenerationDrop: drop,
            HumanityLossFlat: HumanityLossFlat,
            EuphoriaDifficulty: euphoriaDiff,
            AuraStainsYears: auraYears,
            Notes: notes);
    }

    private static void ValidateGeneration(int value, string paramName)
    {
        if (value < 3 || value > 15)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "Поколение должно быть в диапазоне 3..15.");
        }
    }

    private static void ValidateHumanity(int value, string paramName)
    {
        if (value < 1 || value > 10)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "Человечность должна быть в диапазоне 1..10.");
        }
    }
}
