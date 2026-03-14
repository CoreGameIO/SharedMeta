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
    /// </summary>
    public class ExecutionModeProvider : IExecutionModeProvider
    {
        private readonly Dictionary<(string service, string method), ExecutionMode> _overrides = new();

        public ExecutionMode GetMode(string serviceName, string methodName, ExecutionMode defaultMode)
        {
            // Check specific method override
            if (_overrides.TryGetValue((serviceName, methodName), out var mode))
                return mode;

            // Check service-wide override
            if (_overrides.TryGetValue((serviceName, "*"), out mode))
                return mode;

            return defaultMode;
        }

        /// <summary>
        /// Override mode for a specific method.
        /// </summary>
        public ExecutionModeProvider SetMode(string serviceName, string methodName, ExecutionMode mode)
        {
            _overrides[(serviceName, methodName)] = mode;
            return this;
        }

        /// <summary>
        /// Override mode for all methods in a service.
        /// </summary>
        public ExecutionModeProvider SetServiceMode(string serviceName, ExecutionMode mode)
        {
            _overrides[(serviceName, "*")] = mode;
            return this;
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
