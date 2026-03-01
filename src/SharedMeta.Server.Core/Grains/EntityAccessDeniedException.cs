using System;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Thrown when a player is denied access to an entity during subscription.
    /// Caught by SessionManagerGrain and converted to an error response.
    /// </summary>
    public class EntityAccessDeniedException : Exception
    {
        public EntityAccessDeniedException(string message) : base(message) { }
    }
}
