using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.SlashModules;
using Xunit;

namespace RPBot.Tests;

/// <summary>
/// Stub-модуль для тестов реестра. Не делает ничего, только возвращает
/// заранее заданный билдер и поддерживает флаг «был ли вызван Dispatch».
/// </summary>
internal sealed class StubModule : ISlashCommandModule
{
    public StubModule(string name, string command)
    {
        Name = name;
        CommandNames = new[] { command };
    }

    public string Name { get; }
    public IReadOnlyCollection<string> CommandNames { get; }
    public int DispatchCallCount { get; private set; }

    public IReadOnlyList<SlashCommandBuilder> Register() => new List<SlashCommandBuilder>
    {
        new SlashCommandBuilder().WithName(CommandNames.First()).WithDescription("stub"),
    };

    public Task<bool> DispatchAsync(SocketSlashCommand command)
    {
        DispatchCallCount++;
        return Task.FromResult(true);
    }
}

[Collection("SlashModuleRegistry.Sequential")] // (см. CollectionDefinition ниже — глобально сериализуем)
public class SlashModuleRegistryTests
{
    [Fact]
    public void Register_Adds_Module()
    {
        var before = SlashModuleRegistry.All.Count;
        var stub = new StubModule("stub-mod-1", "stub_cmd_1");
        SlashModuleRegistry.Register(stub);
        try
        {
            Assert.Contains(stub, SlashModuleRegistry.All);
            Assert.True(SlashModuleRegistry.All.Count >= before + 1);
        }
        finally
        {
            SlashModuleRegistry.Reset();
        }
    }

    [Fact]
    public void Register_Duplicate_Name_Throws()
    {
        var a = new StubModule("dup-mod", "dup_cmd_a");
        var b = new StubModule("dup-mod", "dup_cmd_b");
        SlashModuleRegistry.Register(a);
        try
        {
            Assert.Throws<InvalidOperationException>(() => SlashModuleRegistry.Register(b));
        }
        finally
        {
            SlashModuleRegistry.Reset();
        }
    }

    [Fact]
    public void FindByCommand_Returns_Registered_Module()
    {
        var stub = new StubModule("find-mod", "find_cmd");
        SlashModuleRegistry.Register(stub);
        try
        {
            var found = SlashModuleRegistry.FindByCommand("find_cmd");
            Assert.Same(stub, found);
        }
        finally
        {
            SlashModuleRegistry.Reset();
        }
    }

    [Fact]
    public void FindByCommand_Is_CaseInsensitive()
    {
        var stub = new StubModule("case-mod", "Case_Cmd");
        SlashModuleRegistry.Register(stub);
        try
        {
            Assert.Same(stub, SlashModuleRegistry.FindByCommand("case_cmd"));
            Assert.Same(stub, SlashModuleRegistry.FindByCommand("CASE_CMD"));
        }
        finally
        {
            SlashModuleRegistry.Reset();
        }
    }

    [Fact]
    public void FindByCommand_Unknown_Returns_Null()
    {
        var stub = new StubModule("single-mod", "single_cmd");
        SlashModuleRegistry.Register(stub);
        try
        {
            Assert.Null(SlashModuleRegistry.FindByCommand("does_not_exist"));
        }
        finally
        {
            SlashModuleRegistry.Reset();
        }
    }

    [Fact]
    public void Register_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SlashModuleRegistry.Register(null!));
    }

    [Fact]
    public void Reset_Clears_Modules()
    {
        SlashModuleRegistry.Register(new StubModule("reset-mod", "reset_cmd"));
        Assert.NotEmpty(SlashModuleRegistry.All);
        SlashModuleRegistry.Reset();
        Assert.Empty(SlashModuleRegistry.All);
    }
}

/// <summary>
/// Глобальный сериализатор для всех тестов, трогающих глобальный
/// <see cref="SlashModuleRegistry"/>, чтобы прогон был предсказуемым.
/// </summary>
[CollectionDefinition("SlashModuleRegistry.Sequential", DisableParallelization = true)]
public class SlashModuleRegistryCollection { }
