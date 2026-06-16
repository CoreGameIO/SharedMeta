using System;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.27.1+ Serialized seed bytes returned by <see cref="IConfigBootstrapper.GetBytesAsync"/>.
    /// Carries the bytes the framework publishes into <see cref="IConfigRegistry"/> plus the audit
    /// metadata that lands in <see cref="IConfigMetadataGrain"/>.
    /// <para>
    /// Version is not on this DTO — it's the second argument to <c>GetBytesAsync</c>, set
    /// by <see cref="IConfigBootstrapper.GetVersionAsync"/> in the earlier phase.
    /// </para>
    /// </summary>
    public sealed class ConfigBootstrapBytes
    {
        /// <summary>Serialized config bytes — must match the project's <see cref="SharedMeta.Core.IMetaSerializer"/> wire format.</summary>
        public byte[] Bytes { get; init; } = Array.Empty<byte>();

        /// <summary>Free-form origin label recorded on audit. Built-ins: <c>"directory"</c>, <c>"default-instance"</c>.</summary>
        public string Origin { get; init; } = "bootstrap";

        /// <summary>Who triggered the publish. Typical: <c>"bootstrap"</c> or the build/CI id.</summary>
        public string PublishedBy { get; init; } = "bootstrap";

        /// <summary>Optional audit note (manifest version, git sha, etc.).</summary>
        public string? Notes { get; init; }
    }
}
