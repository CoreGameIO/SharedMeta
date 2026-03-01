using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Simple context for CounterService (for testing without generated code).
    /// </summary>
    public class CounterServiceContext
    {
        public CounterState State { get; set; } = null!;
        public string CallerId { get; set; } = "";
    }

    /// <summary>
    /// Implementation of the test counter service.
    /// Can be used with generated context or manual context.
    /// </summary>
    [MetaServiceImpl(typeof(ICounterService), typeof(CounterState))]
    public partial class CounterService : ICounterService
    {
        // Manual context for testing without generated code
        private CounterServiceContext? _manualContext;

        /// <summary>
        /// Set context manually (for testing).
        /// </summary>
        public void SetContext(CounterServiceContext context)
        {
            _manualContext = context;
        }

        // Helper to get state (from generated Context or manual context)
        private CounterState GetState()
        {
            if (_manualContext != null)
                return _manualContext.State;
            // Fall back to generated Context if available
            return Context.State;
        }

        // Helper to get caller ID
        private string GetCallerId()
        {
            if (_manualContext != null)
                return _manualContext.CallerId;
            return Context.CallerId ?? "unknown";
        }

        public void AddValue(int value, int clientSequence)
        {
            var state = GetState();
            var callerId = GetCallerId();

            var serverTimeTicks = _manualContext != null ? 0 : Context.ServerTimeTicks;

            state.Operations.Add(new CounterOperation
            {
                CallerId = callerId,
                Value = value,
                ClientSequence = clientSequence,
                ServerTimeTicks = serverTimeTicks
            });
            state.Sum += value;
            state.LastServerTimeTicks = serverTimeTicks;

            Console.WriteLine($"[Counter] {callerId} added {value} (seq={clientSequence}), sum={state.Sum}, ops={state.Operations.Count}, timeTicks={serverTimeTicks}");
        }

        public void Reset()
        {
            var state = GetState();
            state.Operations.Clear();
            state.Sum = 0;

            Console.WriteLine("[Counter] Reset");
        }
    }
}
