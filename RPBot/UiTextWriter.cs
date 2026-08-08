using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RPBot
{
    internal sealed class UiTextWriter : TextWriter
    {
        private readonly Func<BotUI?> _uiProvider;

        public UiTextWriter(Func<BotUI?> uiProvider)
        {
            _uiProvider = uiProvider;
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
            // Дописываем пустую строку, чтобы UI отрисовал перевод строки
            var ui = _uiProvider?.Invoke();
            ui?.AddLog(string.Empty);
        }

        public override void WriteLine()
        {
            var ui = _uiProvider?.Invoke();
            ui?.AddLog(string.Empty);
        }
    }
}
