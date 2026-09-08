using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot
{
    /// <summary>
    /// Диспетчер для кнопок карточки «Ярость» (префикс <c>vam_frenzy:*</c>).
    /// Авторизация по владельцу персонажа (character.PlayerId).
    /// </summary>
    public sealed class VampireFrenzyButtonHandler
    {
        private readonly VampireStorage _storage;
        private readonly IRandom _rng;

        public VampireFrenzyButtonHandler(VampireStorage storage, IRandom rng)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        }

        public async Task HandleAsync(SocketMessageComponent component)
        {
            if (component == null) return;
            if (!VampireFrenzyComponents.TryParse(component.Data.CustomId,
                    out var action, out var characterId, out var kind))
            {
                return;
            }

            var character = _storage.GetByCharacterId(characterId);
            if (character == null)
            {
                await component.RespondAsync("❌ Персонаж не найден.", ephemeral: true);
                return;
            }

            if (component.User.Id != character.PlayerId)
            {
                await component.RespondAsync("⛔ Это не твой персонаж.", ephemeral: true);
                return;
            }

            switch (action)
            {
                case VampireFrenzyAction.RollFrenzy:
                    await HandleRoll(component, character, VampireFrenzyResolver.FrenzyKind.Frenzy, 6).ConfigureAwait(false);
                    break;
                case VampireFrenzyAction.RollRotschreck:
                    await HandleRoll(component, character, VampireFrenzyResolver.FrenzyKind.Rötschreck, 6).ConfigureAwait(false);
                    break;
                default:
                    await component.RespondAsync("❓ Неизвестное действие.", ephemeral: true).ConfigureAwait(false);
                    break;
            }
        }

        private async Task HandleRoll(SocketMessageComponent component, VampireCharacter character,
            VampireFrenzyResolver.FrenzyKind kind, int difficulty)
        {
            // Полная логика через VampireFrenzyResolver:
            //   • Пули: добродетель (Самоконтроль / Смелость), кэп 5.
            //   • Бруха: сложность +2 (кэп 10) через ComputeDifficulty.
            //   • Инстинкты → автопровал.
            //   • Гангрел: при провале автоматически выбирается атавизм.
            //   • Учитывается accumulatedSuccesses (для повторных попыток в том же ходу).
            var computedDifficulty = VampireFrenzyResolver.ComputeDifficulty(
                kind: kind,
                stimulusComplexity: difficulty,
                conscience: character.Virtues?.TryGetValue(VampireParameterCatalog.VirtueConscience, out var cons) == true ? cons : 1,
                clan: character.Clan ?? "");

            var result = VampireFrenzyResolver.Roll(
                character: character,
                kind: kind,
                difficulty: computedDifficulty,
                accumulatedSuccesses: 0,
                rng: _rng,
                rollAtavism: true);

            // Если что-то поменялось на персонаже (атавизм) — сохранить.
            await _storage.UpsertAsync(character).ConfigureAwait(false);

            var pool = VampireFrenzyResolver.ComputePoolSize(character, kind);
            var dicePreview = pool > 0 ? FormatDiceRoll(character, kind, _rng) : "(нет пула)";
            var status = result.Outcome switch
            {
                VampireFrenzyResolver.FrenzyRollOutcome.FullySuppressed =>
                    $"🟢 **Полное подавление** — накоплено {result.AccumulatedSuccesses}/{VampireFrenzyResolver.FullSuppressThreshold}.\n" +
                    $"Сложность {result.Difficulty}, пул {result.PoolSize}, кубы: {dicePreview}.",
                VampireFrenzyResolver.FrenzyRollOutcome.PartiallyContained =>
                    $"🟡 **Частичное сдерживание** — {result.RoundsContained} ход(а) (накоплено {result.AccumulatedSuccesses}/{VampireFrenzyResolver.FullSuppressThreshold}).\n" +
                    $"Сложность {result.Difficulty}, пул {result.PoolSize}, кубы: {dicePreview}.",
                VampireFrenzyResolver.FrenzyRollOutcome.Unleashed when VampireFrenzyResolver.IsInstinctDriven(character) =>
                    $"🐾 **Инстинкты** — Зверь не знает самоконтроля. Бросок невозможен.",
                VampireFrenzyResolver.FrenzyRollOutcome.Unleashed when !string.IsNullOrEmpty(result.ActiveAtavism) =>
                    $"🔴 **Зверь вырывается** — атавизм Гангрела: _{result.ActiveAtavism}_.\n" +
                    $"Сложность {result.Difficulty}, пул {result.PoolSize}, кубы: {dicePreview}.",
                _ =>
                    $"🔴 **Зверь вырывается** — {result.Successes} успехов против сложности {result.Difficulty}.\n" +
                    $"Пул {result.PoolSize}, кубы: {dicePreview}.",
            };

            var embed = VampireFrenzyEmbed.Build(character, status);
            await component.RespondAsync(embed: embed, ephemeral: true).ConfigureAwait(false);
        }

        /// <summary>Превью значений кубов для embed (бросок НЕ сохраняется на персонаже).</summary>
        private static string FormatDiceRoll(VampireCharacter character, VampireFrenzyResolver.FrenzyKind kind, IRandom rng)
        {
            var pool = VampireFrenzyResolver.ComputePoolSize(character, kind);
            if (pool == 0) return "—";
            var dice = new int[pool];
            for (var i = 0; i < pool; i++) dice[i] = rng.Next(1, 11);
            return string.Join(" ", dice);
        }
    }
}
