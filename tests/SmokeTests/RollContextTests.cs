using System.Threading.Tasks;
using RPBot.SlashModules;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="RollContext"/> — DI-точки модуля бросков VtM.
/// </summary>
/// <remarks>
/// Контекст глобально-статический, поэтому тесты используют <see cref="RollContext.Reset"/>
/// перед каждой проверкой и общий lock для сериализации.
/// </remarks>
public class RollContextTests
{
    [Fact]
    public void Configure_SetsRegistry()
    {
        RollContext.Reset();
        var registry = new VampireRollRegistry();
        RollContext.Configure(registry, activeCharacterLookup: null);
        Assert.Same(registry, RollContext.Registry);
    }

    [Fact]
    public void Configure_StoresLookup()
    {
        RollContext.Reset();
        var registry = new VampireRollRegistry();
        Func<ulong, ulong, Task<VampireActiveContext?>> lookup =
            (guildId, userId) => Task.FromResult<VampireActiveContext?>(null);
        RollContext.Configure(registry, activeCharacterLookup: lookup);
        Assert.Same(lookup, RollContext.ActiveCharacterLookup);
    }

    [Fact]
    public void Configure_NullRegistry_Throws()
    {
        RollContext.Reset();
        Assert.Throws<System.ArgumentNullException>(() => RollContext.Configure(null!));
    }

    [Fact]
    public void Reset_ClearsState()
    {
        RollContext.Configure(new VampireRollRegistry());
        Assert.NotNull(RollContext.Registry);

        RollContext.Reset();
        Assert.Null(RollContext.Registry);
        Assert.Null(RollContext.ActiveCharacterLookup);
    }
}
