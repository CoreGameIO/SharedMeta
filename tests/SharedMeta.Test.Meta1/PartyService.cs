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
