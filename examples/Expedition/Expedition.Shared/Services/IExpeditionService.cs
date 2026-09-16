using SharedMeta.Core;

namespace Expedition.Shared
{
    /// <summary>
    /// Expedition service — maze exploration with fog of war.
    /// Move and RemoveObstacle use CrossOptimistic mode for responsive gameplay
    /// with cross-entity calls to profile for energy/money management.
    /// </summary>
    [MetaService(StateType = typeof(ExpeditionState), AccessPolicy = EntityAccessPolicy.Authorized)]
    [ServiceConfig(typeof(ExpeditionConfig), "Config")]
    [ServiceConfig(typeof(PlayerConfig), "PlayerConfig")]
    public interface IExpeditionService : IMetaService
    {
        /// <summary>
        /// Initialize expedition with owner. Called cross-entity from ProfileService.
        /// Sets OwnerPlayerId and ProfileEntityId for authorization and cross-entity calls.
        /// </summary>
        [MetaMethod(Alias = "Init", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void Init(string ownerPlayerId);

        /// <summary>
        /// Move the player by (dx, dy). Reveals fog, collects treasures.
        /// CrossOptimistic: executed locally with cross-entity SpendEnergy/AddMoney,
        /// then validated against server result.
        /// </summary>
        [MetaMethod(Alias = "Move", Mode = ExecutionMode.CrossOptimistic)]
        Task<MoveResult> Move(int dx, int dy);

        /// <summary>
        /// Remove an obstacle at position (playerX+dx, playerY+dy).
        /// Costs more energy than regular movement.
        /// </summary>
        [MetaMethod(Alias = "RemoveObstacle", Mode = ExecutionMode.CrossOptimistic)]
        Task<bool> RemoveObstacle(int dx, int dy);

        /// <summary>
        /// Check if expedition is generated and not yet complete.
        /// Called cross-entity by ProfileService.
        /// </summary>
        [MetaMethod(Alias = "IsActive", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        bool IsActive();

        /// <summary>
        /// Regenerate the map, then corrupt a few cells with <c>System.Random</c> — intentionally
        /// broken, and never how a meta method should be written.
        /// <para>
        /// Client and server produce maps that differ in 5-15 cells while the method returns
        /// nothing at all, so the always-on result comparison has nothing to compare and stays
        /// quiet. Only the patch CRC notices the two sides now hold different state. That gap is
        /// the entire reason deep desync exists.
        /// </para>
        /// </summary>
        [MetaMethod(Alias = "GenerateNewMapBroken", Mode = ExecutionMode.Optimistic)]
        void GenerateNewMapBroken();
    }
}
