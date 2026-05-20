namespace SharedMeta.Core.Framework
{
    /// <summary>
    /// 0.24.0+ Compile-time table of framework subscriber method ids. Each constant matches
    /// the <c>ushort</c> identifier carried in <c>MetaOperation.MethodId</c> on a subscriber
    /// broadcast and the value the generated <c>{Service}SubscriberDispatcher</c> switches on.
    /// <para>
    /// Hand-defined (not codegen-emitted) because framework subscriber interfaces live in the
    /// UPM package — they ship identically with every game build, so the ids are baked in
    /// once here and shared between server-side framework grains (<c>LobbyGrain</c> etc.) and
    /// the per-game generated dispatchers.
    /// </para>
    /// <para>
    /// Numbering rule: framework ids occupy the high half of the <c>ushort</c> space (start
    /// at <c>0xF000</c>) so they never collide with per-game <c>GameMethodIds</c>, which start
    /// at 0 and never reach this far in practice (the wire id is the per-assembly local
    /// index, capped well below 65k methods).
    /// </para>
    /// </summary>
    public static class FrameworkMethodIds
    {
        /// <summary>Reserved high range for framework subscriber methods.</summary>
        public const ushort FrameworkRangeStart = 0xF000;

        // ── ILobbySubscriber ─────────────────────────────────────────────────
        public const ushort ILobbySubscriber_OnMatchFound = 0xF001;
        public const ushort ILobbySubscriber_OnMatchCancelled = 0xF002;
        public const ushort ILobbySubscriber_OnMatchmakingUpdate = 0xF003;
    }
}
