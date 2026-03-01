namespace SharedMeta.Server.Core.Grains;

/// <summary>
/// Controls how EntityGrain decides when to persist state to storage.
/// </summary>
public enum PersistenceMode
{
    /// <summary>Save after every request. Default behavior, fully backward compatible.</summary>
    EveryCall,

    /// <summary>Save every N requests.</summary>
    EveryNRequests,

    /// <summary>Save when M minutes have passed since last save (checked on each request, not by timer).</summary>
    EveryNMinutes,

    /// <summary>Save every N requests OR M minutes, whichever comes first.</summary>
    RequestsOrTime,

    /// <summary>Only save when grain deactivates. Maximum performance, some data loss risk on crash.</summary>
    OnDeactivationOnly
}

/// <summary>
/// Configuration for EntityGrain persistence behavior.
/// Methods marked with [MetaMethod(ForcePersist = true)] always persist regardless of this policy.
/// Subscribe/Unsubscribe, errors, and deactivation always persist regardless of this policy.
/// </summary>
public class PersistencePolicy
{
    /// <summary>The persistence mode.</summary>
    public PersistenceMode Mode { get; set; } = PersistenceMode.EveryCall;

    /// <summary>
    /// Number of requests between saves.
    /// Used by <see cref="PersistenceMode.EveryNRequests"/> and <see cref="PersistenceMode.RequestsOrTime"/>.
    /// </summary>
    public int RequestInterval { get; set; } = 10;

    /// <summary>
    /// Time interval between saves in minutes.
    /// Used by <see cref="PersistenceMode.EveryNMinutes"/> and <see cref="PersistenceMode.RequestsOrTime"/>.
    /// Checked on each request, not by timer.
    /// </summary>
    public double TimeIntervalMinutes { get; set; } = 5.0;

    /// <summary>Save after every request (current default behavior).</summary>
    public static PersistencePolicy EveryCall()
        => new() { Mode = PersistenceMode.EveryCall };

    /// <summary>Save every N requests.</summary>
    public static PersistencePolicy EveryNRequests(int n)
        => new() { Mode = PersistenceMode.EveryNRequests, RequestInterval = n };

    /// <summary>Save when M minutes have passed since last save (checked on each request).</summary>
    public static PersistencePolicy EveryNMinutes(double minutes)
        => new() { Mode = PersistenceMode.EveryNMinutes, TimeIntervalMinutes = minutes };

    /// <summary>Save every N requests OR M minutes, whichever comes first.</summary>
    public static PersistencePolicy RequestsOrTime(int requests, double minutes)
        => new() { Mode = PersistenceMode.RequestsOrTime, RequestInterval = requests, TimeIntervalMinutes = minutes };

    /// <summary>Only save when grain deactivates.</summary>
    public static PersistencePolicy OnDeactivationOnly()
        => new() { Mode = PersistenceMode.OnDeactivationOnly };
}
