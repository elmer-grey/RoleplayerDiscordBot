using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Создает красивые embed сообщения о состоянии бота
    /// </summary>
    public static class StatusMessageBuilder
    {
        /// <summary>
        /// Создает сообщение об успешном подключении/переподключении
        /// </summary>
        public static Embed BuildConnectionSuccessEmbed(
            ConnectionStateInfo info,
            bool isReconnect,
            ConnectionPredictor predictor)
        {
            var embed = new EmbedBuilder()
                .WithTitle(isReconnect ? "✅ Бот переподключен" : "✅ Бот запущен")
                .WithColor(Color.Green)
                .WithDescription(isReconnect
                    ? "Соединение с Discord восстановлено"
                    : "Бот успешно подключен к Discord")
                .AddField("📋 Статус", "Онлайн", true)
                .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                .AddField("🔌 Причина", info.LastConnectReason, true);

            if (isReconnect)
            {
                embed.AddField("⚠️ Отключение", info.LastDisconnectReason, true)
                     .AddField("🔄 Попыток", info.ReconnectAttempts.ToString(), true)
                     .AddField("📊 Успешно", info.SuccessfulReconnects.ToString(), true);
            }

            // Статистика отключений
            if (info.DisconnectStats.Count > 0)
            {
                var topReasons = info.DisconnectStats
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => $"{kv.Key}: {kv.Value} раз");

                embed.AddField("📊 Статистика", string.Join("\n", topReasons), true);
            }

            // Здоровье соединения
            var health = predictor?.GetConnectionHealthStatus() ?? "🟡 Неизвестно";
            embed.AddField("💓 Здоровье", health, true);

            embed.WithFooter(f => f.Text = isReconnect ? "Реконнект выполнен" : "Первичный запуск")
                 .WithCurrentTimestamp();

            return embed.Build();
        }

        /// <summary>
        /// Создает сообщение об отключении
        /// </summary>
        public static Embed BuildDisconnectEmbed(ConnectionStateInfo info)
        {
            var embed = new EmbedBuilder()
                .WithTitle("⚠️ Отключение бота")
                .WithColor(Color.Red)
                .WithDescription("Бот был отключен от Discord")
                .AddField("📋 Причина", info.LastDisconnectReason, true)
                .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                .AddField("🔄 Статус", "Пытаюсь переподключиться...", true);

            if (!string.IsNullOrEmpty(info.LastDisconnectDetails))
            {
                var details = info.LastDisconnectDetails.Length > 100
                    ? info.LastDisconnectDetails.Substring(0, 100) + "..."
                    : info.LastDisconnectDetails;

                embed.AddField("📝 Детали", details, false);
            }

            embed.WithFooter(f => f.Text = "Инициирован реконнект")
                 .WithCurrentTimestamp();

            return embed.Build();
        }

        /// <summary>
        /// Создает сообщение-прогноз
        /// </summary>
        public static Embed BuildPredictionEmbed(ConnectionPredictor.PredictionResult prediction)
        {
            var embed = new EmbedBuilder()
                .WithTitle("🔮 Прогноз отключения")
                .WithColor(Color.Orange)
                .WithDescription("Бот прогнозирует возможное отключение")
                .AddField("⏱️ Предполагаемое время",
                    prediction.PredictedTime.ToString("HH:mm:ss"), true)
                .AddField("📊 Уверенность", $"{prediction.Confidence}%", true)
                .AddField("📋 Причина", prediction.Reason, true);

            if (!string.IsNullOrEmpty(prediction.Recommendation))
            {
                embed.AddField("💡 Рекомендация", prediction.Recommendation, false);
            }

            embed.WithFooter(f => f.Text = "Автоматический прогноз")
                 .WithCurrentTimestamp();

            return embed.Build();
        }
    }
}
