using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace Expedition.Shared
{
    [MetaServiceImpl(typeof(IExpeditionService), typeof(ExpeditionState), typeof(IExpeditionProfileService))]
    public partial class ExpeditionService : IExpeditionService
    {
        private ExpeditionState state => Context.State;

        public void Init(string ownerPlayerId)
        {
            state.OwnerPlayerId = ownerPlayerId;
            state.ProfileEntityId = ownerPlayerId;
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
                for (int i = 1; i < totalCells; i++)
                {
                    if (Context.Random!.Next(100) < Config.WallPercent)
                        state.Cells[i] = (byte)CellType.Wall;
                }

                // Place obstacles on remaining empty cells
                for (int i = 1; i < totalCells; i++)
                {
                    if (state.Cells[i] != (byte)CellType.Empty) continue;
                    if (Context.Random!.Next(100) < Config.ObstaclePercent)
                        state.Cells[i] = (byte)CellType.Obstacle;
                }

                // Place treasures on remaining empty cells
                int treasureCount = 0;
                for (int i = 1; i < totalCells; i++)
                {
                    if (state.Cells[i] != (byte)CellType.Empty) continue;
                    if (Context.Random!.Next(100) < Config.TreasurePercent)
                    {
                        state.Cells[i] = (byte)CellType.Treasure;
                        treasureCount++;
                    }
                }
                state.TotalTreasures = treasureCount;

                RevealArea(0, 0);

                state.IsGenerated = true;
                return Task.FromResult(1);
            }
            return Task.FromResult(version);
        }

        public async Task<MoveResult> Move(int dx, int dy)
        {
            if (state.IsComplete)
                return MoveResult.Complete;

            if (Math.Abs(dx) + Math.Abs(dy) != 1)
                return MoveResult.Blocked;

            int newX = state.PlayerX + dx;
            int newY = state.PlayerY + dy;

            if (newX < 0 || newX >= state.Width || newY < 0 || newY >= state.Height)
                return MoveResult.OutOfBounds;

            int idx = newY * state.Width + newX;
            var cellType = (CellType)state.Cells[idx];

            // Can only move to revealed cells
            if (!state.Revealed[idx])
                return MoveResult.Blocked;

            if (cellType == CellType.Wall)
                return MoveResult.Blocked;

            if (cellType == CellType.Obstacle)
                return MoveResult.Blocked;

            state.PlayerX = newX;
            state.PlayerY = newY;

            // Count new cells that would be revealed
            int newCells = CountNewRevealed(newX, newY);

            if (newCells > 0)
            {
                // Spend as much energy as available, reveal only that many cells
                var profileCaller = GetIExpeditionProfileService(state.ProfileEntityId);
                int spent = await profileCaller.SpendEnergyUpToAsync(newCells * Config.MoveCost);
                int cellsToReveal = spent / Math.Max(Config.MoveCost, 1);
                RevealArea(newX, newY, cellsToReveal);
            }

            if (cellType == CellType.Treasure)
            {
                state.Cells[idx] = (byte)CellType.Empty;
                state.TreasuresCollected++;

                var treasureCaller = GetIExpeditionProfileService(state.ProfileEntityId);
                await treasureCaller.AddMoneyAsync(Config.TreasureReward);

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

            if (Math.Abs(dx) + Math.Abs(dy) != 1)
                return false;

            int targetX = state.PlayerX + dx;
            int targetY = state.PlayerY + dy;

            if (targetX < 0 || targetX >= state.Width || targetY < 0 || targetY >= state.Height)
                return false;

            int idx = targetY * state.Width + targetX;

            if ((CellType)state.Cells[idx] != CellType.Obstacle)
                return false;

            var profileCaller = GetIExpeditionProfileService(state.ProfileEntityId);
            bool spent = await profileCaller.SpendEnergyAsync(Config.ObstacleCost);
            if (!spent)
                return false;

            state.Cells[idx] = (byte)CellType.Empty;
            return true;
        }

        public bool IsActive()
        {
            return state.IsGenerated && !state.IsComplete;
        }

        public void GenerateNewMap()
        {
            RegenerateMap();
        }

        public void GenerateNewMapOptimistic()
        {
            RegenerateMap();
        }

        private void RegenerateMap()
        {
            var width = Config.MapWidth;
            var height = Config.MapHeight;
            var totalCells = width * height;

            state.Width = width;
            state.Height = height;
            state.PlayerX = 0;
            state.PlayerY = 0;
            state.TreasuresCollected = 0;
            state.IsComplete = false;

            state.Cells = new List<byte>(totalCells);
            state.Revealed = new List<bool>(totalCells);

            for (int i = 0; i < totalCells; i++)
            {
                state.Cells.Add((byte)CellType.Empty);
                state.Revealed.Add(false);
            }

            for (int i = 1; i < totalCells; i++)
            {
                if (Context.Random!.Next(100) < Config.WallPercent)
                    state.Cells[i] = (byte)CellType.Wall;
            }

            for (int i = 1; i < totalCells; i++)
            {
                if (state.Cells[i] != (byte)CellType.Empty) continue;
                if (Context.Random!.Next(100) < Config.ObstaclePercent)
                    state.Cells[i] = (byte)CellType.Obstacle;
            }

            int treasureCount = 0;
            for (int i = 1; i < totalCells; i++)
            {
                if (state.Cells[i] != (byte)CellType.Empty) continue;
                if (Context.Random!.Next(100) < Config.TreasurePercent)
                {
                    state.Cells[i] = (byte)CellType.Treasure;
                    treasureCount++;
                }
            }
            state.TotalTreasures = treasureCount;

            RevealArea(0, 0);
            state.IsGenerated = true;
        }

        private int CountNewRevealed(int cx, int cy)
        {
            int count = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int rx = cx + dx;
                    int ry = cy + dy;
                    if (rx >= 0 && rx < state.Width && ry >= 0 && ry < state.Height)
                    {
                        int ri = ry * state.Width + rx;
                        if (!state.Revealed[ri])
                            count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Reveals cells in a 3x3 area. If maxNewCells >= 0, reveals at most that many
        /// previously-hidden cells (closest to center first). Already-revealed cells don't count.
        /// </summary>
        private void RevealArea(int cx, int cy, int maxNewCells = -1)
        {
            // Reveal order: center first, then cardinal neighbors, then diagonals
            int[] dxOrder = { 0, 0, 0, -1, 1, -1, 1, -1, 1 };
            int[] dyOrder = { 0, -1, 1, 0, 0, -1, -1, 1, 1 };

            int revealed = 0;
            for (int i = 0; i < 9; i++)
            {
                int rx = cx + dxOrder[i];
                int ry = cy + dyOrder[i];
                if (rx < 0 || rx >= state.Width || ry < 0 || ry >= state.Height)
                    continue;

                int ri = ry * state.Width + rx;
                if (state.Revealed[ri])
                    continue;

                if (maxNewCells >= 0 && revealed >= maxNewCells)
                    break;

                state.Revealed[ri] = true;
                revealed++;
            }
        }
    }
}
