using System.Collections.Generic;

namespace RPBot.VtM
{
    /// <summary>
    /// Применение последствий проверки совести к <see cref="VampireCharacter"/>
    /// (Roadmap #37, V20 стр. 333). Чистая логика — без Discord.
    /// </summary>
    /// <remarks>
    /// <para>Правила применения (согласованы с владельцем чарника):</para>
    /// <list type="bullet">
    /// <item>Человечность пересчитывается по формуле <c>Совесть + Самоконтроль + HumanityBonus</c>
    ///       и кэпится в 1..10. Потеря пункта Человечности уменьшает <see cref="VampireCharacter.HumanityBonus"/>
    ///       на 1 — это сохраняет намерение игрока (сами добродетели не трогаются).</item>
    /// <item>Ботч дополнительно уменьшает <see cref="VampireCharacter.Virtues"/>[Совесть] на 1.
    ///       Так как Человечность пересчитывается по формуле, падение Совести само по себе
    ///       уже отражается в новой Humanity — отдельной «доп. потери» не происходит.</item>
    /// <item>При ботче автоматически подбирается расстройство из <see cref="VampireDerangementCatalog.All"/>,
    ///       которого ещё нет в списке персонажа. Если все уже есть — берётся первый из каталога
    ///       (V20 не запрещает повторы, и это редкий случай).</item>
    /// <item>Conscience и HumanityBonus не уходят ниже границ, при которых итоговая Humanity = 1
    ///       (т. е. не делаем персонажа NPC через одну проверку совести).</item>
    /// </list>
    /// </remarks>
    public static class VampireMoralityResolver
    {
        /// <summary>Минимальное значение Человечности, ниже которого персонаж становится NPC
        /// (V20 стр. 334). У нас сейчас — не NPC-режим, поэтому служит нижней границей.</summary>
        public const int MinHumanity = 1;

        /// <summary>
        /// Результат применения проверки совести: что фактически изменилось у персонажа.
        /// </summary>
        /// <param name="OldHumanity">Человечность до применения.</param>
        /// <param name="NewHumanity">Человечность после применения (1..10).</param>
        /// <param name="OldConscience">Совесть до применения.</param>
        /// <param name="NewConscience">Совесть после применения (≥ 1).</param>
        /// <param name="AppliedHumanityLoss">Сколько пунктов Чел. реально снято (0..N).</param>
        /// <param name="AppliedConscienceLoss">Сколько пунктов Совести реально снято (0..N).</param>
        /// <param name="AddedDerangement">Имя расстройства, добавленного при ботче, либо null.</param>
        public sealed record MoralityApplyOutcome(
            int OldHumanity,
            int NewHumanity,
            int OldConscience,
            int NewConscience,
            int AppliedHumanityLoss,
            int AppliedConscienceLoss,
            string? AddedDerangement);

