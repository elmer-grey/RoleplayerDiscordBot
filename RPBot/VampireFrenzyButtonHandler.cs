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
            var pool = VampireFrenzyResolver.ComputePoolSize(character, kind);
            var dice = new int[pool];
            for (var i = 0; i < pool; i++) dice[i] = _rng.Next(1, 11);
            var successes = 0;
            for (var i = 0; i < dice.Length; i++) if (dice[i] >= VampireFrenzyResolver.SuccessThreshold) successes++;
            var rolls = string.Join(" ", dice);

            string status;
            if (pool == 0 && kind == VampireFrenzyResolver.FrenzyKind.Frenzy && VampireFrenzyResolver.IsInstinctDriven(character))
            {
                status = $"🐾 Зверь не знает самоконтроля. *Бросок невозможен.*";
            }
            else if (successes == 0)
            {
                status = $"🔴 **{successes}** успехов — Зверь вырывается.\nКубики: {rolls}";
            }
            else
            {
                status = $"🎲 Бросок {KindName(kind)} (пул {pool}, сл. {difficulty}): **{successes}** успех(а).\nКубики: {rolls}";
            }

            var embed = VampireFrenzyEmbed.Build(character, status);
            await component.RespondAsync(embed: embed, ephemeral: true).ConfigureAwait(false);
        }

        private static string KindName(VampireFrenzyResolver.FrenzyKind kind) => kind switch
        {
            VampireFrenzyResolver.FrenzyKind.Frenzy => "Ярости",
            VampireFrenzyResolver.FrenzyKind.Rötschreck => "Ротшрека",
            _ => "?",
        };
    }
}
