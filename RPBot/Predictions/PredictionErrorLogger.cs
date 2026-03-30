using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
	public static class PredictionErrorLogger
	{
		private static readonly SemaphoreSlim _lock = new(1, 1);

		public static async Task LogAsync(string context, Exception ex, string? details = null)
		{
			var message = details == null
				? $"{context}: {ex}"
				: $"{context}: {details}{Environment.NewLine}{ex}";
			await LogAsync(message).ConfigureAwait(false);
		}

		public static async Task LogAsync(string message)
		{
			try
			{
				var logDirRaw = BotConfig.Current?.LogDirectory;
				var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
				Directory.CreateDirectory(logDir);
				var path = Path.Combine(logDir, $"PredictionErrorLog_{DateTime.Now:yyyyMMdd}.txt");

				await _lock.WaitAsync().ConfigureAwait(false);
				try
				{
					await File.AppendAllTextAsync(path, $"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8).ConfigureAwait(false);
				}
				finally
				{
					_lock.Release();
				}
			}
			catch
			{
				// ignore
			}
		}
	}
}
