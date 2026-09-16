using SharedMeta.Core;

namespace Expedition.Shared
{
    /// <summary>
    /// Expedition service implementation — maze generation, movement, obstacle removal.
    /// Uses Config for balance parameters, Context.Random for map generation,
    /// and IExpeditionProfileService for cross-entity energy/money calls.
    /// </summary>
    // DeepDesync generates the patch-tracked copy and the client-side CRC comparison, which is what
    // catches a divergence the return values agree on — both sides answer Ok, but the revealed cells
    // or the position drift apart. Generating it does not switch it on: see DeepDesyncMode on the
    // server. Result-level divergence ("client moved, server refused") needs none of this.
    [MetaServiceImpl(typeof(IExpeditionService), typeof(ExpeditionState), typeof(IExpeditionProfileService), DeepDesync = true)]
    public partial class ExpeditionService : IExpeditionService
    {
        private ExpeditionState state => Context.State;

        public void Init(string ownerPlayerId)
        {
            state.OwnerPlayerId = ownerPlayerId;
            state.ProfileEntityId = ownerPlayerId; // UserOwned profile: entityId == playerId
        }

        public bool IsAuthorized(string playerId)
        {
            return state.OwnerPlayerId == playerId;
        }

        [MetaInit]
        public Task<int> GenerateMap(int version)
        {
            if (version < 1)
            {
                RegenerateMap();
                return Task.FromResult(1);
            }
            return Task.FromResult(version);
        }

        /// <summary>
        /// Deterministic map generation — Context.Random advances identically on both sides, so
        /// client and server land on the same map.
        /// </summary>
        private void RegenerateMap()
        {
            {
                var width = Config.MapWidth;
                var height = Config.MapHeight;

                state.Width = width;
                state.Height = height;
                state.PlayerX = 0;
                state.PlayerY = 0;
                state.TreasuresCollected = 0;
                state.IsComplete = false;

                var totalCells = width * height;
                state.Cells = new List<byte>(totalCells);
                state.Revealed = new List<bool>(totalCells);

                for (int i = 0; i < totalCells; i++)
                {
                    state.Cells.Add((byte)CellType.Empty);
                    state.Revealed.Add(false);
                }

                // Place walls
                for (int i = 0; i < totalCells; i++)
                {
                    if (i == 0) continue; // Start cell stays empty
                    if (Context.Random!.Next(100) < Config.WallPercent)
                        state.Cells[i] = (byte)CellType.Wall;
                }

                // Place obstacles on remaining empty cells
                for (int i = 0; i < totalCells; i++)
                {
                    if (state.Cells[i] != (byte)CellType.Empty) continue;
                    if (i == 0) continue;
                    if (Context.Random!.Next(100) < Config.ObstaclePercent)
                        state.Cells[i] = (byte)CellType.Obstacle;
                }

                // Place treasures on remaining empty cells
                int treasureCount = 0;
                for (int i = 0; i < totalCells; i++)
                {
                    if (state.Cells[i] != (byte)CellType.Empty) continue;
                    if (i == 0) continue;
                    if (Context.Random!.Next(100) < Config.TreasurePercent)
                    {
                        state.Cells[i] = (byte)CellType.Treasure;
                        treasureCount++;
                    }
                }
                state.TotalTreasures = treasureCount;

                // Reveal 3x3 area around start
                RevealArea(0, 0);

                state.IsGenerated = true;
            }
        }

