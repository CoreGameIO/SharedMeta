using System;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Per-version audit metadata for a registered config. Tracked alongside
    /// <see cref="IConfigRegistry"/> bytes (which intentionally don't carry authorship /
    /// provenance) via <see cref="IConfigMetadataGrain"/>.
    /// <para>
    /// <see cref="Origin"/> is a free-form string so projects can label delivery routes
    /// however they like — typical values: <c>"bootstrap"</c>, <c>"upload"</c>,
    /// <c>"edit"</c>, <c>"migration"</c>. The framework never branches on it; admin UI
    /// surfaces the label verbatim.
    /// </para>
    /// </summary>
    [GenerateSerializer, MemoryPackable, MessagePackObject]
    public partial class ConfigVersionInfo
    {
        /// <summary>Canonical <c>"Major.Minor.Patch"</c> string — matches <see cref="SharedMeta.Core.MetaConfigVersion.ToString"/>.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public string Version { get; set; } = "";

        /// <summary>Length of the serialized config bytes at publish time. Diagnostic only.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public int SizeBytes { get; set; }

        /// <summary>Wall-clock timestamp when <see cref="IConfigMetadataGrain.RecordPublishAsync"/> ran.</summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public DateTimeOffset PublishedAt { get; set; }

        /// <summary>Who triggered the publish — project-supplied (admin email, "bootstrap", "ci", …).</summary>
        [Id(3), Key(3), MemoryPackOrder(3)] public string PublishedBy { get; set; } = "";

        /// <summary>Optional free-form note (admin UI comment, ticket ID, etc.).</summary>
        [Id(4), Key(4), MemoryPackOrder(4)] public string? Notes { get; set; }

        /// <summary>Free-form origin label (see type doc).</summary>
        [Id(5), Key(5), MemoryPackOrder(5)] public string Origin { get; set; } = "";
    }

    /// <summary>
    /// 0.27.0+ Versions grouped by <c>Major.Minor</c> branch (newest patch first within the
    /// branch). Produced by <see cref="IConfigAdminGrain.GetConfigAsync"/> for UI display.
    /// </summary>
    [GenerateSerializer, MemoryPackable, MessagePackObject]
    public partial class ConfigBranchInfo
    {
        /// <summary>Branch key <c>"Major.Minor"</c> — same string as <see cref="SharedMeta.Core.MetaConfigVersion.GetBranchKey"/>.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public string Branch { get; set; } = "";

        /// <summary>Patches inside this branch, newest first.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public ConfigVersionInfo[] Versions { get; set; } = Array.Empty<ConfigVersionInfo>();

        /// <summary>
        /// 0.32.0+ When true, this <c>Major.Minor</c> branch is held: the cold-start bootstrap seed
        /// skips it, so a manually uploaded version won't be overwritten or auto-patch-bumped on restart.
        /// Toggle via <see cref="IConfigAdminGrain.SetBranchHoldAsync"/>.
        /// </summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public bool Held { get; set; }
    }

    /// <summary>
    /// 0.27.0+ Cluster-wide client-version snapshot returned by
    /// <see cref="IConfigAdminGrain.GetClientVersionsAsync"/>. Captures the runtime-effective
    /// values across the three subsystems:
    /// <list type="bullet">
    /// <item><see cref="Current"/> — from <c>ICurrentClientVersionGrain</c> (default app version).</item>
    /// <item><see cref="Min"/> / <see cref="Max"/> — from <c>IVersionPolicyGrain</c> (rejection gates).
    ///       <c>null</c> means "no cluster override; static config applies".</item>
    /// <item><see cref="Server"/> — from local <c>MetaTransportOptions.ServerVersion</c>; not
    ///       runtime-managed (changes only via redeploy).</item>
    /// </list>
    /// </summary>
    [GenerateSerializer, MemoryPackable, MessagePackObject]
    public partial class ClientVersionSnapshot
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string? Current { get; set; }
        [Id(1), Key(1), MemoryPackOrder(1)] public string? Min { get; set; }
        [Id(2), Key(2), MemoryPackOrder(2)] public string? Max { get; set; }
        [Id(3), Key(3), MemoryPackOrder(3)] public string? Server { get; set; }
    }

    /// <summary>
    /// 0.27.0+ Per-config overview returned by admin grain — name + branches.
    /// </summary>
    [GenerateSerializer, MemoryPackable, MessagePackObject]
    public partial class ConfigOverview
    {
        /// <summary>
        /// Stable identifier for the config — by default the config type's
        /// <see cref="Type.FullName"/>. Used as the <see cref="IConfigMetadataGrain"/> key
        /// and the lookup token for <see cref="IConfigAdminGrain"/> calls.
        /// </summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public string ConfigName { get; set; } = "";

        /// <summary>Short display name (typically <see cref="Type.Name"/>) — for admin UI titles.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public string DisplayName { get; set; } = "";

        /// <summary>Branches in descending <c>Major.Minor</c> order.</summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public ConfigBranchInfo[] Branches { get; set; } = Array.Empty<ConfigBranchInfo>();
    }
}
