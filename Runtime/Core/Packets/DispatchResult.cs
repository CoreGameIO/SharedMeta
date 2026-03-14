using System.Collections.Generic;

namespace SharedMeta.Core
{
    /// <summary>
    /// Result of dispatching a service method call.
    /// Contains the method result and list of triggers to execute.
    /// </summary>
    public class DispatchResult
    {
        /// <summary>
        /// Serialized result bytes from the method call (null for void methods).
        /// </summary>
        public byte[]? ResultBytes { get; set; }

        /// <summary>
        /// List of trigger method names to execute after this method.
        /// Conditions have already been evaluated - only methods that should fire are included.
        /// </summary>
        public List<string>? TriggersToExecute { get; set; }

        /// <summary>
        /// If true, EntityGrain must persist state immediately after this call,
        /// regardless of the configured PersistencePolicy.
        /// Set by the generated dispatcher when [MetaMethod(ForcePersist = true)].
        /// </summary>
        public bool ForcePersist { get; set; }
    }
}