        public async Task<MoveResult> Move(int dx, int dy)
        {
            if (state.IsComplete)
                return MoveResult.Complete;

            // Validate direction (single step only)
            if (Math.Abs(dx) + Math.Abs(dy) != 1)
                return MoveResult.Blocked;

            int newX = state.PlayerX + dx;
            int newY = state.PlayerY + dy;

            // Bounds check
            if (newX < 0 || newX >= state.Width || newY < 0 || newY >= state.Height)
                return MoveResult.OutOfBounds;

            int idx = newY * state.Width + newX;
            var cellType = (CellType)state.Cells[idx];

            // Wall — cannot pass
            if (cellType == CellType.Wall)
                return MoveResult.Blocked;

            // Obstacle — cannot pass (use RemoveObstacle first)
            if (cellType == CellType.Obstacle)
                return MoveResult.Blocked;

            // Moving into unrevealed cell costs energy
            if (!state.Revealed[idx])
            {
                var profileCaller = GetIExpeditionProfileService(state.ProfileEntityId!);
                bool spent = await profileCaller.SpendEnergyAsync(PlayerConfig.MoveCost);
                if (!spent)
                    return MoveResult.NoEnergy;
            }

            // Move player
            state.PlayerX = newX;
            state.PlayerY = newY;

            // Reveal 3x3 around new position
            RevealArea(newX, newY);

            // Check for treasure
            if (cellType == CellType.Treasure)
            {
                state.Cells[idx] = (byte)CellType.Empty;
                state.TreasuresCollected++;

                var profileCaller = GetIExpeditionProfileService(state.ProfileEntityId!);
                await profileCaller.AddMoneyAsync(Config.TreasureReward);

                // Check completion
                if (state.TreasuresCollected >= state.TotalTreasures)
                {
                    state.IsComplete = true;
                    return MoveResult.Complete;
                }

                return MoveResult.Treasure;
            }

            return MoveResult.Ok;
        }

        public async Task<bool> RemoveObstacle(int dx, int dy)
        {
            if (state.IsComplete)
                return false;

            // Validate direction
            if (Math.Abs(dx) + Math.Abs(dy) != 1)
                return false;

            int targetX = state.PlayerX + dx;
            int targetY = state.PlayerY + dy;

            // Bounds check
            if (targetX < 0 || targetX >= state.Width || targetY < 0 || targetY >= state.Height)
                return false;

            int idx = targetY * state.Width + targetX;

            // Must be an obstacle
            if ((CellType)state.Cells[idx] != CellType.Obstacle)
                return false;

            // Spend energy
            var profileCaller = GetIExpeditionProfileService(state.ProfileEntityId!);
            bool spent = await profileCaller.SpendEnergyAsync(PlayerConfig.ObstacleCost);
            if (!spent)
                return false;

            // Remove obstacle
            state.Cells[idx] = (byte)CellType.Empty;
            return true;
        }

        public bool IsActive()
        {
            return state.IsGenerated && !state.IsComplete;
        }

        public void GenerateNewMapBroken()
        {
            // Deterministic first, so the two sides start from the same map and the divergence
            // stays small enough to read in a patch diff — a wholly different map would diff as
            // "everything changed" and show nothing.
            RegenerateMap();

            // Then corrupt a handful of cells with System.Random: a different sequence on each
            // side, so Cells diverges in 5-15 positions. This is the mistake the feature exists to
            // catch — shared logic reaching for randomness that isn't the framework's.
            var rng = new System.Random(); // BAD: non-deterministic!
            var totalCells = state.Cells.Count;
            int corruptCount = 5 + rng.Next(11); // 5..15 inclusive

            for (int n = 0; n < corruptCount; n++)
            {
                int idx = rng.Next(1, totalCells); // skip [0] — player spawn
                state.Cells[idx] = (byte)rng.Next(4); // any CellType
            }

            // Each side must stay internally consistent on its own: the corruption may have removed
            // or added treasure cells, and a stale TotalTreasures would leave the expedition
            // impossible to finish. The two sides still disagree with each other — that is the
            // point — but neither is left broken by itself.
            int treasureCount = 0;
            for (int i = 0; i < totalCells; i++)
            {
                if (state.Cells[i] == (byte)CellType.Treasure)
                    treasureCount++;
            }
            state.TotalTreasures = treasureCount;
            state.TreasuresCollected = 0;
            state.IsComplete = false;
        }

        private void RevealArea(int cx, int cy)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int rx = cx + dx;
                    int ry = cy + dy;
                    if (rx >= 0 && rx < state.Width && ry >= 0 && ry < state.Height)
                    {
                        state.Revealed[ry * state.Width + rx] = true;
                    }
                }
            }
        }
    }
}
