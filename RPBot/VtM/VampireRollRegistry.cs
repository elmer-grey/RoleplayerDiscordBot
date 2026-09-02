using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RPBot.VtM
{
    /// <summary>
    /// Хранилище «последний бросок кубов» по <c>userId</c>.
    /// </summary>
    /// <remarks>
    /// Используется кнопками переброса по воле:
    ///   • при броске кубов в <c>/rollV</c> сюда пишется regular/hunger/bonus и messageId;
    ///   • при нажатии на «Переброс 1/2/3» handler читает запись и применяет <see cref="WillpowerReroll"/>;
    ///   • TTL — 60 секунд (после броска пользователь должен успеть нажать);
    ///   • после первого переброса запись удаляется (1 бросок = 1 переброс).
    ///
    /// Класс не зависит от Discord и может быть покрыт юнит-тестами.
    /// </remarks>
    public sealed class VampireRollRegistry
    {
        /// <summary>TTL последнего броска — 60 секунд по согласованию 2026-09-03.</summary>
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

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
        public void Record(
            ulong userId,
            ulong messageId,
            int[] regularDice,
            int[] hungerDice,
            int? bonusDie)
        {
            if (regularDice == null) throw new ArgumentNullException(nameof(regularDice));
            if (hungerDice == null) throw new ArgumentNullException(nameof(hungerDice));
            if (regularDice.Length == 0) throw new ArgumentException("Regular пул пуст.", nameof(regularDice));

            _byUser[userId] = new Entry(
                messageId,
                regularDice,
                hungerDice,
                bonusDie,
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
            public DateTime RecordedAt { get; }

            public Entry(ulong messageId, int[] regularDice, int[] hungerDice, int? bonusDie, DateTime recordedAt)
            {
                MessageId = messageId;
                RegularDice = regularDice;
                HungerDice = hungerDice;
                BonusDie = bonusDie;
                RecordedAt = recordedAt;
            }
        }
    }

    /// <summary>
    /// Снимок последнего броска для переброса по воле.
    /// </summary>
    /// <remarks>
    /// Хранит regular и hunger раздельно: перебрасываются только regular-кубики,
    /// hunger остаётся нетронутым. <see cref="BonusDie"/> тоже не перебрасывается.
    /// </remarks>
    public sealed record VampireRollSnapshot(
        ulong MessageId,
        int[] RegularDice,
        int[] HungerDice,
        int? BonusDie,
        DateTime RecordedAt)
    {
        /// <summary>Сколько regular-кубиков доступно для переброса.</summary>
        public int RegularCount => RegularDice?.Length ?? 0;
    }
}