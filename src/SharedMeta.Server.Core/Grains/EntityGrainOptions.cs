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
    /// Runtime switch for the server half of deep desync detection.
    /// When null (default): uses per-service setting from [MetaServiceImpl(DeepDesync = true)].
    /// When true: computes patch CRCs for every service on this silo.
    /// When false: disables deep desync even if attribute says true.
    /// <para>
    /// The switch cannot turn detection on by itself. Comparing those CRCs — and raising
    /// <c>IDesyncDiagnostics.OnPatchDesync</c> — is generated per service from
    /// <c>[MetaServiceImpl(..., DeepDesync = true)]</c>, so a service compiled without the
    /// attribute has nothing on the client side to compare against and stays silent whatever this
    /// is set to. <see cref="SharedMeta.Server.Core.DeepDesyncConfigurationCheck"/> logs an error
    /// at startup when this is true while no service in the build carries the attribute.
    /// </para>
    /// </summary>
    public bool? DeepDesyncEnabled { get; set; }

    /// <summary>
    /// Seed factory for fresh random streams. Invoked once per stream when an entity activates
    /// without persisted random bytes (first activation, or a [NamedRandom] slot that shifted
    /// position). Arguments: <c>(entityId, streamName)</c> where <c>streamName</c> is one of
    /// <c>"server"</c> / <c>"optimistic"</c> / a <c>[NamedRandom]</c> name.
    /// <para>
    /// Default (null) → deterministic <c>"{entityId}:{streamName}"</c> seed. Set to mix in
    /// non-deterministic entropy (e.g. <c>DateTime.UtcNow.Ticks</c>, <c>Random.Shared</c>) when
    /// you want fresh entities recreated under the same id (profile reset, recycled expedition
    /// id) to produce different streams.
    /// </para>
    /// <para>
    /// <b>Replay-safe:</b> the seed is consumed locally on the server and never sent over the
    /// wire — clients receive the post-seed <c>MetaRandom</c> internal state via the subscribe
    /// snapshot, so optimistic execution and replay continue to work without any seed knowledge.
    /// </para>
    /// </summary>
    public System.Func<string, string, string>? FreshRandomSeedFactory { get; set; }
}
