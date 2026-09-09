using System.Linq;
using Discord;
using RPBot.SlashModules;
using Xunit;

namespace RPBot.Tests;

/// <summary>
/// Тесты для <see cref="VampireTopLevelSlashModule"/> — 6 top-level команд:
/// <c>/vampire_create</c>, <c>/vampire_bind</c>, <c>/vampire_unbind</c>,
/// <c>/vampire_send</c>, <c>/vampire_show</c>, <c>/vampire_diablerie</c>.
/// </summary>
public class VampireTopLevelSlashModuleTests
{
    [Fact]
    public void Module_HasExpectedName()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        Assert.Equal("vampire-top-level", module.Name);
    }

    [Fact]
    public void CommandNames_ContainAllSix()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        Assert.Contains("vampire_create",    module.CommandNames);
        Assert.Contains("vampire_bind",      module.CommandNames);
        Assert.Contains("vampire_unbind",    module.CommandNames);
        Assert.Contains("vampire_send",      module.CommandNames);
        Assert.Contains("vampire_show",      module.CommandNames);
        Assert.Contains("vampire_diablerie", module.CommandNames);
    }

    [Fact]
    public void Module_RequiresCommandsInstance()
    {
        Assert.Throws<System.ArgumentNullException>(() => new VampireTopLevelSlashModule(null!));
    }

    [Fact]
    public void Register_ReturnsSixBuilders()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        var builders = module.Register();
        Assert.Equal(6, builders.Count);
        var names = builders.Select(b => b.Name).ToHashSet();
        Assert.Contains("vampire_create",    names);
        Assert.Contains("vampire_bind",      names);
        Assert.Contains("vampire_unbind",    names);
        Assert.Contains("vampire_send",      names);
        Assert.Contains("vampire_show",      names);
        Assert.Contains("vampire_diablerie", names);
    }

    [Fact]
    public void Register_AllHaveNonEmptyDescriptions()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        var built = module.Register().Select(b => b.Build()).ToList();
        Assert.All(built, b => Assert.False(string.IsNullOrWhiteSpace(b.Description.GetValueOrDefault())));
    }

    [Fact]
    public void Register_Create_HasNoOptions()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        var built = module.Register().Single(b => b.Name == "vampire_create").Build();
        var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();
        Assert.Empty(options);
    }

    [Fact]
    public void Register_Bind_RequiresName_HasOptionalUser()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        var built = module.Register().Single(b => b.Name == "vampire_bind").Build();
        var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

        var name = options.Single(o => o.Name == "name");
        Assert.True(name.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.String, name.Type);

        var user = options.Single(o => o.Name == "user");
        Assert.False(user.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.User, user.Type);
    }

    [Fact]
    public void Register_Unbind_RequiresName_NoUser()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        var built = module.Register().Single(b => b.Name == "vampire_unbind").Build();
        var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

        var name = options.Single(o => o.Name == "name");
        Assert.True(name.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.String, name.Type);

        // /vampire_unbind не имеет опции user — снимает привязку с любого.
        Assert.DoesNotContain(options, o => o.Name == "user");
    }

    [Fact]
    public void Register_Send_RequiresName_HasOptionalUser()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        var built = module.Register().Single(b => b.Name == "vampire_send").Build();
        var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

        var name = options.Single(o => o.Name == "name");
        Assert.True(name.IsRequired);

        var user = options.Single(o => o.Name == "user");
        Assert.False(user.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.User, user.Type);

        // /vampire_send не должен содержать `where` — это семантика /vampire_show.
        Assert.DoesNotContain(options, o => o.Name == "where");
    }

    [Fact]
    public void Register_Show_RequiresName_NoUserNoWhere()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        var built = module.Register().Single(b => b.Name == "vampire_show").Build();
        var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

        var name = options.Single(o => o.Name == "name");
        Assert.True(name.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.String, name.Type);

        Assert.DoesNotContain(options, o => o.Name == "user");
        Assert.DoesNotContain(options, o => o.Name == "where");
    }

    [Fact]
    public void Register_Diablerie_RequiresAttackerAndVictim()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        var built = module.Register().Single(b => b.Name == "vampire_diablerie").Build();
        var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

        var attacker = options.Single(o => o.Name == "attacker");
        Assert.True(attacker.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.User, attacker.Type);

        var victim = options.Single(o => o.Name == "victim");
        Assert.True(victim.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.User, victim.Type);

        var gen = options.Single(o => o.Name == "gen");
        Assert.False(gen.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.Integer, gen.Type);

        var hum = options.Single(o => o.Name == "hum");
        Assert.False(hum.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.Integer, hum.Type);

        var success = options.Single(o => o.Name == "success");
        Assert.False(success.IsRequired);
    }

    [Fact]
    public void Register_BindUnbindAndSend_RequireStPermissions()
    {
        var module = new VampireTopLevelSlashModule(new VampireCommands());
        var built = module.Register().ToDictionary(b => b.Name, b => b.Build());

        var bindPerms   = built["vampire_bind"].DefaultMemberPermissions.GetValueOrDefault();
        var unbindPerms = built["vampire_unbind"].DefaultMemberPermissions.GetValueOrDefault();
        var sendPerms   = built["vampire_send"].DefaultMemberPermissions.GetValueOrDefault();
        var createPerms = built["vampire_create"].DefaultMemberPermissions.GetValueOrDefault();
        var showPerms   = built["vampire_show"].DefaultMemberPermissions.GetValueOrDefault();

        Assert.True((bindPerms   & GuildPermission.ManageRoles) != 0, "bind должен требовать ManageRoles");
        Assert.True((unbindPerms & GuildPermission.ManageRoles) != 0, "unbind должен требовать ManageRoles");
        Assert.True((sendPerms   & GuildPermission.ManageRoles) != 0, "send должен требовать ManageRoles");

        Assert.Equal(default(GuildPermission), createPerms);
        Assert.Equal(default(GuildPermission), showPerms);
    }
}
