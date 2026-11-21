using System;
using System.Collections.Generic;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Pre-built metric tags of one server method. Built once per method when the table is
    /// built, so hot paths index an array instead of building strings or tag pairs.
    /// </summary>
    public sealed class MethodTags
    {
        public const string OtherMethod = "_other";
        public const string Unknown = "_unknown";

        internal MethodTags(string serviceName, string methodName, string stateType, string mode, bool enabled)
        {
            ServiceName = serviceName;
            MethodName = methodName;
            StateType = stateType;
            Mode = mode;
            Enabled = enabled;
            ServiceTag = new KeyValuePair<string, object?>("service", serviceName);
            MethodTag = new KeyValuePair<string, object?>("method", enabled ? methodName : OtherMethod);
            StateTypeTag = new KeyValuePair<string, object?>("state_type", stateType);
            ModeTag = new KeyValuePair<string, object?>("mode", mode);
        }

        /// <summary>Real service name — for spans and logs, never folded.</summary>
        public string ServiceName { get; }

        /// <summary>Real method alias — for spans and logs, never folded.</summary>
        public string MethodName { get; }

        /// <summary>Short name of the owning state type.</summary>
        public string StateType { get; }

        /// <summary>Declared mode in snake case (<c>optimistic</c>, <c>server_patch</c>, …).</summary>
        public string Mode { get; }

        /// <summary>True when the method has its own metric series.</summary>
        public bool Enabled { get; }

        public KeyValuePair<string, object?> ServiceTag { get; }

        /// <summary><c>method</c> tag: the alias when <see cref="Enabled"/>, else <c>_other</c>.</summary>
        public KeyValuePair<string, object?> MethodTag { get; }

        public KeyValuePair<string, object?> StateTypeTag { get; }
        public KeyValuePair<string, object?> ModeTag { get; }

        /// <summary>Tags for an id the server signature cannot resolve.</summary>
        public static MethodTags UnknownMethod { get; } = new(Unknown, Unknown, Unknown, Unknown, enabled: true);
    }

    /// <summary>
    /// Server method id → <see cref="MethodTags"/>, resolved from the server signature and
    /// <see cref="MetaTelemetryOptions"/>. Immutable; a configuration change builds a new table.
    /// <para>
    /// Every per-method instrument reads its <c>service</c> / <c>method</c> tags from here, so a
    /// disabled method folds into <c>_other</c> consistently across all of them and an
    /// unresolvable id never lets client-supplied strings into a tag.
    /// </para>
    /// </summary>
    public sealed class MethodTelemetryTable
    {
        private readonly MethodTags?[] _byId;

        public MethodTelemetryTable(MetaServerSignature? signature, MetaTelemetryOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var methods = signature?.Methods ?? Array.Empty<ServerMethodEntry>();
            int max = -1;
            foreach (var m in methods)
                if (m.GlobalIndex > max) max = m.GlobalIndex;
            _byId = new MethodTags?[max + 1];

            var include = PatternSet.From(options.IncludeMethods);
            var exclude = PatternSet.From(options.ExcludeMethods);
            foreach (var m in methods)
            {
                bool enabled = IsEnabled(m, options.MethodMetrics, include, exclude);
                _byId[m.GlobalIndex] = new MethodTags(
                    m.ServiceName,
                    m.Alias,
                    ShortTypeName(m.StateTypeName),
                    ModeName(m.Mode),
                    enabled);
            }
        }

        /// <summary>Tags of a server method id; <see cref="MethodTags.UnknownMethod"/> when unresolvable.</summary>
        public MethodTags Get(ushort serverMethodId)
            => serverMethodId < _byId.Length ? _byId[serverMethodId] ?? MethodTags.UnknownMethod : MethodTags.UnknownMethod;

        /// <summary>Tags for a (service, alias) pair given as strings — e.g. by a client report. Never echoes unknown strings.</summary>
        public MethodTags Find(string? serviceName, string? methodAlias)
        {
            if (serviceName == null || methodAlias == null) return MethodTags.UnknownMethod;
            foreach (var t in _byId)
                if (t != null && t.ServiceName == serviceName && t.MethodName == methodAlias)
                    return t;
            return MethodTags.UnknownMethod;
        }

        private static bool IsEnabled(ServerMethodEntry m, MethodMetricsMode mode, PatternSet include, PatternSet exclude)
        {
            if (exclude.Matches(m.ServiceName, m.Alias)) return false;
            if (include.Matches(m.ServiceName, m.Alias)) return true;
            return mode switch
            {
                MethodMetricsMode.All => m.Metrics != MetaMetrics.Off,
                MethodMetricsMode.Annotated => m.Metrics == MetaMetrics.On,
                _ => false,
            };
        }

        private static string ShortTypeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return MethodTags.Unknown;
            int generic = fullName.IndexOf('<');
            var head = generic >= 0 ? fullName.Substring(0, generic) : fullName;
            int dot = head.LastIndexOf('.');
            return dot >= 0 ? fullName.Substring(dot + 1) : fullName;
        }

        /// <summary>Snake-case mode name used as the <c>mode</c> tag value.</summary>
        public static string ModeName(ExecutionMode mode) => mode switch
        {
            ExecutionMode.LocalQuery => "local_query",
            ExecutionMode.Optimistic => "optimistic",
            ExecutionMode.Server => "server",
            ExecutionMode.CrossOptimistic => "cross_optimistic",
            ExecutionMode.ServerPatch => "server_patch",
            ExecutionMode.ServerReplace => "server_replace",
            ExecutionMode.Query => "query",
            ExecutionMode.Signal => "signal",
            ExecutionMode.Notification => "notification",
            _ => "other",
        };

        /// <summary><c>Service.Method</c> / <c>Service.*</c> / <c>*</c> patterns, ordinal.</summary>
        private readonly struct PatternSet
        {
            private readonly bool _all;
            private readonly HashSet<string>? _exact;
            private readonly HashSet<string>? _services;

            private PatternSet(bool all, HashSet<string>? exact, HashSet<string>? services)
            {
                _all = all;
                _exact = exact;
                _services = services;
            }

            public static PatternSet From(List<string>? patterns)
            {
                if (patterns == null || patterns.Count == 0) return default;
                bool all = false;
                HashSet<string>? exact = null, services = null;
                foreach (var raw in patterns)
                {
                    var p = raw?.Trim();
                    if (string.IsNullOrEmpty(p)) continue;
                    if (p == "*") { all = true; continue; }
                    if (p!.EndsWith(".*", StringComparison.Ordinal))
                        (services ??= new HashSet<string>(StringComparer.Ordinal)).Add(p.Substring(0, p.Length - 2));
                    else
                        (exact ??= new HashSet<string>(StringComparer.Ordinal)).Add(p);
                }
                return new PatternSet(all, exact, services);
            }

            public bool Matches(string service, string alias)
                => _all
                   || (_services != null && _services.Contains(service))
                   || (_exact != null && _exact.Contains(service + "." + alias));
        }
    }
}
