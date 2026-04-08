using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MetaService(StateType = typeof(PartyState))]
    public interface IPartyService : IMetaService
    {
        /// <summary>Add a hero to the party.</summary>
        [MetaMethod(Alias = "AddHero", Mode = ExecutionMode.Optimistic)]
        void AddHero(int id, string name, int level);

        /// <summary>
        /// Award exp to a specific hero. Looks up by Id via a helper method, then
        /// mutates the hero through the wrapper. Used to verify that:
        ///   - the helper method correctly returns HeroPatchWrapper (compile-time check)
        ///   - mutations through the wrapper produce a per-element patch sub-tree
        ///     (instead of a full collection snapshot)
        /// </summary>
        [MetaMethod(Alias = "AwardExp", Mode = ExecutionMode.Optimistic)]
        void AwardExp(int heroId, int amount);

        /// <summary>
        /// Mutate two distinct heroes in the same call. Patch should contain two
        /// element sub-trees, both under the Heroes field, but no full snapshot.
        /// </summary>
        [MetaMethod(Alias = "BatchUpdate", Mode = ExecutionMode.Optimistic)]
        void BatchUpdate(int firstId, int firstExpDelta, int secondId, int secondHpDelta);
    }
}
