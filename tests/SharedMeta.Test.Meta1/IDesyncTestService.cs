using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MetaService(StateType = typeof(DesyncTestState))]
    public interface IDesyncTestService : IMetaService
    {
        /// <summary>Deterministic: increments Value by amount. Should never desync.</summary>
        [MetaMethod(Alias = "Add", Mode = ExecutionMode.Optimistic)]
        int Add(int amount);

        /// <summary>Deterministic: sets Label. Should never desync.</summary>
        [MetaMethod(Alias = "SetLabel", Mode = ExecutionMode.Optimistic)]
        void SetLabel(string label);

        /// <summary>
        /// NON-DETERMINISTIC: uses System.Random to add a random value.
        /// Will cause a state desync between server and client (same return value = 0,
        /// but internal state diverges because System.Random seeds differ).
        /// </summary>
        [MetaMethod(Alias = "AddNonDeterministic", Mode = ExecutionMode.Optimistic)]
        int AddNonDeterministic();
    }
}
