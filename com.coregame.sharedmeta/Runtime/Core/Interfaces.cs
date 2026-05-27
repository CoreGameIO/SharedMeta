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
    /// Marker interface for services that contain shared business logic.
    /// </summary>
    public interface IMetaService
    {
    }
}
