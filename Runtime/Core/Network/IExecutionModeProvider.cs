using System;
using System.Collections.Generic;

namespace SharedMeta.Core.Network
{
    /// <summary>
    /// Provides execution mode for service methods.
    /// Allows runtime switching between Server and Optimistic modes.
    /// 0.24.0+ Keyed by client-local <c>MethodId</c> from <c>GameMethodIds</c> — no string-name
    /// plumbing on the hot path. Use <c>GameMethodIds.{Iface}_{Alias}_v{Version}</c> constants
    /// when configuring overrides.
    /// </summary>
    public interface IExecutionModeProvider
    {
        /// <summary>
        /// Get execution mode for a method.
        /// </summary>
        /// <param name="methodId">Client-local method id from <c>GameMethodIds</c>.</param>
        /// <param name="defaultMode">Default mode from attribute.</param>
        /// <returns>Execution mode to use.</returns>
        ExecutionMode GetMode(ushort methodId, ExecutionMode defaultMode);
    }

    /// <summary>
    /// Default implementation with override support.
    ///
    /// <para><b>Query and Signal are structural, not routing.</b> Both <see cref="ExecutionMode.Query"/>
    /// and <see cref="ExecutionMode.Signal"/> change a method's signature and lifecycle — they cannot
    /// be overridden at runtime, in either direction:</para>
    /// <list type="bullet">
    ///   <item>A method declared <c>Query</c> or <c>Signal</c> ignores any matching entry in the
    ///   overrides map (<see cref="GetMode"/> short-circuits on the default mode).</item>
    ///   <item><see cref="SetMode"/> throws <see cref="System.ArgumentException"/> if asked
    ///   to apply a <c>Query</c> or <c>Signal</c> override — nothing would consume it, and
    ///   the asymmetry of allowing it to be set without effect is more confusing than the
    ///   throw.</item>
    /// </list>
    /// </summary>
    public class ExecutionModeProvider : IExecutionModeProvider
    {
        // 0.24.0+ Keyed by ushort MethodId — the same id used everywhere on the dispatch
        // path. Configure via SetMode(methodId, mode) using GameMethodIds.X constants.
        private readonly Dictionary<ushort, ExecutionMode> _overrides = new();

        public ExecutionMode GetMode(ushort methodId, ExecutionMode defaultMode)
        {
            // Query and Signal are structural traits of the method, not routing strategies:
            // overriding them would fail to reach generated codegen (Query/Signal methods do not
            // emit the Mode-switch block), so we short-circuit here for clarity and defense-in-depth.
            if (defaultMode == ExecutionMode.Query || defaultMode == ExecutionMode.Signal)
                return defaultMode;

            if (_overrides.TryGetValue(methodId, out var mode))
                return mode;

            return defaultMode;
        }

        /// <summary>
        /// Override mode for a specific method. Throws if <paramref name="mode"/> is
        /// <see cref="ExecutionMode.Query"/> or <see cref="ExecutionMode.Signal"/> — these cannot
        /// be assigned at runtime because they carry method-signature / lifecycle semantics
        /// that exist only at codegen time.
        /// </summary>
        public ExecutionModeProvider SetMode(ushort methodId, ExecutionMode mode)
        {
            EnsureAssignableMode(mode);
            _overrides[methodId] = mode;
            return this;
        }

        private static void EnsureAssignableMode(ExecutionMode mode)
        {
            if (mode == ExecutionMode.Query || mode == ExecutionMode.Signal)
                throw new System.ArgumentException(
                    $"ExecutionMode.{mode} cannot be applied as a runtime override — it is a structural trait declared on [MetaMethod] and consumed at code generation time. Declare the method with Mode = ExecutionMode.{mode} instead.",
                    nameof(mode));
        }

        /// <summary>
        /// Clear all overrides.
        /// </summary>
        public void Clear()
        {
            _overrides.Clear();
        }
    }
}
