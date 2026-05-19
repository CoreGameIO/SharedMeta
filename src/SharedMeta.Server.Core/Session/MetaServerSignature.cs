using System.Collections.Generic;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Server-side mirror of <see cref="SharedMeta.Core.Transport.MetaClientSignature"/>:
    /// the full set of methods the server exposes plus the per-method compatibility metadata
    /// needed to compute <see cref="SharedMeta.Core.Transport.ClientCapabilities"/> for an
    /// incoming client signature.
    /// <para>
    /// Emitted as a generated <c>public static readonly</c> singleton on
    /// <c>GameServiceDiscovery</c> (the server-side concrete) by the source generator.
    /// Pure in-process data — never travels on the wire — so it stays a plain C# class
    /// without any serialization attributes.
    /// </para>
    /// </summary>
    public sealed class MetaServerSignature
    {
        /// <summary>Sorted (by <c>ServiceName</c>, <c>Alias</c>, <c>Version</c>) so the
        /// compute pipeline can binary-merge against the client's sorted KnownMethods.</summary>
        public IReadOnlyList<ServerMethodEntry> Methods { get; init; } = new List<ServerMethodEntry>();

        /// <summary>
        /// <c>[MetaConfigStructureBoundary]</c> declarations harvested from every
        /// <c>[MetaConfig]</c> class. Drives per-subscriber boundary compute in
        /// <c>EntityGrain.SubscribeAsync</c>. Empty when no config declares breakpoints.
        /// </summary>
        public IReadOnlyList<ConfigBoundaryEntry> ConfigBoundaries { get; init; } = new List<ConfigBoundaryEntry>();
    }

    /// <summary>
    /// One structural boundary declared on a config class via
    /// <c>[MetaConfigStructureBoundary("X.Y", Reason)]</c>. The compute pipeline reads
    /// these when deciding whether a client's resolved config version sits below a
    /// structural break — if so, every service bound to <see cref="ConfigTypeFullName"/>
    /// is added to <c>ForceServerPatchServices</c> for that session.
    /// </summary>
    public sealed class ConfigBoundaryEntry
    {
        /// <summary>Fully-qualified name of the annotated config class. Matched against
        /// <c>[MetaService(ConfigType = ...)]</c> declarations to find affected services.</summary>
        public string ConfigTypeFullName { get; init; } = "";

        /// <summary>Major.Minor breakpoint. Clients below this version need force-patch.</summary>
        public string MinConfigVersion { get; init; } = "";

        /// <summary>Developer-facing rationale (diagnostic only, never user-shown).</summary>
        public string Reason { get; init; } = "";
    }

    /// <summary>
    /// One server-side method declaration. Carries the version-compatibility floor needed
    /// to decide whether an older client may still call the method optimistically (Stage 6
    /// compute consults this).
    /// </summary>
    public sealed class ServerMethodEntry
    {
        public string ServiceName { get; init; } = "";
        public string Alias { get; init; } = "";

        /// <summary><c>[MetaMethod(Version = N)]</c>. Zero for legacy/unversioned.</summary>
        public int Version { get; init; }

        /// <summary>
        /// <c>[MetaMethod(MinCompatibleVersion = N)]</c>. When a client's KnownMethod for
        /// the same <c>(ServiceName, Alias)</c> reports a <c>Version</c> below this value,
        /// the method is added to <c>RejectedMethods</c> in the client's capabilities.
        /// </summary>
        public int MinCompatibleVersion { get; init; }

        /// <summary>FNV-1a hash of canonical parameter-type sequence — used to detect
        /// signature drift (Case 4) even when alias/version match.</summary>
        public ulong ArgHash { get; init; }

        /// <summary>Mirror of <c>[MetaMethod(GenerateClientApi = false)]</c>. Methods with this
        /// flag are server-only; if a client claims to know one, it is always rejected.</summary>
        public bool GenerateClientApi { get; init; } = true;

        /// <summary>
        /// FQN of the config class this service is bound to (<c>[MetaService(ConfigType = X)]</c>
        /// or the assembly default). Empty when no config is bound. Used to map
        /// config-structural breaks back to affected services.
        /// </summary>
        public string ConfigTypeFullName { get; init; } = "";
    }
}
