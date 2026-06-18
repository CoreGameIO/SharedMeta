using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MetaService(StateType = typeof(PartyState))]
    public interface IPartyService : IMetaService
    {
        /// <summary>Add a hero to the party.</summary>
        [MetaMethod(Alias = "AddHero", Mode = ExecutionMode.Optimistic, Sync = SyncApi.Generate)]
        void AddHero(int id, string name, int level);

        /// <summary>
        /// LocalQuery: count heroes at or above <paramref name="minLevel"/>. Pure client-side read
        /// over locally replicated State — no RPC. Sync defaults to OnlySync for LocalQuery, so the
        /// generator emits only <c>CountHeroesAtLevelSync(int)</c> (no async overload).
        /// </summary>
        [MetaMethod(Alias = "CountHeroesAtLevel", Mode = ExecutionMode.LocalQuery)]
        int CountHeroesAtLevel(int minLevel);

        /// <summary>
        /// LocalQuery with explicit Sync = Generate — generator emits BOTH <c>TotalLevelsSync()</c>
        /// and <c>TotalLevelsAsync()</c> (the async wrapper completes synchronously, no RPC).
        /// </summary>
        [MetaMethod(Alias = "TotalLevels", Mode = ExecutionMode.LocalQuery, Sync = SyncApi.Generate)]
        int TotalLevels();

        /// <summary>
        /// LocalQuery with explicit Sync = None — generator emits ONLY <c>HeroCountAsync()</c>
        /// (no sync overload), honouring the author's explicit choice.
        /// </summary>
        [MetaMethod(Alias = "HeroCount", Mode = ExecutionMode.LocalQuery, Sync = SyncApi.None)]
        int HeroCount();

        /// <summary>
        /// Award exp to a specific hero. Looks up by Id via a helper method, then
        /// mutates the hero through the wrapper. Used to verify that:
        ///   - the helper method correctly returns HeroPatchWrapper (compile-time check)
        ///   - mutations through the wrapper produce a per-element patch sub-tree
        ///     (instead of a full collection snapshot)
        /// </summary>
        [MetaMethod(Alias = "AwardExp", Mode = ExecutionMode.Optimistic, Sync = SyncApi.Generate)]
        void AwardExp(int heroId, int amount);

        /// <summary>
        /// Mutate two distinct heroes in the same call. Patch should contain two
        /// element sub-trees, both under the Heroes field, but no full snapshot.
        /// </summary>
        [MetaMethod(Alias = "BatchUpdate", Mode = ExecutionMode.Optimistic)]
        void BatchUpdate(int firstId, int firstExpDelta, int secondId, int secondHpDelta);

        /// <summary>Remove the hero at the given index. Phase 2: structural op.</summary>
        [MetaMethod(Alias = "RemoveHero", Mode = ExecutionMode.Optimistic)]
        void RemoveHero(int index);

        /// <summary>Insert a hero at the given index. Phase 2: structural op.</summary>
        [MetaMethod(Alias = "InsertHero", Mode = ExecutionMode.Optimistic)]
        void InsertHero(int index, int id, string name, int level);

        /// <summary>Replace the hero at the given index wholesale. Phase 2: Set op.</summary>
        [MetaMethod(Alias = "ReplaceHero", Mode = ExecutionMode.Optimistic)]
        void ReplaceHero(int index, int id, string name, int level);

        /// <summary>Clear the entire hero list.</summary>
        [MetaMethod(Alias = "ClearHeroes", Mode = ExecutionMode.Optimistic)]
        void ClearHeroes();

        /// <summary>
        /// Two-level nested case: add an item to the equipment list of a specific hero
        /// (looked up by index). Verifies that nested element-sub-wrappable lists work
        /// end-to-end — Heroes[i].Equipment.Add(item) flows through one collection node
        /// nested inside another's element subtree.
        /// </summary>
        [MetaMethod(Alias = "AddItemToHero", Mode = ExecutionMode.Optimistic)]
        void AddItemToHero(int heroIndex, int itemId, string itemName, int power);

        /// <summary>
        /// Two-level nested case + element mutation: bump the Power field of a specific
        /// item on a specific hero. <c>Heroes[i].Equipment[j].Power = X</c>.
        /// </summary>
        [MetaMethod(Alias = "UpgradeItem", Mode = ExecutionMode.Optimistic)]
        void UpgradeItem(int heroIndex, int itemIndex, int newPower);

        /// <summary>
        /// Mixed structural op + element mutation in the SAME call: shifts indices.
        /// AwardExp(secondId) is recorded as element subtree, then RemoveHero(firstIdx)
        /// shifts the existing element child down. The applied patch must still find
        /// the correct (post-shift) hero and apply its mutation there.
        /// </summary>
        [MetaMethod(Alias = "MixedShift", Mode = ExecutionMode.Optimistic)]
        void MixedShift(int firstIdxToRemove, int secondHeroId, int expDelta);

        /// <summary>
        /// Reassign-then-mutate: replace the entire Heroes list with a fresh empty
        /// instance and then add a hero in the same call. Repro for the bug where the
        /// list-field setter wrote a terminal Value, the subsequent Add appended a
        /// structural op, and the receiver's early-return on Value silently dropped
        /// the op (so client never saw the new hero).
        /// </summary>
        [MetaMethod(Alias = "RegenerateAndAdd", Mode = ExecutionMode.Optimistic)]
        void RegenerateAndAdd(int newHeroId, string newHeroName);
    }
}
