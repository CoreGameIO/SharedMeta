using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharedMeta.Core.Transport;

namespace SharedMeta.Client
{
    /// <summary>
    /// Reassembles <see cref="SessionResponse"/>s by <see cref="SessionResponse.SequenceNumber"/>
    /// and delivers them to a caller-provided sink in ascending sequence order.
    ///
    /// Thread-safety: responses may be pushed from any thread; delivery is serialised so the
    /// sink is invoked on one thread at a time. The implementation uses a dispatcher-ownership
    /// flag rather than holding a lock across delivery so handler code is free to re-enter
    /// the dispatcher (e.g. by pushing another response from a handler-triggered RPC reply).
    ///
    /// Re-entrancy contract: when a blocking handler (a broadcast handler that does
    /// <c>Task.Run(...).Wait()</c> on a follow-up RPC) is parked on the owning thread, the
    /// re-entrant push arriving on a background thread must be allowed to drain — otherwise
    /// the handler's wait deadlocks. <see cref="EnterHandlerScope"/> marks the AsyncLocal
    /// that propagates into those background chains, and <see cref="Drain"/> bypasses the
    /// ownership check when it detects re-entrant execution.
    /// </summary>
    internal sealed class OrderedDispatcher
    {
        private readonly object _lock = new();
        private readonly SortedDictionary<long, SessionResponse> _pending = new();
        private long _nextExpected = 1;
        private bool _draining;

        // AsyncLocal flows with ExecutionContext through Task.Run / await. Set while a
        // broadcast handler is running so any re-entrant Drain call (on any thread) is
        // recognised as nested inside that handler's call chain.
        private static readonly AsyncLocal<bool> _insideHandler = new();

        private readonly Action<SessionResponse> _deliver;

        public OrderedDispatcher(Action<SessionResponse> deliver)
        {
            _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        }

        /// <summary>Next sequence number the dispatcher is waiting for (== head).</summary>
        public long Head
        {
            get { lock (_lock) return _nextExpected; }
        }

        /// <summary>True when the buffer has at least one response sitting above a gap at head.</summary>
        public bool HasGap
        {
            get { lock (_lock) return _pending.Count > 0 && _pending.Keys.First() != _nextExpected; }
        }

        /// <summary>
        /// Drop all buffered responses and reset the next expected sequence. Called on
        /// disconnect / session supersede / reconnect to start fresh.
        /// </summary>
        public void Reset(long nextExpected = 1)
        {
            lock (_lock)
            {
                _pending.Clear();
                _nextExpected = nextExpected;
            }
        }

        /// <summary>
        /// Queue a response for ordered delivery. Responses with non-positive
        /// <see cref="SessionResponse.SequenceNumber"/> are rejected — they do not
        /// participate in ordering and must be handled by the caller directly.
        /// </summary>
        public void Push(SessionResponse response)
        {
            if (response.SequenceNumber <= 0) return;

            lock (_lock)
            {
                if (response.SequenceNumber < _nextExpected) return; // already processed
                _pending.TryAdd(response.SequenceNumber, response);
            }
        }

        /// <summary>
        /// Drain every contiguous response starting at the head, delivering each to the sink
        /// in order. Safe to call from any thread; concurrent calls either cooperate (re-entrant
        /// from a handler) or return immediately leaving work for the active drainer.
        /// </summary>
        public void Drain()
        {
            var reentrant = _insideHandler.Value;

            if (!reentrant)
            {
                lock (_lock)
                {
                    if (_draining) return; // another thread owns dispatch; it will pick up new items
                    _draining = true;
                }
            }

            try
            {
                while (true)
                {
                    List<SessionResponse>? batch = null;
                    lock (_lock)
                    {
                        while (_pending.Count > 0)
                        {
                            var first = _pending.First();
                            if (first.Key != _nextExpected) break;
                            _pending.Remove(first.Key);
                            _nextExpected = first.Key + 1;
                            (batch ??= new List<SessionResponse>()).Add(first.Value);
                        }
                        if (batch == null) return;
                    }

                    foreach (var r in batch) _deliver(r);
                }
            }
            finally
            {
                if (!reentrant)
                {
                    lock (_lock) { _draining = false; }
                }
            }
        }

        /// <summary>
        /// Mark the current async context as being inside a broadcast handler. Use via
        /// <c>using (dispatcher.EnterHandlerScope()) handler(op);</c>. The flag propagates
        /// through <c>Task.Run</c> / <c>await</c> so an RPC the handler fires on a
        /// background thread — whose reply must be drained to unblock the handler's
        /// <c>Task.Wait</c> — takes the re-entrant path in <see cref="Drain"/>.
        /// </summary>
        public IDisposable EnterHandlerScope()
        {
            var prev = _insideHandler.Value;
            _insideHandler.Value = true;
            return new Scope(prev);
        }

        private sealed class Scope : IDisposable
        {
            private readonly bool _prev;
            private bool _disposed;
            public Scope(bool prev) => _prev = prev;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _insideHandler.Value = _prev;
            }
        }
    }
}
