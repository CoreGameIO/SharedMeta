using System.Threading.Tasks;
using Orleans;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Thin admin facade over <see cref="IConfigRegistry"/> + <see cref="IConfigMetadataGrain"/>.
    /// Project admin tools (Blazor / WPF / .NET CLI / etc.) join the Orleans cluster as a
    /// client and call this grain directly — no HTTP intermediary required.
    /// <para>
    /// Lookup keys (<c>name</c> parameters) match <see cref="ConfigOverview.ConfigName"/> —
    /// typically the config type's <see cref="System.Type.FullName"/>. The framework's
    /// auto-discovery (from <c>[MetaConfig]</c> attributes) populates this name space at
    /// silo startup; projects don't need to register names manually.
    /// </para>
    /// <para>
    /// Authentication / authorization is intentionally out of scope: this is a plain
    /// Orleans grain, so an admin tool wraps it behind whatever auth (cookie, JWT, mutual
    /// TLS, AD) suits the project. The grain trusts its caller — restrict access via the
    /// Orleans cluster's network ACL.
    /// </para>
    /// <para>
    /// YAML (or any other source-format) compile/decode is also out of scope. Projects that
    /// want a "view as YAML" admin flow wrap <see cref="DownloadAsync"/> / <see cref="UploadAsync"/>
    /// in their own UI handlers; the framework only deals in serialized bytes.
    /// </para>
    /// </summary>
    public interface IConfigAdminGrain : IGrainWithIntegerKey
    {
        /// <summary>Stable config names (typically <c>FullName</c>) of every <c>[MetaConfig]</c> type the silo knows about.</summary>
        Task<string[]> ListConfigNamesAsync();

        /// <summary>Overview rows for every known config: name + branches + versions.</summary>
        Task<ConfigOverview[]> ListConfigsAsync();

        /// <summary>One overview row, or <c>null</c> when <paramref name="name"/> isn't a discovered config.</summary>
        Task<ConfigOverview?> GetConfigAsync(string name);

        /// <summary>
        /// Download the raw published bytes for a specific (name, version). Throws when the
        /// config is unknown or the version was never published / has been unpublished.
        /// </summary>
        Task<byte[]> DownloadAsync(string name, string version);

        /// <summary>
        /// Publish <paramref name="bytes"/> under (name, version). Routes through
        /// <see cref="ConfigRegistryExtensions.PublishIfChangedAsync"/> — same-content
        /// re-publish is a no-op; drift (different content under the same version) is
        /// detected and surfaced via <paramref name="failOnDrift"/>.
        /// Records the audit entry via <see cref="IConfigMetadataGrain"/>.
        /// </summary>
        /// <param name="origin">Project-supplied label for delivery route (e.g. <c>"upload"</c>, <c>"edit"</c>).</param>
        Task<ConfigOverview> UploadAsync(string name, string version, byte[] bytes, string origin, string publishedBy, string? notes = null, bool failOnDrift = false);

        /// <summary>
        /// Drop the (name, version) pair from both the registry and audit. Returns <c>false</c>
        /// if the config name is unknown; otherwise <c>true</c> regardless of whether the
        /// version was actually present (the unpublish is idempotent).
        /// </summary>
        Task<bool> UnpublishAsync(string name, string version, string deletedBy);

        /// <summary>
        /// Read the cluster-wide client-version snapshot. <c>Current</c> from
        /// <c>ICurrentClientVersionGrain</c>; <c>Min</c> / <c>Max</c> from
        /// <c>IVersionPolicyGrain</c> (null when no admin override active); <c>Server</c>
        /// from the local silo's <c>MetaTransportOptions.ServerVersion</c>.
        /// </summary>
        Task<ClientVersionSnapshot> GetClientVersionsAsync();

        /// <summary>
        /// Set the cluster-wide current client version (the "default app version" returned
        /// by <c>IConfigVersionResolver.CurrentClientVersion</c>). Writes through to
        /// <c>ICurrentClientVersionGrain</c> and pushes the value into the local silo's
        /// <c>DefaultClientVersionService</c> cache (other silos pick it up via their
        /// background poll). Returns the post-write snapshot.
        /// </summary>
        Task<ClientVersionSnapshot> SetCurrentClientVersionAsync(string version, string changedBy);

        /// <summary>
        /// Set the cluster-wide minimum client version (rejection gate). Pass <c>null</c> to
        /// clear the override and fall back to <c>MetaTransportOptions.MinClientVersion</c>.
        /// Writes through to <c>IVersionPolicyGrain</c>. Returns the post-write snapshot.
        /// </summary>
        Task<ClientVersionSnapshot> SetMinClientVersionAsync(string? version, string changedBy);

        /// <summary>
        /// Set the cluster-wide maximum client version. Pass <c>null</c> to clear the override.
        /// Writes through to <c>IVersionPolicyGrain</c>. Returns the post-write snapshot.
        /// </summary>
        Task<ClientVersionSnapshot> SetMaxClientVersionAsync(string? version, string changedBy);
    }
}
