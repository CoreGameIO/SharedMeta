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
    /// Who gets deep desync detection on this silo. Default <see cref="Grains.DeepDesyncMode.PerPlayer"/>
    /// — off for everyone until a player is flagged for it.
    /// <para>
    /// This does not substitute for <c>[MetaServiceImpl(..., DeepDesync = true)]</c>. The attribute
    /// is what makes a service capable of reporting at all; this decides who the capability is
    /// turned on for. Neither one alone produces a report.
    /// </para>
    /// </summary>
    public DeepDesyncMode DeepDesyncMode { get; set; } = DeepDesyncMode.PerPlayer;

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
