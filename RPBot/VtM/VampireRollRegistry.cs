using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RPBot.VtM
{
    /// <summary>
    /// Хранилище «последний бросок кубов» по <c>userId</c>.
    /// </summary>
    /// <remarks>
    /// Используется кнопками переброса по воле и повторной попытки:
    ///   • при броске кубов в <c>/vampire_roll</c> сюда пишется regular/hunger/bundle и messageId;
    ///   • при нажатии на «Переброс 1/2/3» handler читает запись и применяет <see cref="WillpowerReroll"/>;
    ///   • при нажатии на «🔁 Повторить» handler читает запись и применяет <see cref="VampireRepeatReroll"/>;
    ///   • TTL — 15 секунд (по согласованию 2026-09-09: кнопки не плодят UI, не висят минуту);
    ///   • после первого переброса/повтора запись удаляется (1 бросок = 1 переброс).
    ///
    /// Класс не зависит от Discord и может быть покрыт юнит-тестами.
    /// </remarks>
    public sealed class VampireRollRegistry
    {
        /// <summary>TTL последнего броска — 15 секунд по согласованию 2026-09-09.</summary>
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(15);

        private readonly TimeSpan _ttl;
        private readonly ConcurrentDictionary<ulong, Entry> _byUser = new();

        public VampireRollRegistry() : this(DefaultTtl) { }

        public VampireRollRegistry(TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl), "TTL должен быть > 0.");
            _ttl = ttl;
        }

        /// <summary>
        /// Сохранить результат броска для пользователя.
        /// </summary>
        /// <remarks>
        /// Гибрид V20 + V5: <paramref name="regularDice"/> — обычные кубы,
        /// <paramref name="hungerDice"/> — голодные (V5). Специализация (V20)
        /// удваивает десятки ТОЛЬКО в <paramref name="regularDice"/>.
        /// <paramref name="difficulty"/> — сложность проверки; используется повторной
        /// попыткой (V20 стр. 286: новая сложность = difficulty + 1).
        /// </remarks>
        public void Record(
            ulong userId,
            ulong messageId,
            int[] regularDice,
            int[] hungerDice,
            string? specialization,
            int poolSize,
            int difficulty,
            int? bonusDie = null)
        {
            if (regularDice == null) throw new ArgumentNullException(nameof(regularDice));
            if (hungerDice == null) throw new ArgumentNullException(nameof(hungerDice));
            if (regularDice.Length == 0 && hungerDice.Length == 0)
                throw new ArgumentException("Пул пуст.", nameof(regularDice));
            if (difficulty < 2)
                throw new ArgumentOutOfRangeException(nameof(difficulty), difficulty,
                    "Сложность должна быть ≥ 2.");

            _byUser[userId] = new Entry(
                messageId,
                regularDice,
                hungerDice,
                bonusDie,
                specialization,
                poolSize,
                difficulty,
                DateTime.UtcNow);
        }

        /// <summary>
        /// Прочитать запись, если она существует и не истёк TTL.
        /// </summary>
        /// <returns>true, если запись валидна; иначе <paramref name="entry"/> = null.</returns>
        public bool TryGet(ulong userId, out VampireRollSnapshot? entry)
        {
            entry = null;
            if (!_byUser.TryGetValue(userId, out var raw)) return false;
            if (raw == null) return false;
            if (DateTime.UtcNow - raw.RecordedAt > _ttl)
            {
                _byUser.TryRemove(userId, out _);
                return false;
            }
            entry = new VampireRollSnapshot(
                raw.MessageId,
                raw.RegularDice,
                raw.HungerDice,
                raw.BonusDie,
                raw.Specialization,
                raw.PoolSize,
                raw.Difficulty,
                raw.RecordedAt);
            return true;
        }

        /// <summary>
        /// Удалить запись (после переброса или «Готово»).
        /// </summary>
        public void Forget(ulong userId)
        {
            _byUser.TryRemove(userId, out _);
        }

        /// <summary>Очистить всё (для тестов).</summary>
        public void Clear() => _byUser.Clear();

        /// <summary>Сколько сейчас активных записей (включая просроченные).</summary>
        public int Count => _byUser.Count;

        private sealed class Entry
        {
            public ulong MessageId { get; }
            public int[] RegularDice { get; }
            public int[] HungerDice { get; }
            public int? BonusDie { get; }
            public string? Specialization { get; }
            public int PoolSize { get; }
            public int Difficulty { get; }
            public DateTime RecordedAt { get; }

            public Entry(ulong messageId, int[] regularDice, int[] hungerDice,
                int? bonusDie, string? specialization, int poolSize, int difficulty, DateTime recordedAt)
            {
                MessageId = messageId;
                RegularDice = regularDice;
                HungerDice = hungerDice;
                BonusDie = bonusDie;
                Specialization = specialization;
                PoolSize = poolSize;
                Difficulty = difficulty;
                RecordedAt = recordedAt;
            }
        }
    }

    /// <summary>
    /// Снимок последнего броска для переброса по воле и повторной попытки.
    /// </summary>
    /// <remarks>
    /// Гибрид V20+V5: V5-разбиение на regular/hunger + V20-специализация.
    /// Специализация действует только на regular-кубы (не на hunger).
    /// <see cref="Difficulty"/> — сложность исходной проверки; повторная попытка
    /// (V20 стр. 286) вычисляет новую сложность как <c>Difficulty + 1</c>.
    /// </remarks>
    public sealed record VampireRollSnapshot(
        ulong MessageId,
        int[] RegularDice,
        int[] HungerDice,
        int? BonusDie,
        string? Specialization,
        int PoolSize,
        int Difficulty,
        DateTime RecordedAt)
    {
        /// <summary>Сколько regular-кубиков доступно для переброса.</summary>
        public int RegularCount => RegularDice?.Length ?? 0;

        /// <summary>Сколько голодных кубиков (не перебрасываются за волю).</summary>
        public int HungerCount => HungerDice?.Length ?? 0;
    }
}