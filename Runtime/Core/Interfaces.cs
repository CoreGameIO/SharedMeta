using System;
using System.Threading.Tasks; // Added for Task

namespace SharedMeta.Core
{
    /// <summary>
    /// Marker interface for shared state entities (e.g., Inventory, Profile).
    /// </summary>
    public interface ISharedState
    {
    }

    /// <summary>
    /// Server-only hook on a shared state, called once per entity activation right after the
    /// state is read from storage and before <c>[MetaInit]</c> / migrations run. Intended for
    /// normalizing values missing in old records — e.g. a non-nullable member added after the
    /// record was written, which deserializes as <c>null</c>.
    /// <para>
    /// Clients never see the un-normalized state: every client state comes from the server.
    /// </para>
    /// <para>
    /// Must be idempotent and depend only on the state itself. Condition on absence
    /// (<c>Items ??= new()</c>), not on emptiness — <c>if (Items.Count == 0)</c> also fires
    /// after legitimate gameplay emptied the collection. Versioned data transforms belong in
    /// <c>[MetaInit]</c>.
    /// </para>
    /// <para>
    /// Also runs on the default state of an entity that has no stored record yet.
    /// </para>
    /// </summary>
    public interface IStateLoadedHook
    {
        /// <summary>
        /// Normalizes the freshly loaded state. Return <c>true</c> when anything changed: the
        /// entity is then persisted on deactivation even without calls, so stored records are
        /// eventually fixed and the normalization can later be removed. No immediate write.
        /// </summary>
        bool OnLoadedFromStorage();
    }

    /// <summary>
    /// Marker interface for services that contain shared business logic.
    /// </summary>
    public interface IMetaService
    {
    }
}
