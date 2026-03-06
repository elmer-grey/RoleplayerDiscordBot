using System.IO;
using System.Text;

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
            var ui = _uiProvider?.Invoke();
            ui?.AddLog(value);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
        }
    }
}
