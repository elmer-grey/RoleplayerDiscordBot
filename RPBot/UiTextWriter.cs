using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RPBot
{
    internal sealed class UiTextWriter : TextWriter
    {
        private readonly Func<BotUI?> _uiProvider;
        private readonly bool _includeConsoleColors;
        private static readonly object _consoleLock = new();

        public UiTextWriter(Func<BotUI?> uiProvider, bool includeConsoleColors = false)
        {
            _uiProvider = uiProvider;
            _includeConsoleColors = includeConsoleColors;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;

            // Удаляем ANSI/ESC последовательности и непечатаемые символы (кроме перевода строки)
            string cleaned;
            try
            {
                cleaned = Regex.Replace(value, "\u001B\\[[0-9;?]*[ -/]*[@-~]", string.Empty);
                cleaned = Regex.Replace(cleaned, "[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", string.Empty);
                if (string.IsNullOrWhiteSpace(cleaned)) return;
            }
            catch
            {
                cleaned = value;
            }

            var ui = _uiProvider?.Invoke();
            ui?.AddLog(cleaned);
        }

        public override void WriteLine(string? value)
        {
            if (string.IsNullOrEmpty(value)) { WriteLine(); return; }
            Write(value);
            // Дописываем перевод строки в UI-панель и в реальную консоль
            var ui = _uiProvider?.Invoke();
            ui?.AddLog(string.Empty);
            if (_includeConsoleColors)
            {
                try
                {
                    lock (_consoleLock)
                    {
                        Console.WriteLine();
                    }
                }
                catch { }
            }
        }

        public override void WriteLine()
        {
            var ui = _uiProvider?.Invoke();
            ui?.AddLog(string.Empty);
            if (_includeConsoleColors)
            {
                try
                {
                    lock (_consoleLock) { Console.WriteLine(); }
                }
                catch { }
            }
        }
    }
}