        /// <summary>
        /// Применить <see cref="VampireConscienceResolver.ConscienceApplyResult"/> к персонажу.
        /// Мутирует переданного <paramref name="character"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Согласованная семантика</b> (владелец чарника):</para>
        /// <list type="bullet">
        /// <item>Человечность — это отдельный счётчик, который может падать на 1 за проверку.
        ///       При ботче «−1 к Совести» — отдельный штраф, <b>не удваивающий</b> потерю
        ///       Человечности через формулу <c>Чел. = Con + SC + Bonus</c>.</item>
        /// <item>Поэтому при <b>Failure</b> тратим <see cref="VampireCharacter.HumanityBonus"/> на 1
        ///       (Совесть не трогается).</item>
        /// <item>При <b>Botch</b> уменьшаем Совесть на 1 и компенсируем производный эффект
        ///       в формуле, поднимая <see cref="VampireCharacter.HumanityBonus"/> на +1.
        ///       В результате HumanityBonus не меняется, но Совесть падает, и итоговая Чел.
        ///       теряет ровно 1 пункт.</item>
        /// <item>Это сознательное отступление от «наивного» применения формулы, явно
        ///       продиктованное тем, что в V20 «−1 к Чел.» и «−1 к Совести» — два разных
        ///       штрафа, а не суммирующиеся эффекты.</item>
        /// </list>
        /// <para>Границы: Conscience и HumanityBonus не уходят ниже значений,
        /// при которых итоговая Чел. всё ещё = 1.</para>
        /// </remarks>
        public static MoralityApplyOutcome ApplyConscience(
            VampireCharacter character,
            ConscienceApplyResult apply)
        {
            if (character == null) throw new System.ArgumentNullException(nameof(character));
            if (apply == null) throw new System.ArgumentNullException(nameof(apply));

            var oldHumanity = VampireFinishingResolver.ComputeHumanity(character);
            var oldConscience = VampireAdvantagesResolver.GetVirtueValue(
                character, VampireParameterCatalog.VirtueConscience);

            int humanityLoss = 0; // фактически снято пунктов Чел. (для UI: «было 8 → стало 7»)
            int conscienceLoss = 0;
            string? addedDerangement = null;

            // Текущие значения добродетелей (с учётом MinVirtue = 1).
            var con = VampireAdvantagesResolver.GetVirtueValue(
                character, VampireParameterCatalog.VirtueConscience);
            var scl = VampireAdvantagesResolver.GetVirtueValue(
                character, VampireParameterCatalog.VirtueSelfControl);

            // Нижняя граница HumanityBonus: такая, что итоговая Чел. = MinHumanity.
            // Con и SC всегда ≥ 1 (MinVirtue), так что minBonus всегда корректен.
            int minBonus = -(con + scl) + MinHumanity;

            // ─── 1. Потеря Человечности (через HumanityBonus) ────────────
            if (apply.HumanityDelta < 0)
            {
                int desired = -apply.HumanityDelta;
                int allowed = character.HumanityBonus - minBonus;
                int actual = desired;
                if (allowed < actual) actual = allowed;
                if (actual < 0) actual = 0;

                character.HumanityBonus -= actual;
                humanityLoss += actual;
            }

            // ─── 2. Потеря Совести (только при ботче) ───────────────────
            if (apply.ConscienceDelta < 0)
            {
                int desired = -apply.ConscienceDelta;
                int current = VampireAdvantagesResolver.GetVirtueValue(
                    character, VampireParameterCatalog.VirtueConscience);
                int actual = desired;
                if (current - actual < 1) actual = current - 1;
                if (actual < 0) actual = 0;

                if (actual > 0)
                {
                    character.Virtues ??= new Dictionary<string, int>(System.StringComparer.Ordinal);
                    var newVal = current - actual;
                    if (newVal <= 0) character.Virtues.Remove(VampireParameterCatalog.VirtueConscience);
                    else character.Virtues[VampireParameterCatalog.VirtueConscience] = newVal;
                }
                conscienceLoss = actual;
            }

            // ─── 3. Компенсация производного эффекта формулы при ботче ───
            // Если Conscience реально упала (conscienceLoss > 0), итоговая Чел.
            // по формуле упала бы ещё на 1. По семантике V20 это «другой штраф»,
            // поэтому компенсируем — поднимаем HumanityBonus на +1, чтобы итоговая
            // Чел. потеряла только 1 пункт (а не 2).
            if (conscienceLoss > 0)
            {
                character.HumanityBonus += 1;
            }

            // ─── 4. Расстройство при ботче ──────────────────────────────
            if (apply.AddDerangement)
            {
                addedDerangement = PickNewDerangement(character);
                if (addedDerangement != null)
                {
                    character.Derangements ??= new List<string>();
                    character.Derangements.Add(addedDerangement);
                }
            }

            var newHumanity = VampireFinishingResolver.ComputeHumanity(character);
            var newConscience = VampireAdvantagesResolver.GetVirtueValue(
                character, VampireParameterCatalog.VirtueConscience);

            // humanityLoss отражает «потерю пунктов Чел.» с точки зрения игрока:
            // oldHumanity − newHumanity. Для Failure это 1, для Botch — тоже 1 (благодаря компенсации).
            var observedHumanityLoss = oldHumanity - newHumanity;
            if (observedHumanityLoss != humanityLoss && conscienceLoss > 0)
            {
                // Botch: компенсировали формулу, но observed loss = oldHumanity - newHumanity.
                humanityLoss = observedHumanityLoss;
            }

            return new MoralityApplyOutcome(
                OldHumanity: oldHumanity,
                NewHumanity: newHumanity,
                OldConscience: oldConscience,
                NewConscience: newConscience,
                AppliedHumanityLoss: humanityLoss,
                AppliedConscienceLoss: conscienceLoss,
                AddedDerangement: addedDerangement);
        }

        /// <summary>
        /// Выбрать расстройство из каталога, которого ещё нет у персонажа.
        /// Если все есть — взять первый из каталога. Если каталог пуст — вернуть null
        /// (расстройство просто не добавляется, но потеря Чел/Совести всё равно применяется).
        /// </summary>
        private static string? PickNewDerangement(VampireCharacter character)
        {
            var all = VampireDerangementCatalog.All();
            if (all == null || all.Count == 0) return null;

            var existing = character.Derangements != null
                ? new HashSet<string>(character.Derangements, System.StringComparer.Ordinal)
                : new HashSet<string>(System.StringComparer.Ordinal);

            foreach (var d in all)
            {
                if (!existing.Contains(d.NameRu)) return d.NameRu;
            }
            return all[0].NameRu;
        }
    }
}
