using System.Threading.Tasks;
using Orleans;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Per-config-type audit grain. Keyed by the config's stable name (typically
    /// <see cref="System.Type.FullName"/>). Lives alongside <see cref="IConfigRegistry"/>
    /// — registry stores bytes, this grain stores who/when/why/origin per published version.
    /// <para>
    /// Wired automatically by <c>services.AddSharedMetaConfigAdmin()</c>. Admin tools read
    /// through this grain (or via <see cref="IConfigAdminGrain"/>); the registry itself
    /// never sees the audit fields, so a project that doesn't want audit can skip
    /// the admin wiring and use <see cref="IConfigRegistry"/> directly.
    /// </para>
    /// </summary>
    public interface IConfigMetadataGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Record an audit entry for a (config-name, version) pair after the bytes were
        /// successfully published into <see cref="IConfigRegistry"/>. Idempotent — re-recording
        /// the same version overwrites the existing entry (typical scenario: re-publish
        /// after edit).
        /// </summary>
        Task RecordPublishAsync(string version, int sizeBytes, string origin, string publishedBy, string? notes);

        /// <summary>Drop the audit entry for a specific version. Pairs with <see cref="IConfigRegistry.UnpublishAsync"/>.</summary>
        Task RemoveAsync(string version);

        /// <summary>List every audited version, newest <see cref="ConfigVersionInfo.PublishedAt"/> first.</summary>
        Task<ConfigVersionInfo[]> ListAsync();

        /// <summary>Fetch a single version's audit record, or <c>null</c> if not recorded.</summary>
        Task<ConfigVersionInfo?> GetAsync(string version);

        /// <summary>
        /// 0.32.0+ Hold (<paramref name="held"/> = true) or release a <c>Major.Minor</c> branch key
        /// (e.g. <c>"1.2"</c>). A held branch is skipped by the cold-start config bootstrap seed, so a
        /// manually uploaded version survives silo restarts without being overwritten or auto-patch-bumped
        /// (see <see cref="ConfigSeedStrategy.LoadIfHashDiff"/> / <see cref="ConfigSeedStrategy.LoadAlways"/>).
        /// Idempotent.
        /// </summary>
        Task SetBranchHoldAsync(string branchKey, bool held);

        /// <summary>The set of currently held <c>Major.Minor</c> branch keys. Empty when none are held.</summary>
        Task<string[]> ListHeldBranchesAsync();

        /// <summary>Cheap single-branch hold check used by the bootstrap seed at cold start.</summary>
        Task<bool> IsBranchHeldAsync(string branchKey);
    }
}
