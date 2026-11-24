using System;
using System.Threading;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// W3C trace context (<c>traceparent</c>) of one meta call: 16-byte trace id, 8-byte span id,
    /// flags. The trace id doubles as the call id that correlates client logs, server logs and
    /// spans of one call.
    /// <para>
    /// No dependency on <c>System.Diagnostics.Activity</c>: this type must compile on Unity and
    /// netstandard2.1 without the DiagnosticSource package.
    /// </para>
    /// </summary>
    public readonly struct MetaTraceContext : IEquatable<MetaTraceContext>
    {
        /// <summary>Length of a version-00 <c>traceparent</c> string.</summary>
        public const int TraceParentLength = 55;

        private readonly ulong _traceHi;
        private readonly ulong _traceLo;
        private readonly ulong _spanId;
        private readonly byte _flags;

        public MetaTraceContext(ulong traceHi, ulong traceLo, ulong spanId, byte flags)
        {
            _traceHi = traceHi;
            _traceLo = traceLo;
            _spanId = spanId;
            _flags = flags;
        }

        /// <summary>False for <c>default</c> — no trace.</summary>
        public bool IsValid => (_traceHi | _traceLo) != 0 && _spanId != 0;

        public ulong TraceIdHigh => _traceHi;
        public ulong TraceIdLow => _traceLo;
        public ulong SpanId => _spanId;
        public byte Flags => _flags;

        /// <summary>Bit 0 of the flags: the caller asked for this trace to be recorded.</summary>
        public bool IsSampled => (_flags & 1) != 0;

        /// <summary>Trace id as 32 lowercase hex chars — the call id.</summary>
        public string TraceIdHex => string.Create(32, this, static (span, t) =>
        {
            WriteHex(span, t._traceHi);
            WriteHex(span.Slice(16), t._traceLo);
        });

        /// <summary>Span id as 16 lowercase hex chars.</summary>
        public string SpanIdHex => string.Create(16, _spanId, static (span, v) => WriteHex(span, v));

        /// <summary>New trace with a fresh trace id and span id, sampled flag set.</summary>
        public static MetaTraceContext CreateRandom()
        {
            var rng = Rng.Instance;
            return new MetaTraceContext(rng.NextNonZero(), rng.NextNonZero(), rng.NextNonZero(), 1);
        }

        /// <summary>Same trace, new span id — the context a child hop hands on.</summary>
        public MetaTraceContext WithNewSpan()
            => new MetaTraceContext(_traceHi, _traceLo, Rng.Instance.NextNonZero(), _flags);

        /// <summary>Same trace, given span id (e.g. the id of a span started by the host's tracer).</summary>
        public MetaTraceContext WithSpan(ulong spanId)
            => new MetaTraceContext(_traceHi, _traceLo, spanId, _flags);

        /// <summary><c>00-{trace}-{span}-{flags}</c>, or null for an invalid context.</summary>
        public string? ToTraceParent()
        {
            if (!IsValid) return null;
            // One allocation per call on every hop — no concatenation of intermediate hex strings.
            return string.Create(TraceParentLength, this, static (span, t) =>
            {
                span[0] = '0'; span[1] = '0'; span[2] = '-';
                WriteHex(span.Slice(3), t._traceHi);
                WriteHex(span.Slice(19), t._traceLo);
                span[35] = '-';
                WriteHex(span.Slice(36), t._spanId);
                span[52] = '-';
                span[53] = HexDigit(t._flags >> 4);
                span[54] = HexDigit(t._flags & 0xF);
            });
        }

        /// <summary>
        /// Strict version-00 parse. The input comes from the network, so anything off-format —
        /// wrong length, anything but lowercase hex, zero ids, another version — is rejected
        /// rather than repaired.
        /// </summary>
        public static bool TryParse(string? traceParent, out MetaTraceContext context)
        {
            context = default;
            if (traceParent == null || traceParent.Length != TraceParentLength) return false;
            if (traceParent[2] != '-' || traceParent[35] != '-' || traceParent[52] != '-') return false;
            if (traceParent[0] != '0' || traceParent[1] != '0') return false;

            if (!TryHex(traceParent, 3, 16, out var hi)) return false;
            if (!TryHex(traceParent, 19, 16, out var lo)) return false;
            if (!TryHex(traceParent, 36, 16, out var span)) return false;
            if (!TryHex(traceParent, 53, 2, out var flags)) return false;
            if ((hi | lo) == 0 || span == 0) return false;

            context = new MetaTraceContext(hi, lo, span, (byte)flags);
            return true;
        }

        public bool Equals(MetaTraceContext other)
            => _traceHi == other._traceHi && _traceLo == other._traceLo && _spanId == other._spanId && _flags == other._flags;

        public override bool Equals(object? obj) => obj is MetaTraceContext other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_traceHi, _traceLo, _spanId, _flags);

        public override string ToString() => ToTraceParent() ?? "";

        private static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);

        // 16 lowercase hex digits, most significant first.
        private static void WriteHex(Span<char> destination, ulong value)
        {
            for (int i = 15; i >= 0; i--)
            {
                destination[i] = HexDigit((int)(value & 0xF));
                value >>= 4;
            }
        }

        private static bool TryHex(string s, int start, int length, out ulong value)
        {
            value = 0;
            for (int i = start; i < start + length; i++)
            {
                char c = s[i];
                int digit;
                if (c >= '0' && c <= '9') digit = c - '0';
                else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                else return false;
                value = (value << 4) | (uint)digit;
            }
            return true;
        }

        /// <summary>
        /// splitmix64 per thread, seeded from a Guid. Ids only need to be unique, not secret;
        /// kept away from <c>Context.Random</c> so tracing never moves a deterministic stream.
        /// </summary>
        private sealed class Rng
        {
            [ThreadStatic] private static Rng? _instance;
            private ulong _state;

            private Rng()
            {
                var bytes = Guid.NewGuid().ToByteArray();
                _state = BitConverter.ToUInt64(bytes, 0) ^ BitConverter.ToUInt64(bytes, 8);
            }

            public static Rng Instance => _instance ??= new Rng();

            public ulong NextNonZero()
            {
                ulong v;
                do
                {
                    ulong z = _state += 0x9E3779B97F4A7C15UL;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    v = z ^ (z >> 31);
                } while (v == 0);
                return v;
            }
        }
    }
}
