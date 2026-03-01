using System;
using System.Collections.Generic;
using System.Threading;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

namespace SharedMeta.Client
{
    /// <summary>
    /// Lock-free message buffer for frame-based processing.
    ///
    /// Transport thread pushes SessionResponse objects (any order), game loop drains ready ones (in order).
    /// Uses sparse array indexed by (sequence - head) with minimal shift on drain.
    /// Each SessionResponse has one SequenceNumber — it's the atomic unit of delivery.
    ///
    /// Two modes:
    /// - Frame-based: call DrainReady() once per frame
    /// - Immediate: set OnReadyToDrain callback, will be invoked when messages are ready
    /// </summary>
    public class MessageBuffer
    {
        private const int BufferSize = 64;

        private readonly SessionResponse?[] _buffer = new SessionResponse?[BufferSize];
        private long _head = 1;  // Next expected sequence number
        private int _maxSet = -1; // Highest index with a message (-1 = empty)
        private int _lock;

        /// <summary>
        /// Callback invoked when messages become ready to drain.
        /// Set this for immediate processing mode (async/await style).
        /// Leave null for frame-based mode.
        /// </summary>
        public Action? OnReadyToDrain { get; set; }

        /// <summary>
        /// Next expected sequence number.
        /// </summary>
        public long Head => _head;

        /// <summary>
        /// True if there are messages waiting but we can't process them (gap at head).
        /// </summary>
        public bool HasGap => _maxSet >= 0 && _buffer[0] == null;

        /// <summary>
        /// Called by transport thread. O(1) insert.
        /// If OnReadyToDrain is set, invokes it when messages become ready.
        /// </summary>
        public void Push(SessionResponse response)
        {
            if (response.SequenceNumber <= 0)
                return; // Invalid sequence

            bool shouldNotify = false;

            SpinLock();
            try
            {
                var offset = (int)(response.SequenceNumber - _head);

                if (offset < 0)
                    return; // Old message, already processed

                if (offset >= BufferSize)
                {
                    // Gap too large - shouldn't happen with TCP
                    MetaLog.Error($"[MessageBuffer] Buffer overflow: head={_head}, seq={response.SequenceNumber}, offset={offset}");
                    return;
                }

                _buffer[offset] = response;

                if (offset > _maxSet)
                    _maxSet = offset;

                MetaLog.Debug($"[MessageBuffer] Push: seq={response.SequenceNumber}, head={_head}, offset={offset}, maxSet={_maxSet}, buffer[0]={(_buffer[0] != null ? "set" : "null")}");

                // Check if we should notify: head is now ready
                shouldNotify = OnReadyToDrain != null && _buffer[0] != null;
            }
            finally
            {
                SpinUnlock();
            }

            // Notify outside lock to avoid deadlocks
            if (shouldNotify)
            {
                MetaLog.Debug($"[MessageBuffer] Notifying OnReadyToDrain, head={_head}");
                OnReadyToDrain?.Invoke();
            }
        }

