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
            var module = new VampireSlashModule(new VampireCommands());
            var built = module.Register()[0].Build();
            var options = built.Options.GetValueOrDefault() ?? new System.Collections.Generic.List<Discord.ApplicationCommandOptionProperties>();

            var name = options.First(o => o.Name == "name");
            Assert.True(name.IsRequired);
            Assert.Equal(ApplicationCommandOptionType.String, name.Type);
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
