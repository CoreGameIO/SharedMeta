namespace SharedMeta.Server.Core.Grains;

/// <summary>
/// Options for EntityGrain behavior.
/// </summary>
public class EntityGrainOptions
{
    /// <summary>
    /// How long a subscriber remains valid after their last activity.
    /// Expired subscribers are pruned on entity reactivation.
    /// </summary>
    public TimeSpan SubscriberTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Controls when EntityGrain persists state to storage.
    /// Methods marked with [MetaMethod(ForcePersist = true)] always persist regardless of this policy.
    /// Default: EveryCall (save after every request, fully backward compatible).
    /// </summary>
    public PersistencePolicy PersistencePolicy { get; set; } = PersistencePolicy.EveryCall();

    /// <summary>
    /// Global override for deep desync detection.
    /// When null (default): uses per-service setting from [MetaServiceImpl(DeepDesync = true)].
    /// When true: forces deep desync on for all services.
    /// When false: disables deep desync even if attribute says true.
    /// </summary>
    public bool? DeepDesyncEnabled { get; set; }
}
