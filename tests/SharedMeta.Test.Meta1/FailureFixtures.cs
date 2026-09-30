using System;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MemoryPackable]
    public partial class FailureState : ISharedState
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
        [MemoryPackOrder(1)] public int LastRoll { get; set; }
    }

    /// <summary>
    /// Methods that throw after mutating state — a defect in meta code. The framework's duty is
    /// that every subscriber ends up on the server's state and the entity keeps working.
    /// </summary>
    [MetaService(StateType = typeof(FailureState))]
    public interface IFailureService : IMetaService
    {
        [MetaMethod(Alias = "Add", Mode = ExecutionMode.Server)]
        void Add(int amount);

        [MetaMethod(Alias = "AddThenThrow", Mode = ExecutionMode.Server)]
        void AddThenThrow(int amount);

        /// <summary>
        /// Succeeds on the client; on the server mutates further and consumes one more random
        /// before throwing, so the caller's prediction is wrong in both state and random position.
        /// </summary>
        [MetaMethod(Alias = "AddServerDiverges", Mode = ExecutionMode.Optimistic)]
        void AddServerDiverges(int amount);

        [MetaMethod(Alias = "Roll", Mode = ExecutionMode.Optimistic)]
        int Roll();
    }

    [MetaServiceImpl(typeof(IFailureService), typeof(FailureState))]
    public partial class FailureService : IFailureService
    {
        public void Add(int amount) => State.Value += amount;

        public void AddThenThrow(int amount)
        {
            State.Value += amount;
            throw new InvalidOperationException("AddThenThrow failed after mutating state");
        }

        public void AddServerDiverges(int amount)
        {
            State.Value += amount;
            State.LastRoll = Context.Random!.Next(1000);
            if (!Context.IsServer) return;

            State.Value += 100;
            State.LastRoll = Context.Random!.Next(1000);
            throw new InvalidOperationException("AddServerDiverges failed on the server only");
        }

        public int Roll()
        {
            State.LastRoll = Context.Random!.Next(1000);
            return State.LastRoll;
        }
    }
}
