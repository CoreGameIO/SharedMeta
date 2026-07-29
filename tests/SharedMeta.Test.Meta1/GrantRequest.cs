using System.Collections.Generic;
using MemoryPack;

namespace SharedMeta.Test.Meta1
{
    public enum GrantCurrency
    {
        Gold = 0,
        Gems = 1,
    }

    /// <summary>
    /// Complex argument DTO carrying enum-keyed dictionaries — the shape an admin grant uses.
    /// </summary>
    /// <remarks>
    /// Exists because server-originated calls (<c>{Service}ServerApi</c>) pack arguments through
    /// their own emitter. Primitive arguments would not catch a mismatch in how a collection member
    /// round-trips, and admin methods are <c>GenerateClientApi = false</c>, so no client call
    /// exercises this path. Deliberately mirrors a persisted-state shape: version-tolerant, enum
    /// keys, several collection members.
    /// </remarks>
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class GrantRequest
    {
        [MemoryPackOrder(0)] public Dictionary<GrantCurrency, long> Currencies { get; set; } = new();

        [MemoryPackOrder(1)] public List<string> Items { get; set; } = new();

        [MemoryPackOrder(2)] public string Reason { get; set; } = "";
    }
}
