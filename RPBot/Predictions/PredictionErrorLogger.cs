using System;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Единая точка для записи ошибок. Раньше жёстко использовала LogCategory.Predict,
    /// поэтому ошибки от /roll, /help и других команд попадали в файл Predict.log с
    /// префиксом [Predict]. Сейчас категория задаётся явно (по умолчанию Predict — для
    /// обратной совместимости со старыми вызывающими).
    /// </summary>
    public static class PredictionErrorLogger
    {
        public static Task LogAsync(string context, Exception ex, string? details = null,
            LogCategory category = LogCategory.Predict)
        {
            var message = details == null
                ? $"{context}: {ex}"
                : $"{context}: {details}{Environment.NewLine}{ex}";
            return LogAsync(message, category);
        }

        public static Task LogAsync(string message, LogCategory category = LogCategory.Predict)
        {
            BotLogger.Error(category, message);
            return Task.CompletedTask;
        }
    }
}
