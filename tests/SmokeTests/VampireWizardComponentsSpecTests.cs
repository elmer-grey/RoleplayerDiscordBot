using System.Linq;
using System.Reflection;
using Xunit;
using Discord;
using RPBot.VtM;

namespace SmokeTests
{
    public class VampireWizardComponentsSpecTests
    {
        private static VampireCharacter NewDraft(string clan = "")
        {
            var d = new VampireCharacter
            {
                CharacterId = System.Guid.NewGuid(),
                Clan = clan,
                AttributesPriority = VampireAttributePriority.PhysicalPrimary.ToString(),
                AbilitiesPriority = VampireAbilityPriority.TalentsPrimary.ToString(),
            };
            VampireAttributesResolver.ApplyClanRules(d);
            VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
            return d;
        }

        // Вызов internal static через рефлексию (чтобы обойти лимит 25 опций в BuildFinishingSelect,
        // который не связан со специализациями — это pre-existing проблема другого SelectMenu).
        private static SelectMenuBuilder? BuildSpecMenuDirect(VampireCharacter d)
        {
            var type = typeof(VampireWizardComponents);
            var mi = type.GetMethod("BuildSpecializationSelect", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(mi);
            return (SelectMenuBuilder?)mi!.Invoke(null, new object[] { d });
        }

        /// <summary>Когда freebie не распределены — меню специализаций возвращает null.</summary>
        [Fact]
        public void BuildSpecializationSelect_ReturnsNull_WhenFreebieRemaining()
        {
            var d = NewDraft();
            var menu = BuildSpecMenuDirect(d);
            Assert.Null(menu);
        }

        /// <summary>Когда freebie распределены — меню содержит параметры value >= 4.</summary>
        [Fact]
        public void BuildSpecializationSelect_ReturnsMenu_WhenFreebiesExhausted()
        {
            var d = NewDraft();
            // Сила: 1 + 3 = 4.
            VampireAttributesResolver.Increment(d, VampireAttributeGroup.Physical, "Сила");
            VampireAttributesResolver.Increment(d, VampireAttributeGroup.Physical, "Сила");
            VampireAttributesResolver.Increment(d, VampireAttributeGroup.Physical, "Сила");
            VampireFinishingResolver.MarkFreebiesExhausted(d);
            var menu = BuildSpecMenuDirect(d);
            Assert.NotNull(menu);
            Assert.Contains(menu.Options, o => o.Value == "Сила");
        }

        /// <summary>Меню содержит только опции для параметров с value >= 4.</summary>
        [Fact]
        public void BuildSpecializationSelect_ListsOnlyEligible()
        {
            var d = NewDraft();
            // Сила: 1 + 3 = 4 (пригодна).
            VampireAttributesResolver.Increment(d, VampireAttributeGroup.Physical, "Сила");
            VampireAttributesResolver.Increment(d, VampireAttributeGroup.Physical, "Сила");
            VampireAttributesResolver.Increment(d, VampireAttributeGroup.Physical, "Сила");
            // Атлетика: 0 + 3 = 3 (НЕ пригодна).
            VampireAbilitiesResolver.Increment(d, VampireAbilityGroup.Talents, "Атлетика");
            VampireAbilitiesResolver.Increment(d, VampireAbilityGroup.Talents, "Атлетика");
            VampireAbilitiesResolver.Increment(d, VampireAbilityGroup.Talents, "Атлетика");
            // Бдительность: 0 + 3 = 3 (НЕ пригодна).
            VampireAbilitiesResolver.Increment(d, VampireAbilityGroup.Talents, "Бдительность");
            VampireAbilitiesResolver.Increment(d, VampireAbilityGroup.Talents, "Бдительность");
            VampireAbilitiesResolver.Increment(d, VampireAbilityGroup.Talents, "Бдительность");
            VampireFinishingResolver.MarkFreebiesExhausted(d);

            var menu = BuildSpecMenuDirect(d);
            Assert.NotNull(menu);
            var options = menu!.Options.ToList();
            Assert.Contains(options, o => o.Value == "Сила");
            Assert.DoesNotContain(options, o => o.Value == "Атлетика");
            Assert.DoesNotContain(options, o => o.Value == "Бдительность");
        }

        /// <summary>Для Носферату Привлекательность (база=0) не попадает в список.</summary>
        [Fact]
        public void BuildSpecializationSelect_Nosferatu_AppearanceExcluded()
        {
            var d = NewDraft("Носферату");
            VampireAttributesResolver.Increment(d, VampireAttributeGroup.Physical, "Сила");
            VampireAttributesResolver.Increment(d, VampireAttributeGroup.Physical, "Сила");
            VampireAttributesResolver.Increment(d, VampireAttributeGroup.Physical, "Сила");
            VampireFinishingResolver.MarkFreebiesExhausted(d);

            var menu = BuildSpecMenuDirect(d);
            Assert.NotNull(menu);
            var options = menu!.Options.ToList();
            Assert.Contains(options, o => o.Value == "Сила");
            Assert.DoesNotContain(options, o => o.Value == "Привлекательность");
        }

        /// <summary>Если нет ни одного параметра с value >= 4, меню возвращает null.</summary>
        [Fact]
        public void BuildSpecializationSelect_ReturnsNull_WhenNoEligible()
        {
            var d = NewDraft();
            VampireFinishingResolver.MarkFreebiesExhausted(d);
            var menu = BuildSpecMenuDirect(d);
            Assert.Null(menu);
        }
    }
}
