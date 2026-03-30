using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RPBot
{
    internal sealed class UiTextWriter : TextWriter
    {
        private readonly Func<BotUI?> _uiProvider;
        private readonly string? _terminalLogPath;
        private readonly object _fileLock = new();

        public UiTextWriter(Func<BotUI?> uiProvider, string? terminalLogPath = null)
        {
            _uiProvider = uiProvider;
           _terminalLogPath = terminalLogPath;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;

            // Удаляем ANSI/ESC последовательности и непечатаемые символы (кроме перевода строки)
            try
            {
                var cleaned = Regex.Replace(value, "\u001B\\[[0-9;?]*[ -/]*[@-~]", string.Empty);
                // Удаляем управляющие символы, кроме CR/LF
                cleaned = Regex.Replace(cleaned, "[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", string.Empty);
                if (string.IsNullOrWhiteSpace(cleaned)) return;

                var ui = _uiProvider?.Invoke();
                ui?.AddLog(cleaned);
               AppendToTerminalLog(cleaned);
            }
            catch
            {
                // В случае проблем с очисткой просто отправим оригинал
                var ui = _uiProvider?.Invoke();
                ui?.AddLog(value);
               AppendToTerminalLog(value);
            }
        }

        public override void WriteLine(string? value)
        {
           Write(value);
        }

        private void AppendToTerminalLog(string message)
        {
            if (string.IsNullOrWhiteSpace(_terminalLogPath))
                return;

            try
            {
                lock (_fileLock)
                {
                    var dir = Path.GetDirectoryName(_terminalLogPath) ?? AppContext.BaseDirectory;
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(_terminalLogPath, $"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
