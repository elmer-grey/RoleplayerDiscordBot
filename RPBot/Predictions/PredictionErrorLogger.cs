using System;
using System.Threading.Tasks;

namespace RPBot
{
	public static class PredictionErrorLogger
	{
		public static Task LogAsync(string context, Exception ex, string? details = null)
		{
			var message = details == null
				? $"{context}: {ex}"
				: $"{context}: {details}{Environment.NewLine}{ex}";
			return LogAsync(message);
		}

		public static Task LogAsync(string message)
		{
			BotLogger.Error(LogCategory.Predict, message);
			return Task.CompletedTask;
		}
	}
}
