using System;
using Xunit;
using RPBot.VtM;

namespace SmokeTests
{
    public class VampireMeritsFlawsTests
    {
        private static VampireCharacter NewDraft()
            => new VampireCharacter { CharacterId = Guid.NewGuid() };

        // ─── Каталог ──────────────────────────────────────────────────

        [Fact]
        public void Catalog_HasMerits()
        {
            Assert.NotEmpty(VampireMeritsFlawsCatalog.Merits);
            Assert.Contains(VampireMeritsFlawsCatalog.Merits, m => m.Name == "Обострённые чувства");
        }

        [Fact]
        public void Catalog_HasFlaws()
        {
            Assert.NotEmpty(VampireMeritsFlawsCatalog.Flaws);
            Assert.Contains(VampireMeritsFlawsCatalog.Flaws, f => f.Name == "Некрасивый");
        }

        [Fact]
        public void Catalog_AllCostsInRange1to7()
        {
            foreach (var m in VampireMeritsFlawsCatalog.Merits)
                Assert.True(VampireMeritsFlawsCatalog.IsValidCost(m.Cost),
                    $"Merit {m.Name} has cost {m.Cost} out of 1..7");
            foreach (var f in VampireMeritsFlawsCatalog.Flaws)
                Assert.True(VampireMeritsFlawsCatalog.IsValidCost(f.Cost),
                    $"Flaw {f.Name} has cost {f.Cost} out of 1..7");
        }

        [Fact]
        public void FindMerit_CaseInsensitive()
        {
            var m = VampireMeritsFlawsCatalog.FindMerit("обострённые чувства");
            Assert.NotNull(m);
            Assert.Equal("Обострённые чувства", m!.Name);
        }

        [Fact]
        public void FindFlaw_CaseInsensitive()
        {
            var f = VampireMeritsFlawsCatalog.FindFlaw("Некрасивый");
            Assert.NotNull(f);
            Assert.Equal("Некрасивый", f!.Name);
        }

        [Fact]
        public void FindMerit_Unknown_ReturnsNull()
        {
            Assert.Null(VampireMeritsFlawsCatalog.FindMerit("Пришелец из космоса"));
        }

        // ─── Add / Remove Merits ──────────────────────────────────────

        [Fact]
        public void AddMerit_FromCatalog_UsesCatalogCost()
        {
            var d = NewDraft();
            var r = VampireMeritsFlawsResolver.AddMeritFromCatalog(d, "Бдительный ум");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(1, d.Merits["Бдительный ум"]);
        }

        [Fact]
        public void AddMerit_OverrideCost_UsesProvidedCost()
        {
            var d = NewDraft();
            var r = VampireMeritsFlawsResolver.AddMerit(d, "Бдительный ум", cost: 3);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(3, d.Merits["Бдительный ум"]);
        }

        [Fact]
        public void AddMerit_Duplicate_Fails()
        {
            var d = NewDraft();
            VampireMeritsFlawsResolver.AddMeritFromCatalog(d, "Бдительный ум");
            var r = VampireMeritsFlawsResolver.AddMeritFromCatalog(d, "Бдительный ум");
            Assert.False(r.IsSuccess);
            Assert.Equal(VampireMeritsFlawsResolver.Failure.DuplicateMerit, r.Failure);
        }

        [Fact]
        public void AddMerit_Unknown_Fails()
        {
            var d = NewDraft();
            var r = VampireMeritsFlawsResolver.AddMerit(d, "Несуществующий merit", cost: 2);
            Assert.False(r.IsSuccess);
            Assert.Equal(VampireMeritsFlawsResolver.Failure.InvalidName, r.Failure);
        }

        [Fact]
        public void AddMerit_InvalidCost_Fails()
        {
            var d = NewDraft();
            // cost > 7 — ошибка.
            var r = VampireMeritsFlawsResolver.AddMerit(d, "Скорость", cost: 8);
            Assert.False(r.IsSuccess);
            Assert.Equal(VampireMeritsFlawsResolver.Failure.InvalidCost, r.Failure);

            // cost < 1 — ошибка.
            var r2 = VampireMeritsFlawsResolver.AddMerit(d, "Скорость", cost: 0);
            Assert.False(r2.IsSuccess);
            Assert.Equal(VampireMeritsFlawsResolver.Failure.InvalidCost, r2.Failure);

            var r3 = VampireMeritsFlawsResolver.AddMerit(d, "Скорость", cost: -1);
            Assert.False(r3.IsSuccess);
            Assert.Equal(VampireMeritsFlawsResolver.Failure.InvalidCost, r3.Failure);
        }

        [Fact]
        public void RemoveMerit_Works()
        {
            var d = NewDraft();
            VampireMeritsFlawsResolver.AddMeritFromCatalog(d, "Бдительный ум");
            var r = VampireMeritsFlawsResolver.RemoveMerit(d, "Бдительный ум");
            Assert.True(r.IsSuccess);
            Assert.Empty(d.Merits);
        }

        [Fact]
        public void RemoveMerit_NotFound_Fails()
        {
            var d = NewDraft();
            var r = VampireMeritsFlawsResolver.RemoveMerit(d, "Бдительный ум");
            Assert.False(r.IsSuccess);
            Assert.Equal(VampireMeritsFlawsResolver.Failure.NotFound, r.Failure);
        }

        // ─── Add / Remove Flaws ───────────────────────────────────────

        [Fact]
        public void AddFlaw_FromCatalog_UsesCatalogCost()
        {
            var d = NewDraft();
            var r = VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(1, d.Flaws["Некрасивый"]);
        }

        [Fact]
        public void AddFlaw_Duplicate_Fails()
        {
            var d = NewDraft();
            VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");
            var r = VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");
            Assert.False(r.IsSuccess);
            Assert.Equal(VampireMeritsFlawsResolver.Failure.DuplicateFlaw, r.Failure);
        }

        [Fact]
        public void RemoveFlaw_Works()
        {
            var d = NewDraft();
            VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");
            var r = VampireMeritsFlawsResolver.RemoveFlaw(d, "Некрасивый");
            Assert.True(r.IsSuccess);
            Assert.Empty(d.Flaws);
        }

        // ─── Sums ─────────────────────────────────────────────────────

        [Fact]
        public void MeritsCost_SumsAll()
        {
            var d = NewDraft();
            VampireMeritsFlawsResolver.AddMeritFromCatalog(d, "Бдительный ум"); // 1
            VampireMeritsFlawsResolver.AddMeritFromCatalog(d, "Скорость");       // 3
            Assert.Equal(4, VampireMeritsFlawsResolver.MeritsCost(d));
            Assert.Equal(2, VampireMeritsFlawsResolver.MeritsCount(d));
        }

        [Fact]
        public void FlawsCost_SumsAll()
        {
            var d = NewDraft();
            VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");       // 1
            VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Социальная неуклюжесть"); // 2
            Assert.Equal(3, VampireMeritsFlawsResolver.FlawsCost(d));
            Assert.Equal(2, VampireMeritsFlawsResolver.FlawsCount(d));
        }

        [Fact]
        public void EmptyDraft_ReturnsZero()
        {
            var d = NewDraft();
            Assert.Equal(0, VampireMeritsFlawsResolver.MeritsCost(d));
            Assert.Equal(0, VampireMeritsFlawsResolver.FlawsCost(d));
            Assert.Equal(0, VampireMeritsFlawsResolver.MeritsCount(d));
            Assert.Equal(0, VampireMeritsFlawsResolver.FlawsCount(d));
        }
    }
}
