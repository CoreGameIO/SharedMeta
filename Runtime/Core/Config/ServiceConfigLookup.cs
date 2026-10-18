using System.Collections.Generic;

namespace SharedMeta.Core
{
    /// <summary>
    /// Resolves a <c>[ServiceConfig]</c> entry out of an entity's config list.
    /// </summary>
    /// <remarks>
    /// The list is per ENTITY: several services can share one state, and it holds the union of
    /// their declarations, deduplicated by type. A service's own declaration order is therefore not
    /// the list's order, so addressing it by a service-local index reads a sibling's config —
    /// which is what happened until the accessors moved to a type lookup. Types are unique in the
    /// list, so this is unambiguous and immune to the aggregation order entirely.
    /// <para>
    /// Server and client both go through here so the two generators that emit typed accessors
    /// cannot drift apart on the rule.
    /// </para>
    /// </remarks>
    public static class ServiceConfigLookup
    {
        /// <summary>
        /// First entry assignable to <typeparamref name="TConfig"/>, or null.
        /// </summary>
        public static TConfig? Find<TConfig>(IReadOnlyList<object>? configs) where TConfig : class
        {
            if (configs == null) return null;

            // One entry per [ServiceConfig] declared on the entity — small enough that a scan
            // beats building and carrying a per-context type map.
            for (int i = 0; i < configs.Count; i++)
            {
                if (configs[i] is TConfig match) return match;
            }
            return null;
        }
    }
}
