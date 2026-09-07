using System;
using System.Collections.Generic;
using System.Linq;
using RPBot.VtM;

namespace RPBot.VtM
{
    /// <summary>
    /// Чистая логика расчёта стоимости покупки за опыт (V20, стр. 141, таблица XP-курсов).
    /// </summary>
    /// <remarks>
    /// <para>Курсы покупки (V20 стр. 141):</para>
    /// <code>
    ///   Характеристика              = текущее × 4
    ///   Способность                 = текущее × 2
    ///   Клановая Дисциплина         = текущее × 5
    ///   Сторонняя Дисциплина        = текущее × 7
    ///   Вторичный путь              = текущее × 4   (Некромантия/Тауматургия)
    ///   Добродетель                 = текущее × 2
    ///   Человечность / Путь         = текущее × 2
    ///   Воля                        = текущее × 1
    ///   Новая способность           = 3   (фикс)
    ///   Новая Дисциплина            = 10  (фикс)
    ///   Новый путь Некр./Таум.      = 7   (фикс)
    /// </code>
    ///
    /// <para><b>Каитиф</b>: любая дисциплина — 6 × текущее (вместо 5 или 7) (V20 стр. 131).</para>
    ///
    /// <para>Добродетели, купленные за XP, не должны влиять на Чел/Волю — это требование
    /// V20, см. также <see cref="VampireFinishingResolver"/>. Трекинг купленных добродетелей
    /// будет добавлен вместе с UI траты XP.</para>
    ///
    /// <para>Класс не зависит от Discord — чистая логика для тестов.</para>
    /// </remarks>
    public static class VampireXpSpendResolver
    {
        /// <summary>Множитель для покупки следующего уровня характеристики.</summary>
        public const int AttributeMultiplier = 4;

        /// <summary>Множитель для покупки следующего уровня способности.</summary>
        public const int AbilityMultiplier = 2;

        /// <summary>Множитель для клановой дисциплины.</summary>
        public const int ClanDisciplineMultiplier = 5;

        /// <summary>Множитель для сторонней дисциплины.</summary>
        public const int OutsideDisciplineMultiplier = 7;

        /// <summary>Множитель для вторичного пути (Некромантия/Тауматургия).</summary>
        public const int SecondaryPathMultiplier = 4;

        /// <summary>Множитель для добродетели.</summary>
        public const int VirtueMultiplier = 2;

        /// <summary>Множитель для Человечности/Пути.</summary>
        public const int HumanityMultiplier = 2;

        /// <summary>Множитель для Воли.</summary>
        public const int WillpowerMultiplier = 1;

        /// <summary>Фикс-стоимость новой способности (XP).</summary>
        public const int NewAbilityCost = 3;

        /// <summary>Фикс-стоимость новой дисциплины (XP).</summary>
        public const int NewDisciplineCost = 10;

        /// <summary>Фикс-стоимость нового вторичного пути (XP).</summary>
        public const int NewSecondaryPathCost = 7;

        /// <summary>Каитиф-курс: множитель для любой дисциплины (V20 стр. 131).</summary>
        public const int CaitiffDisciplineMultiplier = 6;

        /// <summary>Стоимость в XP за покупку характеристики (следующего уровня).</summary>
        /// <param name="current">Текущее значение (0..4). Покупается (current + 1).</param>
        /// <returns>XP = (current + 1) × 4. -1, если current вне диапазона.</returns>
        public static int AttributeXp(int current)
        {
            if (current < 0 || current >= 5) return -1;
            return (current + 1) * AttributeMultiplier;
        }

        /// <summary>Стоимость в XP за покупку способности (следующего уровня).</summary>
        /// <param name="current">Текущее значение (0..4).</param>
        /// <returns>XP = (current + 1) × 2. -1, если current вне диапазона.</returns>
        public static int AbilityXp(int current)
        {
            if (current < 0 || current >= 5) return -1;
            return (current + 1) * AbilityMultiplier;
        }

        /// <summary>Стоимость в XP за покупку дисциплины.</summary>
        /// <param name="current">Текущий уровень дисциплины (0..4).</param>
        /// <param name="isCaitiff">Каитиф-ли персонаж? Если да — множитель 6.</param>
        /// <param name="isClanDiscipline">Клановая ли дисциплина? Для Каитифа игнорируется (любая = клановая).</param>
        /// <returns>XP = (current + 1) × множитель. -1, если current вне диапазона.</returns>
        public static int DisciplineXp(int current, bool isCaitiff, bool isClanDiscipline)
        {
            if (current < 0 || current >= 5) return -1;
            int multiplier = isCaitiff
                ? CaitiffDisciplineMultiplier
                : (isClanDiscipline ? ClanDisciplineMultiplier : OutsideDisciplineMultiplier);
            return (current + 1) * multiplier;
        }

