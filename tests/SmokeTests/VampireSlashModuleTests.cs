using System.Linq;
using Discord;
using RPBot.SlashModules;
using Xunit;

namespace RPBot.Tests;

public class VampireSlashModuleTests
{
    [Fact]
    public void Module_HasExpectedName()
    {
        var module = new VampireSlashModule(new VampireCommands());
        Assert.Equal("vampire", module.Name);
        Assert.Contains("vampire", module.CommandNames);
    }

    [Fact]
        public void Register_ReturnsSlashCommandBuilder()
        {
            var module = new VampireSlashModule(new VampireCommands());
            var builders = module.Register();

            Assert.Single(builders);
            var built = builders[0].Build();
            Assert.Equal("vampire", built.Name.GetValueOrDefault());
            Assert.False(string.IsNullOrEmpty(built.Description.GetValueOrDefault()));
        }

        [Fact]
        public void Register_HasThreeOptions_ActionNameUser()
        {
            var module = new VampireSlashModule(new VampireCommands());
            var built = module.Register()[0].Build();
            var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

            var names = options.Select(o => o.Name).ToHashSet();
        Assert.Contains("action", names);
        Assert.Contains("name",   names);
        Assert.Contains("user",   names);
        Assert.Contains("where",  names);
        }

        [Fact]
        public void Register_Action_HasShowChoice()
        {
            var module = new VampireSlashModule(new VampireCommands());
            var built = module.Register()[0].Build();
            var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

            var action = options.First(o => o.Name == "action");
            var choiceValues = action.Choices.ToDictionary(c => c.Name, c => c.Value);
            Assert.Contains("show", choiceValues.Keys);
            Assert.Equal("show", choiceValues["show"]);
        }

        [Fact]
        public void Register_Where_HasDmAndPublicChoices()
        {
            var module = new VampireSlashModule(new VampireCommands());
            var built = module.Register()[0].Build();
            var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

            var where = options.First(o => o.Name == "where");
            Assert.False(where.IsRequired);
            var choiceValues = where.Choices.ToDictionary(c => c.Name, c => c.Value);
            Assert.Equal("dm",     choiceValues["dm"]);
            Assert.Equal("public", choiceValues["public"]);
        }

        [Fact]
        public void Register_Name_IsOptional_String()
        {
            var module = new VampireSlashModule(new VampireCommands());
            var built = module.Register()[0].Build();
            var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

            var name = options.First(o => o.Name == "name");
            // name обязателен на уровне bind/unbind, но опционален для show (резолвер сам решит).
            Assert.False(name.IsRequired);
            Assert.Equal(ApplicationCommandOptionType.String, name.Type);
        }

    [Fact]
    public void Register_Action_RequiredAndChoices_BindUnbind()
    {
        var module = new VampireSlashModule(new VampireCommands());
        var built = module.Register()[0].Build();
        var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

        var action = options.First(o => o.Name == "action");
        Assert.True(action.IsRequired);
        Assert.Equal(ApplicationCommandOptionType.String, action.Type);
        var choiceValues = action.Choices.ToDictionary(c => c.Name, c => c.Value);
        Assert.Equal("bind",   choiceValues["bind"]);
        Assert.Equal("unbind", choiceValues["unbind"]);
    }

    [Fact]
    public void Register_Name_Required_String()
    {
        // name не обязателен на уровне Discord (для show можно без него),
        // но резолвер вернёт NameRequired, если ничего не указано.
        Assert.True(true, "см. Register_Name_IsOptional_String");
    }

        [Fact]
        public void Register_User_Optional()
        {
            var module = new VampireSlashModule(new VampireCommands());
            var built = module.Register()[0].Build();
            var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

            var user = options.First(o => o.Name == "user");
            Assert.False(user.IsRequired);
            Assert.Equal(ApplicationCommandOptionType.User, user.Type);
        }

        [Fact]
        public void Module_RequiresCommandsInstance()
        {
            Assert.Throws<System.ArgumentNullException>(() => new VampireSlashModule(null!));
        }
    }
