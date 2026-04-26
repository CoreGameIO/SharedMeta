using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MetaServiceImpl(typeof(ICounterAuxService), typeof(CounterState))]
    public partial class CounterAuxService : ICounterAuxService
    {
        public int AuxAdd(int value)
        {
            Context.State.Sum += value;
            Context.State.Operations.Add(new CounterOperation
            {
                CallerId = Context.CallerId ?? "",
                Value = value,
                ClientSequence = -1,
                ServerTimeTicks = Context.ServerTimeTicks
            });
            return (int)Context.State.Sum;
        }

        public void AuxBumpReactive()
        {
            // Touch the [Tracked] field via its generated public setter so reactive subscribers fire.
            Context.State.ReactiveCounter = Context.State.ReactiveCounter + 1;
        }
    }
}
