using SharedMeta.Core;

namespace ClanWars.Shared
{
    // ════════════════════════════════════════════════════════════════════════════
    //  ClanConfig
    //
    //  Two declared config versions:
    //    1.0  — original v1 surface (CreateClanCost, MaxMembers).
    //    2.0  — adds friendship + officer fields. STRUCTURAL — v1 clients can't reason
    //           about these mechanics, so when a clan is pinned at config 2.0 the server
    //           forces ServerPatch for v1 subscribers on the clan service.
    //
    //  [MetaConfigVersion] maps client app version → config version. Server resolves the
    //  per-session config branch from the client's transmitted ClientAppVersion:
    //    "1.0.0" → ClanConfig 1.0
    //    "2.0.0" → ClanConfig 2.0
    //  In production both versions live in the same class (open-closed evolution). The
    //  v1 fields stay forever; the v2 fields are simply unused on v1 clients' method paths.
    // ════════════════════════════════════════════════════════════════════════════

    [MetaConfig(Default = true)]
    [MetaConfigVersion(Client = "1.0.0", Config = "1.0.0")]
    [MetaConfigVersion(Client = "2.0.0", Config = "2.0.0")]
    [MetaConfigStructureBoundary("2.0", Reason = "Friendship + officer mechanics added; v1 method bodies cannot reason about these fields.")]
    public class ClanConfig
    {
        // v1 surface (present in both versions).
        public int CreateClanCost { get; set; } = 100;
        public int MaxMembers { get; set; } = 100;

        // v2-only additions. Defaulted to zero/empty for v1 clients reading old bytes —
        // their method bodies don't reference these fields, so the defaults are invisible.
        public int MaxFriendships { get; set; }
        public int MaxOfficers { get; set; }
    }
}
