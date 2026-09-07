using System;

namespace RPBot.VtM
{
    /// <summary>
    /// Чистая логика восстановления Воли за архетип (Roadmap #35, V20 стр. 92, 280+).
    /// </summary>
    /// <remarks>
    /// <para>В V20 каждый архетип даёт персонажу способ восстановить 1 пункт Воли при
    /// выполнении определённого условия (см. <see cref="VampireParameterCatalog.GetArchetypeRestoreCondition"/>).
    /// Рассказчик решает, выполнено ли условие, и начисляет пункт.</para>
    ///
    /// <para>Решение от {DATE}: без программного лимита на частоту восстановления.
    /// Рассказчик сам решает, когда условие архетипа выполнено. Попытки ввести
    /// "12 часов назад = новая ночь" дают ненадёжную логику (часовой пояс,
    /// долгие/короткие сцены, разрывы между сессиями), которая скорее мешает,
    /// чем помогает. Лимит можно ввести позже через дополнительное поле, если
    /// возникнет реальная злоупотребление.</para>
    ///
    /// <para>Класс не зависит от Discord — чистая логика для тестов.</para>
    /// </remarks>
    public static class VampireArchetypeResolver
    {
        /// <summary>Результат проверки возможности восстановления Воли за архетип.</summary>
        public enum CanRestoreResult
        {
            /// <summary>Можно восстановить — архетип задан и Воля не на максимуме.</summary>
            Allowed,

            /// <summary>У персонажа не задан архетип.</summary>
            NoArchetype,

            /// <summary>Запас Воли уже на максимуме — нечего восстанавливать.</summary>
            AlreadyFull,

            /// <summary>Воля не определена (максимум &lt;= 0).</summary>
            NoWillpower,
        }

        /// <summary>
        /// Можно ли сейчас восстановить пункт Воли за архетип.
        /// </summary>
        /// <param name="character">Персонаж.</param>
        /// <param name="maxWillpower">Текущий максимум Воли (обычно = Смелость, кэп 10).</param>
        /// <param name="currentWillpower">Текущий запас пунктов Воли.</param>
        /// <remarks>
        /// Без программного лимита на частоту: решение принимает рассказчик.
        /// Поле <see cref="VampireCharacter.LastArchetypeRestore"/> сохранено в модели
        /// на будущее, но в этой проверке не используется.
        /// </remarks>
        public static CanRestoreResult CanRestore(
            VampireCharacter character,
            int maxWillpower,
            int currentWillpower)
        {
            if (character == null) return CanRestoreResult.NoArchetype;
            if (string.IsNullOrEmpty(character.Archetype)) return CanRestoreResult.NoArchetype;
            if (maxWillpower <= 0) return CanRestoreResult.NoWillpower;
            if (currentWillpower >= maxWillpower) return CanRestoreResult.AlreadyFull;
            return CanRestoreResult.Allowed;
        }

        /// <summary>
                /// Текстовое описание результата для UI/логов.
                /// </summary>
                public static string Describe(CanRestoreResult result) => result switch
                {
                    CanRestoreResult.Allowed      => "Можно восстановить пункт Воли за архетип.",
                    CanRestoreResult.NoArchetype  => "Сначала задайте архетип персонажа.",
                    CanRestoreResult.AlreadyFull  => "Запас Воли уже на максимуме.",
                    CanRestoreResult.NoWillpower  => "Воля не определена.",
                    _ => "",
                };
            }
        }
