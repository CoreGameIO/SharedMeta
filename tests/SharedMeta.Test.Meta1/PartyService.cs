using System.Linq;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MetaServiceImpl(typeof(IPartyService), typeof(PartyState), DeepDesync = true)]
    public partial class PartyService : IPartyService
    {
        private PartyState state => State;

        public void AddHero(int id, string name, int level)
        {
            state.Heroes.Add(new Hero
            {
                Id = id,
                Name = name,
                Level = level,
                Hp = 100,
                Exp = 0,
            });
        }

        // LocalQuery reads: run on the client over replicated State, no server round-trip.
        // Read-only by contract (validated by the 0.29.1 read-only validator).
        public int CountHeroesAtLevel(int minLevel)
        {
            return state.Heroes.Count(h => h.Level >= minLevel);
        }

        public int TotalLevels()
        {
            return state.Heroes.Sum(h => h.Level);
        }

        public int HeroCount()
        {
            return state.Heroes.Count;
        }

        public void AwardExp(int heroId, int amount)
        {
            // The helper returns a HeroPatchWrapper in the _PatchTracked copy of this
            // class (state.Heroes is the specialized HeroPatchableList, FirstOrDefault
            // yields HeroPatchWrapper). In the regular copy state.Heroes is a plain
            // List<Hero> and FirstOrDefault yields Hero, which the implicit operator
            // wraps into a (untracked) HeroPatchWrapper. The same source compiles in
            // both branches, but only the PatchTracked branch produces a real patch.
            var hero = FindById(heroId);
            if (hero == null) return;
            hero.Exp += amount;
        }

        public void BatchUpdate(int firstId, int firstExpDelta, int secondId, int secondHpDelta)
        {
            var first = FindById(firstId);
            var second = FindById(secondId);
            if (first != null) first.Exp += firstExpDelta;
            if (second != null) second.Hp += secondHpDelta;
        }

        public void RemoveHero(int index)
        {
            state.Heroes.RemoveAt(index);
        }

        public void InsertHero(int index, int id, string name, int level)
        {
            state.Heroes.Insert(index, new Hero
            {
                Id = id,
                Name = name,
                Level = level,
                Hp = 100,
                Exp = 0,
            });
        }

        public void ReplaceHero(int index, int id, string name, int level)
        {
            state.Heroes[index] = new Hero
            {
                Id = id,
                Name = name,
                Level = level,
                Hp = 100,
                Exp = 0,
            };
        }

        public void ClearHeroes()
        {
            state.Heroes.Clear();
        }

        public void AddItemToHero(int heroIndex, int itemId, string itemName, int power)
        {
            // Two-level nested case: indexer into Heroes returns HeroPatchWrapper bound
            // to its element subtree node, then .Equipment is the inner specialized list
            // bound to a child of that subtree node. Add an item there → structural op
            // on the inner collection node, which propagates HasChanges all the way up.
            state.Heroes[heroIndex].Equipment.Add(new Item
            {
                ItemId = itemId,
                Name = itemName,
                Power = power,
            });
        }

        public void UpgradeItem(int heroIndex, int itemIndex, int newPower)
        {
            // Two-level nested + element mutation: hero[i].Equipment[j].Power = X.
            // Both indexers create element-by-index subtree nodes, then .Power setter
            // writes a Field child terminal at the deepest level.
            state.Heroes[heroIndex].Equipment[itemIndex].Power = newPower;
        }

        public void MixedShift(int firstIdxToRemove, int secondHeroId, int expDelta)
        {
            // First record an element-subtree mutation for secondHero (via id lookup,
            // which gives the wrapper-bound element child at its current index).
            var second = FindById(secondHeroId);
            if (second != null) second.Exp += expDelta;

            // Then a structural op that shifts indices. The element child for secondHero
            // (if its index is > firstIdxToRemove) must shift down by 1 — the specialized
            // list class handles this via ShiftElementChildren.
            state.Heroes.RemoveAt(firstIdxToRemove);
        }

        public void RegenerateAndAdd(int newHeroId, string newHeroName)
        {
            // Wholesale replacement followed by an in-place mutation in the same call.
            // This exercises the FullReplace structural op path: the setter records
            // FullReplace(packed empty list), then Add records Insert. On the receiver
            // both ops apply in submission order, so the new hero must materialize.
            state.Heroes = new System.Collections.Generic.List<Hero>();
            state.Heroes.Add(new Hero
            {
                Id = newHeroId,
                Name = newHeroName,
                Level = 1,
                Hp = 100,
                Exp = 0,
            });
        }

        // Helper method that must be typed as PartyStatePatchWrapper.HeroPatchWrapper
        // (the wrapper returned from state.Heroes in the _PatchTracked copy). Returning
        // raw Hero here would compile fine in the regular copy but FAIL the _PatchTracked
        // copy because there's no implicit HeroPatchWrapper → Hero conversion. This is
        // the compile-time guard that catches "silent loss of patch tracking" bugs.
        // The type system enforces it; no analyzer or runtime check needed.
        private PartyStatePatchWrapper.HeroPatchWrapper? FindById(int heroId)
        {
            return state.Heroes.FirstOrDefault(h => h.Id == heroId);
        }
    }
}
