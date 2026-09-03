using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.SlashModules;

/// <summary>
/// Модуль slash-команд. Каждый модуль — отдельный файл/класс,
/// реализующий две обязанности: объявить свои команды и обработать входящие.
/// </summary>
/// <remarks>
/// <para>Идея — расширяемость: чтобы добавить новый набор команд, достаточно
/// создать класс и зарегистрировать его в <see cref="SlashModuleRegistry"/>.</para>
/// <para>Используется тот же жизненный цикл, что у остальных команд проекта:
/// регистрация билдеров — в <c>CommandHandler.GetAllCommands()</c>,
/// диспатч — единая точка <c>Program.OnSlashCommandExecuted</c>, которая
/// делегирует <see cref="DispatchAsync"/> нужному модулю по имени команды.</para>
/// </remarks>
public interface ISlashCommandModule
{
    /// <summary>Человеко-читаемое имя модуля (для логов).</summary>
    string Name { get; }

    /// <summary>Имена команд, которые обрабатывает этот модуль. Используется для диспатча.</summary>
    IReadOnlyCollection<string> CommandNames { get; }

    /// <summary>Вернуть список билдеров команд модуля.</summary>
    IReadOnlyList<SlashCommandBuilder> Register();

    /// <summary>Обработать входящую slash-команду модуля.</summary>
    /// <returns><c>true</c>, если команда была распознана и обработана; иначе модуль не отвечает.</returns>
    Task<bool> DispatchAsync(SocketSlashCommand command);
}

/// <summary>
/// Глобальный реестр модулей. Сейчас модули регистрируются вручную
/// (в <c>Program.Main</c> при старте). При необходимости легко перейти
/// на автоматическое сканирование через DI или рефлексию.
/// </summary>
public static class SlashModuleRegistry
{
    private static readonly List<ISlashCommandModule> _modules = new();

    /// <summary>Зарегистрировать модуль (без дублей).</summary>
    public static void Register(ISlashCommandModule module)
    {
        if (module is null) throw new ArgumentNullException(nameof(module));
        if (_modules.Any(m => m.Name == module.Name))
            throw new InvalidOperationException($"Модуль с именем «{module.Name}» уже зарегистрирован.");
        _modules.Add(module);
    }

    /// <summary>Сброс реестра (для тестов).</summary>
    public static void Reset()
    {
        _modules.Clear();
    }

    /// <summary>Все зарегистрированные модули (только чтение).</summary>
    public static IReadOnlyList<ISlashCommandModule> All => _modules;

    /// <summary>Найти модуль по имени команды. <c>null</c>, если никто не заявил.</summary>
    public static ISlashCommandModule? FindByCommand(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName)) return null;
        foreach (var m in _modules)
        {
            foreach (var n in m.CommandNames)
            {
                if (string.Equals(n, commandName, StringComparison.OrdinalIgnoreCase))
                    return m;
            }
        }
        return null;
    }
}
