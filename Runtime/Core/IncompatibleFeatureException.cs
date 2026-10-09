using System;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core
{
    /// <summary>
    /// Thrown server-side when a client requests a feature (entity, method, config branch)
    /// it cannot support due to version mismatch. The 0.22.0 compatibility-negotiation
    /// pipeline (signature registry + per-client capabilities) prevents most of these from
    /// reaching the server in the first place — but back-stop validation on subscribe /
    /// dispatch surfaces them when a client misreports its signature or skips negotiation.
    /// <para>
    /// Carries structured diagnostics so the client surface (and the game UI on top of it)
    /// can show a non-blocking "update required for this feature" notification rather than
    /// a blocking error dialog.
    /// </para>
    /// <para>
    /// <see cref="GenerateSerializerAttribute"/> is required so the exception preserves its
    /// type identity when thrown across Orleans grain boundaries (EntityGrain → SessionManagerGrain).
    /// Without it, Orleans wraps the throw in a generic codec-error and the caller's
    /// <c>catch (IncompatibleFeatureException)</c> never matches.
    /// </para>
    /// </summary>
    [GenerateSerializer]
    public class IncompatibleFeatureException : Exception
    {
        /// <summary>Structured payload describing what the client lacks.</summary>
        [Id(0)] public FeatureRequirement Requirement { get; private set; }

        public IncompatibleFeatureException(FeatureRequirement requirement)
            : base(requirement.ToErrorMessage())
        {
            Requirement = requirement ?? throw new ArgumentNullException(nameof(requirement));
        }

        /// <summary>
        /// Parameterless constructor used by the Orleans serializer to materialize the
        /// exception on the receiving grain side; <see cref="Requirement"/> is then set
        /// by the serializer from the persisted <c>[Id(0)]</c> field.
        /// </summary>
        protected IncompatibleFeatureException() : base()
        {
            Requirement = new FeatureRequirement();
        }
    }

    /// <summary>
    /// Wire-safe structured description of what the client must do to access a feature.
    /// Travels through <c>SubscribeResponse.FeatureRequirement</c> / RPC error envelopes
    /// so the client-side framework can reconstruct an <see cref="IncompatibleFeatureException"/>
    /// rather than an opaque <c>InvalidOperationException</c> string.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class FeatureRequirement
    {
        /// <summary>What kind of feature is gated. One of: <c>"State"</c>, <c>"Method"</c>, <c>"Config"</c>.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public string FeatureKind { get; set; } = "";

        /// <summary>
        /// Stable identifier of the gated feature. For <c>FeatureKind = "State"</c> this is the
        /// state type full name; for <c>"Method"</c> it's <c>"IServiceInterface.MethodName"</c>;
        /// for <c>"Config"</c> it's the config type full name.
        /// </summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public string Identifier { get; set; } = "";

        /// <summary>
        /// Minimum config version (Major.Minor.Patch) the client must support to access the
        /// feature. Empty when the requirement isn't expressible as a config version
        /// (e.g. a method with <c>BreakingSignature = true</c> needs the client to be on a
        /// build that includes the new signature, not strictly a config version).
        /// </summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public string MinRequiredVersion { get; set; } = "";

        /// <summary>
        /// Optional developer-supplied free-text explanation surfaced in logs / dev UIs.
        /// Not intended for end-user display verbatim — game UI typically renders a
        /// localized message based on <see cref="FeatureKind"/> + <see cref="Identifier"/>.
        /// </summary>
        [Id(3), Key(3), MemoryPackOrder(3)] public string Reason { get; set; } = "";

        /// <summary>Renders a single-line message suitable for log lines and exception text.</summary>
        public string ToErrorMessage()
        {
            var ver = string.IsNullOrEmpty(MinRequiredVersion) ? "" : $" (requires version {MinRequiredVersion})";
            var why = string.IsNullOrEmpty(Reason) ? "" : $": {Reason}";
            return $"Incompatible {FeatureKind.ToLowerInvariant()} '{Identifier}'{ver}{why}. Update the client to use this feature.";
        }
    }
}
