using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Runtime side of <see cref="MetaTelemetryOptions"/>: current options, the method tag table
    /// for a server signature, and the registered <see cref="IMetaServerObserver"/>s.
    /// <para>
    /// A DI singleton (<c>AddSharedMetaTelemetry</c>). Components that receive none — hosts and
    /// tests that never registered it — use <see cref="Default"/>, which runs on default options
    /// with no observers, so telemetry behaves the same with or without registration.
    /// </para>
    /// </summary>
    public sealed class SharedMetaTelemetry
    {
        /// <summary>Instance for components constructed without one.</summary>
        public static SharedMetaTelemetry Default { get; } = new(null, null, null);

        private readonly IOptionsMonitor<MetaTelemetryOptions>? _monitor;
        private readonly MetaTelemetryOptions _fixed = new();
        private readonly IMetaServerObserver[] _observers;
        private readonly ILogger _logger;

        // A table per signature instance; rebuilt when the options object changes.
        private readonly ConditionalWeakTable<MetaServerSignature, TableHolder> _tables = new();
        private TableHolder? _nullSignatureTable;

        public SharedMetaTelemetry(
            IOptionsMonitor<MetaTelemetryOptions>? options,
            IEnumerable<IMetaServerObserver>? observers,
            ILoggerFactory? loggerFactory)
        {
            _monitor = options;
            _observers = observers?.ToArray() ?? Array.Empty<IMetaServerObserver>();
            _hasCustomObservers = _observers.Any(o => o is not LoggingServerObserver);
            _logger = loggerFactory?.CreateLogger("SharedMeta.Telemetry") ?? NullLogger.Instance;
        }

        private readonly bool _hasCustomObservers;

        /// <summary>Current options (follows reloads).</summary>
        public MetaTelemetryOptions Options => _monitor?.CurrentValue ?? _fixed;

        /// <summary>
        /// True when some observer will act on a call — guard before building a <see cref="MetaCallInfo"/>.
        /// The built-in analytics-log observer only counts while <see cref="MetaTelemetryOptions.AnalyticsLog"/> is on.
        /// </summary>
        public bool HasObservers => _hasCustomObservers || (_observers.Length > 0 && Options.AnalyticsLog);

        /// <summary>Method tag table for <paramref name="signature"/> under the current options.</summary>
        public MethodTelemetryTable GetTable(MetaServerSignature? signature)
        {
            var options = Options;
            // Hot path: one signature per process in practice — skip the weak-table lookup.
            var last = _last;
            if (last != null && ReferenceEquals(last.Signature, signature) && ReferenceEquals(last.Options, options))
                return last.Table;
            var table = GetTableSlow(signature, options);
            _last = new TableHolder(options, table, signature);
            return table;
        }

        // Published whole (all fields set in the constructor) through a volatile write, so a reader
        // on a weakly ordered CPU never sees a holder with a stale Signature.
        private volatile TableHolder? _last;

        private MethodTelemetryTable GetTableSlow(MetaServerSignature? signature, MetaTelemetryOptions options)
        {
            if (signature == null)
            {
                var holder = _nullSignatureTable;
                if (holder == null || !ReferenceEquals(holder.Options, options))
                    _nullSignatureTable = holder = new TableHolder(options, new MethodTelemetryTable(null, options));
                return holder.Table;
            }

            if (_tables.TryGetValue(signature, out var existing) && ReferenceEquals(existing.Options, options))
                return existing.Table;

            var built = new TableHolder(options, new MethodTelemetryTable(signature, options));
            _tables.AddOrUpdate(signature, built);
            return built.Table;
        }

        /// <summary>Tags of one server method under the current options.</summary>
        public MethodTags GetTags(MetaServerSignature? signature, ushort serverMethodId)
            => GetTable(signature).Get(serverMethodId);

        public void CallCompleted(in MetaCallInfo call)
        {
            foreach (var o in _observers)
            {
                try { o.OnCallCompleted(call); }
                catch (Exception ex) { _logger.LogWarning(ex, "IMetaServerObserver {Observer} failed in OnCallCompleted", o.GetType().Name); }
            }
        }

        public void SessionStarted(string playerId, string sessionId, string? clientVersion, bool resumed)
        {
            foreach (var o in _observers)
            {
                try { o.OnSessionStarted(playerId, sessionId, clientVersion, resumed); }
                catch (Exception ex) { _logger.LogWarning(ex, "IMetaServerObserver {Observer} failed in OnSessionStarted", o.GetType().Name); }
            }
        }

        public void SessionEnded(string playerId, string sessionId, MetaSessionEndReason reason, double durationMs)
        {
            foreach (var o in _observers)
            {
                try { o.OnSessionEnded(playerId, sessionId, reason, durationMs); }
                catch (Exception ex) { _logger.LogWarning(ex, "IMetaServerObserver {Observer} failed in OnSessionEnded", o.GetType().Name); }
            }
        }

        private sealed class TableHolder
        {
            public TableHolder(MetaTelemetryOptions options, MethodTelemetryTable table, MetaServerSignature? signature = null)
            {
                Options = options;
                Table = table;
                Signature = signature;
            }

            public MetaTelemetryOptions Options { get; }
            public MethodTelemetryTable Table { get; }
            public MetaServerSignature? Signature { get; }
        }
    }
}
