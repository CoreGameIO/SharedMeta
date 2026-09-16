namespace SharedMeta.Server.Core.Grains;

/// <summary>
/// Silo-wide activation of deep desync detection.
/// <para>
/// Orthogonal to <c>[MetaServiceImpl(..., DeepDesync = true)]</c>: the attribute decides at compile
/// time which services can report (it emits the <c>_PatchTracked</c> copy and the client-side CRC
/// comparison), this decides at runtime who actually does. A service without the attribute stays
/// silent in every mode — there is no comparison in its generated client to run.
/// </para>
/// </summary>
public enum DeepDesyncMode
{
    /// <summary>
    /// Nobody gets detection. A client asking for it is refused rather than merged in — this is the
    /// mode that actually guarantees no CRC work happens on the silo.
    /// </summary>
    Off,

    /// <summary>
    /// Active only for players individually flagged on the server. The flag is read once when the
    /// session starts; changing it takes effect on the player's next session.
    /// </summary>
    PerPlayer,

    /// <summary>
    /// Active for every client on this silo, no per-player flag consulted.
    /// </summary>
    Forced
}
