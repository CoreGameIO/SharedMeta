using System;
using System.Collections.Generic;

namespace SharedMeta.Core.Network
{
    /// <summary>
    /// Provides execution mode for service methods.
    /// Allows runtime switching between Server and Optimistic modes.
    /// </summary>
    public interface IExecutionModeProvider
    {
        /// <summary>
        /// Get execution mode for a method.
        /// </summary>
        /// <param name="serviceName">Service interface name (e.g., "IProfileService").</param>
        /// <param name="methodName">Method name (e.g., "SetName").</param>
        /// <param name="defaultMode">Default mode from attribute.</param>
        /// <returns>Execution mode to use.</returns>
        ExecutionMode GetMode(string serviceName, string methodName, ExecutionMode defaultMode);
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
    ///   <item><see cref="SetMode"/> and <see cref="SetServiceMode"/> throw
    ///   <see cref="System.ArgumentException"/> if asked to apply a <c>Query</c> or <c>Signal</c>
    ///   override — nothing would consume it, and the asymmetry of allowing it to be set
    ///   without effect is more confusing than the throw.</item>
    /// </list>
    /// </summary>
    public class ExecutionModeProvider : IExecutionModeProvider
    {
        private readonly Dictionary<(string service, string method), ExecutionMode> _overrides = new();

        public ExecutionMode GetMode(string serviceName, string methodName, ExecutionMode defaultMode)
        {
            // Query and Signal are structural traits of the method, not routing strategies:
            // overriding them would fail to reach generated codegen (Query/Signal methods do not
            // emit the Mode-switch block), so we short-circuit here for clarity and defense-in-depth.
            if (defaultMode == ExecutionMode.Query || defaultMode == ExecutionMode.Signal)
                return defaultMode;

            // Check specific method override
            if (_overrides.TryGetValue((serviceName, methodName), out var mode))
                return mode;

            // Check service-wide override
            if (_overrides.TryGetValue((serviceName, "*"), out mode))
                return mode;

            return defaultMode;
        }

        /// <summary>
        /// Override mode for a specific method. Throws if <paramref name="mode"/> is
        /// <see cref="ExecutionMode.Query"/> or <see cref="ExecutionMode.Signal"/> — these cannot
        /// be assigned at runtime because they carry method-signature / lifecycle semantics
        /// that exist only at codegen time.
        /// </summary>
        public ExecutionModeProvider SetMode(string serviceName, string methodName, ExecutionMode mode)
        {
            EnsureAssignableMode(mode);
            _overrides[(serviceName, methodName)] = mode;
            return this;
        }

        /// <summary>
        /// Override mode for all methods in a service. Throws for Query/Signal targets —
        /// see <see cref="SetMode"/>.
        /// </summary>
        public ExecutionModeProvider SetServiceMode(string serviceName, ExecutionMode mode)
        {
            EnsureAssignableMode(mode);
            _overrides[(serviceName, "*")] = mode;
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

        /// <summary>
        /// Load overrides from a JSON manifest.
        /// Format: { "overrides": { "IServiceName.MethodAlias": "ServerPatch", ... } }
        /// Service-wide overrides use "*" as method: "IServiceName.*": "ServerPatch"
        /// </summary>
        public void LoadManifest(string json)
        {
            var manifest = ExecutionModeManifest.Parse(json);
            if (manifest.Overrides == null) return;

            foreach (var kvp in manifest.Overrides)
            {
                var dotIndex = kvp.Key.LastIndexOf('.');
                if (dotIndex <= 0) continue;

                var serviceName = kvp.Key.Substring(0, dotIndex);
                var methodName = kvp.Key.Substring(dotIndex + 1);

                if (Enum.TryParse<ExecutionMode>(kvp.Value, ignoreCase: true, out var mode))
                {
                    _overrides[(serviceName, methodName)] = mode;
                }
            }
        }
    }
}
