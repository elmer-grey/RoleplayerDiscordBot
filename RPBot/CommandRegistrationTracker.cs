using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RPBot
{
    public class CommandRegistrationTracker
    {
        private readonly List<string> _allCommands = new();
        private readonly List<string> _registeredCommands = new();
        private DateTime _startTime;

        public event Func<int, int, string, Task> OnProgressUpdated;
        public event Func<Task> OnRegistrationCompleted;

        public void StartRegistration(IEnumerable<string> commands)
        {
            _startTime = DateTime.UtcNow;
            _allCommands.Clear();
            _registeredCommands.Clear();
            _allCommands.AddRange(commands);

            _ = Task.Run(async () =>
            {
                // Имитация прогресса - в реальности вызывайте CommandRegistered()
                for (int i = 0; i <= _allCommands.Count; i++)
                {
                    await Task.Delay(50); // Симуляция
                    var percent = (int)((double)i / _allCommands.Count * 100);
                    await OnProgressUpdated?.Invoke(i, _allCommands.Count,
                        $"Регистрация команд: {percent}% ({i}/{_allCommands.Count})");
                }

                await OnRegistrationCompleted?.Invoke();
            });
        }

        public void CommandRegistered(string commandName)
        {
            if (!_registeredCommands.Contains(commandName))
            {
                _registeredCommands.Add(commandName);
                var percent = (int)((double)_registeredCommands.Count / _allCommands.Count * 100);

                var timeElapsed = DateTime.UtcNow - _startTime;
                var eta = timeElapsed.TotalSeconds / _registeredCommands.Count *
                         (_allCommands.Count - _registeredCommands.Count);

                OnProgressUpdated?.Invoke(
                    _registeredCommands.Count,
                    _allCommands.Count,
                    $"📝 {percent}% | +{commandName} | Осталось: {eta:F1}с"
                );
            }
        }

        public int GetProgress() =>
            _allCommands.Count == 0 ? 0 :
            (int)((double)_registeredCommands.Count / _allCommands.Count * 100);
    }
}