        /// <summary>
        /// Called once per frame by game loop.
        /// Drains all consecutive ready SessionResponses starting from head.
        /// Also skips past direct response sequences that won't have broadcasts.
        /// </summary>
        /// <param name="output">List to append ready responses to.</param>
        /// <returns>Number of responses drained.</returns>
        public int DrainReady(List<SessionResponse> output)
        {
            SpinLock();
            try
            {
                // First, skip any direct response sequences at the head
                AdvancePastDirectResponses();

                if (_maxSet < 0 || _buffer[0] == null)
                    return 0; // Empty or gap at head

                // Find how many consecutive messages we have (also skipping direct responses)
                var count = 0;
                var offset = 0;
                while (offset <= _maxSet)
                {
                    // Check if current position is a direct response (skip it)
                    if (_directResponseSequences.Contains(_head + offset))
                    {
                        _directResponseSequences.Remove(_head + offset);
                        offset++;
                        continue;
                    }

                    // Check if there's a message at this position
                    if (_buffer[offset] == null)
                        break;

                    output.Add(_buffer[offset]!);
                    count++;
                    offset++;
                }

                if (offset == 0)
                {
                    return 0;
                }

                MetaLog.Debug($"[MessageBuffer] DrainReady: count={count}, offset={offset}, head={_head}, maxSet={_maxSet}");

                // Shift only up to _maxSet (not whole buffer)
                var shiftLen = _maxSet - offset + 1;
                if (shiftLen > 0)
                {
                    Array.Copy(_buffer, offset, _buffer, 0, shiftLen);
                }

                // Clear the tail
                var clearStart = Math.Max(0, _maxSet - offset + 1);
                var clearLen = Math.Min(offset, _maxSet + 1);
                Array.Clear(_buffer, clearStart, clearLen);

                _head += offset;
                _maxSet -= offset;

                // After draining, skip any trailing direct responses
                AdvancePastDirectResponses();

                return count;
            }
            finally
            {
                SpinUnlock();
            }
        }

        /// <summary>
        /// Advances head past consecutive direct response sequences (called under lock).
        /// </summary>
        private void AdvancePastDirectResponses()
        {
            while (_directResponseSequences.Contains(_head))
            {
                _directResponseSequences.Remove(_head);

                // Shift buffer
                if (_maxSet >= 0)
                {
                    if (_maxSet >= 1)
                    {
                        Array.Copy(_buffer, 1, _buffer, 0, _maxSet);
                        _buffer[_maxSet] = null;
                    }
                    else
                    {
                        _buffer[0] = null;
                    }
                    _maxSet--;
                }

                _head++;
            }
        }

        /// <summary>
        /// Clears all pending messages. Call on disconnect.
        /// </summary>
        public void Clear()
        {
            SpinLock();
            try
            {
                Array.Clear(_buffer, 0, BufferSize);
                _maxSet = -1;
                _directResponseSequences.Clear();
            }
            finally
            {
                SpinUnlock();
            }
        }

        /// <summary>
        /// Resets head sequence (call after reconnect with new starting sequence).
        /// </summary>
        public void Reset(long startSequence)
        {
            SpinLock();
            try
            {
                Array.Clear(_buffer, 0, BufferSize);
                _head = startSequence;
                _maxSet = -1;
                _directResponseSequences.Clear();
            }
            finally
            {
                SpinUnlock();
            }
        }

        // Track sequences that are direct responses (not broadcasts to wait for)
        private readonly HashSet<long> _directResponseSequences = new();

        /// <summary>
        /// Marks a sequence as a direct response (not a broadcast).
        /// The buffer will skip past this sequence when draining.
        /// Call this after receiving a direct RPC response with a sequence number.
        /// </summary>
        /// <param name="sequence">The sequence number of the direct response.</param>
        public void MarkDirectResponse(long sequence)
        {
            if (sequence <= 0) return;

            bool shouldNotify = false;

            SpinLock();
            try
            {
                if (sequence < _head)
                    return; // Already past this sequence

                _directResponseSequences.Add(sequence);
                MetaLog.Debug($"[MessageBuffer] MarkDirectResponse: seq={sequence}, head={_head}");

                // Advance head past consecutive direct responses at the head
                AdvancePastDirectResponses();

                // Check if we should notify: head is now ready
                shouldNotify = OnReadyToDrain != null && _maxSet >= 0 && _buffer[0] != null;
            }
            finally
            {
                SpinUnlock();
            }

            if (shouldNotify)
            {
                MetaLog.Debug($"[MessageBuffer] MarkDirectResponse: notifying drain, head={_head}");
                OnReadyToDrain?.Invoke();
            }
        }

        private void SpinLock()
        {
            while (Interlocked.CompareExchange(ref _lock, 1, 0) != 0)
                Thread.SpinWait(1);
        }

        private void SpinUnlock() => Interlocked.Exchange(ref _lock, 0);
    }
}