        /// <summary>Стоимость в XP за покупку вторичного пути (Некромантия/Тауматургия).</summary>
        /// <param name="current">Текущий уровень пути (0..4).</param>
        public static int SecondaryPathXp(int current)
        {
            if (current < 0 || current >= 5) return -1;
            return (current + 1) * SecondaryPathMultiplier;
        }

        /// <summary>Стоимость в XP за покупку следующего уровня добродетели.</summary>
        /// <param name="current">Текущее значение (0..4).</param>
        public static int VirtueXp(int current)
        {
            if (current < 0 || current >= 5) return -1;
            return (current + 1) * VirtueMultiplier;
        }

        /// <summary>Стоимость в XP за покупку следующего уровня Человечности/Пути.</summary>
        /// <param name="current">Текущая Чел/Путь (1..10).</param>
        public static int HumanityXp(int current)
        {
            if (current < 1 || current >= 10) return -1;
            return (current + 1) * HumanityMultiplier;
        }

        /// <summary>Стоимость в XP за покупку следующего уровня Воли.</summary>
        /// <param name="current">Текущая Воля (1..10).</param>
        public static int WillpowerXp(int current)
        {
            if (current < 1 || current >= 10) return -1;
            return (current + 1) * WillpowerMultiplier;
        }

        /// <summary>Фикс-стоимость: новая способность (ранг 1).</summary>
        public static int NewAbilityXp() => NewAbilityCost;

        /// <summary>Фикс-стоимость: новая дисциплина (ранг 1).</summary>
        public static int NewDisciplineXp() => NewDisciplineCost;

        /// <summary>Фикс-стоимость: новый вторичный путь (ранг 1).</summary>
        public static int NewSecondaryPathXp() => NewSecondaryPathCost;

        // ─── Проверки возможности покупки ─────────────────────────────────────────

        /// <summary>Можно ли сейчас купить следующий уровень характеристики?</summary>
        public static bool CanBuyAttribute(int current) => AttributeXp(current) > 0;

        /// <summary>Можно ли сейчас купить следующий уровень способности?</summary>
        public static bool CanBuyAbility(int current) => AbilityXp(current) > 0;

        /// <summary>Можно ли сейчас купить следующий уровень дисциплины?</summary>
        public static bool CanBuyDiscipline(int current) => DisciplineXp(current, false, false) > 0;

        /// <summary>Можно ли сейчас купить следующий уровень добродетели?</summary>
        public static bool CanBuyVirtue(int current) => VirtueXp(current) > 0;

        /// <summary>Можно ли сейчас купить следующий уровень Человечности/Пути?</summary>
        public static bool CanBuyHumanity(int current) => HumanityXp(current) > 0;

        /// <summary>Можно ли сейчас купить следующий уровень Воли?</summary>
        public static bool CanBuyWillpower(int current) => WillpowerXp(current) > 0;

        // ─── Высокоуровневый API по персонажу ────────────────────────────────────

        /// <summary>
        /// Получить текущее значение дисциплины (сумма Шага 4.1 и freebie).
        /// </summary>
        public static int GetCurrentDisciplineLevel(VampireCharacter c, string disciplineName)
        {
            int lvl = 0;
            if (c.Disciplines != null && c.Disciplines.TryGetValue(disciplineName, out var baseLvl))
                lvl = baseLvl;
            if (c.FreebieDisciplines != null && c.FreebieDisciplines.TryGetValue(disciplineName, out var fbLvl))
                lvl += fbLvl;
            return lvl;
        }

        /// <summary>
        /// Является ли дисциплина клановой для данного персонажа.
        /// Для Каитифа любая дисциплина считается клановой (правило «6 × текущее»).
        /// </summary>
        public static bool IsClanDisciplineFor(VampireCharacter c, string disciplineName)
        {
            if (c == null || string.IsNullOrEmpty(c.Clan)) return false;
            if (c.Clan == "Каитиф") return true; // V20 стр. 131
            var clan = VampireParameterCatalog.GetClanDisciplines(c.Clan);
            return clan.Contains(disciplineName);
        }

        /// <summary>
        /// Стоимость покупки дисциплины с учётом клана и Каитиф-правила.
        /// </summary>
        public static int XpCostForDiscipline(VampireCharacter c, string disciplineName)
        {
            int current = GetCurrentDisciplineLevel(c, disciplineName);
            bool isCaitiff = c?.Clan == "Каитиф";
            bool isClan = IsClanDisciplineFor(c, disciplineName);
            return DisciplineXp(current, isCaitiff, isClan);
        }

        /// <summary>
        /// Текстовое описание цены (для UI и логов). Возвращает «—», если покупка невозможна.
        /// </summary>
        public static string FormatCost(int xp)
        {
            return xp > 0 ? $"{xp} XP" : "—";
        }
    }
}
