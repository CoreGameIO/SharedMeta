using System;
using System.Threading.Tasks;
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
    [MetaServiceImpl(typeof(ICounterService), typeof(CounterState), typeof(ICounterService))]
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

        [MetaInit]
        public Task<int> Init(int version)
        {
            if (version < 1)
            {
                // First-time initialization
                var state = GetState();
                state.InitializedVersion = 1;
                return Task.FromResult(1);
            }
            return Task.FromResult(version);
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

        public void AddReactive(int value)
        {
            var state = GetState();
            state.ReactiveCounter += value;
        }

        public void Reset()
        {
            var state = GetState();
            state.Operations.Clear();
            state.Sum = 0;

            Console.WriteLine("[Counter] Reset");
        }

        public async Task<int> AddCrossEntity(string targetEntityId, int value)
        {
            var targetService = GetICounterService(targetEntityId);
            int clamped = await targetService.AddClampedAsync(value);
            Console.WriteLine($"[Counter] AddCrossEntity: target={targetEntityId}, value={value}, clamped={clamped}");
            return clamped;
        }

        public int AddClamped(int value)
        {
            var config = (CounterConfig)Context.Config!;
            int clamped = Math.Min(value, config.MaxValue);
            var state = GetState();
            state.Sum += clamped;
            Console.WriteLine($"[Counter] AddClamped: value={value}, max={config.MaxValue}, clamped={clamped}, sum={state.Sum}");
            return clamped;
        }
    }
}
