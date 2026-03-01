using System.Collections.Concurrent;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Transport.HttpPolling
{
    /// <summary>
    /// IBroadcastSender implementation for HTTP long polling.
    /// Queues messages that are drained by the poll endpoint.
    /// Uses TaskCompletionSource to wake up long-poll waiters when data arrives.
    /// </summary>
    internal class HttpPollingBroadcastSender : IBroadcastSender
    {
        private readonly ConcurrentQueue<SessionResponse> _broadcasts = new();
        private readonly ConcurrentQueue<string> _deactivatingEntities = new();
        private volatile string? _sessionTerminated;
        private volatile TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SendBroadcast(SessionResponse message)
        {
            _broadcasts.Enqueue(message);
            SignalWaiter();
        }

        public void SendSessionTerminated(string reason)
        {
            _sessionTerminated = reason;
            SignalWaiter();
        }

        public void SendEntityDeactivating(string entityId)
        {
            _deactivatingEntities.Enqueue(entityId);
            SignalWaiter();
        }

        /// <summary>
        /// Called by poll endpoint. Blocks until data arrives or timeout.
        /// Returns all queued messages in one batch.
        /// </summary>
        public async Task<PollResponse> WaitForMessagesAsync(TimeSpan timeout, CancellationToken ct)
        {
            // Check if there's already data
            if (!IsEmpty())
                return Drain();

            // Wait for signal or timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                await _signal.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout or cancellation — return whatever we have (may be empty)
            }

            return Drain();
        }

        private PollResponse Drain()
        {
            var response = new PollResponse();

            // Drain broadcasts
            List<SessionResponse>? broadcasts = null;
            while (_broadcasts.TryDequeue(out var msg))
            {
                broadcasts ??= new();
                broadcasts.Add(msg);
            }
            response.Broadcasts = broadcasts;

            // Drain deactivating entities
            List<string>? deactivating = null;
            while (_deactivatingEntities.TryDequeue(out var entityId))
            {
                deactivating ??= new();
                deactivating.Add(entityId);
            }
            response.DeactivatingEntities = deactivating;

            // Session terminated (one-shot — clear after reading)
            response.SessionTerminated = _sessionTerminated;
            _sessionTerminated = null;

            // Reset signal for next wait
            ResetSignal();

            return response;
        }

        private void SignalWaiter()
        {
            _signal.TrySetResult();
        }

        private void ResetSignal()
        {
            var current = _signal;
            if (current.Task.IsCompleted)
            {
                Interlocked.CompareExchange(
                    ref _signal,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    current);
            }
        }

        /// <summary>
        /// Clear all queued data. Called when a new session is established
        /// on the same connection, to prevent stale data from the old session.
        /// </summary>
        public void Reset()
        {
            while (_broadcasts.TryDequeue(out _)) { }
            while (_deactivatingEntities.TryDequeue(out _)) { }
            _sessionTerminated = null;
            ResetSignal();
        }

        private bool IsEmpty()
        {
            return _broadcasts.IsEmpty
                   && _deactivatingEntities.IsEmpty
                   && _sessionTerminated == null;
        }
    }
}
